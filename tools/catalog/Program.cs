using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XivMcp.Core;

const string Begin = "<!-- BEGIN GENERATED CATALOG: dotnet run --project tools/catalog -- readme --write README.md -->";
const string End = "<!-- END GENERATED CATALOG -->";

var mode = args.FirstOrDefault() ?? "--help";
string? pluginPath = null, write = null;
var port = 41812;
for (var i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--plugin": pluginPath = Next(); break;
        case "--write": write = Next(); break;
        case "--port": port = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

if (mode is not ("readme" or "serve" or "json" or "docs" or "all" or "check"))
{
    Console.WriteLine("""
        xiv-mcp-catalog readme [--plugin XivMcp.dll] [--write README.md]
            Print the tool/resource/prompt catalog as Markdown, or replace the generated block in a README.
        xiv-mcp-catalog json [--plugin XivMcp.dll] [--write docs/tools.json]
            The machine-readable catalogue: every tool with tier, approval, availability, data sources, MCP hints and schemas.
        xiv-mcp-catalog docs [--plugin XivMcp.dll] [--write docs/TOOLS.md]
            The full reference generated from that catalogue.
        xiv-mcp-catalog all --write REPO_ROOT      write docs/tools.json, docs/TOOLS.md and the README block
        xiv-mcp-catalog check --write REPO_ROOT    exit 1 when any of the three is stale (CI)
        xiv-mcp-catalog serve [--plugin XivMcp.dll] [--port 41812]
            Serve the plugin's real tool list on http://127.0.0.1:PORT/mcp (no token, every tier and category enabled,
            nothing callable: providers are not constructed). For schema linting only.
        Default plugin: <artifacts>/bin/XivMcp.Plugin/release/XivMcp.dll next to this tool's artifacts directory.
        Dalamud assemblies: $DALAMUD_HOME or ~/.xlcore/dalamud/Hooks/dev/.
        """);
    return mode is "--help" or "-h" ? 0 : 2;
}

pluginPath ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "XivMcp.Plugin", "release", "XivMcp.dll"));
pluginPath = Path.GetFullPath(pluginPath);
if (!File.Exists(pluginPath))
{
    Console.Error.WriteLine($"plugin assembly not found: {pluginPath} (build src/XivMcp.Plugin first or pass --plugin)");
    return 1;
}

var dalamudDir = Environment.GetEnvironmentVariable("DALAMUD_HOME") is { Length: > 0 } home
    ? home
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xlcore", "dalamud", "Hooks", "dev");
var pluginDir = Path.GetDirectoryName(pluginPath)!;
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (var dir in new[] { pluginDir, dalamudDir })
    {
        var candidate = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(candidate))
            return context.LoadFromAssemblyPath(candidate);
    }

    return null;
};

var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
Type[] types;
try
{
    types = plugin.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    foreach (var loader in ex.LoaderExceptions.Take(5))
        Console.Error.WriteLine("type load: " + loader?.Message);
    types = ex.Types.Where(t => t is not null).ToArray()!;
}

var providers = types
    .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<McpProviderAttribute>() is not null)
    .OrderBy(t => t.GetCustomAttribute<McpProviderAttribute>()!.Category, StringComparer.Ordinal)
    .ThenBy(t => t.FullName, StringComparer.Ordinal)
    .ToArray();

await using var server = new McpServer(new McpServerOptions { Port = port, BearerToken = null, ServerTitle = "xiv-mcp catalog (list only)" }, new InlineGame(), new AllowAll(),
    (message, ex) => Console.Error.WriteLine(ex is null ? message : $"{message}: {ex.Message}"));
var failures = 0;
foreach (var type in providers)
{
    try
    {
        server.RegisterProvider(RuntimeHelpers.GetUninitializedObject(type));
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAILED {type.FullName}: {ex.Message}");
    }
}

if (mode == "serve")
{
    await server.StartAsync();
    Console.WriteLine($"{server.ListRegisteredTools().Count} tools from {providers.Length - failures}/{providers.Length} providers on {server.GetStatus().Endpoint}");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
    await stop.Task;
    await server.StopAsync();
    return failures == 0 ? 0 : 1;
}

if (failures > 0)
    return 1;

var catalogue = server.ExportCatalogue();
var json = catalogue.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
var readmeBlock = Catalog.RenderReadme(catalogue);
var reference = Catalog.RenderReference(catalogue);

switch (mode)
{
    case "json":
        return Emit(write, json);
    case "docs":
        return Emit(write, reference);
    case "readme":
        return write is null ? Emit(null, readmeBlock) : WriteReadme(write, readmeBlock, check: false);
    default:
    {
        if (write is null)
        {
            Console.Error.WriteLine($"{mode} needs --write REPO_ROOT");
            return 2;
        }

        var check = mode == "check";
        var stale = 0;
        stale += Sync(Path.Combine(write, "docs", "tools.json"), json, check);
        stale += Sync(Path.Combine(write, "docs", "TOOLS.md"), reference, check);
        stale += WriteReadme(Path.Combine(write, "README.md"), readmeBlock, check);
        if (check && stale > 0)
            Console.Error.WriteLine("The generated catalogue is stale. Run: dotnet run --project tools/catalog -c Release -- all --write .");
        return check && stale > 0 ? 1 : 0;
    }
}

static int Emit(string? path, string text)
{
    if (path is null)
        Console.Write(text);
    else
        Sync(path, text, check: false);
    return 0;
}

static int Sync(string path, string text, bool check)
{
    var current = File.Exists(path) ? File.ReadAllText(path) : null;
    if (current == text)
    {
        Console.WriteLine($"{path} is up to date");
        return 0;
    }

    if (check)
    {
        Console.Error.WriteLine($"{path} is stale");
        return 1;
    }

    File.WriteAllText(path, text);
    Console.WriteLine($"updated {path}");
    return 0;
}

static int WriteReadme(string path, string markdown, bool check)
{
    var readme = File.ReadAllText(path);
    var start = readme.IndexOf(Begin, StringComparison.Ordinal);
    var end = readme.IndexOf(End, StringComparison.Ordinal);
    if (start < 0 || end < start)
    {
        Console.Error.WriteLine($"{path} has no generated block; add the lines\n{Begin}\n{End}");
        return 1;
    }

    return Sync(path, readme[..(start + Begin.Length)] + "\n\n" + markdown + "\n" + readme[end..], check) is 0 ? 0 : 1;
}

internal static partial class Catalog
{
    private static string S(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";

    private static bool B(JsonNode? node, string key) => node?[key]?.GetValue<bool>() == true;

    private static string Tier(JsonNode tool) => S(tool, "permission") is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] : "Read";

    /// <summary>The README block: one row per tool, generated from the catalogue.</summary>
    public static string RenderReadme(JsonObject catalogue)
    {
        var tools = catalogue["tools"]!.AsArray().Select(t => t!).ToArray();
        var resources = catalogue["resources"]!.AsArray().Select(r => (Uri: S(r, "uri"), Node: r!))
            .Concat(catalogue["resourceTemplates"]!.AsArray().Select(r => (Uri: S(r, "uriTemplate"), Node: r!))).ToArray();
        var prompts = catalogue["prompts"]!.AsArray().Select(p => p!).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{tools.Length} tools ({tools.Count(t => B(t, "needsApproval"))} change something and go through the approval switch; {tools.Count(t => S(t, "availability") == "static")} also work before the game starts), {resources.Length} resources and templates, {prompts.Length} prompts.");
        sb.AppendLine("Catalogue version " + catalogue["catalogueVersion"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + "; the machine-readable listing is [docs/tools.json](docs/tools.json) and the full reference (arguments, data sources, approval text) is [docs/TOOLS.md](docs/TOOLS.md).");
        sb.AppendLine("Tier and category are the in-game switches; *Login* means the call fails at the title screen; *Approval* means the call waits for you in game");
        sb.AppendLine("while *Ask me before anything changes* is ticked; *Pre-game* means the standalone host serves it while the game is closed.");
        sb.AppendLine("Behaviour in game is unverified unless stated elsewhere.");
        sb.AppendLine();
        sb.AppendLine("### Tools");
        sb.AppendLine();
        sb.AppendLine("| Tool | Tier | Category | Login | Approval | Pre-game | What it does |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var t in tools.OrderBy(t => S(t, "category"), StringComparer.Ordinal).ThenBy(t => TierOrder(Tier(t))).ThenBy(t => S(t, "name"), StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{S(t, "name")}` | {Tier(t)} | {S(t, "category")} | {(B(t, "requiresLogin") ? "yes" : "no")} | {(B(t, "needsApproval") ? "yes" : "—")} | {(S(t, "availability") == "static" ? "yes" : "—")} | {Cell(Summary(S(t, "description")))} |");

        sb.AppendLine();
        sb.AppendLine("### Resources");
        sb.AppendLine();
        sb.AppendLine("Resources and templates follow the Read tier and their category.");
        sb.AppendLine();
        sb.AppendLine("| URI | Category | Login | What |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var r in resources.OrderBy(r => S(r.Node, "category"), StringComparer.Ordinal).ThenBy(r => r.Uri, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{r.Uri}` | {S(r.Node, "category")} | {(B(r.Node, "requiresLogin") ? "yes" : "no")} | {Cell(Summary(S(r.Node, "description") is { Length: > 0 } d ? d : S(r.Node, "name")))} |");

        sb.AppendLine();
        sb.AppendLine("### Prompts");
        sb.AppendLine();
        sb.AppendLine("Prompts only return instructions; every game interaction still goes through tools and their tiers.");
        sb.AppendLine();
        sb.AppendLine("| Prompt | Category | Arguments | Workflow |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var p in prompts.OrderBy(p => S(p, "name"), StringComparer.Ordinal))
        {
            var arguments = string.Join(", ", p["arguments"]!.AsArray().Select(a => $"`{S(a, "name")}{(B(a, "required") ? "" : "?")}`"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{S(p, "name")}` | {S(p, "category")} | {(arguments.Length > 0 ? arguments : "—")} | {Cell(Summary(S(p, "description")))} |");
        }

        return sb.ToString();
    }

    /// <summary>docs/TOOLS.md: every tool with its arguments, hints, data sources and approval sentence.</summary>
    public static string RenderReference(JsonObject catalogue)
    {
        var tools = catalogue["tools"]!.AsArray().Select(t => t!).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine("# Tool reference");
        sb.AppendLine();
        sb.AppendLine("<!-- GENERATED by `dotnet run --project tools/catalog -c Release -- all --write .` from the plugin assembly. Do not edit. -->");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Catalogue version {catalogue["catalogueVersion"]!.GetValue<int>()}: {tools.Length} tools. The same data, with full JSON schemas, is in [tools.json](tools.json).");
        sb.AppendLine("Nothing here has been verified inside the running game unless another document says so. What these tools will never do is in [HARD-LINES.md](HARD-LINES.md).");
        sb.AppendLine();
        sb.AppendLine("Columns: **hints** are the MCP annotations (`readOnly`, `destructive`, `idempotent`, `openWorld`); **approval** is the sentence the");
        sb.AppendLine("player sees before a state-changing call runs (only while *Ask me before anything changes* is ticked; it is always written to the");
        sb.AppendLine("action log); **pre-game** tools are served by the standalone host while the game is closed; **sources** say where the answer comes from.");
        foreach (var group in tools.GroupBy(t => S(t, "category"), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            foreach (var t in group.OrderBy(t => TierOrder(Tier(t))).ThenBy(t => S(t, "name"), StringComparer.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"### `{S(t, "name")}`");
                sb.AppendLine();
                var hints = new List<string>();
                if (B(t, "readOnlyHint")) hints.Add("readOnly");
                if (B(t, "destructiveHint")) hints.Add("destructive");
                if (B(t, "idempotentHint")) hints.Add("idempotent");
                if (B(t, "openWorldHint")) hints.Add("openWorld");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{Tier(t)} tier · hints: {(hints.Count > 0 ? string.Join(", ", hints) : "none")} · login {(B(t, "requiresLogin") ? "required" : "not required")} · pre-game: {(S(t, "availability") == "static" ? "yes" : "no")}");
                sb.AppendLine();
                if (B(t, "needsApproval"))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"**Approval:** {(S(t, "approvalSummary") is { Length: > 0 } a ? a : "(the tool name and its arguments)")}");
                    sb.AppendLine();
                }

                var sources = t["dataSources"]!.AsArray().Select(s => "`" + s!.GetValue<string>() + "`").ToArray();
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Sources:** {(sources.Length > 0 ? string.Join(", ", sources) : "—")}");
                sb.AppendLine();
                sb.AppendLine(WhiteSpace().Replace(S(t, "description"), " ").Trim());
                if (t["inputSchema"]?["properties"] is JsonObject properties && properties.Count > 0)
                {
                    var required = (t["inputSchema"]!["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
                    sb.AppendLine();
                    sb.AppendLine("| Argument | Type | Required | Description |");
                    sb.AppendLine("| --- | --- | --- | --- |");
                    foreach (var (name, schema) in properties)
                        sb.AppendLine(CultureInfo.InvariantCulture, $"| `{name}` | {Cell(TypeOf(schema))} | {(required.Contains(name) ? "yes" : "no")} | {Cell(WhiteSpace().Replace(S(schema, "description"), " ").Trim())} |");
                }
            }
        }

        return sb.ToString();
    }

    private static string TypeOf(JsonNode? schema)
    {
        if (schema is null)
            return "any";
        var type = schema["type"] switch
        {
            JsonArray many => string.Join(" or ", many.Select(m => m!.GetValue<string>())),
            JsonValue one => one.GetValue<string>(),
            _ => "any",
        };
        if (schema["enum"] is JsonArray values)
            type += " (" + string.Join(", ", values.Select(v => v?.ToJsonString() ?? "null")) + ")";
        return type;
    }

    private static int TierOrder(string tier) => tier switch { "Read" => 0, "Ui" => 1, "Action" => 2, _ => 3 };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>First sentence (not fooled by "e.g." or "i.e."), at most 220 characters.</summary>
    private static string Summary(string description)
    {
        var text = WhiteSpace().Replace(description ?? "", " ").Trim();
        foreach (Match m in SentenceEnd().Matches(text))
        {
            var before = text[..m.Index];
            if (before.EndsWith("e.g", StringComparison.Ordinal) || before.EndsWith("i.e", StringComparison.Ordinal) || before.EndsWith("vs", StringComparison.Ordinal))
                continue;
            text = text[..(m.Index + 1)];
            break;
        }

        return text.Length <= 220 ? text : text[..217].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\.(?=\s+[A-Z(""`])")]
    private static partial Regex SentenceEnd();
}

internal sealed class InlineGame : IGameThread
{
    public bool IsOnGameThread => true;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default) =>
        Task.FromException<T>(new McpToolException("xiv-mcp-catalog lists tools only; nothing is callable."));

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        Task.FromException(new McpToolException("xiv-mcp-catalog lists tools only; nothing is callable."));
}

internal sealed class AllowAll : IHostState
{
    public bool IsLoggedIn => false;

    public bool IsPermitted(ToolPermission permission) => true;

    public bool IsCategoryEnabled(string category) => true;
}
