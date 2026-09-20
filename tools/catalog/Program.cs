using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
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

if (mode is not ("readme" or "serve"))
{
    Console.WriteLine("""
        xiv-mcp-catalog readme [--plugin XivMcp.dll] [--write README.md]
            Print the tool/resource/prompt catalog as Markdown, or replace the generated block in a README.
        xiv-mcp-catalog serve [--plugin XivMcp.dll] [--port 41812]
            Serve the plugin's real tool list on http://127.0.0.1:PORT/mcp (no token, every tier and category enabled,
            nothing callable: providers are not constructed). For schema linting only.
        Default plugin: <artifacts>/bin/XivMcp.Plugin/release/XivMcp.dll next to this tool's artifacts directory.
        Dalamud assemblies: $DALAMUD_HOME or ~/.xlcore/dalamud/Hooks/dev/.
        """);
    return mode is "--help" or "-h" ? 0 : 2;
}

pluginPath ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "XivMcp.Plugin", "release", "XivMcp.dll"));
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

if (mode == "serve")
{
    using var server = new McpServer(new McpServerOptions { Port = port, BearerToken = null, ServerTitle = "xiv-mcp catalog (list only)" }, new InlineGame(), new AllowAll(),
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

var markdown = Catalog.Render(providers);
if (write is null)
{
    Console.Write(markdown);
    return 0;
}

var readme = File.ReadAllText(write);
var start = readme.IndexOf(Begin, StringComparison.Ordinal);
var end = readme.IndexOf(End, StringComparison.Ordinal);
if (start < 0 || end < start)
{
    Console.Error.WriteLine($"{write} has no generated block; add the lines\n{Begin}\n{End}");
    return 1;
}

var updated = readme[..(start + Begin.Length)] + "\n\n" + markdown + "\n" + readme[end..];
if (updated != readme)
{
    File.WriteAllText(write, updated);
    Console.WriteLine($"updated {write}");
}
else
{
    Console.WriteLine($"{write} is up to date");
}

return 0;

internal static partial class Catalog
{
    public static string Render(IReadOnlyList<Type> providers)
    {
        var tools = new List<(string Name, string Tier, string Category, bool Login, string Summary)>();
        var resources = new List<(string Uri, string Category, bool Login, string Summary)>();
        var prompts = new List<(string Name, string Category, string Arguments, string Summary)>();

        foreach (var type in providers)
        {
            var category = type.GetCustomAttribute<McpProviderAttribute>()!.Category;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpToolAttribute>() is { } tool)
                    tools.Add((tool.Name, tool.Permission.ToString(), category, tool.RequiresLogin, Summary(tool.Description)));
                if (method.GetCustomAttribute<McpResourceAttribute>() is { } resource)
                    resources.Add((resource.Uri, category, resource.RequiresLogin, Summary(string.IsNullOrWhiteSpace(resource.Description) ? resource.Name : resource.Description)));
                if (method.GetCustomAttribute<McpResourceTemplateAttribute>() is { } template)
                    resources.Add((template.UriTemplate, category, template.RequiresLogin, Summary(string.IsNullOrWhiteSpace(template.Description) ? template.Name : template.Description)));
                if (method.GetCustomAttribute<McpPromptAttribute>() is { } prompt)
                {
                    var arguments = method.GetParameters()
                        .Where(p => p.ParameterType == typeof(string) || p.ParameterType.IsEnum || Nullable.GetUnderlyingType(p.ParameterType)?.IsEnum == true || p.ParameterType.IsPrimitive)
                        .Select(p => $"`{p.Name}{(p.HasDefaultValue ? "?" : "")}`");
                    prompts.Add((prompt.Name, category, string.Join(", ", arguments) is { Length: > 0 } a ? a : "—", Summary(prompt.Description)));
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{tools.Count} tools, {resources.Count} resources and templates, {prompts.Count} prompts. Tier and category are the in-game switches");
        sb.AppendLine("(Settings → Permissions / Categories); *Login* means the call fails at the title screen. Descriptions are the first");
        sb.AppendLine("sentence of what clients see; `tools/list` has the full text and schemas. Behaviour in game is unverified unless");
        sb.AppendLine("stated elsewhere.");
        sb.AppendLine();
        sb.AppendLine("### Tools");
        sb.AppendLine();
        sb.AppendLine("| Tool | Tier | Category | Login | What it does |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var t in tools.OrderBy(t => t.Category, StringComparer.Ordinal).ThenBy(t => TierOrder(t.Tier)).ThenBy(t => t.Name, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{t.Name}` | {t.Tier} | {t.Category} | {(t.Login ? "yes" : "no")} | {Cell(t.Summary)} |");

        sb.AppendLine();
        sb.AppendLine("### Resources");
        sb.AppendLine();
        sb.AppendLine("Resources and templates follow the Read tier and their category.");
        sb.AppendLine();
        sb.AppendLine("| URI | Category | Login | What |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var r in resources.OrderBy(r => r.Category, StringComparer.Ordinal).ThenBy(r => r.Uri, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{r.Uri}` | {r.Category} | {(r.Login ? "yes" : "no")} | {Cell(r.Summary)} |");

        sb.AppendLine();
        sb.AppendLine("### Prompts");
        sb.AppendLine();
        sb.AppendLine("Prompts only return instructions; every game interaction still goes through tools and their tiers.");
        sb.AppendLine();
        sb.AppendLine("| Prompt | Category | Arguments | Workflow |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var p in prompts.OrderBy(p => p.Name, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{p.Name}` | {p.Category} | {p.Arguments} | {Cell(p.Summary)} |");
        return sb.ToString();
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
