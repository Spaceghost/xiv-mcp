using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Plugin.Ipc;
using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Windows;

namespace XivMcp.Plugin;

/// <summary>
/// Dalamud entry point. Wires configuration, the MCP server host, providers, windows, the slash
/// command, IPC and the DTR entry. Nothing here may throw into Dalamud after construction.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/xivmcp";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;

    private readonly List<IDisposable> disposables = [];
    private readonly Configuration config;
    private readonly ConfirmationService confirmations;
    private readonly ServerHost host;
    private readonly WindowSystem windowSystem = new("XivMcp");
    private readonly MainWindow mainWindow;
    private readonly ConfirmWindow confirmWindow;
    private readonly ObjectiveTracker objectives;
    private readonly ObjectiveChatCommand questCommand;
    private readonly ApprovalWiring approvalWiring;
    private readonly ProvisionStore provision;
    private DateTime nextTick;
    private bool commandRegistered;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICommandManager commands,
        IChatGui chat,
        IDtrBar dtrBar,
        IDataManager data,
        IGameGui gameGui,
        ITextureProvider textures,
        IToastGui toasts,
        INotificationManager notifications)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;

        try
        {
            config = Configuration.Load(pluginInterface);

            // An optional outside file can override the saved settings (config management, other
            // machines). Read before anything uses the configuration so the first start already binds
            // what it asks for. It is only ever read; its contents are never logged.
            provision = new ProvisionStore(ProvisionFile.ResolvePath(), (message, ex) => log.Warning(ex, "XivMcp provisioning: {Message}", message));
            provision.Poll(config, force: true);
            if (provision.Path is { } provisionPath)
                log.Information("XivMcp provisioning file: {Path} ({State})", provisionPath, provision.Current is null ? "absent" : "in force");

            var gameThread = new DalamudGameThread(framework);
            confirmations = Track(new ConfirmationService(config));
            var approvalSessions = Track(new ApprovalSessionService(config));
            confirmations.Sessions = approvalSessions;
            var hostState = new HostState(config, clientState, objectTable, framework);
            var notifier = new NotifierProxy(log);
            var board = new AgentBoard(config);

            var objectiveStore = new ObjectiveStore(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "objectives.json"));
            objectiveStore.Load();
            if (objectiveStore.LastError is { } objectiveError)
                log.Warning("XivMcp objectives: {Error}", objectiveError);
            objectives = Track(new ObjectiveTracker(objectiveStore, config, framework, clientState, objectTable, data, toasts, log));
            questCommand = new ObjectiveChatCommand(objectives, config, pluginInterface, Print);

            host = Track(new ServerHost(pluginInterface, log, config, gameThread, hostState, notifier, confirmations));

            // Deferred approvals: tickets persist next to the plugin config and run through the server once approved.
            var ticketStore = new TicketStore(
                Path.Combine(pluginInterface.GetPluginConfigDirectory(), TicketStore.FileName),
                (message, ex) => log.Warning(ex, "{Message}", message));
            var approvalQueue = Track(new ApprovalQueue(config, ticketStore, new ServerToolRunner(host.Server), confirmations));
            approvalWiring = Track(new ApprovalWiring(host, approvalQueue, approvalSessions, notifications, log));

            // Scoped objects providers may request in their constructors (besides Dalamud services).
            host.LoadProviders(typeof(Plugin).Assembly, config, gameThread, notifier, board, host, hostState, confirmations, objectives, approvalQueue, approvalSessions);

            mainWindow = new MainWindow(pluginInterface, config, host, board, confirmations);
            mainWindow.Approvals = approvalQueue;
            mainWindow.Provision = provision;
            mainWindow.ApprovalSessions = approvalSessions;
            confirmWindow = new ConfirmWindow(confirmations);
            windowSystem.AddWindow(mainWindow);
            windowSystem.AddWindow(confirmWindow);
            windowSystem.AddWindow(Track(new ObjectiveOverlay(objectives, config, gameGui, textures, pluginInterface, Print)));

            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the XivMcp window. \"/xivmcp start|stop|restart|status|settings\"; custom objectives: " + ObjectiveCommands.Usage + ".",
            });

            Track(new IpcProvider(pluginInterface, framework, log, host, board, config, ToggleMainUi));
            Track(new DtrEntry(dtrBar, framework, log, config, host, confirmations, ToggleMainUi));

            framework.Update += OnFrameworkUpdate;

            if (config.Enabled)
                _ = host.StartAsync();
        }
        catch
        {
            // Dalamud does not call Dispose when the constructor throws; release what was acquired.
            DisposeCore();
            throw;
        }
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        disposables.Add(disposable);
        return disposable;
    }

    private void DrawUi()
    {
        try
        {
            windowSystem.Draw();
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivMcp UI draw failed");
        }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (now < nextTick)
            return;
        nextTick = now + TimeSpan.FromSeconds(1);
        try
        {
            host.Tick();
            approvalWiring.Tick();
            if (provision.Poll(config))
                _ = host.ApplyConfigAsync();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ServerHost tick failed");
        }
    }

    private void OpenMainUi() => mainWindow.OpenTab(MainWindow.StatusTab);

    private void OpenConfigUi() => mainWindow.OpenTab(MainWindow.SettingsTab);

    private void ToggleMainUi() => mainWindow.Toggle();

    private void OnCommand(string command, string arguments)
    {
        try
        {
            var trimmed = arguments.Trim();
            if (trimmed.Equals("quests", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("quests ", StringComparison.OrdinalIgnoreCase))
            {
                questCommand.Handle(trimmed[6..]);
                return;
            }

            switch (trimmed.ToLowerInvariant())
            {
                case "":
                    ToggleMainUi();
                    break;
                case "start":
                    Print("starting…");
                    _ = host.StartAsync().ContinueWith(_ => PrintStatus(), TaskScheduler.Default);
                    break;
                case "stop":
                    _ = host.StopAsync().ContinueWith(_ => PrintStatus(), TaskScheduler.Default);
                    break;
                case "restart":
                    Print("restarting…");
                    _ = host.RestartAsync().ContinueWith(_ => PrintStatus(), TaskScheduler.Default);
                    break;
                case "status":
                    PrintStatus();
                    break;
                case "settings" or "config":
                    OpenConfigUi();
                    break;
                default:
                    Print($"unknown argument \"{arguments.Trim()}\". Use {Command} [start|stop|restart|status|settings|quests].");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
    }

    private void PrintStatus()
    {
        var status = host.Status;
        if (host.IsRunning)
            Print($"running on {host.Endpoint} — {status.ActiveSessions} session(s), {status.TotalRequests} request(s), {status.FailedRequests} failed.");
        else
            Print($"stopped.{(host.LastError is { } error ? " Last error: " + error : "")}");
    }

    private void Print(string message)
    {
        // Chat output must happen on the framework thread; continuations arrive on the pool.
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                chat.Print(message, "XivMcp");
            }
            catch
            {
                // Chat unavailable (e.g. title screen): nothing useful to do.
            }
        });
    }

    public void Dispose() => DisposeCore();

    private void DisposeCore()
    {
        try
        {
            framework.Update -= OnFrameworkUpdate;
            if (commandRegistered)
                commands.RemoveHandler(Command);
            pluginInterface.UiBuilder.Draw -= DrawUi;
            pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            windowSystem.RemoveAllWindows();
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivMcp UI teardown failed");
        }

        // Deny pending confirmations before the server stops so no call waits on a closed window.
        confirmations?.DenyAll();

        // Reverse order: DTR, IPC, then the server host (stops the listener, disposes providers), then confirmations.
        for (var i = disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                disposables[i].Dispose();
            }
            catch (Exception ex)
            {
                log.Error(ex, "XivMcp failed to dispose {Type}", disposables[i].GetType().Name);
            }
        }

        disposables.Clear();
    }
}
