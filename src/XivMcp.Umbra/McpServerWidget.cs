// "MCP Server" toolbar widget: server state, session count, running agents; popup on click,
// configurable secondary action on right/middle click.
using Dalamud.Bindings.ImGui;
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;

namespace Umbra.XivMcp;

[ToolbarWidget(
    "XivMcpServer",
    "MCP Server",
    "Status of the XivMcp Model Context Protocol server running inside this game client: running state, connected sessions, running agents and recent tool calls. Needs the XivMcp Dalamud plugin.",
    ["mcp", "xivmcp", "ai", "agent", "server", "claude"])]
public sealed class McpServerWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    /// <summary>Main-command "system" icon (a monitor with an up arrow). Changeable in the widget settings.</summary>
    public const uint DefaultIconId = 35;

    internal const string ActionNone = "None";
    internal const string ActionToggleWindow = "ToggleWindow";
    internal const string ActionToggleServer = "ToggleServer";

    private const string CvarShowSessions = "ShowSessionCount";
    private const string CvarShowAgents = "ShowAgentCount";
    private const string CvarCompact = "CompactMode";
    private const string CvarColorize = "ColorizeState";
    private const string CvarDesaturateOff = "DesaturateWhenOff";
    private const string CvarTooltip = "ShowTooltip";
    private const string CvarRightClick = "RightClickAction";
    private const string CvarMiddleClick = "MiddleClickAction";

    private XivMcpClient? _client;

    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text
        | StandardWidgetFeatures.SubText
        | StandardWidgetFeatures.Icon
        | StandardWidgetFeatures.CustomizableIcon;

    protected override uint DefaultGameIconId => DefaultIconId;

    protected override bool DefaultShowSubText => false;

    public override WidgetPopup Popup { get; } = new McpPopup(agentsFocused: false);

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        var list = new List<IWidgetConfigVariable>(base.GetConfigVariables())
        {
            new BooleanWidgetConfigVariable(CvarShowSessions, "Show session count",
                "Append the number of connected MCP sessions while the server is running (\"MCP ● 2\").", true),
            new BooleanWidgetConfigVariable(CvarShowAgents, "Show running agents",
                "Append the number of agents whose board state is \"running\" (\"▶ 1\").", true),
            new BooleanWidgetConfigVariable(CvarCompact, "Compact mode",
                "Drop the \"MCP\" prefix and spacing to save toolbar room.", false),
            new BooleanWidgetConfigVariable(CvarColorize, "Color by state",
                "Tint the label: green running, grey stopped or missing, amber on IPC problems. Ignored when a custom text color is enabled.", true),
            new BooleanWidgetConfigVariable(CvarDesaturateOff, "Grey icon when not running",
                "Desaturate the icon while the server is stopped or XivMcp is unavailable.", true),
            new BooleanWidgetConfigVariable(CvarTooltip, "Show tooltip",
                "Hover tooltip with endpoint, connected clients and request counters.", true),
            new SelectWidgetConfigVariable(CvarRightClick, "Right-click action",
                "What right-clicking the widget does. Ctrl+right-click stays reserved for Umbra's quick settings.",
                ActionToggleWindow, ActionOptions()),
            new SelectWidgetConfigVariable(CvarMiddleClick, "Middle-click action",
                "What middle-clicking the widget does.", ActionNone, ActionOptions()),
        };
        return list;
    }

    protected override void OnLoad()
    {
        _client = Framework.Service<XivMcpClient>();
        Node.OnRightClick += OnRightClick;
        Node.OnMiddleClick += OnMiddleClick;
    }

    protected override void OnUnload()
    {
        Node.OnRightClick -= OnRightClick;
        Node.OnMiddleClick -= OnMiddleClick;
        _client = null;
    }

    protected override void OnDraw()
    {
        var client = _client;
        if (client is null) return;
        var s = client.Snapshot;
        var running = s.State == McpLinkState.Running;

        SetText(McpFormat.WidgetText(s, new WidgetTextOptions(
            GetConfigValue<bool>(CvarShowSessions),
            GetConfigValue<bool>(CvarShowAgents),
            GetConfigValue<bool>(CvarCompact))));
        SetSubText(McpFormat.WidgetSubText(s));

        // The base class reset text colors and icon saturation just before OnDraw; layer state cues on top.
        if (GetConfigValue<bool>(CvarColorize) && !GetConfigValue<bool>("UseCustomTextColor"))
        {
            SetTextColor(new Color(McpFormat.StateColor(s.State)), new Color("Widget.TextOutline"));
            SetSubTextColor(new Color("Widget.TextMuted"), new Color("Widget.TextOutline"));
        }

        if (!running && GetConfigValue<bool>(CvarDesaturateOff)) SetIconDesaturated(true);

        SetTooltip(GetConfigValue<bool>(CvarTooltip)
            ? McpFormat.Tooltip(s, ActionHint(GetConfigValue<string>(CvarRightClick)))
            : null);
    }

    private void OnRightClick(Node _)
    {
        if (IsModifierHeld()) return; // Ctrl/Shift+right-click belongs to Umbra's quick settings.
        RunAction(GetConfigValue<string>(CvarRightClick));
    }

    private void OnMiddleClick(Node _) => RunAction(GetConfigValue<string>(CvarMiddleClick));

    private void RunAction(string action)
    {
        var client = _client;
        if (client is null) return;
        try
        {
            switch (action)
            {
                case ActionToggleWindow:
                    client.ToggleWindow();
                    break;
                case ActionToggleServer:
                    var state = client.Snapshot.State;
                    if (state is McpLinkState.Running or McpLinkState.Stopped)
                        client.SetRunning(state != McpLinkState.Running);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivMcp] widget action failed: " + ex.Message);
        }
    }

    internal static Dictionary<string, string> ActionOptions() => new()
    {
        [ActionNone] = "Nothing",
        [ActionToggleWindow] = "Toggle the XivMcp window",
        [ActionToggleServer] = "Start/stop the MCP server",
    };

    internal static string? ActionHint(string action) => action switch
    {
        ActionToggleWindow => "XivMcp window",
        ActionToggleServer => "start/stop server",
        _ => null,
    };

    internal static bool IsModifierHeld()
    {
        try
        {
            var io = ImGui.GetIO();
            return io.KeyCtrl || io.KeyShift;
        }
        catch
        {
            return false;
        }
    }
}
