// Umbra service that talks to the XivMcp Dalamud plugin exclusively through Dalamud IPC.
// It never throws into Umbra or into XivMcp: every IPC call is guarded, and the Changed
// handler (which XivMcp raises from whatever thread it likes) only sets a flag.
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Umbra.Common;
using XivMcp.Shared;

namespace Umbra.XivMcp;

[Service]
public sealed class XivMcpClient : IDisposable
{
    /// <summary>Newest N activity entries requested from the plugin (the popup shows at most this many).</summary>
    public const int ActivityFetchCount = 25;

    private const long PollIntervalMs = 1000;
    private const long MinRefreshSpacingMs = 100;

    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog? _log;

    private ICallGateSubscriber<int>? _apiVersion;
    private ICallGateSubscriber<string>? _getStatus;
    private ICallGateSubscriber<int, string>? _getActivity;
    private ICallGateSubscriber<string>? _getAgentBoard;
    private ICallGateSubscriber<bool, bool>? _setRunning;
    private ICallGateSubscriber<object>? _toggleWindow;
    private ICallGateSubscriber<object>? _changed;

    private readonly Action _onChanged;
    private bool _subscribed;
    private int _dirty = 1;
    private int _activityViewers;
    private long _lastRefreshMs = long.MinValue / 2;
    private long _lastPollMs = long.MinValue / 2;
    private string? _lastLoggedProblem;
    private bool _disposed;

    public XivMcpClient()
    {
        _pi = Framework.DalamudPlugin;
        try { _log = Framework.Service<IPluginLog>(); } catch { _log = null; }
        _onChanged = OnChanged;
        EnsureGates();
        Refresh();
    }

    /// <summary>Latest snapshot. Always non-null; replaced atomically on refresh.</summary>
    public McpSnapshot Snapshot { get; private set; } = McpSnapshot.Initial;

    public bool IsAvailable => Snapshot.State is McpLinkState.Running or McpLinkState.Stopped;

    /// <summary>
    /// Popups call this on open/close. Activity is only fetched while at least one popup shows it;
    /// status and the agent board (needed for the toolbar labels) are always fetched.
    /// </summary>
    public void SetActivityViewer(bool viewing)
    {
        var n = viewing ? Interlocked.Increment(ref _activityViewers) : Interlocked.Decrement(ref _activityViewers);
        if (n < 0) Interlocked.Exchange(ref _activityViewers, 0);
        if (viewing) Invalidate();
    }

    /// <summary>Request an immediate refresh on the next tick.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    [OnTick]
    private void Tick()
    {
        if (_disposed) return;
        try
        {
            var now = Environment.TickCount64;
            var due = now - _lastPollMs >= PollIntervalMs;
            var dirty = Volatile.Read(ref _dirty) == 1 && now - _lastRefreshMs >= MinRefreshSpacingMs;
            if (!due && !dirty) return;
            if (due) _lastPollMs = now;
            Refresh();
        }
        catch (Exception ex)
        {
            // Last line of defence: never let Umbra's scheduler see an exception from us.
            LogOnce("XivMcp widget refresh failed: " + ex.Message);
        }
    }

    /// <summary>Start (true) or stop (false) the MCP server. Returns the running state afterwards, or null on failure.</summary>
    public bool? SetRunning(bool running)
    {
        if (!IsAvailable || _setRunning is null) return null;
        try
        {
            var result = _setRunning.InvokeFunc(running);
            Invalidate();
            return result;
        }
        catch (Exception ex)
        {
            LogOnce("XivMcp.SetRunning failed: " + ex.Message);
            Invalidate();
            return null;
        }
    }

    /// <summary>Toggle the XivMcp plugin window. Returns false when XivMcp is not reachable.</summary>
    public bool ToggleWindow()
    {
        if (Snapshot.State is McpLinkState.Missing or McpLinkState.VersionMismatch || _toggleWindow is null) return false;
        try
        {
            _toggleWindow.InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("XivMcp.ToggleWindow failed: " + ex.Message);
            return false;
        }
    }

    private void OnChanged()
    {
        // Called by XivMcp, possibly off the framework thread. Flag only; no allocation, no throw.
        Interlocked.Exchange(ref _dirty, 1);
    }

    private void EnsureGates()
    {
        try
        {
            _apiVersion ??= _pi.GetIpcSubscriber<int>(IpcContract.ApiVersion);
            _getStatus ??= _pi.GetIpcSubscriber<string>(IpcContract.GetStatus);
            _getActivity ??= _pi.GetIpcSubscriber<int, string>(IpcContract.GetActivity);
            _getAgentBoard ??= _pi.GetIpcSubscriber<string>(IpcContract.GetAgentBoard);
            _setRunning ??= _pi.GetIpcSubscriber<bool, bool>(IpcContract.SetRunning);
            _toggleWindow ??= _pi.GetIpcSubscriber<object>(IpcContract.ToggleWindow);
            _changed ??= _pi.GetIpcSubscriber<object>(IpcContract.Changed);

            // Subscriptions live on the named channel, so they survive XivMcp reloads. Retry until one sticks.
            if (!_subscribed && _changed is not null)
            {
                _changed.Subscribe(_onChanged);
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            LogOnce("XivMcp IPC subscription failed (will retry): " + ex.Message);
        }
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _dirty, 0);
        _lastRefreshMs = Environment.TickCount64;
        EnsureGates();

        var next = Fetch();
        var prev = Snapshot;
        Snapshot = next;

        if (next.Problem != prev.Problem && next.Problem is not null && next.State is not McpLinkState.Missing)
            LogOnce(next.Problem);
    }

    private McpSnapshot Fetch()
    {
        var now = DateTimeOffset.Now;
        if (_apiVersion is null || !SafeHasFunction(_apiVersion))
            return new McpSnapshot(McpLinkState.Missing, null, null, [], [], "XivMcp plugin is not loaded.", now);

        int version;
        try
        {
            version = _apiVersion.InvokeFunc();
        }
        catch (Exception ex)
        {
            return new McpSnapshot(McpLinkState.Error, null, null, [], [], "XivMcp IPC not ready: " + ex.Message, now);
        }

        if (version != IpcContract.Version)
        {
            return new McpSnapshot(McpLinkState.VersionMismatch, version, null, [], [],
                $"XivMcp speaks IPC v{version}, this widget expects v{IpcContract.Version}. Update Umbra.XivMcp.dll and XivMcp together.",
                now);
        }

        McpStatus? status;
        try
        {
            status = IpcPayloadParser.ParseStatus(_getStatus?.InvokeFunc());
        }
        catch (Exception ex)
        {
            return new McpSnapshot(McpLinkState.Error, version, null, [], [], "XivMcp.GetStatus failed: " + ex.Message, now);
        }

        if (status is null)
            return new McpSnapshot(McpLinkState.Error, version, null, [], [], "XivMcp.GetStatus returned an unreadable payload.", now);

        // Activity and board are optional extras: a failure there keeps the status usable.
        string? problem = null;
        IReadOnlyList<McpActivity> activity = Snapshot.Activity;
        IReadOnlyList<McpAgentPost> agents = [];
        try
        {
            if (Volatile.Read(ref _activityViewers) > 0 && _getActivity is not null && SafeHasFunction(_getActivity))
                activity = IpcPayloadParser.ParseActivity(_getActivity.InvokeFunc(ActivityFetchCount));
        }
        catch (Exception ex)
        {
            problem = "XivMcp.GetActivity failed: " + ex.Message;
        }

        try
        {
            if (_getAgentBoard is not null && SafeHasFunction(_getAgentBoard))
                agents = IpcPayloadParser.ParseAgentBoard(_getAgentBoard.InvokeFunc());
        }
        catch (Exception ex)
        {
            problem ??= "XivMcp.GetAgentBoard failed: " + ex.Message;
        }

        return new McpSnapshot(status.Running ? McpLinkState.Running : McpLinkState.Stopped,
            version, status, activity, agents, problem, now);
    }

    private static bool SafeHasFunction(ICallGateSubscriber gate)
    {
        try { return gate.HasFunction; }
        catch { return false; }
    }

    private void LogOnce(string message)
    {
        if (message == _lastLoggedProblem) return;
        _lastLoggedProblem = message;
        try { _log?.Warning("[Umbra.XivMcp] " + message); }
        catch { /* logging must never break the widget */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Umbra loads this assembly into a collectible context: leaving a delegate on the
            // Dalamud channel would pin it (and call into unloaded code on the next Changed).
            if (_subscribed) _changed?.Unsubscribe(_onChanged);
        }
        catch
        {
            // ignored: nothing useful to do while unloading
        }

        _subscribed = false;
    }
}
