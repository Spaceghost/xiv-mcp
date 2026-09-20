using System.Globalization;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

/// <summary>One GhosttyDalamud panel: panel.list merged with window.list by id.</summary>
public sealed record TerminalPanelDto(
    long Id,
    string Kind,
    string Title,
    string? App,
    string? Profile,
    bool? Running,
    string View,
    string? Anchor,
    string? State,
    long? Width,
    long? Height,
    bool Focused,
    bool Hidden,
    long? Order);

public sealed record TerminalPanelListDto(long? Rev, int Total, int Offset, bool Truncated, List<TerminalPanelDto> Panels, string Note);

/// <summary>One panel of a reconstructed layout: what would have to be sent to put a panel like it back.</summary>
public sealed record TerminalLayoutPanelDto(
    long Id,
    string Kind,
    string Title,
    string? Profile,
    string? App,
    string View,
    long? Order,
    string? Pin,
    bool Reproducible,
    string How);

public sealed record TerminalLayoutDto(long? Rev, int Total, int Reproducible, bool Truncated, List<TerminalLayoutPanelDto> Panels, string Note);

public sealed record TerminalStatusDto(string? Status, JsonNode? Agent, JsonNode? Focus, JsonNode? Api, string Note);

public sealed record TerminalRequestDto(string Request, string State, string? Method, bool? Ok, JsonNode? Result, string? Error, string Note);

/// <summary>A change handed to GhosttyDalamud, with its outcome when that arrived within the short wait.</summary>
public sealed record TerminalChangeDto(string Method, string Via, string? Request, bool Completed, long? PanelId, JsonNode? Result, string Note);

public sealed record BridgeDataDto(string Bridge, string Capability, JsonNode? Result, bool Truncated, string? RawPrefix, string? Note = null);

/// <summary>
/// Everything the typed terminal tools decide, with Dalamud behind <see cref="IBridgeInvoker"/>: which verbs may be sent
/// at all, the exact request JSON, how answers are read, how a layout is reconstructed from the panel snapshot, and the
/// bounded wait for a queued change's outcome.
/// </summary>
public sealed class TerminalBridge
{
    public const string Bridge = "ghostty";
    public const string Sibling = "GhosttyDalamud";
    public const string CallGate = "GhosttyDalamud.v1.Call";
    public const string PostGate = "GhosttyDalamud.v1.Post";
    public const string StatusGate = "GhosttyDalamud.v1.Status";
    public const string Caller = "XivMcp";

    /// <summary>How long a change tool waits for its outcome before handing back the request id instead.</summary>
    public static readonly TimeSpan OutcomeWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>Read verbs GhosttyDalamud documents today.</summary>
    public static readonly IReadOnlyList<string> ReadVerbs = ["window.list", "panel.list", "agent.status", "focus.get", "status"];

    /// <summary>
    /// Change verbs the typed tools send today. Deliberately short: no window.toggle_pet/keys.reserve/focus.cycle (the raw
    /// ghostty_command covers the first and last; reserving keys is for plugins that read keys), and nothing that types
    /// into a shell.
    /// </summary>
    public static readonly IReadOnlyList<string> ChangeVerbs =
        ["window.open", "terminal.new", "window.hide", "panel.focus", "panel.close", "panel.place", "panel.order"];

    /// <summary>/term command words the typed tools may post. "send" and "type" are not here and never will be.</summary>
    public static readonly IReadOnlyList<string> PostCommands = ["shot", "clip", "theme", "selftest"];

    private static readonly string[] PinWords = ["here", "me", "target", "orbit", "pet", "hud", "hide"];

    private readonly IBridgeInvoker invoker;
    private readonly IGameThread game;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public TerminalBridge(IBridgeInvoker invoker, IGameThread game, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.invoker = invoker;
        this.game = game;
        this.delay = delay ?? Task.Delay;
    }

    // ---- request building (pure) ----------------------------------------------------------------------------

    /// <summary>The request JSON for a verb. Refuses anything not on an allow-list or in the proposals table.</summary>
    public static string BuildRequest(string method, JsonObject? parameters = null)
    {
        if (!ReadVerbs.Contains(method, StringComparer.Ordinal) && !ChangeVerbs.Contains(method, StringComparer.Ordinal) &&
            SiblingIpcProposals.Find(CallGate, method) is null)
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"\"{method}\" is not a GhosttyDalamud verb XivMcp sends.");
        }

        var request = new JsonObject { ["method"] = method, ["caller"] = Caller };
        if (parameters is { Count: > 0 })
        {
            request["params"] = parameters;
        }

        return request.ToJsonString();
    }

    /// <summary>The /term command line for the Post gate (without "/term "). Refuses command words off the allow-list.</summary>
    public static string BuildPost(string command, params string?[] words)
    {
        if (!PostCommands.Contains(command, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"\"/term {command}\" is not a command XivMcp posts to GhosttyDalamud.");
        }

        var parts = new List<string> { command };
        parts.AddRange(words.Where(w => !string.IsNullOrEmpty(w)).Select(w => w!));
        return BridgeJson.CheckArgument("command", string.Join(' ', parts));
    }

    /// <summary>Checks /term pin arguments: a known word, then numbers (or "off" after hide). Returns them normalised.</summary>
    public static string CheckPin(string? pin)
    {
        var text = BridgeJson.CheckArgument("pin", pin, maxBytes: 64);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var word = parts[0].ToLowerInvariant();
        if (!PinWords.Contains(word, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments,
                $"pin starts with one of: {string.Join(", ", PinWords)} (e.g. \"pet\", \"here\", \"me 2 1.7\", \"orbit 3.5\", \"hud 0.85 0.2\").");
        }

        foreach (var part in parts.Skip(1))
        {
            var isOff = word == "hide" && part.Equals("off", StringComparison.OrdinalIgnoreCase);
            if (!isOff && !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, $"pin: \"{part}\" is not a number.");
            }
        }

        if (parts.Length > 4)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "pin takes at most three numbers.");
        }

        return string.Join(' ', new[] { word }.Concat(parts.Skip(1).Select(p => p.ToLowerInvariant())));
    }

    public static JsonObject OpenParameters(string? run, string? profile, string? pin, out string method)
    {
        var command = BridgeJson.CheckArgument("run", run, allowEmpty: true);
        var profileName = BridgeJson.CheckArgument("profile", profile, allowEmpty: true, maxBytes: 128);
        if (command.Length > 0 && profileName.Length > 0)
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments,
                "Give run (a program streamed from the desktop agent) or profile (a terminal profile), not both.");
        }

        var parameters = new JsonObject();
        if (command.Length > 0)
        {
            method = "window.open";
            parameters["run"] = command;
        }
        else
        {
            method = "terminal.new";
            if (profileName.Length > 0)
            {
                parameters["profile"] = profileName;
            }
        }

        if (!string.IsNullOrWhiteSpace(pin))
        {
            parameters["pin"] = CheckPin(pin);
        }

        return parameters;
    }

    public static JsonObject OrderParameters(long id, string? to)
    {
        var text = BridgeJson.CheckArgument("to", to, maxBytes: 16).ToLowerInvariant();
        var parameters = new JsonObject { ["id"] = CheckId(id) };
        if (text is "left" or "right" or "first" or "last")
        {
            parameters["to"] = text;
        }
        else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var place) && place >= 1)
        {
            parameters["to"] = place;
        }
        else
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "to is left, right, first, last or a place from 1.");
        }

        return parameters;
    }

    public static long CheckId(long id) =>
        id > 0 ? id : throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "id is a panel id from list_terminal_panels (a positive number).");

    /// <summary>Suite names for /term selftest: lower-case words only, never "leakwatch" (that one toggles a setting).</summary>
    public static List<string> CheckSuites(string? suites)
    {
        var names = (suites ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (names.Count == 0)
        {
            names.Add("all");
        }

        if (names.Count > 16 || names.Any(n => n.Length > 32 || !n.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')))
        {
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "suites is up to 16 names of a-z, 0-9, _ and -, separated by spaces (or \"all\", \"list\").");
        }

        if (names.Contains("leakwatch"))
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, "\"leakwatch\" switches a diagnostic setting rather than running a suite; it is not sent from here.");
        }

        return names;
    }

    // ---- reading (pure) -------------------------------------------------------------------------------------

    /// <summary>panel.list and window.list results merged by id, oldest first as GhosttyDalamud lists them.</summary>
    public static List<TerminalPanelDto> MergePanels(JsonNode? panelList, JsonNode? windowList)
    {
        var windows = BridgeJson.Objects(windowList, "windows")
            .Select(w => (Id: BridgeJson.Integer(w, "id"), Node: w))
            .Where(w => w.Id is not null)
            .GroupBy(w => w.Id!.Value)
            .ToDictionary(g => g.Key, g => g.First().Node);

        var panels = new List<TerminalPanelDto>();
        var seen = new HashSet<long>();
        foreach (var p in BridgeJson.Objects(panelList, "panels"))
        {
            if (BridgeJson.Integer(p, "id") is not { } id || !seen.Add(id))
            {
                continue;
            }

            windows.TryGetValue(id, out var w);
            var view = Clip(BridgeJson.Text(p, "view")) ?? "unknown";
            panels.Add(new TerminalPanelDto(
                id,
                Clip(BridgeJson.Text(p, "kind")) ?? "unknown",
                Clip(BridgeJson.Text(p, "title")) ?? "",
                NullIfEmpty(Clip(BridgeJson.Text(p, "app") ?? BridgeJson.Text(w, "app"))),
                Clip(BridgeJson.Text(p, "profile")),
                BridgeJson.Flag(p, "running"),
                view,
                Clip(BridgeJson.Text(w, "anchor")),
                Clip(BridgeJson.Text(w, "state")),
                BridgeJson.Integer(w, "w"),
                BridgeJson.Integer(w, "h"),
                BridgeJson.Flag(p, "focused") ?? BridgeJson.Flag(w, "focused") ?? false,
                view == "hidden" || (BridgeJson.Flag(w, "hidden") ?? false),
                BridgeJson.Integer(p, "order")));
        }

        // A window the panel snapshot does not know yet (the two are read one after the other).
        foreach (var (id, w) in windows.Where(kv => !seen.Contains(kv.Key)).OrderBy(kv => kv.Key))
        {
            panels.Add(new TerminalPanelDto(id, "window", Clip(BridgeJson.Text(w, "title")) ?? "", NullIfEmpty(Clip(BridgeJson.Text(w, "app"))), null, null,
                Clip(BridgeJson.Text(w, "kind")) ?? "unknown", Clip(BridgeJson.Text(w, "anchor")), Clip(BridgeJson.Text(w, "state")),
                BridgeJson.Integer(w, "w"), BridgeJson.Integer(w, "h"), BridgeJson.Flag(w, "focused") ?? false, BridgeJson.Flag(w, "hidden") ?? false, null));
        }

        return panels;
    }

    /// <summary>
    /// The layout as far as the snapshot shows it. GhosttyDalamud publishes where a panel is (view, anchor, pet order) but
    /// not the numbers behind it (a pin's world position, an orbit's distance, a HUD dock's X/Y), so only some places can be
    /// put back exactly; <see cref="TerminalLayoutPanelDto.Reproducible"/> says which.
    /// </summary>
    public static List<TerminalLayoutPanelDto> ReconstructLayout(IEnumerable<TerminalPanelDto> panels) =>
        panels.Select(p =>
        {
            var (pin, exact, why) = PinFor(p);
            var open = p.Kind switch
            {
                "terminal" => $"terminal.new {{profile: \"{p.Profile ?? "default"}\"{(pin is null ? "" : $", pin: \"{pin}\"")}}}",
                "window" when p.App is not null => $"window.open {{match: \"{p.App}\"{(pin is null ? "" : $", pin: \"{pin}\"")}}} (or run, if the program is not open)",
                "window" => "window.open with the window's wid from agent.windows (the snapshot does not say which program it was)",
                _ => "not opened through IPC (an adopted plugin window or the chat); it can only be moved with place_terminal_panel",
            };
            var order = p.Order is { } o ? $"; then order_terminal_panel to {o.ToString(CultureInfo.InvariantCulture)}" : "";
            return new TerminalLayoutPanelDto(p.Id, p.Kind, p.Title, p.Profile, p.App, p.View, p.Order, pin, exact, $"{open}{order}. {why}");
        }).ToList();

    private static (string? Pin, bool Exact, string Why) PinFor(TerminalPanelDto p)
    {
        if (p.Hidden && p.View == "hidden")
        {
            return (null, false, "Hidden: the snapshot does not say where it was before it was hidden.");
        }

        return p.View switch
        {
            "pet" => ("pet", true, "A pet beside the character; its place in the lineup is order."),
            "hud" => ("hud", false, "Docked to the screen, but the dock's X/Y and distance are not exposed; \"hud\" alone docks a panel where it currently shows."),
            "pin" => p.Anchor switch
            {
                "me" => ("me", false, "Follows the character; the offset numbers are not exposed, so GhosttyDalamud's defaults apply."),
                "target" => ("target", false, "Follows the target; the offset numbers are not exposed."),
                "orbit" => ("orbit", false, "Orbits the character; the distance is not exposed, so the default applies."),
                _ => (null, false, "Fixed in the world at a position the snapshot does not expose (terminals also report no anchor); \"here\" would pin it at the character instead."),
            },
            "tab" or "dropdown" or "min" => (null, false, $"In the dropdown ({p.View}); IPC can move a panel into the world but not back into the dropdown."),
            "full" => (null, false, "Full screen for now; its anchor underneath is " + (p.Anchor ?? "unknown") + "."),
            _ => (null, false, "No place the snapshot describes."),
        };
    }

    /// <summary>Looks a request id up in window.list's requests[] (the last 16 outcomes).</summary>
    public static TerminalRequestDto FindRequest(JsonNode? windowList, long request)
    {
        var id = request.ToString(CultureInfo.InvariantCulture);
        var entry = BridgeJson.Objects(windowList, "requests").LastOrDefault(r => BridgeJson.Integer(r, "request") == request);
        if (entry is null)
        {
            return new TerminalRequestDto(id, "unknown", null, null, null, null,
                "Not in the last 16 outcomes: either GhosttyDalamud has not run it yet (it runs queued changes at the end of its next frame, and not at all " +
                "while it is disabled) or newer changes pushed it out.");
        }

        var ok = BridgeJson.Flag(entry, "ok") ?? false;
        var result = BridgeJson.Cap(entry["result"]?.DeepClone());
        return new TerminalRequestDto(id, ok ? "done" : "failed", Clip(BridgeJson.Text(entry, "method")), ok, result.Node,
            ok ? null : BridgeJson.Clip(BridgeJson.Text(entry, "error") ?? "no reason given", BridgeJson.MaxErrorChars),
            ok ? "The change ran." : "GhosttyDalamud ran the change and it failed; error is its own text.");
    }

    // ---- calls ----------------------------------------------------------------------------------------------

    /// <summary>Calls a read verb that exists today. Must run on the framework thread (the tools' default).</summary>
    public JsonNode? Read(string method)
    {
        if (!ReadVerbs.Contains(method, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"\"{method}\" is not a GhosttyDalamud read verb.");
        }

        invoker.Require(Bridge);
        return Send(method, null);
    }

    /// <summary>Calls a proposed read verb; <c>unavailable</c> until GhosttyDalamud ships it.</summary>
    public BridgeDataDto ReadProposed(string method, JsonObject? parameters = null)
    {
        var proposal = Proposal(method, SiblingIpcKind.Read);
        invoker.Require(Bridge);
        try
        {
            var capped = BridgeJson.Cap(Send(method, parameters));
            return new BridgeDataDto(Bridge, proposal.Capability, capped.Node, capped.Truncated, capped.RawPrefix);
        }
        catch (BridgeCapabilityMissingException)
        {
            throw SiblingIpcProposals.NotShipped(proposal);
        }
    }

    /// <summary>A proposed read verb whose absence is not an error for the calling tool: null until it ships.</summary>
    public JsonNode? TryReadProposed(string method)
    {
        Proposal(method, SiblingIpcKind.Read);
        try
        {
            return BridgeJson.Cap(Send(method, null)).Node;
        }
        catch (Exception ex) when (ex is BridgeCapabilityMissingException or McpToolException)
        {
            return null;
        }
    }

    public TerminalPanelListDto ListPanels(int limit, int offset)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        invoker.Require(Bridge);
        var panelList = Send("panel.list", null);
        var all = MergePanels(panelList, Send("window.list", null));
        var page = all.Skip(offset).Take(limit).ToList();
        return new TerminalPanelListDto(BridgeJson.Integer(panelList, "rev"), all.Count, offset, offset + page.Count < all.Count, page,
            "view: tab/dropdown/min (in the dropdown), pet, pin, hud, full or hidden. anchor, state, width and height exist only for remote-window panels. " +
            "order is a pet's place in the lineup from 1.");
    }

    public TerminalLayoutDto GetLayout()
    {
        invoker.Require(Bridge);
        var panelList = Send("panel.list", null);
        var all = ReconstructLayout(MergePanels(panelList, Send("window.list", null)));
        var page = all.Take(200).ToList();
        return new TerminalLayoutDto(BridgeJson.Integer(panelList, "rev"), all.Count, all.Count(p => p.Reproducible), all.Count > page.Count, page,
            "Reconstructed: GhosttyDalamud has no layout.get. Its snapshot says where each panel is (view, anchor, pet order) but not the numbers behind a " +
            "place, so only reproducible=true rows can be put back exactly with place_terminal_panel; how explains the rest. " +
            "get_terminal_layouts reads real saved layouts once GhosttyDalamud offers them.");
    }

    public TerminalStatusDto GetStatus()
    {
        invoker.Require(Bridge);
        string? status;
        try
        {
            status = BridgeJson.Clip(invoker.Call(StatusGate), 200);
        }
        catch (BridgeCapabilityMissingException)
        {
            status = null;
        }

        var agent = BridgeJson.Cap(Send("agent.status", null)).Node;
        var focus = BridgeJson.Cap(Send("focus.get", null)).Node;

        return new TerminalStatusDto(status, agent, focus, TryReadProposed("api.version"),
            "agent: the desktop agent that streams windows (connected, version, windows_ok). focus: the world panel with the keyboard ({id: 0} when none). " +
            "api is null until GhosttyDalamud offers an api.version verb.");
    }

    public TerminalRequestDto GetRequest(string? request)
    {
        var id = ParseRequest(request);
        invoker.Require(Bridge);
        return FindRequest(Send("window.list", null), id);
    }

    public static long ParseRequest(string? request) =>
        long.TryParse(request?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "request is the request id a terminal change tool returned (digits only).");

    /// <summary>
    /// Queues a change and waits, off the framework thread, at most <see cref="OutcomeWait"/> for GhosttyDalamud to run it.
    /// Each IPC call is a short hop onto the framework thread; the waiting in between is a timer.
    /// </summary>
    public async Task<TerminalChangeDto> ChangeAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var proposal = SiblingIpcProposals.Find(CallGate, method);
        if (proposal is null && !ChangeVerbs.Contains(method, StringComparer.Ordinal))
        {
            throw McpToolException.WithCode(McpErrorCodes.Refused, $"\"{method}\" is not a GhosttyDalamud change verb XivMcp sends.");
        }

        if (proposal is { Kind: not SiblingIpcKind.Change })
        {
            throw new InvalidOperationException($"{method} is a read.");
        }

        var request = BuildRequest(method, parameters);
        JsonNode? queued;
        try
        {
            queued = await game.InvokeAsync(() =>
            {
                invoker.Require(Bridge);
                return BridgeJson.ParseEnvelope(Sibling, CallGate, method, invoker.Call(CallGate, request));
            }, ct).ConfigureAwait(false);
        }
        catch (BridgeCapabilityMissingException ex)
        {
            throw proposal is null ? Missing(ex) : SiblingIpcProposals.NotShipped(proposal);
        }

        if (BridgeJson.Integer(queued, "request") is not { } id)
        {
            var capped = BridgeJson.Cap(queued);
            return new TerminalChangeDto(method, "call", null, true, null, capped.Node, "GhosttyDalamud answered at once, without queueing.");
        }

        var listRequest = BuildRequest("window.list");
        var waited = TimeSpan.Zero;
        while (waited < OutcomeWait)
        {
            await delay(PollEvery, ct).ConfigureAwait(false);
            waited += PollEvery; // leak-audit: ok - adds a TimeSpan to a local, not an event subscription
            var list = await game.InvokeAsync(() => BridgeJson.ParseEnvelope(Sibling, CallGate, "window.list", invoker.Call(CallGate, listRequest)), ct).ConfigureAwait(false);
            var outcome = FindRequest(list, id);
            if (outcome.State == "failed")
            {
                throw new McpToolException($"{Sibling} ran {method} and it failed: {outcome.Error}");
            }

            if (outcome.State == "done")
            {
                return new TerminalChangeDto(method, "call", outcome.Request, true, BridgeJson.Integer(outcome.Result, "id"), outcome.Result, "The change ran.");
            }
        }

        return new TerminalChangeDto(method, "call", id.ToString(CultureInfo.InvariantCulture), false, null, null,
            "Queued, but GhosttyDalamud had not run it within 2 seconds (it runs changes at the end of its next frame and waits while it is disabled). " +
            "Look the outcome up with get_terminal_request.");
    }

    /// <summary>
    /// Sends a change through its proposed verb when GhosttyDalamud has it, otherwise as the /term command it stands for.
    /// The Post gate returns nothing, so that path can only say "requested".
    /// </summary>
    public async Task<TerminalChangeDto> ChangeOrPostAsync(string method, JsonObject? parameters, string postLine, string postNote, CancellationToken ct)
    {
        try
        {
            return await ChangeAsync(method, parameters, ct).ConfigureAwait(false);
        }
        catch (McpToolException ex) when (ex.Code == McpErrorCodes.Unavailable && SiblingIpcProposals.Find(CallGate, method) is not null)
        {
            // Not shipped yet: fall through to the chat command.
        }

        await game.InvokeAsync(() =>
        {
            invoker.Require(Bridge);
            try
            {
                invoker.Post(PostGate, postLine);
            }
            catch (BridgeCapabilityMissingException)
            {
                throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{Sibling} is loaded but {PostGate} is not registered; its core may still be starting.", retryable: true);
            }
        }, ct).ConfigureAwait(false);

        return new TerminalChangeDto(method, "post", null, false, null, null,
            $"Requested as \"/term {postLine}\". {Sibling} has no \"{method}\" IPC verb yet and its Post gate returns nothing, so whether it happened cannot be confirmed from here. {postNote}");
    }

    private JsonNode? Send(string method, JsonObject? parameters)
    {
        try
        {
            return BridgeJson.ParseEnvelope(Sibling, CallGate, method, invoker.Call(CallGate, BuildRequest(method, parameters)));
        }
        catch (BridgeCapabilityMissingException ex)
        {
            throw SiblingIpcProposals.Find(CallGate, method) is { } proposal ? new BridgeCapabilityMissingException(proposal.Capability, ex) : Missing(ex);
        }
    }

    private static SiblingIpcProposal Proposal(string method, SiblingIpcKind kind) =>
        SiblingIpcProposals.Find(CallGate, method) is { } p && p.Kind == kind
            ? p
            : throw new InvalidOperationException($"{method} is not a proposed {kind} verb.");

    private static McpToolException Missing(BridgeCapabilityMissingException ex) =>
        McpToolException.WithCode(McpErrorCodes.Unavailable,
            ex.Capability.Contains('#', StringComparison.Ordinal)
                ? $"This {Sibling} is older than {ex.Capability} (it answered \"unknown method\"). Update GhosttyDalamud."
                : $"{Sibling} is loaded but {CallGate} is not registered: its core is older than the JSON call gate, or still starting.",
            retryable: true);

    private static string? Clip(string? text) => text is null ? null : BridgeJson.Clip(text, 200);

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
