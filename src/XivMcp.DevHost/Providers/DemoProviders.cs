using System.ComponentModel;
using XivMcp.Core;

namespace XivMcp.DevHost.Providers;

public enum DemoJob
{
    Paladin,
    Warrior,
    WhiteMage,
    BlackMage,
    Dancer,
}

public enum ItemSort
{
    Name,
    ItemLevel,
    Quantity,
}

public sealed record WorldPosition([property: Description("East-west world coordinate")] float X, float Y, float Z);

public sealed record MapCoordinates(double X, double Y);

public sealed record PlayerInfo(
    string Name,
    string World,
    DemoJob Job,
    int Level,
    string ContentId,
    uint EntityId,
    WorldPosition Position,
    MapCoordinates Map,
    string? Title,
    IReadOnlyList<string> Statuses);

public sealed record PartyMember(string Name, DemoJob Job, int Level, uint CurrentHp, uint MaxHp);

public sealed record ItemSummary(uint ItemId, string Name, int ItemLevel, int Quantity, bool HighQuality);

public sealed record ItemPage(IReadOnlyList<ItemSummary> Items, int Total, int Offset, bool Truncated);

public sealed record ThreadReport(bool OnGameThread, string ThreadName, long Frame);

/// <summary>Fake "character" data. Everything runs on the simulated framework thread.</summary>
[McpProvider("character")]
public sealed class DemoCharacterProvider
{
    private readonly SimulatedFrameworkThread _framework;

    public DemoCharacterProvider(SimulatedFrameworkThread framework) => _framework = framework;

    private static readonly PlayerInfo Player = new(
        "Wyn Starling", "Twintania", DemoJob.WhiteMage, 100, "18014398509481985", 0x10A3F2C1,
        new WorldPosition(12.5f, 0.02f, -44.25f), new MapCoordinates(11.2, 10.8), null, ["Medica II", "Swiftcast"]);

    [McpTool("get_player", Title = "Local player", Description =
        "Returns the logged-in character: name, home world, current job and level, content id (string), " +
        "entity id, world position and in-game map coordinates, title and active statuses. Use it first to learn who is playing.")]
    public PlayerInfo GetPlayer()
    {
        if (!_framework.IsOnGameThread)
            throw new InvalidOperationException("get_player must run on the framework thread");
        return Player;
    }

    [McpTool("get_party", Title = "Party list", Description = "Returns the members of the current party (empty list when solo), with job, level and HP.")]
    public List<PartyMember> GetParty() =>
    [
        new("Wyn Starling", DemoJob.WhiteMage, 100, 98_000, 98_000),
        new("Garrick Stone", DemoJob.Paladin, 100, 140_210, 151_000),
    ];

    [McpResource("ffxiv://player", Name = "player", Description = "The logged-in character as JSON (same data as get_player).")]
    public PlayerInfo PlayerResource() => Player;

    [McpResourceTemplate("ffxiv://job/{job}", Name = "job", Description = "Short description of a job.", MimeType = "text/plain", GameThread = false)]
    public string JobResource(DemoJob job) => $"{job}: a fine job for a fine adventurer.";

    [McpPrompt("plan_session", Title = "Plan a play session", Description = "Asks the model to plan a play session for a job.")]
    public string PlanSession(
        [McpParam("Job to plan for")] DemoJob job,
        [McpParam("What the player wants to achieve")] string? goal = null) =>
        $"I am playing {job}. Plan a 2-hour session" + (string.IsNullOrWhiteSpace(goal) ? "." : $" focused on: {goal}.");
}

[McpProvider("inventory")]
public sealed class DemoInventoryProvider
{
    private static readonly ItemSummary[] Items = Enumerable.Range(1, 120)
        .Select(i => new ItemSummary((uint)(5000 + i), $"Demo Item {i:000}", 500 + i % 300, i % 99 + 1, i % 7 == 0))
        .ToArray();

    [McpTool("search_items", Title = "Search inventory", Description =
        "Searches the player's inventory by case-insensitive name substring. Returns a page of items " +
        "(itemId, name, itemLevel, quantity, highQuality), the total match count and truncated=true when more pages exist.")]
    public ItemPage SearchItems(
        [McpParam("Substring of the item name; empty matches everything")] string query = "",
        [McpParam("Maximum items to return", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Number of matches to skip", Minimum = 0)] int offset = 0,
        [McpParam("Sort order")] ItemSort sort = ItemSort.Name)
    {
        IEnumerable<ItemSummary> matches = Items.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        matches = sort switch
        {
            ItemSort.ItemLevel => matches.OrderByDescending(i => i.ItemLevel),
            ItemSort.Quantity => matches.OrderByDescending(i => i.Quantity),
            _ => matches.OrderBy(i => i.Name, StringComparer.Ordinal),
        };
        var all = matches.ToArray();
        var page = all.Skip(offset).Take(limit).ToArray();
        return new ItemPage(page, all.Length, offset, offset + page.Length < all.Length);
    }

    [McpTool("get_item", Title = "Item by id", Description = "Returns one inventory item by its item id. Fails with a clear message when the id is not in the inventory.")]
    public ItemSummary GetItem([McpParam("Item row id, e.g. 5007")] uint itemId) =>
        Items.FirstOrDefault(i => i.ItemId == itemId) ?? throw new McpToolException($"Item {itemId} not found in the inventory.");

    [McpResourceTemplate("ffxiv://item/{itemId}", Name = "item", Description = "One inventory item as JSON.")]
    public ItemSummary ItemResource(uint itemId) =>
        Items.FirstOrDefault(i => i.ItemId == itemId) ?? throw new McpToolException($"Item {itemId} not found");
}

public enum FailureMode
{
    ToolError,
    Exception,
    Hang,
}

/// <summary>Exercises every server feature: progress, logs, cancellation, timeouts, gates, notifications, content types.</summary>
[McpProvider("diagnostics")]
public sealed class DemoDiagnosticsProvider
{
    private readonly SimulatedFrameworkThread _framework;
    private readonly DevHostState _host;

    public DemoDiagnosticsProvider(SimulatedFrameworkThread framework, DevHostState host)
    {
        _framework = framework;
        _host = host;
    }

    [McpTool("echo", Description = "Returns the given text unchanged (as the result string). Use to test connectivity.", GameThread = false, RequiresLogin = false)]
    public string Echo([McpParam("Text to echo back")] string text) => text;

    [McpTool("sum", Description = "Adds a list of numbers and returns the sum.", GameThread = false, RequiresLogin = false)]
    public double Sum([McpParam("Numbers to add")] double[] numbers) => numbers.Sum();

    [McpTool("thread_info", Description = "Reports whether the call ran on the (simulated) framework thread and the current frame number.")]
    public ThreadReport ThreadInfo() => new(_framework.IsOnGameThread, Thread.CurrentThread.Name ?? "?", _framework.FrameCount);

    [McpTool("async_frames", Description =
        "Async tool that does not run on the framework thread itself but hops onto it once per frame to count frames. Returns frames observed.",
        GameThread = false, RequiresLogin = false)]
    public async Task<ThreadReport> AsyncFrames([McpParam("Frames to wait", Minimum = 1, Maximum = 600)] int frames, ToolContext context)
    {
        var start = await context.Game.InvokeAsync(() => _framework.FrameCount, context.CancellationToken);
        long now = start;
        while (now - start < frames)
            now = await context.Game.InvokeAsync(() => _framework.FrameCount, context.CancellationToken);
        return new ThreadReport(_framework.IsOnGameThread, "thread-pool", now);
    }

    [McpTool("slow_count", Description =
        "Counts from 1 to count, waiting delayMs between steps. Emits notifications/progress (when the request has a progressToken) " +
        "and notifications/message log lines (when logging is enabled). Honors cancellation. Returns the final count.",
        GameThread = false, RequiresLogin = false)]
    public async Task<int> SlowCount(
        [McpParam("How far to count", Minimum = 1, Maximum = 100)] int count,
        ToolContext context,
        CancellationToken cancellationToken,
        [McpParam("Delay between steps in milliseconds", Minimum = 0, Maximum = 10_000)] int delayMs = 100)
    {
        for (var i = 1; i <= count; i++)
        {
            await Task.Delay(delayMs, cancellationToken);
            await context.ReportProgress(i, count, $"counted {i} of {count}");
            context.Notifier.Log(McpLogLevel.Info, "slow_count", new { step = i });
        }

        return count;
    }

    [McpTool("fail", Description = "Fails on purpose: toolError (McpToolException), exception (unexpected crash) or hang (never returns; hits the call timeout).", GameThread = false, RequiresLogin = false)]
    public async Task<string> Fail([McpParam("How to fail")] FailureMode mode, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case FailureMode.ToolError:
                throw new McpToolException("This is a user-facing failure; try a different argument.");
            case FailureMode.Exception:
                throw new InvalidOperationException("simulated crash");
            default:
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
                return "unreachable";
        }
    }

    [McpTool("set_logged_in", Description = "Dev host only: simulates logging in or out.", Permission = ToolPermission.Ui, RequiresLogin = false, GameThread = false)]
    public string SetLoggedIn(bool loggedIn)
    {
        _host.LoggedIn = loggedIn;
        return loggedIn ? "logged in" : "logged out";
    }

    [McpTool("set_permission", Description = "Dev host only: enables or disables a permission tier (tools/list changes, list_changed is emitted).",
        Permission = ToolPermission.Ui, RequiresLogin = false, GameThread = false)]
    public string SetPermission(ToolPermission tier, bool enabled, ToolContext context)
    {
        if (tier is ToolPermission.Read or ToolPermission.Ui && !enabled)
            throw new McpToolException("Refusing to disable the tiers this tool itself needs.");
        _host.SetPermission(tier, enabled);
        context.Notifier.ToolListChanged();
        return $"{tier} is now {(enabled ? "enabled" : "disabled")}";
    }

    [McpTool("touch_resource", Description = "Dev host only: emits notifications/resources/updated for the given URI.", Permission = ToolPermission.Ui, RequiresLogin = false, GameThread = false)]
    public string TouchResource(string uri, ToolContext context)
    {
        context.Notifier.ResourceUpdated(uri);
        return "notified " + uri;
    }

    [McpTool("render_swatch", Description = "Returns a 1x1 PNG image content block of the given colour (explicit ToolResult).", GameThread = false, RequiresLogin = false)]
    public ToolResult RenderSwatch([McpParam("Colour name", Enum = ["red", "green", "blue"])] string colour)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        return new ToolResult { Content = [ContentBlock.FromText($"A {colour} swatch:"), ContentBlock.FromImage(png, "image/png")] };
    }

    [McpTool("place_marker", Description = "Places a (pretend) map marker at a world position. Action tier.", Permission = ToolPermission.Action, Idempotent = false)]
    public string PlaceMarker(WorldPosition position, string? label = null) => $"marker '{label ?? "unnamed"}' at {position.X}/{position.Y}/{position.Z}";

    [McpTool("say", Description = "Says something in /say (pretend). Chat tier: other players would see it.", Permission = ToolPermission.Chat, OpenWorld = true, Idempotent = false)]
    public void Say(string text)
    {
    }

    [McpResource("ffxiv://motd", Name = "motd", Description = "Message of the day (plain text).", MimeType = "text/plain", GameThread = false, RequiresLogin = false)]
    public string Motd() => "Welcome to the xiv-mcp dev host.";

    [McpResource("ffxiv://swatch.png", Name = "swatch", Description = "A 1x1 PNG (binary resource).", MimeType = "image/png", GameThread = false, RequiresLogin = false)]
    public byte[] Swatch() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [McpPrompt("explain_item", Description = "Asks the model to explain an item.")]
    public PromptResult ExplainItem([McpParam("Item id")] uint itemId) => new()
    {
        Description = "Explain an inventory item",
        Messages = [PromptMessage.User($"Read ffxiv://item/{itemId} and explain what the item is for.")],
    };
}
