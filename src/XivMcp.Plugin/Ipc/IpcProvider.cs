using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Services;
using XivMcp.Shared;

namespace XivMcp.Plugin.Ipc;

/// <summary>
/// Dalamud IPC surface described by <see cref="IpcContract"/> (consumed by the Umbra widget).
/// Payloads are camelCase JSON strings. <see cref="IpcContract.Changed"/> is coalesced and sent
/// from the framework thread at most 4 times per second.
/// </summary>
/// <remarks>
/// Gate type parameters (subscribers must use the same):
/// ApiVersion <c>&lt;int&gt;</c>, GetStatus <c>&lt;string&gt;</c>, GetActivity <c>&lt;int, string&gt;</c>,
/// GetAgentBoard <c>&lt;string&gt;</c>, SetRunning <c>&lt;bool, bool&gt;</c>,
/// ToggleWindow <c>&lt;object&gt;</c> (action), Changed <c>&lt;object&gt;</c> (message, no arguments).
/// </remarks>
public sealed class IpcProvider : IDisposable
{
    private static readonly TimeSpan ChangedInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ServerHost host;
    private readonly AgentBoard board;
    private readonly Configuration config;

    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<string> getStatus;
    private readonly ICallGateProvider<int, string> getActivity;
    private readonly ICallGateProvider<string> getAgentBoard;
    private readonly ICallGateProvider<bool, bool> setRunning;
    private readonly ICallGateProvider<object> toggleWindow;
    private readonly ICallGateProvider<object> changed;

    private int dirty;
    private DateTime lastChangedSent = DateTime.MinValue;

    public IpcProvider(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log, ServerHost host, AgentBoard board, Configuration config, Action toggleMainWindow)
    {
        this.framework = framework;
        this.log = log;
        this.host = host;
        this.board = board;
        this.config = config;

        apiVersion = pluginInterface.GetIpcProvider<int>(IpcContract.ApiVersion);
        getStatus = pluginInterface.GetIpcProvider<string>(IpcContract.GetStatus);
        getActivity = pluginInterface.GetIpcProvider<int, string>(IpcContract.GetActivity);
        getAgentBoard = pluginInterface.GetIpcProvider<string>(IpcContract.GetAgentBoard);
        setRunning = pluginInterface.GetIpcProvider<bool, bool>(IpcContract.SetRunning);
        toggleWindow = pluginInterface.GetIpcProvider<object>(IpcContract.ToggleWindow);
        changed = pluginInterface.GetIpcProvider<object>(IpcContract.Changed);

        apiVersion.RegisterFunc(static () => IpcContract.Version);
        getStatus.RegisterFunc(StatusJson);
        getActivity.RegisterFunc(ActivityJson);
        getAgentBoard.RegisterFunc(BoardJson);
        setRunning.RegisterFunc(SetRunning);
        toggleWindow.RegisterAction(() =>
        {
            try
            {
                toggleMainWindow();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "IPC ToggleWindow failed");
            }
        });

        host.StateChanged += MarkDirty;
        host.ActivityRecorded += OnActivity;
        board.Changed += MarkDirty;
        framework.Update += OnUpdate;
    }

    private void OnActivity(ActivityEntry entry) => MarkDirty();

    private void MarkDirty() => Interlocked.Exchange(ref dirty, 1);

    private void OnUpdate(IFramework fw)
    {
        try
        {
            if (Volatile.Read(ref dirty) == 0)
                return;
            var now = DateTime.UtcNow;
            if (now - lastChangedSent < ChangedInterval)
                return;
            Interlocked.Exchange(ref dirty, 0);
            lastChangedSent = now;
            changed.SendMessage();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "IPC Changed dispatch failed");
        }
    }

    internal string StatusJson() => IpcJson.Status(
        host.IsRunning,
        host.Endpoint,
        host.Status,
        host.LastError,
        host.HostState,
        board.Count,
        config.ConfirmActions);

    internal string ActivityJson(int max) => IpcJson.Activity(host.GetActivity(Math.Clamp(max, 1, 500)));

    internal string BoardJson() => IpcJson.Board(board.Snapshot());

    private bool SetRunning(bool run)
    {
        try
        {
            // Subscribers call from the framework thread: never block it. The transition runs on the thread pool and
            // XivMcp.Changed fires when it completes; the return value is the state at the time of the call.
            _ = run ? host.StartAsync() : host.StopAsync();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "IPC SetRunning failed");
        }

        return host.IsRunning;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        host.StateChanged -= MarkDirty;
        host.ActivityRecorded -= OnActivity;
        board.Changed -= MarkDirty;

        apiVersion.UnregisterFunc();
        getStatus.UnregisterFunc();
        getActivity.UnregisterFunc();
        getAgentBoard.UnregisterFunc();
        setRunning.UnregisterFunc();
        toggleWindow.UnregisterAction();
    }
}
