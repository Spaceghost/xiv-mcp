using System.Net;
using System.Net.Sockets;
using System.Reflection;
using XivMcp.Core;
using XivMcp.Core.Net;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Providers.World;

namespace XivMcp.Standalone;

/// <summary>Everything the host needs to start, already resolved (see <see cref="Program"/> for where each value comes from).</summary>
internal sealed record StandaloneHostOptions
{
    public required SharedSettings Settings { get; init; }

    /// <summary>Addresses to bind; loopback when empty.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public IReadOnlyList<string> AllowedHostNames { get; init; } = [];

    /// <summary>The game's sqpack directory, or null when no installation was found.</summary>
    public string? SqpackPath { get; init; }

    public string Language { get; init; } = "en";

    public int RateLimitPerMinute { get; init; } = 600;

    public Action<string> Log { get; init; } = static _ => { };
}

/// <summary>
/// The pre-game MCP host: one <see cref="McpServer"/> with the plugin's static providers (compiled from the same source),
/// its own get_server_info, and a stub for every other tool in the plugin's catalogue.
/// </summary>
internal sealed class StandaloneHost : IAsyncDisposable, IListener
{
    private readonly StandaloneHostOptions options;
    private readonly LuminaGameDataSource? data;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public StandaloneHost(StandaloneHostOptions options)
    {
        this.options = options;
        var settings = options.Settings;
        var serverOptions = new McpServerOptions
        {
            Port = settings.Port,
            Path = settings.Path,
            BearerToken = settings.RequireToken ? settings.BearerToken : null,
            ClientTokens = settings.ClientTokens,
            ServerTitle = "FINAL FANTASY XIV (standalone: game data only)",
            ServerVersion = Version,
            RateLimitPerMinute = options.RateLimitPerMinute,
            Instructions =
                "This is the standalone xiv-mcp host: FINAL FANTASY XIV is not running. Tools whose _meta dev.xivmcp/availability is \"static\" " +
                "work (game data: items, recipes, quests, duties, actions, zones, weather, Eorzea time). Every other tool answers with the " +
                "game_not_running error until the player starts the game, when the in-game plugin takes over this same endpoint.",
        };
        serverOptions.Hosts.AddRange(options.Hosts);
        serverOptions.AllowedHostNames.AddRange(options.AllowedHostNames);
        serverOptions.AllowedOrigins.AddRange(settings.AllowedOrigins);

        Server = new McpServer(serverOptions, new OfflineGameThread(), new OfflineHostState(), (message, ex) => options.Log(ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}"));

        if (options.SqpackPath is { } sqpack)
        {
            try
            {
                data = new LuminaGameDataSource(sqpack, LuminaGameDataSource.ParseLanguage(options.Language));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                options.Log($"could not open the game data at {sqpack}: {ex.Message}");
            }
        }

        RegisterProviders();
        Coordinator = new HandoffCoordinator(this, PortIsFree, options.Log);
        Server.ControlHandler = HandleControlAsync;
    }

    public static string Version { get; } = typeof(StandaloneHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public McpServer Server { get; }

    public HandoffCoordinator Coordinator { get; }

    public bool HasGameData => data is not null;

    public string? GameVersion => data?.GameVersion;

    /// <summary>Tools this host runs for real (the rest are stubs).</summary>
    public IReadOnlySet<string> ServedTools { get; private set; } = new HashSet<string>();

    public string Endpoint => $"http://{(options.Hosts.Count > 0 ? options.Hosts[0] : "127.0.0.1")}:{options.Settings.Port}{options.Settings.Path}";

    Task IListener.StartAsync() => Server.StartAsync();

    Task IListener.StopAsync() => Server.StopAsync();

    private void RegisterProviders()
    {
        var services = new List<object>
        {
            new OfflineWorld(),
            Server.GameThread,
            Server.Notifier,
            new StandaloneFacts(Version, () => Endpoint, startedAt, data?.GameVersion, options.Language, Server),
        };
        if (data is not null)
            services.Add(data);

        foreach (var type in typeof(StandaloneHost).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<McpProviderAttribute>() is not null)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
            var arguments = ctor.GetParameters().Select(p => services.FirstOrDefault(s => p.ParameterType.IsInstanceOfType(s))).ToArray();
            if (arguments.Any(a => a is null))
            {
                // Needs the game data and there is none: its tools are stubbed as "unavailable" below.
                continue;
            }

            try
            {
                Server.RegisterProvider(ctor.Invoke(arguments));
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException)
            {
                options.Log($"provider {type.Name} failed to load: {(ex.InnerException ?? ex).Message}");
            }
        }

        ServedTools = Server.ListRegisteredTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Server.RegisterExternalTools(LiveToolStubs.Build(LiveToolStubs.LoadCatalogue(), ServedTools, gameDataMissing: data is null));
    }

    private Task<ControlResponse?> HandleControlAsync(ControlRequest request) => Task.FromResult<ControlResponse?>(request switch
    {
        { Method: "GET", SubPath: HostHandoff.HostRoute } => new ControlResponse(200, HostHandoff.Identity(HostKind.Standalone, Version, gameRunning: false)),
        { Method: "POST", SubPath: HostHandoff.HandoffRoute } => Coordinator.OnHandoffRequested(request, Version),
        _ => null,
    });

    /// <summary>True when nothing listens on our first address and port (checked by binding it for an instant).</summary>
    private bool PortIsFree()
    {
        try
        {
            var address = options.Hosts.Count > 0 && IPAddress.TryParse(options.Hosts[0], out var parsed) ? parsed : IPAddress.Loopback;
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            probe.Bind(new IPEndPoint(address, options.Settings.Port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync().ConfigureAwait(false);
        Coordinator.Dispose();
        data?.Dispose();
    }
}
