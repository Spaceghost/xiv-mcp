using System.ComponentModel;
using System.Text.Json.Nodes;

namespace XivMcp.Core.Tests.Infrastructure;

public enum Colour
{
    Red,
    DarkBlue,
    LightGreen,
}

public sealed record Vec3([property: Description("East-west")] float X, float Y, float Z);

public sealed record Waymark(string Name, Vec3 Position, Colour Colour, int? Order, string? Note, List<string> Tags);

public sealed record CountResult(int Count, bool Cancelled);

public sealed record ThreadResult(bool OnGameThread);

[McpProvider("basic")]
public sealed class BasicProvider
{
    private readonly FakeGameThread _game;

    public BasicProvider(FakeGameThread game) => _game = game;

    public static readonly List<string> Calls = [];

    [McpTool("echo", Title = "Echo", Description = "Echoes text.", GameThread = false, RequiresLogin = false)]
    public string Echo([McpParam("Text to echo")] string text) => text;

    [McpTool("add", Description = "Adds two integers.", GameThread = false, RequiresLogin = false)]
    public long Add(int a, int b) => (long)a + b;

    [McpTool("on_game_thread", Description = "Reports the thread.")]
    public ThreadResult OnGameThread() => new(_game.IsOnGameThread);

    [McpTool("off_game_thread", Description = "Reports the thread.", GameThread = false)]
    public ThreadResult OffGameThread() => new(_game.IsOnGameThread);

    [McpTool("make_waymark", Description = "Builds a waymark record.", GameThread = false, RequiresLogin = false)]
    public Waymark MakeWaymark(
        string name,
        Vec3 position,
        Colour colour = Colour.DarkBlue,
        int? order = null,
        [McpParam("Tags", Enum = ["a", "b", "c"])] List<string>? tags = null) =>
        new(name, position, colour, order, null, tags ?? []);

    [McpTool("list_numbers", Description = "Returns numbers 1..count.", GameThread = false, RequiresLogin = false)]
    public int[] ListNumbers([McpParam("How many", Minimum = 0, Maximum = 50)] int count = 3) => Enumerable.Range(1, count).ToArray();

    [McpTool("maybe_waymark", Description = "Nullable object result.", GameThread = false, RequiresLogin = false)]
    public Waymark? MaybeWaymark(bool give) => give ? new Waymark("w", new Vec3(1, 2, 3), Colour.Red, 1, "n", ["a"]) : null;

    [McpTool("nothing", Description = "Void tool.", GameThread = false, RequiresLogin = false)]
    public void Nothing()
    {
    }

    [McpTool("explicit_result", Description = "Returns a ToolResult.", GameThread = false, RequiresLogin = false)]
    public ToolResult ExplicitResult(bool error) => new()
    {
        Content = [ContentBlock.FromText(error ? "explicit failure" : "explicit ok"), ContentBlock.FromImage([1, 2, 3], "image/png")],
        StructuredContent = new JsonObject { ["ok"] = !error },
        IsError = error,
    };

    [McpTool("user_error", Description = "Throws McpToolException.", GameThread = false, RequiresLogin = false)]
    public string UserError() => throw new McpToolException("Item 12345 not found");

    [McpTool("crash", Description = "Throws.", RequiresLogin = false)]
    public string Crash() => throw new InvalidOperationException("boom");

    [McpTool("needs_login", Description = "Requires login.")]
    public string NeedsLogin() => "logged in";

    [McpTool("async_needs_login", Description = "Requires login, async, off thread.", GameThread = false)]
    public async Task<string> AsyncNeedsLogin()
    {
        await Task.Yield();
        return "ok";
    }

    [McpTool("pick", Description = "Enum argument.", GameThread = false, RequiresLogin = false)]
    public string Pick(Colour colour, [McpParam("Mode", Enum = ["fast", "slow"])] string mode = "fast") => $"{colour}/{mode}";

    [McpTool("numbers", Description = "Numeric conversions.", GameThread = false, RequiresLogin = false)]
    public string Numbers(byte small, uint big, double real, [McpParam("pct", Minimum = 0, Maximum = 100)] int percent = 50) =>
        $"{small}/{big}/{real}/{percent}";
}

[McpProvider("slow")]
public sealed class SlowProvider
{
    public static TaskCompletionSource<bool> CancelObserved { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource<bool> Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        CancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [McpTool("wait_for_cancel", Description = "Waits until cancelled.", GameThread = false, RequiresLogin = false)]
    public async Task<CountResult> WaitForCancel(CancellationToken cancellationToken)
    {
        Started.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancelObserved.TrySetResult(true);
            throw;
        }

        return new CountResult(0, false);
    }

    [McpTool("hang", Description = "Ignores cancellation and never finishes.", GameThread = false, RequiresLogin = false)]
    public async Task<string> Hang()
    {
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        return "never";
    }

    [McpTool("progress", Description = "Reports progress and logs.", GameThread = false, RequiresLogin = false)]
    public async Task<CountResult> Progress(int steps, ToolContext context)
    {
        for (var i = 1; i <= steps; i++)
        {
            await context.ReportProgress(i, steps, $"step {i}");
            await context.ReportProgress(i - 0.5, steps, "going backwards is dropped");
            context.Notifier.Log(McpLogLevel.Debug, "progress", new { step = i });
            context.Notifier.Log(McpLogLevel.Warning, "progress", $"warn {i}");
            await Task.Delay(20);
        }

        return new CountResult(steps, false);
    }

    [McpTool("sleep", Description = "Sleeps.", GameThread = false, RequiresLogin = false)]
    public async Task<int> Sleep(int ms, CancellationToken cancellationToken)
    {
        await Task.Delay(ms, cancellationToken);
        return ms;
    }
}

[McpProvider("gated")]
public sealed class GatedProvider
{
    [McpTool("do_action", Description = "Action tier.", Permission = ToolPermission.Action, GameThread = false, RequiresLogin = false, Idempotent = false)]
    public string DoAction() => "acted";

    [McpTool("say_hello", Description = "Chat tier.", Permission = ToolPermission.Chat, GameThread = false, RequiresLogin = false, OpenWorld = true, Destructive = true)]
    public string SayHello() => "said";

    [McpTool("gated_read", Description = "Read tier in the gated category.", GameThread = false, RequiresLogin = false)]
    public string GatedRead() => "read";

    [McpResource("test://gated", Name = "gated", GameThread = false, RequiresLogin = false)]
    public string GatedResource() => "gated";
}

[McpProvider("data")]
public sealed class DataProvider
{
    [McpResource("test://text", Name = "text", Description = "Plain text.", MimeType = "text/plain", GameThread = false, RequiresLogin = false)]
    public string Text() => "hello";

    [McpResource("test://json", Name = "json", Description = "JSON object.")]
    public Vec3 Json() => new(1, 2, 3);

    [McpResource("test://blob", Name = "blob", MimeType = "application/octet-stream", GameThread = false, RequiresLogin = false)]
    public byte[] Blob() => [0, 1, 2, 255];

    [McpResource("test://login", Name = "login", RequiresLogin = true)]
    public string Login() => "secret";

    [McpResourceTemplate("test://item/{id}", Name = "item", Description = "Item by id.", GameThread = false)]
    public Vec3 Item(uint id) => id == 404 ? throw new McpToolException($"Item {id} not found") : new Vec3(id, 0, 0);

    [McpResourceTemplate("test://colour/{colour}/{+rest}", Name = "colour", MimeType = "text/plain", GameThread = false)]
    public string ColourResource(Colour colour, string rest) => $"{colour}:{rest}";

    [McpPrompt("greet", Title = "Greeting", Description = "Greets someone.")]
    public string Greet([McpParam("Who")] string name, Colour colour = Colour.Red) => $"Hello {name} in {colour}";

    [McpPrompt("structured", Description = "Structured prompt.")]
    public PromptResult Structured(int count) => new()
    {
        Description = "structured",
        Messages = [PromptMessage.User($"count={count}"), PromptMessage.Assistant("ok")],
    };
}
