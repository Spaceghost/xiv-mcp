using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

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

    internal string StatusJson()
    {
        var status = host.Status;
        return JsonSerializer.Serialize(
            new
            {
                running = host.IsRunning,
                endpoint = host.Endpoint,
                activeSessions = status.ActiveSessions,
                totalRequests = status.TotalRequests,
                failedRequests = status.FailedRequests,
                lastError = host.LastError ?? status.LastError,
                connectedClients = status.ConnectedClients,
                permissions = new
                {
                    read = host.HostState.IsPermitted(ToolPermission.Read),
                    ui = host.HostState.IsPermitted(ToolPermission.Ui),
                    action = host.HostState.IsPermitted(ToolPermission.Action),
                    chat = host.HostState.IsPermitted(ToolPermission.Chat),
                },
                agents = board.Count,
                confirmActions = config.ConfirmActions,
            },
            Json);
    }

    internal string ActivityJson(int max)
    {
        var entries = host.GetActivity(Math.Clamp(max, 1, 500));
        return JsonSerializer.Serialize(
            entries.Select(e => new
            {
                timestamp = e.Timestamp,
                sessionId = e.SessionId,
                clientName = e.ClientName,
                method = e.Method,
                target = e.Target,
                success = e.Success,
                error = e.Error,
                durationMs = e.DurationMs,
            }),
            Json);
    }

    internal string BoardJson() => JsonSerializer.Serialize(
        board.Snapshot().Select(p => new
        {
            agent = p.Agent,
            status = p.Status,
            state = p.StateName,
            progress = p.Progress,
            detail = p.Detail,
            clientName = p.ClientName,
            updatedAt = p.UpdatedAt,
        }),
        Json);

    private bool SetRunning(bool run)
    {
        try
        {
            var task = run ? host.StartAsync() : host.StopAsync();

            // Subscribers call from the framework thread: wait briefly for the bind result, never long.
            task.Wait(TimeSpan.FromMilliseconds(300));
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
