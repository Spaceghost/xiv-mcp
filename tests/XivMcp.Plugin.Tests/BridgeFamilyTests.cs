using System.Reflection;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;
using XivMcp.Plugin.Providers.Bridges;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>
/// The typed tools over the owner's other plugins (GhosttyDalamud, XivDesktop, Almanac), exercised against a fake IPC
/// layer: what is sent (exact JSON), what is refused before anything is sent, how answers are read, and that every tool
/// written against IPC a sibling has not shipped says so with <c>unavailable</c>.
/// </summary>
public class BridgeFamilyTests
{
    // Shapes from GhosttyDalamud's docs/IPC.md.
    private const string PanelList = """
        {"rev": 9, "panels": [
          {"id": 3, "kind": "terminal", "title": "bash", "profile": "bash", "running": true, "view": "tab", "focused": true},
          {"id": 4, "kind": "terminal", "title": "htop", "profile": "bash", "running": true, "view": "pet", "focused": false, "order": 2},
          {"id": 5, "kind": "terminal", "title": "zsh", "profile": "zsh", "running": false, "view": "min", "focused": false},
          {"id": 12, "kind": "window", "title": "Yad Window", "app": "yad", "icon": "~/.cache/ghostty-agent/icons/yad.png", "view": "pet", "focused": false, "order": 1},
          {"id": 13, "kind": "window", "title": "Mozilla Firefox", "app": "firefox", "view": "pin", "focused": false},
          {"id": 14, "kind": "adopted", "title": "Mappy", "app": "Mappy", "view": "hud", "focused": false},
          {"id": 15, "kind": "chat", "title": "Chat", "view": "hidden", "focused": false}
        ]}
        """;

    private const string WindowList = """
        {"rev": 9, "windows": [
          {"id": 12, "sid": 5, "title": "Yad Window", "app": "yad", "w": 640, "h": 400, "state": "live", "kind": "pet", "anchor": "pet", "hidden": false, "focused": false, "agent": "default", "key": "yad-1"},
          {"id": 13, "sid": 6, "title": "Mozilla Firefox", "app": "firefox", "w": 800, "h": 600, "state": "live", "kind": "pin", "anchor": "orbit", "hidden": false, "focused": false, "agent": "default", "key": ""}
        ], "requests": [
          {"request": 1726732800001, "method": "window.open", "ok": true, "result": {"id": 12}},
          {"request": 1726732800002, "method": "window.place", "ok": false, "error": "pin: usage: /term pin here"}
        ]}
        """;

    private static string Ok(string result) => "{\"ok\":true,\"result\":" + result + "}";

    /// <summary>A GhosttyDalamud as it is today: the documented verbs answer, anything else is an unknown method.</summary>
    private static FakeBridgeInvoker Ghostty(Dictionary<string, Func<JsonObject?, string>>? verbs = null)
    {
        var fake = new FakeBridgeInvoker();
        fake.PostGates.Add(TerminalBridge.PostGate);
        fake.Gates[TerminalBridge.StatusGate] = _ => "ghostty 3";
        fake.Gates[TerminalBridge.CallGate] = raw =>
        {
            var request = JsonNode.Parse(raw!)!.AsObject();
            var method = request["method"]!.GetValue<string>();
            if (verbs is not null && verbs.TryGetValue(method, out var handler))
                return handler(request["params"] as JsonObject);
            return method switch
            {
                "panel.list" => Ok(PanelList),
                "window.list" => Ok(WindowList),
                "agent.status" => Ok("""{"connected": true, "version": 3, "windows_ok": true, "agent": "127.0.0.1:7777", "window_lists": 2}"""),
                "focus.get" => Ok("""{"id": 12, "kind": "window"}"""),
                _ => "{\"ok\":false,\"error\":\"unknown method: " + method + "\"}",
            };
        };
        return fake;
    }

    private static TerminalBridge Terminal(FakeBridgeInvoker fake, InlineGameThread? game = null) =>
        new(fake, game ?? new InlineGameThread(), (_, _) => Task.CompletedTask);

    private static List<string?> Sent(FakeBridgeInvoker fake, string gate) => fake.Calls.Where(c => c.Gate == gate).Select(c => c.Argument).ToList();

    private static McpToolException Throws(Action act) => Assert.Throws<McpToolException>(act);

    // ---- allow-lists ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("send")]
    [InlineData("type")]
    [InlineData("keys.reserve")]
    [InlineData("window.toggle_pet")]
    [InlineData("WINDOW.LIST")]
    [InlineData("")]
    public void VerbsOffTheListAreNeverBuilt(string method)
    {
        Assert.Equal(McpErrorCodes.Refused, Throws(() => TerminalBridge.BuildRequest(method)).Code);
        Assert.Equal(McpErrorCodes.Refused, Throws(() => AlmanacBridge.BuildRequest(method)).Code);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("type")]
    [InlineData("share")]
    [InlineData("reload")]
    [InlineData("ask")]
    public void OnlyFourTermCommandsArePosted(string command)
    {
        Assert.Equal(McpErrorCodes.Refused, Throws(() => TerminalBridge.BuildPost(command, "x")).Code);
        Assert.Equal(["shot", "clip", "theme", "selftest"], TerminalBridge.PostCommands);
    }

    [Fact]
    public async Task ChangeAndReadRefuseVerbsOfTheOtherKind()
    {
        var fake = Ghostty();
        Assert.Equal(McpErrorCodes.Refused, Throws(() => Terminal(fake).Read("window.open")).Code);
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Terminal(fake).ChangeAsync("window.list", null, default));
        Assert.Equal(McpErrorCodes.Refused, ex.Code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void ThereIsNoToolThatTypesIntoAShell()
    {
        var names = ToolMethods().Select(t => t.Tool.Name).ToList();
        Assert.DoesNotContain("send_terminal_text", names);
        Assert.DoesNotContain(names, n => n.Contains("type_terminal", StringComparison.Ordinal) || n.Contains("terminal_text", StringComparison.Ordinal));
    }

    // ---- request JSON, tool by tool -------------------------------------------------------------------------

    public static TheoryData<string, object?[], string> TerminalChanges => new()
    {
        { "open_terminal", [null, null, null], """{"method":"terminal.new","caller":"XivMcp"}""" },
        { "open_terminal", [null, "pwsh", "here"], """{"method":"terminal.new","caller":"XivMcp","params":{"profile":"pwsh","pin":"here"}}""" },
        { "open_terminal", ["foot htop", null, "PET"], """{"method":"window.open","caller":"XivMcp","params":{"run":"foot htop","pin":"pet"}}""" },
        { "place_terminal_panel", [4L, "hud 0.85  0.2"], """{"method":"panel.place","caller":"XivMcp","params":{"id":4,"pin":"hud 0.85 0.2"}}""" },
        { "focus_terminal_panel", [4L], """{"method":"panel.focus","caller":"XivMcp","params":{"id":4,"fly":false}}""" },
        { "close_terminal_panel", [4L], """{"method":"panel.close","caller":"XivMcp","params":{"id":4}}""" },
        { "set_terminal_panel_hidden", [12L, true], """{"method":"window.hide","caller":"XivMcp","params":{"id":12,"hidden":true}}""" },
        { "order_terminal_panel", [4L, "Left"], """{"method":"panel.order","caller":"XivMcp","params":{"id":4,"to":"left"}}""" },
        { "order_terminal_panel", [4L, "2"], """{"method":"panel.order","caller":"XivMcp","params":{"id":4,"to":2}}""" },
        { "capture_terminal_screenshot", ["panel", true], """{"method":"capture.shot","caller":"XivMcp","params":{"target":"panel","clean":true}}""" },
        { "capture_terminal_clip", [10, "MP4", true, false], """{"method":"capture.clip","caller":"XivMcp","params":{"seconds":10,"format":"mp4","panel":true,"clean":false}}""" },
        { "set_terminal_theme", ["Gruvbox Light"], """{"method":"theme.set","caller":"XivMcp","params":{"name":"Gruvbox Light"}}""" },
        { "run_terminal_selftest", ["themes, bell"], """{"method":"selftest.run","caller":"XivMcp","params":{"suites":["themes","bell"]}}""" },
        { "apply_terminal_layout", ["raid"], """{"method":"layout.set","caller":"XivMcp","params":{"name":"raid","apply":true}}""" },
        { "share_terminal_capture", ["last"], """{"method":"gallery.share","caller":"XivMcp","params":{"last":true}}""" },
        { "share_terminal_capture", ["~/captures/shot-0001.png"], """{"method":"gallery.share","caller":"XivMcp","params":{"path":"~/captures/shot-0001.png"}}""" },
    };

    [Theory]
    [MemberData(nameof(TerminalChanges))]
    public async Task TerminalChangeToolsSendExactlyThis(string tool, object?[] arguments, string expected)
    {
        var fake = Ghostty();
        try
        {
            await InvokeAsync(TerminalProvider(fake), tool, arguments);
        }
        catch (McpToolException)
        {
            // Proposal-backed verbs are unknown to this fake; what was sent is still recorded.
        }

        Assert.Equal(expected, Sent(fake, TerminalBridge.CallGate).First(a => !a!.Contains("window.list", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("capture_terminal_screenshot", new object?[] { "full", false }, "shot")]
    [InlineData("capture_terminal_screenshot", new object?[] { "panel", true }, "shot panel clean")]
    [InlineData("capture_terminal_clip", new object?[] { 6, "gif", false, false }, "clip 6 gif")]
    [InlineData("capture_terminal_clip", new object?[] { 8, "mp4", true, true }, "clip 8 mp4 panel clean")]
    [InlineData("set_terminal_theme", new object?[] { "Gruvbox Light" }, "theme Gruvbox Light")]
    [InlineData("run_terminal_selftest", new object?[] { "all" }, "selftest all")]
    [InlineData("run_terminal_selftest", new object?[] { "themes bell" }, "selftest themes bell")]
    public async Task UntilGhosttyHasTheVerbTheChatCommandIsPostedAndNothingIsClaimed(string tool, object?[] arguments, string line)
    {
        var fake = Ghostty();
        var result = (TerminalChangeDto)(await InvokeAsync(TerminalProvider(fake), tool, arguments))!;
        Assert.Equal([line], Sent(fake, TerminalBridge.PostGate));
        Assert.Equal("post", result.Via);
        Assert.False(result.Completed);
        Assert.Null(result.Request);
        Assert.Contains("cannot be confirmed", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceGhosttyHasTheVerbNothingIsPostedAndTheOutcomeComesBack()
    {
        var polls = 0;
        var fake = Ghostty(new()
        {
            ["capture.shot"] = _ => Ok("""{"queued": true, "request": 77}"""),
            ["window.list"] = _ => ++polls < 3
                ? Ok(WindowList)
                : Ok("""{"rev": 10, "windows": [], "requests": [{"request": 77, "method": "capture.shot", "ok": true, "result": {"path": "~/shot.png", "width": 2560, "height": 1440}}]}"""),
        });
        var result = (TerminalChangeDto)(await InvokeAsync(TerminalProvider(fake), "capture_terminal_screenshot", ["full", false]))!;
        Assert.Empty(Sent(fake, TerminalBridge.PostGate));
        Assert.Equal("call", result.Via);
        Assert.True(result.Completed);
        Assert.Equal("77", result.Request);
        Assert.Equal(2560, result.Result!["width"]!.GetValue<int>());
        Assert.Equal(3, polls);
    }

    [Theory]
    [InlineData("rm -rf ~\nls", null, null)]
    [InlineData("htop", "bash", null)]
    [InlineData(null, null, "teleport 1 2")]
    [InlineData(null, null, "orbit far")]
    [InlineData(null, null, "hud 1 2 3 4")]
    public void OpenTerminalRefusesBadArgumentsBeforeSending(string? run, string? profile, string? pin) =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.OpenParameters(run, profile, pin, out _)).Code);

    [Fact]
    public void ArgumentsAreHeldToGhosttysStringRules()
    {
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => BridgeJson.CheckArgument("run", new string('a', 1025))).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => BridgeJson.CheckArgument("run", U.S("a<2028>b"))).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => BridgeJson.CheckArgument("run", "a\tb")).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => BridgeJson.CheckArgument("run", " ")).Code);
        Assert.Equal("yad --calendar", BridgeJson.CheckArgument("run", "  yad --calendar "));
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.CheckId(0)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.OrderParameters(4, "0")).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.OrderParameters(4, "up")).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.CheckSuites("all; reload")).Code);
        Assert.Equal(McpErrorCodes.Refused, Throws(() => TerminalBridge.CheckSuites("leakwatch on")).Code);
        Assert.Equal(["all"], TerminalBridge.CheckSuites(" "));
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => TerminalBridge.ParseRequest("12a")).Code);
    }

    // ---- reading --------------------------------------------------------------------------------------------

    [Fact]
    public void PanelsAreMergedFromBothSnapshots()
    {
        var list = Terminal(Ghostty()).ListPanels(50, 0);
        Assert.Equal(9, list.Rev);
        Assert.Equal(7, list.Total);
        Assert.False(list.Truncated);
        Assert.Equal([3L, 4, 5, 12, 13, 14, 15], list.Panels.Select(p => p.Id));

        var yad = list.Panels.Single(p => p.Id == 12);
        Assert.Equal(("window", "yad", "pet", "pet", "live", (long?)640, (long?)400, (long?)1), (yad.Kind, yad.App, yad.View, yad.Anchor, yad.State, yad.Width, yad.Height, yad.Order));
        var bash = list.Panels.Single(p => p.Id == 3);
        Assert.Equal(("terminal", "bash", (bool?)true, "tab", true), (bash.Kind, bash.Profile, bash.Running, bash.View, bash.Focused));
        Assert.Null(bash.Anchor);
        Assert.True(list.Panels.Single(p => p.Id == 15).Hidden);

        var page = Terminal(Ghostty()).ListPanels(2, 1);
        Assert.Equal([4L, 5], page.Panels.Select(p => p.Id));
        Assert.True(page.Truncated);
    }

    [Fact]
    public void TheLayoutIsReconstructedAndSaysWhatItCannotKnow()
    {
        var layout = Terminal(Ghostty()).GetLayout();
        Assert.Contains("no layout.get", layout.Note, StringComparison.Ordinal);
        Assert.Equal(7, layout.Total);
        Assert.Equal(2, layout.Reproducible);

        var htop = layout.Panels.Single(p => p.Id == 4);
        Assert.Equal(("pet", true, (long?)2), (htop.Pin, htop.Reproducible, htop.Order));
        Assert.Contains("terminal.new {profile: \"bash\", pin: \"pet\"}", htop.How, StringComparison.Ordinal);
        Assert.Contains("order_terminal_panel to 2", htop.How, StringComparison.Ordinal);

        var firefox = layout.Panels.Single(p => p.Id == 13);
        Assert.Equal(("orbit", false), (firefox.Pin, firefox.Reproducible));
        Assert.Contains("distance is not exposed", firefox.How, StringComparison.Ordinal);
        Assert.Contains("window.open {match: \"firefox\", pin: \"orbit\"}", firefox.How, StringComparison.Ordinal);

        Assert.Equal(("hud", false), (layout.Panels.Single(p => p.Id == 14).Pin, layout.Panels.Single(p => p.Id == 14).Reproducible));
        Assert.All(layout.Panels.Where(p => p.Id is 3 or 5 or 15), p => Assert.Null(p.Pin));
    }

    [Fact]
    public void AMalformedSnapshotYieldsNoPanelsRatherThanAnException()
    {
        var panels = TerminalBridge.MergePanels(JsonNode.Parse("""{"panels": [1, "x", {"id": "3"}, {"id": 4.5}, {"id": 6, "view": 7, "focused": "yes"}, {"id": 6}]}"""), JsonNode.Parse("[]"));
        var only = Assert.Single(panels);
        Assert.Equal((6L, "unknown", "unknown", false), (only.Id, only.Kind, only.View, only.Focused));
        Assert.Empty(TerminalBridge.MergePanels(null, null));
    }

    [Fact]
    public void RequestsAreLookedUpById()
    {
        var bridge = Terminal(Ghostty());
        var done = bridge.GetRequest("1726732800001");
        Assert.Equal(("done", "window.open", (bool?)true), (done.State, done.Method, done.Ok));
        Assert.Equal(12, done.Result!["id"]!.GetValue<int>());
        var failed = bridge.GetRequest("1726732800002");
        Assert.Equal(("failed", "pin: usage: /term pin here"), (failed.State, failed.Error));
        Assert.Equal("unknown", bridge.GetRequest("5").State);
    }

    [Fact]
    public void StatusGathersThreeReadsAndToleratesTheMissingVersionVerb()
    {
        var status = Terminal(Ghostty()).GetStatus();
        Assert.Equal("ghostty 3", status.Status);
        Assert.True(status.Agent!["windows_ok"]!.GetValue<bool>());
        Assert.Equal(12, status.Focus!["id"]!.GetValue<int>());
        Assert.Null(status.Api);

        var newer = Terminal(Ghostty(new() { ["api.version"] = _ => Ok("""{"version": 2, "verbs": ["layout.get"]}""") })).GetStatus();
        Assert.Equal(2, newer.Api!["version"]!.GetValue<int>());
    }

    // ---- the bounded wait -----------------------------------------------------------------------------------

    [Fact]
    public async Task AChangeReturnsThePanelIdWhenTheOutcomeArrivesInTime()
    {
        var game = new InlineGameThread();
        var fake = Ghostty(new() { ["terminal.new"] = _ => Ok("""{"queued": true, "request": 1726732800001}""") });
        var result = await Terminal(fake, game).ChangeAsync("terminal.new", null, default);
        Assert.Equal(("1726732800001", true, (long?)12), (result.Request, result.Completed, result.PanelId));
        Assert.Equal(2, game.Hops);
    }

    [Fact]
    public async Task AChangeGhosttyRanAndRefusedFailsWithItsReason()
    {
        var fake = Ghostty(new() { ["panel.place"] = _ => Ok("""{"queued": true, "request": 1726732800002}""") });
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Terminal(fake).ChangeAsync("panel.place", new JsonObject { ["id"] = 4, ["pin"] = "here" }, default));
        Assert.Contains("pin: usage: /term pin here", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeThatDoesNotRunWithinTwoSecondsHandsBackTheRequestId()
    {
        var waited = TimeSpan.Zero;
        var fake = Ghostty(new() { ["panel.close"] = _ => Ok("""{"queued": true, "request": 999}""") });
        var bridge = new TerminalBridge(fake, new InlineGameThread(), (d, _) =>
        {
            waited += d;
            return Task.CompletedTask;
        });
        var result = await bridge.ChangeAsync("panel.close", new JsonObject { ["id"] = 4 }, default);
        Assert.Equal(("999", false), (result.Request, result.Completed));
        Assert.Contains("get_terminal_request", result.Note, StringComparison.Ordinal);
        Assert.Equal(TerminalBridge.OutcomeWait, waited);
        Assert.True(waited <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ARefusalAtTheGateCarriesGhosttysWords()
    {
        var fake = Ghostty(new() { ["panel.close"] = _ => """{"ok":false,"error":"too many changes waiting"}""" });
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Terminal(fake).ChangeAsync("panel.close", new JsonObject { ["id"] = 4 }, default));
        Assert.Contains("too many changes waiting", ex.Message, StringComparison.Ordinal);
    }

    // ---- response parsing -----------------------------------------------------------------------------------

    [Fact]
    public void EnvelopesAreReadDefensively()
    {
        Assert.Equal(3, BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":true,"result":{"a":3}}""")!["a"]!.GetValue<int>());
        Assert.Null(BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":true}"""));
        Assert.Contains("refused m: nope", Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":false,"error":"nope"}""")).Message, StringComparison.Ordinal);
        Assert.Contains("no reason given", Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":false,"error":{"x":1}}""")).Message, StringComparison.Ordinal);
        Assert.Equal("G#m", Assert.Throws<BridgeCapabilityMissingException>(() => BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":false,"error":"unknown method: m"}""")).Capability);
        Assert.Contains("not JSON", Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", "ghostty 3 {")).Message, StringComparison.Ordinal);
        Assert.Contains("envelope", Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", "[1,2]")).Message, StringComparison.Ordinal);
        Assert.Contains("envelope", Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", """{"ok":"yes"}""")).Message, StringComparison.Ordinal);
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", " ")).Code);
        Assert.Contains("is not read", Throws(() => BridgeJson.ParseJson("S", "G", new string(' ', BridgeJson.MaxParseChars) + "1")).Message, StringComparison.Ordinal);
        Assert.Contains("not JSON", Throws(() => BridgeJson.ParseJson("S", "G", new string('[', 100) + new string(']', 100))).Message, StringComparison.Ordinal);

        var longError = "{\"ok\":false,\"error\":\"" + new string('e', 5000) + "\"}";
        Assert.True(Throws(() => BridgeJson.ParseEnvelope("S", "G", "m", longError)).Message.Length < 600);
    }

    [Fact]
    public void OversizedResultsAreCutWithAFlag()
    {
        var small = BridgeJson.Cap(JsonNode.Parse("""{"a":1}"""));
        Assert.False(small.Truncated);
        Assert.NotNull(small.Node);

        var big = BridgeJson.Cap(new JsonObject { ["text"] = U.Repeat("<00E9>", 40_000) });
        Assert.True(big.Truncated);
        Assert.Null(big.Node);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(big.RawPrefix!), 60_000, BridgeJson.MaxResultBytes);

        var fake = Ghostty(new() { ["theme.list"] = _ => Ok("{\"themes\":\"" + new string('t', 70_000) + "\"}") });
        var dto = Terminal(fake).ReadProposed("theme.list");
        Assert.True(dto.Truncated);
        Assert.Null(dto.Result);
    }

    [Fact]
    public void TextRepliesAreOkOrTheSiblingsError()
    {
        Assert.Equal("launched Text Editor (org.gnome.TextEditor)", BridgeJson.ParseTextReply("S", "G", "ok: launched Text Editor (org.gnome.TextEditor)"));
        Assert.Contains("no application matches", Throws(() => BridgeJson.ParseTextReply("S", "G", "error: no application matches")).Message, StringComparison.Ordinal);
        Assert.Contains("weird", Throws(() => BridgeJson.ParseTextReply("S", "G", "weird")).Message, StringComparison.Ordinal);
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => BridgeJson.ParseTextReply("S", "G", "")).Code);
    }

    // ---- XivDesktop -----------------------------------------------------------------------------------------

    private const string Apps = """
        [{"id":"org.mozilla.firefox","name":"Firefox","generic":"Web Browser","categories":["Network"],"favourite":true,"keywords":["internet"],"recent":0,"terminal":false,"launchable":true},
         {"id":"org.gnome.TextEditor","name":"Text Editor","generic":"","categories":["Utility"],"favourite":false,"keywords":[],"recent":null,"terminal":false,"launchable":true},
         {"id":"htop","name":"Htop","generic":"Process Viewer","categories":["System"],"favourite":false,"keywords":["top"],"terminal":true,"launchable":true},
         {"name":"no id"}, 7]
        """;

    private static FakeBridgeInvoker Desktop()
    {
        var fake = new FakeBridgeInvoker();
        fake.Gates[DesktopBridge.ListAppsGate] = _ => Apps;
        fake.Gates[DesktopBridge.StatusGate] = _ => """{"ghostty":true,"apps":3,"scanning":false,"summary":"3 apps"}""";
        fake.Gates[DesktopBridge.WindowsGate] = _ => """{"available":true,"rev":9,"workspace":1,"target":12,"windows":[]}""";
        fake.Gates[DesktopBridge.PaletteGate] = q => """[{"provider":"app","title":"Firefox"},{"provider":"calc","title":"2"},{"provider":"app","title":"x"}]""";
        fake.Gates[DesktopBridge.LaunchGate] = a => a == "nothing" ? "error: no application matches \"nothing\"" : "ok: launched Firefox (org.mozilla.firefox)";
        fake.Gates[DesktopBridge.WindowActionGate] = _ => "ok: queued";
        fake.Gates[DesktopBridge.WorkspaceGate] = n => "ok: workspace " + n;
        fake.Gates[DesktopBridge.AskGate] = _ => "ok: asked";
        return fake;
    }

    [Fact]
    public void AppsAreFilteredAndPagedHere()
    {
        var bridge = new DesktopBridge(Desktop());
        var all = bridge.ListApps(null, false, 2, 0);
        Assert.Equal((3, 3, true), (all.Total, all.Matched, all.Truncated));
        Assert.Equal(["org.mozilla.firefox", "org.gnome.TextEditor"], all.Apps.Select(a => a.Id));
        Assert.Equal(0, all.Apps[0].Recent);
        Assert.Null(all.Apps[1].Recent);
        Assert.Null(all.Apps[1].Generic);

        Assert.Equal(["htop"], bridge.ListApps("PROCESS", false, 50, 0).Apps.Select(a => a.Id));
        Assert.Equal(["org.mozilla.firefox"], bridge.ListApps("internet", false, 50, 0).Apps.Select(a => a.Id));
        Assert.Equal(["org.mozilla.firefox"], bridge.ListApps(null, true, 50, 0).Apps.Select(a => a.Id));
        Assert.Empty(bridge.ListApps(null, false, 50, 3).Apps);
    }

    [Theory]
    [InlineData("focus", 12L, null, 0, """{"action":"focus","id":12}""")]
    [InlineData("CLOSE", 0L, null, 0, """{"action":"close","id":0}""")]
    [InlineData("place", 12L, "orbit 3.5", 0, """{"action":"place","id":12,"pin":"orbit 3.5"}""")]
    [InlineData("move", 12L, null, 3, """{"action":"move","id":12,"workspace":3}""")]
    public void WindowActionsBuildExactlyThis(string action, long id, string? pin, int workspace, string expected) =>
        Assert.Equal(expected, DesktopBridge.WindowActionJson(action, id, pin, workspace));

    [Theory]
    [InlineData("minimize", 1L, null, 0)]
    [InlineData("place", 1L, null, 0)]
    [InlineData("focus", 1L, "here", 0)]
    [InlineData("move", 1L, null, 0)]
    [InlineData("move", 1L, null, 10)]
    [InlineData("focus", 1L, null, 2)]
    [InlineData("focus", -1L, null, 0)]
    public void WindowActionsOffTheContractAreRefused(string action, long id, string? pin, int workspace) =>
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => DesktopBridge.WindowActionJson(action, id, pin, workspace)).Code);

    [Fact]
    public void DesktopChangesSendTheArgumentAndReadTheReply()
    {
        var fake = Desktop();
        var bridge = new DesktopBridge(fake);
        Assert.Equal("launched Firefox (org.mozilla.firefox)", bridge.Launch(" org.mozilla.firefox ").Message);
        Assert.Contains("no application matches", Throws(() => bridge.Launch("nothing")).Message, StringComparison.Ordinal);
        Assert.Equal("workspace 3", bridge.SwitchWorkspace(3).Message);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => bridge.SwitchWorkspace(0)).Code);
        bridge.Ask("as npc:1000 where is the market board?");
        bridge.Ask("");
        bridge.WindowAction("pet", 12, null, 0);

        Assert.Equal(["org.mozilla.firefox", "nothing"], Sent(fake, DesktopBridge.LaunchGate));
        Assert.Equal(["3"], Sent(fake, DesktopBridge.WorkspaceGate));
        Assert.Equal(["as npc:1000 where is the market board?", ""], Sent(fake, DesktopBridge.AskGate));
        Assert.Equal(["""{"action":"pet","id":12}"""], Sent(fake, DesktopBridge.WindowActionGate));
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => bridge.Ask("a\nb")).Code);
    }

    [Fact]
    public void DesktopReadsAreCappedAndStatusToleratesTheMissingVersionGate()
    {
        var fake = Desktop();
        var bridge = new DesktopBridge(fake);
        var palette = bridge.SearchPalette("fire", 2);
        Assert.Equal(2, palette.Result!.AsArray().Count);
        Assert.True(palette.Truncated);
        Assert.Equal(["fire"], Sent(fake, DesktopBridge.PaletteGate));

        Assert.Null(bridge.GetStatus().Result!["apiVersion"]);
        fake.IntGates[SiblingIpcProposals.DesktopApiVersionGate] = 1;
        Assert.Equal(1, bridge.GetStatus().Result!["apiVersion"]!.GetValue<int>());

        fake.Gates[DesktopBridge.WindowsGate] = _ => "{}{";
        Assert.Contains("not JSON", Throws(() => bridge.ReadJson(DesktopBridge.WindowsGate)).Message, StringComparison.Ordinal);
    }

    // ---- Almanac --------------------------------------------------------------------------------------------

    [Fact]
    public void AlmanacIsInTheCatalog()
    {
        var almanac = BridgeCatalog.Find("almanac")!;
        Assert.Equal(("Almanac.ApiVersion", BridgeProbeKind.Int), (almanac.ProbeGate, almanac.ProbeKind));
        Assert.Contains("Almanac", almanac.InternalNames);
        var enumValues = typeof(BridgeProvider).GetMethod(nameof(BridgeProvider.GetBridgeState))!.GetParameters()[0].GetCustomAttribute<McpParamAttribute>()!.Enum!;
        Assert.Equal(BridgeCatalog.Keys.Order(), enumValues.Order());
    }

    [Fact]
    public void TodaysAlmanacTakesAQuestionThroughItsBoolGate()
    {
        var fake = new FakeBridgeInvoker();
        fake.BoolGates[AlmanacBridge.AskGate] = q => q != "busy";
        var bridge = new AlmanacBridge(fake);

        var asked = bridge.Ask(" where do I unlock Eureka? ", false);
        Assert.Equal((true, "ask"), (asked.Accepted, asked.Via));
        Assert.Null(asked.ThreadId);
        Assert.Contains("chat window", asked.Note, StringComparison.Ordinal);
        Assert.Equal("where do I unlock Eureka?", fake.Calls.Last().Argument);
        Assert.Equal("""{"method":"ask","caller":"XivMcp","params":{"text":"where do I unlock Eureka?"}}""", Sent(fake, AlmanacBridge.CallGate).Single());

        Assert.False(bridge.Ask("busy", false).Accepted);
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => bridge.Ask("x", newThread: true)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => bridge.Ask("", false)).Code);

        var status = bridge.GetStatus();
        Assert.Equal((true, false), (status.AskGate, status.CallGate));
    }

    [Fact]
    public void ANewerAlmanacAnswersThroughTheJsonGate()
    {
        var fake = new FakeBridgeInvoker();
        fake.Gates[AlmanacBridge.CallGate] = raw => JsonNode.Parse(raw!)!["method"]!.GetValue<string>() switch
        {
            "thread.new" => Ok("""{"threadId":"t_9"}"""),
            "ask" => Ok("""{"accepted":true,"threadId":"t_9","turn":1}"""),
            "bench.status" => Ok("""{"runId":"b_1","state":"running","done":5,"total":24}"""),
            "model.status" => Ok("""{"model":"m","baseUrlHost":"localhost:11434"}"""),
            var m => "{\"ok\":false,\"error\":\"unknown method: " + m + "\"}",
        };
        var bridge = new AlmanacBridge(fake);

        var asked = bridge.Ask("hello", newThread: true);
        Assert.Equal((true, "call", "t_9", (long?)1), (asked.Accepted, asked.Via, asked.ThreadId, asked.Turn));
        Assert.Equal(
            ["""{"method":"thread.new","caller":"XivMcp"}""", """{"method":"ask","caller":"XivMcp","params":{"text":"hello","threadId":"t_9"}}"""],
            Sent(fake, AlmanacBridge.CallGate));
        Assert.DoesNotContain(fake.Calls, c => c.Gate == AlmanacBridge.AskGate);

        Assert.Equal("running", bridge.GetBenchmark("b_1").Result!["state"]!.GetValue<string>());
        Assert.EndsWith("#bench.status", bridge.GetBenchmark("b_1").Capability, StringComparison.Ordinal);
        Assert.Null(bridge.GetModelStatus().Backends);
        Assert.True(bridge.GetStatus().CallGate);
    }

    [Theory]
    [InlineData("mock", null, null, """{"mode":"mock"}""")]
    [InlineData("LIVE", "qwen3:8b", "t1, t2 t1", """{"mode":"live","model":"qwen3:8b","tasks":["t1","t2"]}""")]
    public void BenchmarkRequestsBuildExactlyThis(string mode, string? model, string? tasks, string expected) =>
        Assert.Equal(expected, AlmanacBridge.BenchmarkParameters(mode, model, tasks).ToJsonString());

    [Fact]
    public void BenchmarkResultsAreNeverSubmittedAnywhere()
    {
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => AlmanacBridge.BenchmarkParameters("submit", null, null)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Throws(() => AlmanacBridge.BenchmarkParameters("mock", null, "a;b")).Code);
        Assert.DoesNotContain(SiblingIpcProposals.All, p => (p.Verb ?? p.Gate).Contains("submit", StringComparison.OrdinalIgnoreCase) || (p.Verb ?? "").Contains("upload", StringComparison.OrdinalIgnoreCase));
    }

    // ---- proposals ------------------------------------------------------------------------------------------

    [Fact]
    public void TheProposalsTableAndTheToolsAgree()
    {
        var bySource = SiblingIpcProposals.All.ToDictionary(p => p.Source, StringComparer.Ordinal);
        Assert.Equal(SiblingIpcProposals.All.Count, bySource.Count);

        // Every proposed-source constant names exactly one row, and every row has a constant.
        var constants = typeof(SiblingIpcProposals).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && ((string)f.GetRawConstantValue()!).StartsWith("ipc:", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(bySource.Keys.Order(StringComparer.Ordinal), constants.Order(StringComparer.Ordinal));

        var tools = ToolMethods().ToDictionary(t => t.Tool.Name, t => t.Tool, StringComparer.Ordinal);
        foreach (var proposal in SiblingIpcProposals.All)
        {
            Assert.NotEmpty(proposal.Tools);
            Assert.NotNull(BridgeCatalog.Find(proposal.Sibling));
            Assert.All([proposal.Signature, proposal.Request, proposal.Response, proposal.Threading, proposal.Why], s => Assert.False(string.IsNullOrWhiteSpace(s)));
            foreach (var name in proposal.Tools)
            {
                Assert.True(tools.TryGetValue(name, out var tool), $"{proposal.Capability} names the tool {name}, which does not exist");
                Assert.Contains(proposal.Source, tool!.Sources!);
                if (proposal.Kind == SiblingIpcKind.Change)
                    Assert.True(tool.NeedsApproval, $"{name} calls the change {proposal.Capability} and must be gated");
            }
        }

        // The other way round: a tool that claims a proposed source is listed by that row.
        foreach (var (name, tool) in tools)
        {
            foreach (var source in tool.Sources!.Where(bySource.ContainsKey))
                Assert.Contains(name, bySource[source].Tools);
        }

        // Nothing proposed exists today under the same name.
        Assert.DoesNotContain(SiblingIpcProposals.All, p => p.Verb is not null && p.Gate == TerminalBridge.CallGate &&
            (TerminalBridge.ReadVerbs.Contains(p.Verb) || TerminalBridge.ChangeVerbs.Contains(p.Verb)));
    }

    [Fact]
    public async Task EveryToolWrittenOnlyAgainstProposedIpcIsUnavailableUntilTheSiblingShipsIt()
    {
        var proposed = SiblingIpcProposals.All.Select(p => p.Source).ToHashSet(StringComparer.Ordinal);
        var pure = ToolMethods().Where(t => t.Tool.Sources!.All(proposed.Contains)).ToList();
        Assert.True(pure.Count >= 14, $"only {pure.Count} proposal-only tools found");

        foreach (var (tool, method) in pure)
        {
            // Today's siblings: GhosttyDalamud answers "unknown method"; XivDesktop and Almanac do not register the gates.
            object provider = method.DeclaringType == typeof(TerminalBridgeProvider) ? TerminalProvider(Ghostty())
                : method.DeclaringType == typeof(DesktopAppsBridgeProvider) ? With(new DesktopAppsBridgeProvider(Pi(), Log()), new DesktopBridge(Desktop()))
                : With(new AlmanacBridgeProvider(Pi(), Log()), new AlmanacBridge(new FakeBridgeInvoker()));
            var arguments = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? "x" : Convert.ChangeType(1, p.ParameterType, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            var ex = await Assert.ThrowsAsync<McpToolException>(() => InvokeAsync(provider, tool.Name, arguments));
            Assert.True(ex.Code == McpErrorCodes.Unavailable, $"{tool.Name}: expected unavailable, got {ex.Code}: {ex.Message}");
            var rows = SiblingIpcProposals.All.Where(p => tool.Sources!.Contains(p.Source));
            Assert.Contains(rows, row => ex.Message.Contains(row.Verb ?? row.Gate, StringComparison.Ordinal));
            Assert.Contains("yet", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnAbsentSiblingIsUnavailableBeforeAnythingIsSent()
    {
        var fake = Ghostty();
        fake.Loaded = false;
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => Terminal(fake).ListPanels(50, 0)).Code);
        Assert.Equal(McpErrorCodes.Unavailable, (await Assert.ThrowsAsync<McpToolException>(() => Terminal(fake).ChangeAsync("terminal.new", null, default))).Code);
        Assert.Equal(McpErrorCodes.Unavailable, (await Assert.ThrowsAsync<McpToolException>(
            () => Terminal(fake).ChangeOrPostAsync("capture.shot", null, "shot", "", default))).Code);
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => new DesktopBridge(fake).Launch("x")).Code);
        Assert.Equal(McpErrorCodes.Unavailable, Throws(() => new AlmanacBridge(fake).Ask("x", false)).Code);
        Assert.False(new AlmanacBridge(fake).GetStatus().AskGate);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void AnOlderGhosttyWithoutTheCallGateOrThePanelVerbsSaysSo()
    {
        var noGate = new FakeBridgeInvoker();
        Assert.Contains("not registered", Throws(() => Terminal(noGate).ListPanels(50, 0)).Message, StringComparison.Ordinal);
        var noPanels = Ghostty(new() { ["panel.list"] = _ => """{"ok":false,"error":"unknown method: panel.list"}""" });
        var ex = Throws(() => Terminal(noPanels).ListPanels(50, 0));
        Assert.Equal(McpErrorCodes.Unavailable, ex.Code);
        Assert.Contains("panel.list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsThatRunOnTheOwnersComputerAreShownVerbatim()
    {
        var tools = ToolMethods().ToDictionary(t => t.Tool.Name, t => t.Tool, StringComparer.Ordinal);
        var open = tools["open_terminal"];
        Assert.Contains("Run this command on your computer in a new terminal: {run}", open.ApprovalSummary!, StringComparison.Ordinal);
        Assert.Equal((false, false, false), (open.Destructive, open.Idempotent, open.OpenWorld));
        Assert.Contains("Launch this application on your computer: {app}", tools["launch_desktop_app"].ApprovalSummary!, StringComparison.Ordinal);
        Assert.Contains("{text}", tools["ask_npc_assistant"].ApprovalSummary!, StringComparison.Ordinal);
        Assert.Contains("{question}", tools["ask_almanac"].ApprovalSummary!, StringComparison.Ordinal);
        Assert.Contains("{path}", tools["share_terminal_capture"].ApprovalSummary!, StringComparison.Ordinal);
        Assert.True(tools["share_terminal_capture"].OpenWorld);
        Assert.True(tools["close_terminal_panel"].Destructive);
        Assert.Contains("{mode}", tools["run_almanac_benchmark"].ApprovalSummary!, StringComparison.Ordinal);
        Assert.Contains("{model}", tools["run_almanac_benchmark"].ApprovalSummary!, StringComparison.Ordinal);
    }

    // ---- plumbing -------------------------------------------------------------------------------------------

    private static IDalamudPluginInterface Pi() => FakeProxy.Create<IDalamudPluginInterface>();

    private static IPluginLog Log() => FakeProxy.Create<IPluginLog>();

    private static TerminalBridgeProvider TerminalProvider(FakeBridgeInvoker fake) =>
        With(new TerminalBridgeProvider(Pi(), Log(), new InlineGameThread()), Terminal(fake));

    /// <summary>Providers have one constructor (the one Dalamud's injection uses), so tests swap the engine behind it.</summary>
    private static T With<T>(T provider, object engine)
        where T : class
    {
        typeof(T).GetField("bridge", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(provider, engine);
        return provider;
    }

    private static IEnumerable<(McpToolAttribute Tool, MethodInfo Method)> ToolMethods() =>
        new[] { typeof(TerminalBridgeProvider), typeof(DesktopAppsBridgeProvider), typeof(AlmanacBridgeProvider) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => (Tool: m.GetCustomAttribute<McpToolAttribute>()!, Method: m))
            .Where(x => x.Tool is not null);

    private static async Task<object?> InvokeAsync(object provider, string tool, object?[] arguments)
    {
        var method = provider.GetType().GetMethods().Single(m => m.GetCustomAttribute<McpToolAttribute>()?.Name == tool);
        var parameters = method.GetParameters();
        var bound = parameters.Select((p, i) => i < arguments.Length ? arguments[i] : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        try
        {
            var result = method.Invoke(provider, bound);
            if (result is Task task)
            {
                await task;
                return task.GetType().GetProperty("Result")!.GetValue(task);
            }

            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
