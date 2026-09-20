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
/// Revision 2 (additive): ApiRevision <c>&lt;int&gt;</c>, GetLocalModel <c>&lt;string&gt;</c>,
/// ConnectClient <c>&lt;string, string&gt;</c>, LocalModelChanged <c>&lt;object&gt;</c> (message, no arguments).
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
    private readonly ICallGateProvider<int> apiRevision;
    private readonly ICallGateProvider<string> getLocalModel;
    private readonly ICallGateProvider<string, string> connectClient;
    private readonly ICallGateProvider<object> localModelChanged;
    private readonly IDalamudPluginInterface pluginInterface;

    private int dirty;
    private DateTime lastChangedSent = DateTime.MinValue;

    public IpcProvider(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log, ServerHost host, AgentBoard board, Configuration config, Action toggleMainWindow)
    {
        this.framework = framework;
        this.log = log;
        this.host = host;
        this.board = board;
        this.config = config;
        this.pluginInterface = pluginInterface;

        apiVersion = pluginInterface.GetIpcProvider<int>(IpcContract.ApiVersion);
        getStatus = pluginInterface.GetIpcProvider<string>(IpcContract.GetStatus);
        getActivity = pluginInterface.GetIpcProvider<int, string>(IpcContract.GetActivity);
        getAgentBoard = pluginInterface.GetIpcProvider<string>(IpcContract.GetAgentBoard);
        setRunning = pluginInterface.GetIpcProvider<bool, bool>(IpcContract.SetRunning);
        toggleWindow = pluginInterface.GetIpcProvider<object>(IpcContract.ToggleWindow);
        changed = pluginInterface.GetIpcProvider<object>(IpcContract.Changed);
        apiRevision = pluginInterface.GetIpcProvider<int>(IpcContract.ApiRevision);
        getLocalModel = pluginInterface.GetIpcProvider<string>(IpcContract.GetLocalModel);
        connectClient = pluginInterface.GetIpcProvider<string, string>(IpcContract.ConnectClient);
        localModelChanged = pluginInterface.GetIpcProvider<object>(IpcContract.LocalModelChanged);

        apiVersion.RegisterFunc(static () => IpcContract.Version);
        getStatus.RegisterFunc(StatusJson);
        getActivity.RegisterFunc(ActivityJson);
        getAgentBoard.RegisterFunc(BoardJson);
        setRunning.RegisterFunc(SetRunning);
        apiRevision.RegisterFunc(static () => IpcContract.Revision);
        getLocalModel.RegisterFunc(LocalModelJson);
        connectClient.RegisterFunc(ConnectClient);
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
        host.PreferredEndpoint,
        host.Status,
        host.LastError,
        host.HostState,
        board.Count,
        config.ConfirmActions,
        host.Endpoints);

    internal string ActivityJson(int max) => IpcJson.Activity(host.GetActivity(Math.Clamp(max, 1, 500)));

    internal string BoardJson() => IpcJson.Board(board.Snapshot());

    internal string LocalModelJson() => IpcJson.LocalModel(config.LocalModelEndpoint, config.LocalModelName, config.LocalModelApiKey);

    /// <summary>Raises XivMcp.LocalModelChanged (called by the Settings tab after saving the local model block).</summary>
    public void NotifyLocalModelChanged()
    {
        try
        {
            localModelChanged.SendMessage();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "IPC LocalModelChanged dispatch failed");
        }
    }

    private string ConnectClient(string clientName)
    {
        try
        {
            // The configuration is written only on the framework thread (inline when the caller is already on it).
            return framework.RunOnFrameworkThread(() =>
            {
                var result = ClientConnector.Connect(config, clientName, DateTimeOffset.UtcNow);
                if (!result.Ok)
                    return IpcJson.Error(result.Error!);
                config.Normalize();
                pluginInterface.SavePluginConfig(config);
                _ = host.ApplyConfigAsync();
                log.Information("IPC ConnectClient: issued a client token for {Client}", result.ClientName!);
                MarkDirty();
                // The endpoint comes from the server, not from Configuration.Host/Port: ServerHost.Endpoint is the
                // address the listener actually bound (the configured one only while it is stopped), so it stays right
                // when the listener moves. When the bind-mode work (tailnet-bind) lands, this must consume its endpoint
                // list + preferred endpoint instead, with the provisioning file being just another source of those
                // values; the IPC must not read the configuration directly. Add `endpoints` to the payload then.
                return IpcJson.Connect(host.Endpoint, result.Token!, result.ClientName!);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "IPC ConnectClient failed");
            return IpcJson.Error("failed");
        }
    }

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
        apiRevision.UnregisterFunc();
        getLocalModel.UnregisterFunc();
        connectClient.UnregisterFunc();
    }
}
