// "MCP Agents" toolbar widget: number of running agents on the XivMcp agent board and their
// average progress (text + the standard Umbra progress bar). Click opens the agent board popup.
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;

namespace Umbra.XivMcp;

[ToolbarWidget(
    "XivMcpAgents",
    "MCP Agents",
    "Number of agents currently reporting \"running\" on the XivMcp agent board, with their average progress. Click for the board. Needs the XivMcp Dalamud plugin.",
    ["mcp", "xivmcp", "ai", "agent", "progress"])]
public sealed class McpAgentsWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    private const string CvarCompact = "CompactMode";
    private const string CvarHideWhenIdle = "HideWhenIdle";
    private const string CvarRightClick = "RightClickAction";

    private XivMcpClient? _client;

    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text
        | StandardWidgetFeatures.SubText
        | StandardWidgetFeatures.Icon
        | StandardWidgetFeatures.CustomizableIcon
        | StandardWidgetFeatures.ProgressBar;

    protected override uint DefaultGameIconId => McpServerWidget.DefaultIconId;

    protected override bool DefaultShowSubText => false;

    public override WidgetPopup Popup { get; } = new McpPopup(agentsFocused: true);

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        var list = new List<IWidgetConfigVariable>(base.GetConfigVariables())
        {
            new BooleanWidgetConfigVariable(CvarCompact, "Compact mode",
                "Drop the \"Agents\" prefix.", false),
            new BooleanWidgetConfigVariable(CvarHideWhenIdle, "Hide when no agent is running",
                "Hide the widget entirely unless at least one agent reports \"running\".", false),
            new SelectWidgetConfigVariable(CvarRightClick, "Right-click action",
                "What right-clicking the widget does. Ctrl+right-click stays reserved for Umbra's quick settings.",
                McpServerWidget.ActionToggleWindow, McpServerWidget.ActionOptions()),
        };
        return list;
    }

    protected override void OnLoad()
    {
        _client = Framework.Service<XivMcpClient>();
        SetProgressBarConstraint(0, 100);
        Node.OnRightClick += OnRightClick;
    }

    protected override void OnUnload()
    {
        Node.OnRightClick -= OnRightClick;
        _client = null;
    }

    protected override void OnDraw()
    {
        var client = _client;
        if (client is null) return;
        var s = client.Snapshot;

        IsVisible = !GetConfigValue<bool>(CvarHideWhenIdle) || s.RunningAgents > 0;

        SetText(McpFormat.AgentsWidgetText(s, GetConfigValue<bool>(CvarCompact)));

        var newestRunning = s.Agents.FirstOrDefault(a => a.IsRunning);
        SetSubText(newestRunning is null ? null : McpFormat.Truncate(newestRunning.Agent + ": " + newestRunning.Status, 32));

        var avg = McpFormat.AverageRunningProgress(s.Agents);
        ProgressBarVisibility = avg is not null;
        SetProgressBarValue(avg is { } a ? (int)Math.Round(a * 100) : 0);

        if (s.RunningAgents == 0) SetIconDesaturated(true);

        var tooltip = s.State is McpLinkState.Running or McpLinkState.Stopped
            ? McpFormat.Plural(s.RunningAgents, "agent") + " running, " + s.Agents.Count + " on the board"
            : "XivMcp: " + McpFormat.StateLabel(s.State);
        SetTooltip(tooltip);
    }

    private void OnRightClick(Node _)
    {
        if (McpServerWidget.IsModifierHeld()) return;
        var client = _client;
        if (client is null) return;
        try
        {
            switch (GetConfigValue<string>(CvarRightClick))
            {
                case McpServerWidget.ActionToggleWindow:
                    client.ToggleWindow();
                    break;
                case McpServerWidget.ActionToggleServer:
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
}
