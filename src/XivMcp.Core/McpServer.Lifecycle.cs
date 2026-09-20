using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Http;
using XivMcp.Core.Protocol;
using XivMcp.Core.Registry;

namespace XivMcp.Core;

public sealed partial class McpServer
{
    private const int MaxSessions = 256;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, LegacySession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ListenSubscription, byte> _listeners = new();
    private readonly ConcurrentDictionary<string, long> _recentModernClients = new(StringComparer.Ordinal);

    private ProviderRegistry _registry = null!;
    private ActivityLog _activity = null!;
    private ServerNotifier _notifier = null!;

    private HttpServer? _http;
    private CancellationTokenSource _serverCts = new();
    private Task? _housekeeping;
    private CancellationTokenSource? _housekeepingCts;

    // Snapshotted at StartAsync.
    private byte[]? _tokenHash;
    private HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private bool _loopbackBind;
    private string _path = "/mcp";
    private string _endpoint = "";

    private long _totalRequests;
    private long _failedRequests;
    private volatile string? _lastError;
    private string _gateSignature = "";

    /// <summary>The TCP port actually bound (useful when <see cref="McpServerOptions.Port"/> is 0), or 0 when stopped.</summary>
    public int ListeningPort => _http?.Port ?? 0;

    /// <summary>Test hook: replaces the HTTP limits derived from options at the next StartAsync.</summary>
    internal HttpLimits? HttpLimitsOverride { get; set; }

    /// <summary>True while the listener is bound.</summary>
    public bool IsRunning => _http is not null;

    private partial void InitializeCore()
    {
        _registry = new ProviderRegistry();
        _activity = new ActivityLog(Options.ActivityCapacity);
        _notifier = new ServerNotifier(this);
    }

    public partial IMcpNotifier Notifier => _notifier;

    public partial void RegisterProvider(object provider)
    {
        var change = _registry.Register(provider);
        if (_http is null)
            return;
        if (change.HasFlag(RegistryChange.Tools))
            _notifier.ToolListChanged();
        if (change.HasFlag(RegistryChange.Resources))
            _notifier.ResourceListChanged();
        if (change.HasFlag(RegistryChange.Prompts))
            _notifier.PromptListChanged();
    }

    public partial void RegisterProviders(Assembly assembly, Func<Type, object> factory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(factory);

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
            foreach (var loaderException in ex.LoaderExceptions)
                LogSink($"type load failed while scanning {assembly.GetName().Name}", loaderException);
        }

        var failures = new List<Exception>();
        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<McpProviderAttribute>() is not null).OrderBy(t => t!.FullName, StringComparer.Ordinal))
        {
            try
            {
                RegisterProvider(factory(type!));
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                var wrapped = new InvalidOperationException($"Provider {type!.FullName} was not registered: {inner.Message}", inner);
                failures.Add(wrapped);
                LogSink(wrapped.Message, inner);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} MCP provider(s) failed to register; the others are active.", failures);
    }

    public partial async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_http is not null)
                return;

            var address = ResolveAddress(Options.Host);
            _loopbackBind = IPAddress.IsLoopback(address);
            _tokenHash = string.IsNullOrEmpty(Options.BearerToken) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(Options.BearerToken));
            _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var origin in Options.AllowedOrigins)
            {
                if (NormalizeOrigin(origin) is { } normalized)
                {
                    _allowedOrigins.Add(normalized.Origin);
                    _allowedHosts.Add(normalized.Host);
                }
            }

            var path = string.IsNullOrWhiteSpace(Options.Path) ? "/mcp" : Options.Path.Trim();
            if (!path.StartsWith('/'))
                path = "/" + path;
            _path = path.Length > 1 ? path.TrimEnd('/') : path;

            var limits = HttpLimitsOverride ?? new HttpLimits { MaxBodyBytes = Math.Max(1024, Options.MaxRequestBytes) };
            // Every start gets a fresh cancellation source; the previous one is dead and its
            // registrations must not be kept alive across a restart.
            var previousServerCts = _serverCts;
            _serverCts = new CancellationTokenSource();
            previousServerCts.Dispose();
            var http = new HttpServer(limits, HandleHttpAsync, LogSink);

            var deadline = Environment.TickCount64 + 3000;
            while (true)
            {
                try
                {
                    http.Start(address, Options.Port);
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && Environment.TickCount64 < deadline && Options.Port != 0)
                {
                    // A previous instance (plugin hot-reload) may still be releasing the port.
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }

            _http = http;
            var hostText = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                hostText = "127.0.0.1";
            _endpoint = $"http://{hostText}:{http.Port}{_path}";
            _gateSignature = ComputeGateSignature();

            _housekeepingCts = new CancellationTokenSource();
            var token = _housekeepingCts.Token;
            _housekeeping = Task.Run(() => HousekeepingLoopAsync(token), CancellationToken.None);
            LogSink($"MCP server listening on {_endpoint}", null);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public partial async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var http = _http;
            if (http is null)
                return;
            _http = null;

            // 1. Release the port first so a reloaded plugin can bind immediately.
            http.StopAccepting();

            // 2. Cancel in-flight calls and end streams gracefully.
            try
            {
                _serverCts.Cancel();
            }
            catch (Exception ex)
            {
                LogSink("cancellation callback failed during stop", ex);
            }

            var writers = new List<Task>();
            foreach (var listener in _listeners.Keys)
            {
                try
                {
                    var result = new JsonObject
                    {
                        ["resultType"] = "complete",
                        ["_meta"] = new JsonObject
                        {
                            [MetaKeys.SubscriptionId] = listener.RequestId.DeepClone(),
                            [MetaKeys.ServerInfo] = ServerInfoJson(ProtocolVersions.LatestModern),
                        },
                    };
                    listener.Writer.TryEnqueueMessage(null, JsonRpc.Serialize(JsonRpc.Result(listener.RequestId, result)));
                    listener.Writer.Complete();
                    writers.Add(listener.Writer.Completion);
                }
                catch (Exception ex)
                {
                    LogSink("failed to close subscription stream", ex);
                }
            }

            foreach (var session in _sessions.Values)
                session.Close();
            _sessions.Clear();
            _recentModernClients.Clear();

            try
            {
                await Task.WhenAll(writers).WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort flush only.
            }

            // 3. Reset every remaining connection (no TIME_WAIT on the server side).
            await http.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            http.Dispose();
            _listeners.Clear();

            _housekeepingCts?.Cancel();
            if (_housekeeping is not null)
            {
                try
                {
                    await _housekeeping.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _housekeeping = null;
            _housekeepingCts?.Dispose();
            _housekeepingCts = null;
            LogSink("MCP server stopped", null);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public partial async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        // A Dalamud plugin is reloaded in-process: anything still holding a cancellation
        // registration or a wait handle after unload is a leak that survives the reload.
        // StopAsync already disposed the housekeeping source; this is the rest.
        _housekeepingCts?.Dispose();
        _housekeepingCts = null;
        _http?.Dispose();
        _http = null;
        _serverCts.Dispose();
        _lifecycleLock.Dispose();
    }

    public partial ServerStatus GetStatus()
    {
        var clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sessions.Values)
            clients.Add(ClientLabel(s.ClientName, s.ClientVersion));
        foreach (var name in _recentModernClients.Keys)
            clients.Add(name);

        var running = _http is not null;
        return new ServerStatus(
            running,
            running ? _endpoint : BuildIdleEndpoint(),
            _sessions.Count + _recentModernClients.Count,
            Interlocked.Read(ref _totalRequests),
            Interlocked.Read(ref _failedRequests),
            _lastError,
            clients.ToArray());
    }

    public partial IReadOnlyList<ActivityEntry> GetActivity(int max) => _activity.Newest(max);

    public partial IReadOnlyList<(string Name, string? Title, string Description, ToolPermission Permission, string Category)> ListRegisteredTools() =>
        _registry.Snapshot.Tools.Select(t => (t.Name, t.Title, t.Description, t.Permission, t.Category)).ToArray();

    private string BuildIdleEndpoint()
    {
        var host = Options.Host is "0.0.0.0" or "*" or "::" ? "127.0.0.1" : Options.Host;
        if (host.Contains(':') && !host.StartsWith('['))
            host = "[" + host + "]";
        var path = string.IsNullOrWhiteSpace(Options.Path) ? "/mcp" : Options.Path;
        return $"http://{host}:{Options.Port}{path}";
    }

    private static string ClientLabel(string? name, string? version) =>
        string.IsNullOrWhiteSpace(name) ? "(unnamed client)" : string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";

    private static IPAddress ResolveAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (host is "*" or "+")
            return IPAddress.Any;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return ip;
        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
    }

    internal static (string Origin, string Host)? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return null;
        if (!Uri.TryCreate(origin.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        var normalized = uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
        return (normalized.ToLowerInvariant(), uri.IdnHost.Trim('[', ']').ToLowerInvariant());
    }

    internal static bool IsLoopbackHostName(string host)
    {
        host = host.Trim().Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    private string ComputeGateSignature()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var p in Enum.GetValues<ToolPermission>())
                sb.Append(HostState.IsPermitted(p) ? '1' : '0');
            foreach (var category in _registry.Snapshot.Categories.OrderBy(c => c, StringComparer.Ordinal))
                sb.Append('|').Append(category).Append('=').Append(HostState.IsCategoryEnabled(category) ? '1' : '0');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            LogSink("host state check failed", ex);
            return _gateSignature;
        }
    }

    private async Task HousekeepingLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                tick++;
                try
                {
                    var now = Environment.TickCount64;
                    var idleMs = (long)Math.Max(1000, Options.SessionIdleTimeout.TotalMilliseconds);
                    foreach (var session in _sessions.Values)
                    {
                        if (session.OpenStreams == 0 && session.InFlight.IsEmpty && now - session.LastActivity > idleMs)
                        {
                            if (_sessions.TryRemove(session.Id, out _))
                            {
                                session.Close();
                                LogSink($"session {session.Id[..8]}… ({session.ClientName}) expired after idling", null);
                            }
                        }
                        else if (tick % 10 == 0)
                        {
                            session.PurgeStreams(now, 5 * 60 * 1000);
                        }
                    }

                    foreach (var kv in _recentModernClients)
                    {
                        if (now - kv.Value > idleMs && !_listeners.Keys.Any(l => l.ClientName == kv.Key))
                            _recentModernClients.TryRemove(kv.Key, out _);
                    }

                    if (tick % 2 == 0 && (!_sessions.IsEmpty || !_listeners.IsEmpty))
                    {
                        var signature = ComputeGateSignature();
                        if (signature != _gateSignature)
                        {
                            _gateSignature = signature;
                            _notifier.ToolListChanged();
                            _notifier.ResourceListChanged();
                            _notifier.PromptListChanged();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSink("housekeeping failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EvictSessionsIfFull()
    {
        if (_sessions.Count < MaxSessions)
            return;
        var victim = _sessions.Values.OrderBy(s => s.OpenStreams > 0 ? 1 : 0).ThenBy(s => s.LastActivity).FirstOrDefault();
        if (victim is not null && _sessions.TryRemove(victim.Id, out _))
            victim.Close();
    }

    private void RecordActivity(string? sessionId, string? clientName, string method, string? target, bool success, string? error, double durationMs)
    {
        Interlocked.Increment(ref _totalRequests);
        if (!success)
        {
            Interlocked.Increment(ref _failedRequests);
            _lastError = error is null ? method : $"{method}{(target is null ? "" : " " + target)}: {error}";
        }

        if (error is { Length: > 500 })
            error = error[..500] + "…";
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, sessionId, clientName, method, target, success, error, Math.Round(durationMs, 2));
        _activity.Add(entry);
        if (ActivityRecordedHasSubscribers)
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Server.SafeRaise(state.Entry), (Server: this, Entry: entry), preferLocal: false);
    }

    private bool ActivityRecordedHasSubscribers => ActivityRecorded is not null;

    private void SafeRaise(ActivityEntry entry)
    {
        try
        {
            RaiseActivity(entry);
        }
        catch (Exception ex)
        {
            LogSink("ActivityRecorded handler threw", ex);
        }
    }
}
