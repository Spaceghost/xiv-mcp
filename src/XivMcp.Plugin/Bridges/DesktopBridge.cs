using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

public sealed record DesktopAppDto(string Id, string Name, string? Generic, List<string> Categories, bool Favourite, bool Terminal, bool Launchable, long? Recent);

public sealed record DesktopAppListDto(int Total, int Matched, int Offset, bool Truncated, List<DesktopAppDto> Apps, string Note);

public sealed record DesktopReplyDto(string Gate, bool Ok, string Message, string Note);

/// <summary>The typed XivDesktop tools' decisions: gate names, argument checks, paging and reply parsing.</summary>
public sealed class DesktopBridge
{
    public const string Bridge = "xivdesktop";
    public const string Sibling = "XivDesktop";
    public const string ListAppsGate = "XivDesktop.v1.ListApps";
    public const string LaunchGate = "XivDesktop.v1.Launch";
    public const string StatusGate = "XivDesktop.v1.Status";
    public const string WindowsGate = "XivDesktop.v1.Windows";
    public const string WorkspaceGate = "XivDesktop.v1.Workspace";
    public const string PaletteGate = "XivDesktop.v1.Palette";
    public const string WindowActionGate = "XivDesktop.v1.WindowAction";
    public const string AskGate = "XivDesktop.v1.Ask";

    /// <summary>Window actions XivDesktop documents. Nothing else is passed through.</summary>
    public static readonly IReadOnlyList<string> WindowActions = ["focus", "close", "pet", "pin", "toggle", "place", "move"];

    private readonly IBridgeInvoker invoker;

    public DesktopBridge(IBridgeInvoker invoker) => this.invoker = invoker;

    /// <summary>Filters and pages a ListApps payload. Entries that are not objects with an id are dropped.</summary>
    public static DesktopAppListDto PageApps(JsonNode? payload, string? query, bool favouritesOnly, int limit, int offset)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var all = (payload as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => !string.IsNullOrEmpty(BridgeJson.Text(a, "id")))
            .ToList();
        var needle = query?.Trim() ?? "";
        var matched = all.Where(a => (!favouritesOnly || BridgeJson.Flag(a, "favourite") == true) && (needle.Length == 0 || Matches(a, needle))).ToList();
        var page = matched.Skip(offset).Take(limit).Select(a => new DesktopAppDto(
            BridgeJson.Clip(BridgeJson.Text(a, "id")!, 200),
            BridgeJson.Clip(BridgeJson.Text(a, "name") ?? "", 200),
            BridgeJson.Text(a, "generic") is { Length: > 0 } g ? BridgeJson.Clip(g, 200) : null,
            Strings(a, "categories"),
            BridgeJson.Flag(a, "favourite") ?? false,
            BridgeJson.Flag(a, "terminal") ?? false,
            BridgeJson.Flag(a, "launchable") ?? true,
            BridgeJson.Integer(a, "recent"))).ToList();
        return new DesktopAppListDto(all.Count, matched.Count, offset, offset + page.Count < matched.Count, page,
            "id is the desktop-file id launch_desktop_app takes. recent: 0 is the most recently launched, absent when never. " +
            "query matches id, name, generic name, keywords and categories, ignoring case.");
    }

    public static string WindowActionJson(string? action, long id, string? pin, int workspace)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();
        if (!WindowActions.Contains(verb, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"action is one of: {string.Join(", ", WindowActions)}.");
        }

        if (id < 0)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "id is a window id from list_desktop_windows, or 0 for the focused (else last focused) window.");
        }

        var request = new JsonObject { ["action"] = verb, ["id"] = id };
        if (verb == "place")
        {
            request["pin"] = TerminalBridge.CheckPin(pin);
        }
        else if (!string.IsNullOrWhiteSpace(pin))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "pin only goes with action place.");
        }

        if (verb == "move")
        {
            request["workspace"] = CheckWorkspace(workspace);
        }
        else if (workspace != 0)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "workspace only goes with action move.");
        }

        return request.ToJsonString();
    }

    public static int CheckWorkspace(int workspace) =>
        workspace is >= 1 and <= 9 ? workspace : throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "workspace is a number from 1 to 9.");

    public DesktopAppListDto ListApps(string? query, bool favouritesOnly, int limit, int offset)
    {
        invoker.Require(Bridge);
        return PageApps(BridgeJson.ParseJson(Sibling, ListAppsGate, Existing(ListAppsGate, () => invoker.Call(ListAppsGate))), query, favouritesOnly, limit, offset);
    }

    public BridgeDataDto ReadJson(string gate, string? argument = null)
    {
        invoker.Require(Bridge);
        var raw = Existing(gate, () => argument is null ? invoker.Call(gate) : invoker.Call(gate, argument));
        var capped = BridgeJson.Cap(BridgeJson.ParseJson(Sibling, gate, raw));
        return new BridgeDataDto(Bridge, gate, capped.Node, capped.Truncated, capped.RawPrefix);
    }

    public BridgeDataDto SearchPalette(string? query, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        invoker.Require(Bridge);
        var text = BridgeJson.CheckArgument("query", query, allowEmpty: true, maxBytes: 256);
        var node = BridgeJson.ParseJson(Sibling, PaletteGate, Existing(PaletteGate, () => invoker.Call(PaletteGate, text)));
        var truncated = false;
        if (node is JsonArray array && array.Count > limit)
        {
            node = new JsonArray(array.Take(limit).Select(n => n?.DeepClone()).ToArray());
            truncated = true;
        }

        var capped = BridgeJson.Cap(node);
        return new BridgeDataDto(Bridge, PaletteGate, capped.Node, truncated || capped.Truncated, capped.RawPrefix,
            "Each entry: provider (app|window|action|calc|command), title, subtitle, score, enabled, reason, command {kind, arg, id, number}, appId, windowId. " +
            "Nothing is run; use launch_desktop_app or desktop_window_action to act.");
    }

    public BridgeDataDto GetStatus()
    {
        var status = ReadJson(StatusGate);
        int? api = null;
        try
        {
            api = invoker.CallInt(SiblingIpcProposals.DesktopApiVersionGate);
        }
        catch (Exception ex) when (ex is BridgeCapabilityMissingException or McpToolException)
        {
            // Not shipped yet.
        }

        if (status.Result is JsonObject o)
        {
            o["apiVersion"] = api;
        }

        return status with
        {
            Note = "ghostty: launching is possible (GhosttyDalamud's Post gate is there). apps/scanning/scannedAt: the application catalogue. " +
                   "apiVersion is null until XivDesktop offers " + SiblingIpcProposals.DesktopApiVersionGate + ".",
        };
    }

    /// <summary>A proposed no-argument JSON read gate.</summary>
    public BridgeDataDto ReadProposed(string gate)
    {
        var proposal = Proposal(gate);
        invoker.Require(Bridge);
        try
        {
            var capped = BridgeJson.Cap(BridgeJson.ParseJson(Sibling, gate, invoker.Call(gate)));
            return new BridgeDataDto(Bridge, gate, capped.Node, capped.Truncated, capped.RawPrefix);
        }
        catch (BridgeCapabilityMissingException)
        {
            throw SiblingIpcProposals.NotShipped(proposal);
        }
    }

    /// <summary>A proposed no-argument change gate answering "ok: …" / "error: …".</summary>
    public DesktopReplyDto ChangeProposed(string gate, string note)
    {
        var proposal = Proposal(gate);
        invoker.Require(Bridge);
        try
        {
            return new DesktopReplyDto(gate, true, BridgeJson.ParseTextReply(Sibling, gate, invoker.Call(gate)), note);
        }
        catch (BridgeCapabilityMissingException)
        {
            throw SiblingIpcProposals.NotShipped(proposal);
        }
    }

    public DesktopReplyDto Launch(string? app)
    {
        var text = BridgeJson.CheckArgument("app", app, maxBytes: 256);
        return Change(LaunchGate, text, "\"ok\" means the launch was handed to GhosttyDalamud's desktop agent, not that a window appeared; list_desktop_windows shows it once it has.");
    }

    public DesktopReplyDto WindowAction(string? action, long id, string? pin, int workspace) =>
        Change(WindowActionGate, WindowActionJson(action, id, pin, workspace), "\"ok\" means GhosttyDalamud queued the change.");

    public DesktopReplyDto SwitchWorkspace(int workspace)
    {
        CheckWorkspace(workspace);
        invoker.Require(Bridge);
        var raw = Existing(WorkspaceGate, () => invoker.Call(WorkspaceGate, workspace));
        return new DesktopReplyDto(WorkspaceGate, true, BridgeJson.ParseTextReply(Sibling, WorkspaceGate, raw), "Windows on other workspaces are hidden, not closed.");
    }

    public DesktopReplyDto Ask(string? text)
    {
        var line = BridgeJson.CheckArgument("text", text, allowEmpty: true);
        return Change(AskGate, line,
            "The gate only says the request was taken. The answer streams into the in-game dialogue window for the player to read; it is not returned here.");
    }

    private DesktopReplyDto Change(string gate, string argument, string note)
    {
        invoker.Require(Bridge);
        var raw = Existing(gate, () => invoker.Call(gate, argument));
        return new DesktopReplyDto(gate, true, BridgeJson.ParseTextReply(Sibling, gate, raw), note);
    }

    private static string Existing(string gate, Func<string> call)
    {
        try
        {
            return call();
        }
        catch (BridgeCapabilityMissingException)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"This {Sibling} does not register {gate}; update XivDesktop.", retryable: true);
        }
    }

    private static SiblingIpcProposal Proposal(string gate) =>
        SiblingIpcProposals.Find(gate, null) ?? throw new InvalidOperationException($"{gate} is not a proposed gate.");

    private static bool Matches(JsonObject app, string needle) =>
        new[] { BridgeJson.Text(app, "id"), BridgeJson.Text(app, "name"), BridgeJson.Text(app, "generic") }
            .Concat(Strings(app, "keywords")).Concat(Strings(app, "categories"))
            .Any(t => t is not null && t.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static List<string> Strings(JsonObject app, string key) =>
        app[key] is JsonArray array
            ? array.Select(BridgeJson.Text).Where(t => t is not null).Select(t => BridgeJson.Clip(t!, 100)).Take(32).ToList()
            : [];
}
