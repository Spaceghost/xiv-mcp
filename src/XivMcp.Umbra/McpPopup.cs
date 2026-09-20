// Native Umbra popup (Una.Drawing nodes) shared by both widgets: server header, agent board,
// recent activity and window/clipboard buttons. Row nodes are pooled; each frame only changes
// values that differ, so an open popup costs almost nothing.
using System.Diagnostics.CodeAnalysis;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.Windows.Components;
using Una.Drawing;
using Una.Drawing.Templating.StyleParser;

namespace Umbra.XivMcp;

public sealed class McpPopup : WidgetPopup
{
    private const int MaxAgentRows = 20;
    private const int MaxActivityRows = XivMcpClient.ActivityFetchCount;
    private const float TrackWidth = 425;

    private const string CvarShowServer = "PopupShowServer";
    private const string CvarShowAgents = "PopupShowAgents";
    private const string CvarMaxAgents = "PopupMaxAgents";
    private const string CvarShowActivity = "PopupShowActivity";
    private const string CvarActivityCount = "PopupActivityCount";

    private static readonly Stylesheet PopupStylesheet = StyleParser.StylesheetFromCode(
        """
        @import "globals";

        .mcp-popup {
            flow: vertical;
            size: 460 0;
            padding: 10;
            gap: 10;
        }

        .text {
            font-size: 13;
            color: "Widget.PopupMenuText";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            text-overflow: false;
            word-wrap: false;
        }

        .muted {
            font-size: 11;
            color: "Widget.PopupMenuTextMuted";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            text-overflow: false;
            word-wrap: false;
        }

        .line {
            size: 440 0;
            word-wrap: true;
        }

        .header-subtitle {
            size: 330 0;
            font-size: 11;
            color: "Widget.PopupMenuTextMuted";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            word-wrap: true;
        }

        .section {
            flow: vertical;
            auto-size: grow fit;
            gap: 4;
        }

        .section-title {
            auto-size: grow fit;
            font-size: 12;
            color: "Widget.PopupMenuTextMuted";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
            padding: 0 0 3 0;
            border-color: "Widget.Border";
            border-width: 0 0 1 0;
        }

        .list {
            flow: vertical;
            auto-size: grow fit;
            gap: 5;
        }

        .header {
            flow: horizontal;
            auto-size: grow fit;
            gap: 8;
        }

        .header-dot {
            anchor: middle-left;
            size: 12 12;
            border-radius: 6;
            is-antialiased: true;
        }

        .header-text {
            anchor: middle-left;
            flow: vertical;
            gap: 1;
        }

        .header-title {
            font-size: 16;
            color: "Widget.PopupMenuText";
            outline-color: "Widget.PopupMenuTextOutline";
            outline-size: 1;
        }

        .header-buttons {
            anchor: middle-right;
            flow: horizontal;
            gap: 6;
        }

        .row {
            flow: vertical;
            auto-size: grow fit;
            gap: 3;
        }

        .row-line {
            flow: horizontal;
            auto-size: grow fit;
            gap: 6;
        }

        .state-bar {
            anchor: middle-left;
            size: 3 14;
            border-radius: 1;
        }

        .agent-name { anchor: middle-left; size: 120 0; max-width: 120; }
        .agent-status { anchor: middle-left; size: 230 0; max-width: 230; }
        .agent-age { anchor: middle-right; size: 40 0; text-align: middle-right; }
        .agent-detail { size: 425 0; max-width: 425; padding: 0 0 0 9; }

        .track {
            size: 425 4;
            margin: 0 0 0 9;
            border-radius: 2;
            background-color: "Widget.Background";
        }

        .fill {
            anchor: top-left;
            size: 0 4;
            border-radius: 2;
        }

        .act-time { anchor: middle-left; size: 56 0; }
        .act-dot { anchor: middle-left; size: 10 0; font-size: 10; text-align: middle-center; }
        .act-client { anchor: middle-left; size: 84 0; max-width: 84; }
        .act-label { anchor: middle-left; size: 220 0; max-width: 220; }
        .act-ms { anchor: middle-right; size: 48 0; text-align: middle-right; }

        .footer {
            flow: horizontal;
            auto-size: grow fit;
            gap: 6;
            padding: 4 0 0 0;
        }
        """);

    private readonly XivMcpClient _client = Framework.Service<XivMcpClient>();
    private readonly bool _defaultShowServer;
    private readonly bool _defaultShowActivity;

    private readonly Node _header;
    private readonly Node _headerDot;
    private readonly Node _headerTitle;
    private readonly Node _headerSubtitle;
    private readonly ButtonNode _runButton;
    private readonly Node _serverInfo;
    private readonly Node _stats;
    private readonly Node _lastError;
    private readonly Node _clients;
    private readonly Node _tiers;
    private readonly Node _agentsSection;
    private readonly Node _agentsTitle;
    private readonly Node _agentsEmpty;
    private readonly AgentRow[] _agentRows = new AgentRow[MaxAgentRows];
    private readonly Node _activitySection;
    private readonly Node _activityEmpty;
    private readonly ActivityRow[] _activityRows = new ActivityRow[MaxActivityRows];
    private readonly ButtonNode _windowButton;
    private readonly ButtonNode _copyButton;

    private bool _showServer;
    private bool _showAgents = true;
    private int _maxAgents = 8;
    private bool _showActivity;
    private int _activityCount = 10;

    /// <param name="agentsFocused">Defaults for the "MCP Agents" widget: board first, no server header or activity.</param>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfer: every node built here is appended to a parent Node, and Una.Drawing's node tree disposes its children. Disposing them here would tear down the live popup.")]
    public McpPopup(bool agentsFocused = false)
    {
        _defaultShowServer = !agentsFocused;
        _defaultShowActivity = !agentsFocused;
        _showServer = _defaultShowServer;
        _showActivity = _defaultShowActivity;

        _headerDot = N("header-dot");
        _headerTitle = N("header-title", "MCP Server");
        _headerSubtitle = N("header-subtitle");
        _runButton = new ButtonNode("run", "Start", FontAwesomeIcon.Play, isGhost: false, isSmall: true);
        _runButton.OnClick += _ => SafeUi(ToggleRunning);

        _header = N("header");
        var headerText = N("header-text");
        headerText.AppendChild(_headerTitle);
        headerText.AppendChild(_headerSubtitle);
        var headerButtons = N("header-buttons");
        headerButtons.AppendChild(_runButton);
        _header.AppendChild(_headerDot);
        _header.AppendChild(headerText);
        _header.AppendChild(headerButtons);

        _stats = N("muted line");
        _lastError = N("muted line");
        _clients = N("muted line");
        _tiers = N("muted line");
        _serverInfo = N("section");
        _serverInfo.AppendChild(_stats);
        _serverInfo.AppendChild(_lastError);
        _serverInfo.AppendChild(_clients);
        _serverInfo.AppendChild(_tiers);

        _agentsSection = N("section");
        _agentsTitle = N("section-title", "Agents");
        _agentsEmpty = N("muted line", "No agent has posted to the board yet.");
        var agentList = N("list");
        for (var i = 0; i < MaxAgentRows; i++)
        {
            _agentRows[i] = new AgentRow();
            agentList.AppendChild(_agentRows[i].Root);
        }

        _agentsSection.AppendChild(_agentsTitle);
        _agentsSection.AppendChild(_agentsEmpty);
        _agentsSection.AppendChild(agentList);

        _activitySection = N("section");
        _activityEmpty = N("muted line", "No requests yet.");
        var activityList = N("list");
        for (var i = 0; i < MaxActivityRows; i++)
        {
            _activityRows[i] = new ActivityRow();
            activityList.AppendChild(_activityRows[i].Root);
        }

        _activitySection.AppendChild(N("section-title", "Recent activity"));
        _activitySection.AppendChild(_activityEmpty);
        _activitySection.AppendChild(activityList);

        _windowButton = new ButtonNode("window", "Open XivMcp window", FontAwesomeIcon.WindowRestore, isGhost: false, isSmall: true);
        _windowButton.OnClick += _ => SafeUi(() =>
        {
            if (_client.ToggleWindow()) Close();
        });
        _copyButton = new ButtonNode("copy", "Copy endpoint", FontAwesomeIcon.Copy, isGhost: true, isSmall: true);
        _copyButton.OnClick += _ => SafeUi(() =>
        {
            if (_client.Snapshot.Status?.Endpoint is { Length: > 0 } endpoint) ImGui.SetClipboardText(endpoint);
        });
        var footer = N("footer");
        footer.AppendChild(_windowButton);
        footer.AppendChild(_copyButton);

        Node = new Node
        {
            ClassList = new ObservableHashSet<string> { "mcp-popup" },
            Stylesheet = PopupStylesheet,
        };
        Node.AppendChild(_header);
        Node.AppendChild(_serverInfo);
        Node.AppendChild(_agentsSection);
        Node.AppendChild(_activitySection);
        Node.AppendChild(footer);
    }

    protected override Node Node { get; }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfer: Umbra takes these config variables and owns their lifetime; the widget must not dispose them.")]
    public override IEnumerable<IWidgetConfigVariable> GetConfigVariables() =>
    [
        new BooleanWidgetConfigVariable(CvarShowServer, "Show server details",
            "Show endpoint, request counters, connected clients and enabled permission tiers.", _defaultShowServer)
            { Category = "Popup" },
        new BooleanWidgetConfigVariable(CvarShowAgents, "Show agent board",
            "Show the posts agents made with the agent board tool (name, status, progress, age).", true)
            { Category = "Popup" },
        new IntegerWidgetConfigVariable(CvarMaxAgents, "Agents shown",
            "Maximum number of agent board entries in the popup (newest first).", 8, 1, MaxAgentRows)
            { Category = "Popup" },
        new BooleanWidgetConfigVariable(CvarShowActivity, "Show recent activity",
            "Show the most recent MCP requests (time, client, method/target, result, duration).", _defaultShowActivity)
            { Category = "Popup" },
        new IntegerWidgetConfigVariable(CvarActivityCount, "Activity entries",
            "Number of recent requests listed in the popup.", 10, 1, MaxActivityRows)
            { Category = "Popup" },
    ];

    protected override void UpdateConfigVariables(ToolbarWidget widget)
    {
        _showServer = widget.GetConfigValue<bool>(CvarShowServer);
        _showAgents = widget.GetConfigValue<bool>(CvarShowAgents);
        _maxAgents = Math.Clamp(widget.GetConfigValue<int>(CvarMaxAgents), 1, MaxAgentRows);
        _showActivity = widget.GetConfigValue<bool>(CvarShowActivity);
        _activityCount = Math.Clamp(widget.GetConfigValue<int>(CvarActivityCount), 1, MaxActivityRows);
    }

    private bool _viewingActivity;

    protected override void OnOpen()
    {
        _viewingActivity = true;
        _client.SetActivityViewer(true);
    }

    protected override void OnClose()
    {
        if (!_viewingActivity) return;
        _viewingActivity = false;
        _client.SetActivityViewer(false);
    }

    protected override void OnDisposed() => OnClose();

    protected override void OnUpdate()
    {
        var s = _client.Snapshot;
        var now = DateTimeOffset.Now;
        var available = s.State is McpLinkState.Running or McpLinkState.Stopped;

        // Header
        SetBg(_headerDot, McpFormat.StateColor(s.State));
        _headerTitle.NodeValue = "MCP Server · " + McpFormat.StateLabel(s.State);
        _headerSubtitle.NodeValue = s.State switch
        {
            McpLinkState.Missing => "XivMcp is not loaded. Install/enable it in Dalamud; this widget reconnects automatically.",
            McpLinkState.VersionMismatch or McpLinkState.Error => McpFormat.Truncate(s.Problem, 160),
            _ => s.Status?.Endpoint is { Length: > 0 } ep ? ep : "(no endpoint)",
        };
        _runButton.Style.IsVisible = available;
        _runButton.Label = s.State == McpLinkState.Running ? "Stop" : "Start";
        _runButton.Icon = s.State == McpLinkState.Running ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        // Server details
        _serverInfo.Style.IsVisible = _showServer && s.Status is not null;
        if (_showServer && s.Status is { } st)
        {
            var stats = McpFormat.Plural(st.ActiveSessions, "session") + " · "
                        + McpFormat.Plural(st.TotalRequests, "request") + " · "
                        + st.FailedRequests.ToString(System.Globalization.CultureInfo.InvariantCulture) + " failed";
            _stats.NodeValue = stats;
            _lastError.Style.IsVisible = !string.IsNullOrEmpty(st.LastError);
            if (!string.IsNullOrEmpty(st.LastError)) _lastError.NodeValue = "Last error: " + McpFormat.Truncate(st.LastError, 160);
            _clients.NodeValue = "Clients: " + (st.ConnectedClients.Count > 0
                ? McpFormat.Truncate(string.Join(", ", st.ConnectedClients), 160)
                : "none connected");
            _tiers.Style.IsVisible = st.Permissions is not null;
            if (st.Permissions is { } p)
            {
                var tiers = new List<string>(4);
                if (p.Read) tiers.Add("read");
                if (p.Ui) tiers.Add("ui");
                if (p.Action) tiers.Add("action");
                if (p.Chat) tiers.Add("chat");
                _tiers.NodeValue = "Enabled tiers: " + (tiers.Count > 0 ? string.Join(", ", tiers) : "none");
            }
        }

        // Agents
        _agentsSection.Style.IsVisible = _showAgents && available;
        if (_showAgents && available)
        {
            var running = s.RunningAgents;
            _agentsTitle.NodeValue = s.Agents.Count == 0
                ? "Agents"
                : $"Agents · {running} running · {s.Agents.Count} total";
            _agentsEmpty.Style.IsVisible = s.Agents.Count == 0;
            for (var i = 0; i < MaxAgentRows; i++)
            {
                var visible = i < _maxAgents && i < s.Agents.Count;
                _agentRows[i].Root.Style.IsVisible = visible;
                if (visible) _agentRows[i].Update(s.Agents[i], now);
            }
        }

        // Activity
        _activitySection.Style.IsVisible = _showActivity && available;
        if (_showActivity && available)
        {
            _activityEmpty.Style.IsVisible = s.Activity.Count == 0;
            for (var i = 0; i < MaxActivityRows; i++)
            {
                var visible = i < _activityCount && i < s.Activity.Count;
                _activityRows[i].Root.Style.IsVisible = visible;
                if (visible) _activityRows[i].Update(s.Activity[i]);
            }
        }

        // Footer
        _windowButton.IsDisabled = !available;
        _copyButton.IsDisabled = s.Status?.Endpoint is not { Length: > 0 };
    }

    private void ToggleRunning()
    {
        var state = _client.Snapshot.State;
        if (state is McpLinkState.Running or McpLinkState.Stopped)
            _client.SetRunning(state != McpLinkState.Running);
    }

    private static void SafeUi(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Warning("[Umbra.XivMcp] popup action failed: " + ex.Message);
        }
    }

    internal static Node N(string cls, string? value = null)
    {
        var classes = new ObservableHashSet<string>();
        foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries)) classes.Add(c);
        return new Node { ClassList = classes, NodeValue = value };
    }

    internal static void SetBg(Node node, uint argb)
    {
        var current = node.Style.BackgroundColor;
        if (current is { } c && c.ToUInt() == argb) return;
        node.Style.BackgroundColor = new Color(argb);
    }

    internal static void SetFg(Node node, uint argb)
    {
        var current = node.Style.Color;
        if (current is { } c && c.ToUInt() == argb) return;
        node.Style.Color = new Color(argb);
    }

    private sealed class AgentRow
    {
        private readonly Node _bar = N("state-bar");
        private readonly Node _name = N("text agent-name");
        private readonly Node _status = N("muted agent-status");
        private readonly Node _age = N("muted agent-age");
        private readonly Node _detail = N("muted agent-detail");
        private readonly Node _track = N("track");
        private readonly Node _fill = N("fill");
        private float _fillWidth = -1;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Ownership transfer: every node built here is appended to a parent Node, and Una.Drawing's node tree disposes its children. Disposing them here would tear down the live popup.")]
        public AgentRow()
        {
            Root = N("row");
            var line = N("row-line");
            line.AppendChild(_bar);
            line.AppendChild(_name);
            line.AppendChild(_status);
            line.AppendChild(_age);
            _track.AppendChild(_fill);
            Root.AppendChild(line);
            Root.AppendChild(_track);
            Root.AppendChild(_detail);
        }

        public Node Root { get; }

        public void Update(McpAgentPost post, DateTimeOffset now)
        {
            var color = McpFormat.AgentStateColor(post.State);
            SetBg(_bar, color);
            _name.NodeValue = McpFormat.Truncate(post.Agent, 18);
            _name.Tooltip = post.ClientName is { Length: > 0 } client ? post.Agent + " (via " + client + ")" : post.Agent;
            var status = post.Status.Length > 0 ? post.Status : post.State;
            if (post.Fraction is { } f) status = McpFormat.Percent(f) + " · " + status;
            _status.NodeValue = McpFormat.Truncate(status, 40);
            _age.NodeValue = McpFormat.Age(post.UpdatedAt, now);

            _track.Style.IsVisible = post.Fraction is not null;
            if (post.Fraction is { } fraction)
            {
                var width = MathF.Round((float)fraction * TrackWidth);
                if (width != _fillWidth)
                {
                    _fillWidth = width;
                    _fill.Style.Size = new Size(width, 4);
                    _fill.Style.IsVisible = width > 0;
                }

                SetBg(_fill, color);
            }

            _detail.Style.IsVisible = !string.IsNullOrWhiteSpace(post.Detail);
            if (!string.IsNullOrWhiteSpace(post.Detail)) _detail.NodeValue = McpFormat.Truncate(post.Detail, 72);
        }
    }

    private sealed class ActivityRow
    {
        private readonly Node _time = N("muted act-time");
        private readonly Node _dot = N("text act-dot", McpFormat.DotRunning);
        private readonly Node _client = N("muted act-client");
        private readonly Node _label = N("text act-label");
        private readonly Node _ms = N("muted act-ms");

        public ActivityRow()
        {
            Root = N("row-line");
            Root.AppendChild(_time);
            Root.AppendChild(_dot);
            Root.AppendChild(_client);
            Root.AppendChild(_label);
            Root.AppendChild(_ms);
        }

        public Node Root { get; }

        public void Update(McpActivity a)
        {
            _time.NodeValue = McpFormat.ClockTime(a.Timestamp);
            SetFg(_dot, a.Success ? McpFormat.ColorRunning : McpFormat.ColorFailed);
            _client.NodeValue = McpFormat.Truncate(a.ClientName ?? "?", 14);
            _label.NodeValue = McpFormat.ActivityLabel(a, 34);
            _label.Tooltip = a.Success ? null : McpFormat.Truncate(a.Error, 200);
            _ms.NodeValue = McpFormat.Duration(a.DurationMs);
        }
    }
}
