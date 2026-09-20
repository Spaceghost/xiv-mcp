using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Core.Net;

namespace XivMcp.Plugin.Services;

/// <summary>Outcome of constructing and registering one [McpProvider] type.</summary>
public sealed record ProviderInfo(Type Type, string Category, object? Instance, string? Error)
{
    public bool Loaded => Instance != null && Error == null;
}

/// <summary>
/// Owns the single <see cref="McpServer"/> for the plugin's lifetime: provider discovery and DI,
/// start/stop/restart (serialized), config application, status snapshots and unload. Every
/// failure is captured into <see cref="LastError"/> / <see cref="Providers"/> for the UI instead
/// of being thrown into Dalamud.
/// </summary>
public sealed class ServerHost : IDisposable
{
    private static readonly MethodInfo CreateAsyncMethod =
        typeof(IDalamudPluginInterface).GetMethod(nameof(IDalamudPluginInterface.CreateAsync))!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly NotifierProxy notifier;
    private readonly ConfirmationService confirmations;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly List<ProviderInfo> providers = [];
    private readonly DateTimeOffset loadedAt = DateTimeOffset.UtcNow;

    private readonly TailnetProbe tailnetProbe;

    private string appliedFingerprint = "";
    private BindPlan appliedPlan;
    private string permissionFingerprint;
    private volatile ServerStatus status;
    private volatile bool running;
    private volatile bool transitioning;
    private DateTimeOffset? startedAt;
    private long activityVersion;
    private bool disposed;

    public ServerHost(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        Configuration config,
        IGameThread gameThread,
        HostState hostState,
        NotifierProxy notifier,
        ConfirmationService confirmations)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.config = config;
        this.notifier = notifier;
        this.confirmations = confirmations;
        HostState = hostState;

        PluginVersion = typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        tailnetProbe = new TailnetProbe(OnServerLog);
        appliedPlan = ResolvePlan();
        var options = new McpServerOptions();
        ApplyOptions(options);
        Server = new McpServer(options, gameThread, hostState, OnServerLog) { Approver = confirmations };
        Server.ActivityRecorded += OnActivityRecorded;
        notifier.Attach(Server);
        status = SafeGetStatus();
        permissionFingerprint = PermissionFingerprint();
    }

    public McpServer Server { get; }

    public HostState HostState { get; }

    public ConfirmationService Confirmations => confirmations;

    public string PluginVersion { get; }

    public bool IsRunning => running;

    public bool IsTransitioning => transitioning;

    /// <summary>Last start/stop/bind error, cleared by a successful start.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? StartedAt => startedAt;

    public DateTimeOffset LoadedAt => loadedAt;

    /// <summary>
    /// The bind plan in force: what the running server was started with, or what a start would use now.
    /// This is the one place that turns <see cref="Configuration.BindMode"/> plus live tailnet detection
    /// into addresses; the window, the status payload and IPC all read it instead of deriving their own.
    /// </summary>
    public BindPlan Plan => running ? appliedPlan : ResolvePlan();

    /// <summary>Every endpoint URL the server answers on (or would), in bind order.</summary>
    public IReadOnlyList<string> Endpoints => BindPlanner.Endpoints(Plan, config.Port, config.Path);

    /// <summary>
    /// The endpoint to hand a client. With the tailnet bound this is the tailnet address, which is the one
    /// that works from another machine as well as from this one; otherwise it is loopback.
    /// </summary>
    public string PreferredEndpoint => BindPlanner.Endpoint(Plan.PreferredHost, config.Port, config.Path);

    /// <summary>The loopback endpoint, or null in tailnet-only mode where loopback is not bound.</summary>
    public string? LoopbackEndpoint =>
        Plan.LoopbackHost is { } host ? BindPlanner.Endpoint(host, config.Port, config.Path) : null;

    /// <summary>The detected tailnet address, without probing. Null before the first probe or when there is none.</summary>
    public TailnetAddress? TailnetAddress => tailnetProbe.Cached();

    /// <summary>True once tailnet detection has run at least once, so the UI can say "none" rather than "unknown".</summary>
    public bool TailnetProbed => tailnetProbe.HasProbed;

    /// <summary>
    /// Probes for the tailnet address again and returns it. Blocking (adapter enumeration, possibly a
    /// reverse lookup): call it from a background task, not from the draw loop.
    /// </summary>
    public TailnetAddress? RefreshTailnet() => tailnetProbe.Refresh();

    /// <summary>Endpoint the running server was started with (or would start with).</summary>
    public string Endpoint => running ? RunningEndpoint : PreferredEndpoint;

    private string RunningEndpoint { get; set; } = "";

    /// <summary>The plan the current configuration and the last detection result produce.</summary>
    private BindPlan ResolvePlan() => config.ResolveBindPlan(tailnetProbe.Cached());

    /// <summary>Increments on every handled request (cheap change detection for UIs).</summary>
    public long ActivityVersion => Interlocked.Read(ref activityVersion);

    /// <summary>Latest server snapshot (refreshed by <see cref="Tick"/> and lifecycle changes).</summary>
    public ServerStatus Status => status;

    /// <summary>True when the endpoint-affecting settings differ from what the server runs with.</summary>
    public bool RestartPending => running && appliedFingerprint != EndpointFingerprint();

    public IReadOnlyList<ProviderInfo> Providers
    {
        get
        {
            lock (providers)
                return providers.ToArray();
        }
    }

    public IReadOnlyList<string> Categories =>
        Providers.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Raised (any thread) when running state, status counters or errors change.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (caller's thread) after permission tiers, categories or the confirmation toggle changed.</summary>
    public event Action? PermissionsChanged;

    /// <summary>Raised (thread-pool thread) for every handled request.</summary>
    public event Action<ActivityEntry>? ActivityRecorded;

    // ---- providers ------------------------------------------------------------------------

    /// <summary>
    /// Constructs every [McpProvider] type in <paramref name="assembly"/> through Dalamud's IoC
    /// (services + <paramref name="scopedObjects"/>) and registers each one on its own, so one
    /// provider failing to construct or register never affects the others.
    /// </summary>
    public void LoadProviders(Assembly assembly, params object[] scopedObjects)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
            log.Error(ex, "Some types in {Assembly} failed to load", assembly.GetName().Name ?? "?");
        }

        var candidates = types
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<McpProviderAttribute>()))
            .Where(x => x.Attr != null && x.Type is { IsClass: true, IsAbstract: false })
            .OrderBy(x => x.Attr!.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Type.FullName, StringComparer.Ordinal);

        foreach (var (type, attr) in candidates)
        {
            var info = LoadProvider(type, attr!.Category, scopedObjects);
            lock (providers)
                providers.Add(info);
        }

        var failed = Providers.Count(p => !p.Loaded);
        log.Information("Loaded {Loaded} MCP providers ({Failed} failed)", Providers.Count - failed, failed);
        RaiseStateChanged();
    }

    private ProviderInfo LoadProvider(Type type, string category, object[] scopedObjects)
    {
        object instance;
        try
        {
            var task = (Task)CreateAsyncMethod.MakeGenericMethod(type).Invoke(pluginInterface, [scopedObjects])!;
            task.GetAwaiter().GetResult();
            instance = task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)
                ?? throw new InvalidOperationException("Dalamud IoC returned null.");
        }
        catch (Exception ex)
        {
            var message = "construct: " + Describe(ex);
            log.Error(ex, "MCP provider {Provider} failed to construct", type.FullName ?? type.Name);
            return new ProviderInfo(type, category, null, message);
        }

        try
        {
            Server.RegisterProvider(instance);
            return new ProviderInfo(type, category, instance, null);
        }
        catch (Exception ex)
        {
            log.Error(ex, "MCP provider {Provider} failed to register", type.FullName ?? type.Name);

            // Keep the instance so it is still disposed on unload (it may have subscribed to events).
            return new ProviderInfo(type, category, instance, "register: " + Describe(ex));
        }
    }

    /// <summary>Innermost meaningful exception message, unwrapping IoC/reflection wrappers.</summary>
    public static string Describe(Exception ex)
    {
        var current = ex;
        while (current is AggregateException { InnerException: not null } or TargetInvocationException { InnerException: not null }
               || (current.InnerException != null && current.Message.StartsWith("Failed to create ", StringComparison.Ordinal)))
        {
            current = current.InnerException!;
        }

        return $"{current.GetType().Name}: {current.Message}";
    }

    // ---- lifecycle -------------------------------------------------------------------------

    public Task StartAsync() => RunLifecycle(StartCoreAsync);

    public Task StopAsync() => RunLifecycle(StopCoreAsync);

    public Task RestartAsync() => RunLifecycle(async () =>
    {
        await StopCoreAsync().ConfigureAwait(false);
        await StartCoreAsync().ConfigureAwait(false);
    });

    /// <summary>
    /// Call after the configuration was edited and saved. Restarts a running server when the
    /// endpoint, token, origins or timeouts changed; announces list changes when permission tiers
    /// or categories changed.
    /// </summary>
    public Task ApplyConfigAsync()
    {
        // Live options (no restart needed).
        Server.Options.ApprovalTimeout = TimeSpan.FromSeconds(config.ConfirmTimeoutSeconds);
        Server.Options.CallTimeout = TimeSpan.FromSeconds(config.CallTimeoutSeconds);
        Server.Options.ClientTokens = config.ClientTokenHashes();

        var permissions = PermissionFingerprint();
        if (permissions != permissionFingerprint)
        {
            permissionFingerprint = permissions;

            // A grant was given under the old settings; ask again under the new ones.
            confirmations.RevokeGrants();
            try
            {
                PermissionsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "PermissionsChanged subscriber failed");
            }

            notifier.AllListsChanged();
            RaiseStateChanged();
        }

        return RestartPending ? RestartAsync() : Task.CompletedTask;
    }

    private async Task RunLifecycle(Func<Task> step)
    {
        if (disposed)
            return;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        transitioning = true;
        RaiseStateChanged();
        try
        {
            await Task.Run(step).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            log.Error(ex, "MCP server lifecycle step failed");
        }
        finally
        {
            transitioning = false;
            lifecycle.Release();
            status = SafeGetStatus();
            RaiseStateChanged();
        }
    }

    private async Task StartCoreAsync()
    {
        if (running || disposed)
            return;

        // Detect the tailnet afresh on every start: the address survives reboots but not always a
        // Tailscale restart, and the plugin outlives both.
        tailnetProbe.Refresh();
        var plan = ResolvePlan();

        if (plan.RequiresToken && (!config.RequireToken || string.IsNullOrEmpty(config.BearerToken)))
        {
            LastError =
                $"Refusing to listen on {BindPlanner.Describe(plan)} without a bearer token: anyone who can reach that address could drive the game. "
                + "Switch 'Require bearer token' on, or use the loopback-only bind mode.";
            return;
        }

        ApplyOptions(Server.Options, plan);
        try
        {
            await Server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = $"Could not start on {BindPlanner.Describe(plan)}:{config.Port}: {Describe(ex)}";
            log.Error(ex, "MCP server failed to start on {Hosts}:{Port}", BindPlanner.Describe(plan), config.Port);
            try
            {
                await Server.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort: release anything a partial start bound.
            }

            return;
        }

        running = true;
        startedAt = DateTimeOffset.UtcNow;
        appliedPlan = plan;
        appliedFingerprint = EndpointFingerprint();
        RunningEndpoint = BindPlanner.Endpoint(plan.PreferredHost, config.Port, config.Path);
        LastError = null;
        if (plan.Notice is { } notice)
            log.Warning("MCP bind: {Notice}", notice);
        if (plan.RequiresToken)
            log.Warning("MCP server is listening OFF THIS MACHINE on {Hosts}:{Port}", BindPlanner.Describe(plan), config.Port);
        else
            log.Information("MCP server listening on {Endpoint}", RunningEndpoint);
    }

    private async Task StopCoreAsync()
    {
        if (!running)
            return;
        try
        {
            await Server.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            running = false;
            startedAt = null;
            log.Information("MCP server stopped");
        }
    }

    private void ApplyOptions(McpServerOptions options) => ApplyOptions(options, ResolvePlan());

    private void ApplyOptions(McpServerOptions options, BindPlan plan)
    {
        options.Host = plan.Hosts[0];
        options.Hosts = plan.Hosts.ToList();

        // The DNS-rebinding check accepts loopback names plus the bound addresses; the MagicDNS name is
        // the one extra name a tailnet client is likely to put in the Host header.
        options.AllowedHostNames = plan.AllowedHostNames.ToList();
        options.Port = config.Port;
        options.Path = config.Path;
        options.BearerToken = config.RequireToken ? config.BearerToken : null;
        options.AllowedOrigins = config.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        options.ClientTokens = config.ClientTokenHashes();
        options.ServerVersion = PluginVersion;
        options.Instructions = ServerInstructions;

        // The call timeout starts after in-game approval, which has its own timeout.
        options.CallTimeout = TimeSpan.FromSeconds(config.CallTimeoutSeconds);
        options.ApprovalTimeout = TimeSpan.FromSeconds(config.ConfirmTimeoutSeconds);
    }

    private string EndpointFingerprint()
    {
        var plan = ResolvePlan();
        return string.Join(
            '',
            string.Join(',', plan.Hosts),
            string.Join(',', plan.AllowedHostNames),
            config.Port,
            config.Path,
            config.RequireToken ? config.BearerToken : "",
            string.Join(',', config.AllowedOrigins));
    }

    private string PermissionFingerprint() => string.Join(
        '',
        config.AllowRead,
        config.AllowUi,
        config.AllowAction,
        config.AllowChat,
        config.ConfirmActions,
        string.Join(',', config.DisabledCategories.Order(StringComparer.OrdinalIgnoreCase)));

    public const string ServerInstructions =
        "This server runs inside the FINAL FANTASY XIV client (Dalamud plugin) of the player you are helping. " +
        "Tools are grouped into permission tiers the player controls in game: Read (observe game state and game data), " +
        "Ui (local-only visible effects such as echo messages, toasts and map flags), Action (changes the local client: " +
        "targeting, gearsets, teleport, slash commands) and Chat (text other players can see). Disabled tiers and " +
        "categories are hidden from tools/list; call get_server_info to see what is enabled. Action and Chat calls may " +
        "wait for the player to approve them in game and fail if denied or not confirmed in time; do not retry a denied " +
        "call unless the player asks. Many tools need a logged-in character and " +
        "report a clear error otherwise. For multi-step work, call post_status with a short agent name to show your " +
        "progress in the player's game UI, and finish with state done or failed. Never send Chat-tier text the player " +
        "did not ask for. If the player may be away, queue Action/Chat calls with request_action and poll get_ticket instead of " +
        "calling them directly; the player approves tickets later.";

    // ---- status / activity ------------------------------------------------------------------

    /// <summary>Call about once a second from the framework update; raises StateChanged on change.</summary>
    public void Tick()
    {
        var next = SafeGetStatus();
        var previous = status;
        status = next;
        if (previous.ActiveSessions != next.ActiveSessions
            || previous.Running != next.Running
            || previous.LastError != next.LastError
            || !previous.ConnectedClients.SequenceEqual(next.ConnectedClients))
        {
            RaiseStateChanged();
        }
    }

    private ServerStatus SafeGetStatus()
    {
        try
        {
            return Server.GetStatus();
        }
        catch (Exception ex)
        {
            return new ServerStatus(running, config.EndpointUrl, 0, 0, 0, Describe(ex), []);
        }
    }

    public IReadOnlyList<ActivityEntry> GetActivity(int max)
    {
        try
        {
            return Server.GetActivity(max);
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<(string Name, string? Title, string Description, ToolPermission Permission, string Category)> ListTools()
    {
        try
        {
            return Server.ListRegisteredTools();
        }
        catch
        {
            return [];
        }
    }

    private static readonly HashSet<string> CallMethods = new(StringComparer.Ordinal) { "tools/call", "resources/read", "prompts/get" };

    private void OnActivityRecorded(ActivityEntry entry)
    {
        try
        {
            Interlocked.Increment(ref activityVersion);
            var level = config.ActivityLogLevel;
            var shouldLog = level switch
            {
                ActivityLogLevel.All => true,
                ActivityLogLevel.Calls => !entry.Success || CallMethods.Contains(entry.Method),
                ActivityLogLevel.Failures => !entry.Success,
                _ => false,
            };
            if (shouldLog)
            {
                if (entry.Success)
                    log.Information("MCP {Method} {Target} from {Client} ok in {Ms:0}ms", entry.Method, entry.Target ?? "", entry.ClientName ?? "?", entry.DurationMs);
                else
                    log.Warning("MCP {Method} {Target} from {Client} failed in {Ms:0}ms: {Error}", entry.Method, entry.Target ?? "", entry.ClientName ?? "?", entry.DurationMs, entry.Error ?? "");
            }

            ActivityRecorded?.Invoke(entry);
        }
        catch
        {
            // Raised on server threads; a UI/IPC subscriber failure must not surface there.
        }
    }

    private void OnServerLog(string message, Exception? exception)
    {
        try
        {
            if (exception != null)
                log.Warning(exception, "[core] {Message}", message);
            else
                log.Debug("[core] {Message}", message);
        }
        catch
        {
            // Logging must never throw into the server.
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "StateChanged subscriber failed");
        }
    }

    // ---- unload ----------------------------------------------------------------------------

    /// <summary>
    /// Stops the server synchronously (bounded) so a hot reload frees the port, then disposes
    /// the server and every provider that implements IDisposable/IAsyncDisposable.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Server.ActivityRecorded -= OnActivityRecorded;

        // Run off the calling (possibly framework) thread and bound the wait: in-flight calls
        // queued for the framework thread cannot complete while that thread is blocked here.
        WaitBounded(() => Server.StopAsync(), "stop");
        running = false;
        WaitBounded(() => Server.DisposeAsync().AsTask(), "dispose");
        notifier.Attach(null);

        foreach (var info in Providers.Reverse())
        {
            try
            {
                switch (info.Instance)
                {
                    case IAsyncDisposable asyncDisposable:
                        WaitBounded(() => asyncDisposable.DisposeAsync().AsTask(), info.Type.Name);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "MCP provider {Provider} failed to dispose", info.Type.FullName ?? info.Type.Name);
            }
        }

        lock (providers)
            providers.Clear();
    }

    private void WaitBounded(Func<Task> action, string what)
    {
        try
        {
            if (!Task.Run(action).Wait(TimeSpan.FromSeconds(5)))
                log.Warning("MCP {What} did not finish within 5s during unload", what);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MCP {What} failed during unload", what);
        }
    }
}
