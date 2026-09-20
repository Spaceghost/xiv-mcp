using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XivMcp.Core.Registry;

internal enum AsyncShape
{
    None,
    Task,
    TaskOfT,
    ValueTask,
    ValueTaskOfT,
}

internal abstract class MemberDescriptor
{
    public required object? Target { get; init; }

    public required MethodInfo Method { get; init; }

    public required MethodInvoker Invoker { get; init; }

    public required ParameterBinding[] Parameters { get; init; }

    public required string Category { get; init; }

    public required AsyncShape AsyncShape { get; init; }

    /// <summary>Result type after unwrapping Task&lt;T&gt;/ValueTask&lt;T&gt; (typeof(void) for none).</summary>
    public required Type ValueType { get; init; }

    public bool ResultNullable { get; init; }

    public bool HasToolContext => Parameters.Any(p => p.Kind == ParameterKind.ToolContext);

    /// <summary>Invokes the method synchronously; exceptions from the method propagate unwrapped.</summary>
    public object? Invoke(object?[] args)
    {
        try
        {
            return Invoker.Invoke(Target, new Span<object?>(args));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Awaits a returned Task/ValueTask (if any) and yields the final value.</summary>
    public async Task<object?> CompleteAsync(object? returned)
    {
        switch (AsyncShape)
        {
            case AsyncShape.None:
                return returned;
            case AsyncShape.Task:
                if (returned is Task t)
                    await t.ConfigureAwait(false);
                return null;
            case AsyncShape.TaskOfT:
                if (returned is not Task tt)
                    return null;
                await tt.ConfigureAwait(false);
                return tt.GetType().GetProperty("Result")!.GetValue(tt);
            case AsyncShape.ValueTask:
                if (returned is ValueTask vt)
                    await vt.ConfigureAwait(false);
                return null;
            case AsyncShape.ValueTaskOfT:
                if (returned is null)
                    return null;
                var asTask = (Task)returned.GetType().GetMethod("AsTask")!.Invoke(returned, null)!;
                await asTask.ConfigureAwait(false);
                return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
            default:
                return returned;
        }
    }
}

internal sealed class ToolDescriptor : MemberDescriptor
{
    public required string Name { get; init; }

    public string? Title { get; init; }

    public required string Description { get; init; }

    public required ToolPermission Permission { get; init; }

    public required bool GameThread { get; init; }

    public required bool RequiresLogin { get; init; }

    public required bool Destructive { get; init; }

    public required bool Idempotent { get; init; }

    public required bool OpenWorld { get; init; }

    public required JsonObject InputSchema { get; init; }

    public JsonObject? OutputSchema { get; init; }

    public bool WrapResult { get; init; }

    public bool ReturnsToolResult { get; init; }

    public required string Signature { get; init; }

    public string[] Sources { get; init; } = [];

    public ToolAvailability Availability { get; init; }

    public string? ApprovalSummary { get; init; }

    /// <summary>The call is put to the approver before it runs (Action, Chat, or a Ui tool that asked for it).</summary>
    public bool NeedsApproval { get; init; }

    /// <summary>Set for <see cref="ExternalTool"/>s: called instead of <see cref="MemberDescriptor.Method"/>, with the raw arguments.</summary>
    public Func<JsonObject?, ToolContext, Task<ToolResult>>? ExternalHandler { get; init; }
}

internal sealed class ResourceDescriptor : MemberDescriptor
{
    public required string Uri { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MimeType { get; init; }

    public required bool GameThread { get; init; }

    public required bool RequiresLogin { get; init; }
}

internal sealed class ResourceTemplateDescriptor : MemberDescriptor
{
    public required UriTemplate Template { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MimeType { get; init; }

    public required bool GameThread { get; init; }

    public required bool RequiresLogin { get; init; }
}

internal sealed class PromptDescriptor : MemberDescriptor
{
    public required string Name { get; init; }

    public string? Title { get; init; }

    public required string Description { get; init; }
}

internal sealed class RegistrySnapshot
{
    public static readonly RegistrySnapshot Empty = new([], [], [], []);

    public RegistrySnapshot(ToolDescriptor[] tools, ResourceDescriptor[] resources, ResourceTemplateDescriptor[] templates, PromptDescriptor[] prompts)
    {
        Tools = tools;
        Resources = resources;
        Templates = templates;
        Prompts = prompts;
        ToolsByName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        ResourcesByUri = resources.ToDictionary(r => r.Uri, StringComparer.Ordinal);
        PromptsByName = prompts.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public ToolDescriptor[] Tools { get; }

    public ResourceDescriptor[] Resources { get; }

    public ResourceTemplateDescriptor[] Templates { get; }

    public PromptDescriptor[] Prompts { get; }

    public Dictionary<string, ToolDescriptor> ToolsByName { get; }

    public Dictionary<string, ResourceDescriptor> ResourcesByUri { get; }

    public Dictionary<string, PromptDescriptor> PromptsByName { get; }

    public IEnumerable<string> Categories =>
        Tools.Select(t => t.Category)
            .Concat(Resources.Select(r => r.Category))
            .Concat(Templates.Select(t => t.Category))
            .Concat(Prompts.Select(p => p.Category))
            .Distinct(StringComparer.Ordinal);
}

[Flags]
internal enum RegistryChange
{
    None = 0,
    Tools = 1,
    Resources = 2,
    Prompts = 4,
}

internal sealed class ProviderRegistry
{
    private static readonly Regex ToolNamePattern = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    private volatile RegistrySnapshot _snapshot = RegistrySnapshot.Empty;

    public RegistrySnapshot Snapshot => _snapshot;

    /// <summary>Validates and adds every attributed member of <paramref name="provider"/> atomically.</summary>
    public RegistryChange Register(object provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var type = provider.GetType();
        var category = type.GetCustomAttribute<McpProviderAttribute>()?.Category;
        if (string.IsNullOrWhiteSpace(category))
            category = "general";

        var tools = new List<ToolDescriptor>();
        var resources = new List<ResourceDescriptor>();
        var templates = new List<ResourceTemplateDescriptor>();
        var prompts = new List<PromptDescriptor>();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        foreach (var method in type.GetMethods(flags).OrderBy(m => m.MetadataToken))
        {
            var tool = method.GetCustomAttribute<McpToolAttribute>();
            var resource = method.GetCustomAttribute<McpResourceAttribute>();
            var template = method.GetCustomAttribute<McpResourceTemplateAttribute>();
            var prompt = method.GetCustomAttribute<McpPromptAttribute>();
            var count = (tool is null ? 0 : 1) + (resource is null ? 0 : 1) + (template is null ? 0 : 1) + (prompt is null ? 0 : 1);
            if (count == 0)
                continue;
            if (count > 1)
                throw new ArgumentException($"{Where(method)} has more than one MCP attribute.");
            if (method.IsGenericMethodDefinition)
                throw new ArgumentException($"{Where(method)}: generic methods cannot be MCP members.");

            var target = method.IsStatic ? null : provider;
            if (tool is not null)
                tools.Add(BuildTool(tool, method, target, category));
            else if (resource is not null)
                resources.Add(BuildResource(resource, method, target, category));
            else if (template is not null)
                templates.Add(BuildTemplate(template, method, target, category));
            else
                prompts.Add(BuildPrompt(prompt!, method, target, category));
        }

        lock (_lock)
        {
            var current = _snapshot;
            var toolNames = new HashSet<string>(current.Tools.Select(t => t.Name), StringComparer.Ordinal);
            foreach (var t in tools)
            {
                if (!toolNames.Add(t.Name))
                    throw new ArgumentException($"Duplicate tool name '{t.Name}' ({Where(t.Method)}).");
            }

            var uris = new HashSet<string>(current.Resources.Select(r => r.Uri), StringComparer.Ordinal);
            foreach (var r in resources)
            {
                if (!uris.Add(r.Uri))
                    throw new ArgumentException($"Duplicate resource URI '{r.Uri}' ({Where(r.Method)}).");
            }

            var templateTexts = new HashSet<string>(current.Templates.Select(r => r.Template.Template), StringComparer.Ordinal);
            foreach (var r in templates)
            {
                if (!templateTexts.Add(r.Template.Template))
                    throw new ArgumentException($"Duplicate resource template '{r.Template.Template}' ({Where(r.Method)}).");
            }

            var promptNames = new HashSet<string>(current.Prompts.Select(p => p.Name), StringComparer.Ordinal);
            foreach (var p in prompts)
            {
                if (!promptNames.Add(p.Name))
                    throw new ArgumentException($"Duplicate prompt name '{p.Name}' ({Where(p.Method)}).");
            }

            _snapshot = new RegistrySnapshot(
                current.Tools.Concat(tools).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(),
                current.Resources.Concat(resources).OrderBy(r => r.Uri, StringComparer.Ordinal).ToArray(),
                current.Templates.Concat(templates).OrderBy(r => r.Template.Template, StringComparer.Ordinal).ToArray(),
                current.Prompts.Concat(prompts).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());
        }

        var change = RegistryChange.None;
        if (tools.Count > 0)
            change |= RegistryChange.Tools;
        if (resources.Count > 0 || templates.Count > 0)
            change |= RegistryChange.Resources;
        if (prompts.Count > 0)
            change |= RegistryChange.Prompts;
        return change;
    }

    private static readonly MethodInfo ExternalPlaceholder = typeof(ProviderRegistry).GetMethod(nameof(ExternalPlaceholderBody), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ExternalPlaceholderBody()
    {
    }

    /// <summary>Adds tools described by data instead of by an attributed method. Duplicate names throw.</summary>
    public RegistryChange RegisterExternal(IReadOnlyList<ExternalTool> externals)
    {
        var tools = new List<ToolDescriptor>(externals.Count);
        foreach (var e in externals)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || !ToolNamePattern.IsMatch(e.Name))
                throw new ArgumentException($"external tool name '{e.Name}' must match ^[A-Za-z0-9_.-]{{1,128}}$.");
            ArgumentNullException.ThrowIfNull(e.Handler);
            var needsApproval = e.Permission >= ToolPermission.Action || e.RequiresApproval;
            tools.Add(new ToolDescriptor
            {
                Name = e.Name,
                Title = e.Title,
                Description = e.Description,
                Permission = e.Permission,
                GameThread = false,
                RequiresLogin = false,
                Destructive = e.Destructive,
                Idempotent = e.Idempotent,
                OpenWorld = e.OpenWorld,
                Sources = [.. e.Sources],
                Availability = e.Availability,
                ApprovalSummary = e.ApprovalSummary,
                NeedsApproval = needsApproval,
                ExternalHandler = e.Handler,
                Target = null,
                Method = ExternalPlaceholder,
                Invoker = MethodInvoker.Create(ExternalPlaceholder),
                Parameters = [],
                Category = string.IsNullOrWhiteSpace(e.Category) ? "general" : e.Category,
                AsyncShape = AsyncShape.None,
                ValueType = typeof(ToolResult),
                InputSchema = (JsonObject)e.InputSchema.DeepClone(),
                OutputSchema = e.OutputSchema?.DeepClone() as JsonObject,
                ReturnsToolResult = true,
                Signature = "(see inputSchema)",
            });
        }

        lock (_lock)
        {
            var current = _snapshot;
            var names = new HashSet<string>(current.Tools.Select(t => t.Name), StringComparer.Ordinal);
            foreach (var t in tools)
            {
                if (!names.Add(t.Name))
                    throw new ArgumentException($"Duplicate tool name '{t.Name}' (external tool).");
            }

            _snapshot = new RegistrySnapshot(
                current.Tools.Concat(tools).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(),
                current.Resources,
                current.Templates,
                current.Prompts);
        }

        return tools.Count > 0 ? RegistryChange.Tools : RegistryChange.None;
    }

    private static string Where(MethodInfo m) => $"{m.DeclaringType?.FullName}.{m.Name}";

    private static (AsyncShape Shape, Type ValueType) AnalyzeReturn(MethodInfo method)
    {
        var rt = method.ReturnType;
        if (rt == typeof(void))
            return (AsyncShape.None, typeof(void));
        if (rt == typeof(Task))
            return (AsyncShape.Task, typeof(void));
        if (rt == typeof(ValueTask))
            return (AsyncShape.ValueTask, typeof(void));
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            return (AsyncShape.TaskOfT, rt.GetGenericArguments()[0]);
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return (AsyncShape.ValueTaskOfT, rt.GetGenericArguments()[0]);
        return (AsyncShape.None, rt);
    }

    private static bool IsResultNullable(MethodInfo method, AsyncShape shape)
    {
        try
        {
            var info = new NullabilityInfoContext().Create(method.ReturnParameter);
            if (shape is AsyncShape.TaskOfT or AsyncShape.ValueTaskOfT)
                info = info.GenericTypeArguments[0];
            var t = info.Type;
            return Nullable.GetUnderlyingType(t) is not null || (!t.IsValueType && info.ReadState == NullabilityState.Nullable);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ToolDescriptor BuildTool(McpToolAttribute attr, MethodInfo method, object? target, string category)
    {
        if (string.IsNullOrWhiteSpace(attr.Name) || !ToolNamePattern.IsMatch(attr.Name))
            throw new ArgumentException($"{Where(method)}: tool name '{attr.Name}' must match ^[A-Za-z0-9_.-]{{1,128}}$.");

        var parameters = ArgumentBinder.Describe(method);
        var (shape, valueType) = AnalyzeReturn(method);
        var nullable = IsResultNullable(method, shape);
        var returnsToolResult = TypeShapes.UnwrapNullable(valueType) == typeof(ToolResult);
        var output = returnsToolResult || valueType == typeof(void)
            ? null
            : SchemaGenerator.BuildOutputSchema(valueType, nullable, out _);
        var wrap = false;
        if (output is not null)
            SchemaGenerator.BuildOutputSchema(valueType, nullable, out wrap);

        return new ToolDescriptor
        {
            Name = attr.Name,
            Title = attr.Title,
            Description = attr.Description ?? "",
            Permission = attr.Permission,
            GameThread = attr.GameThread,
            RequiresLogin = attr.RequiresLogin,
            Destructive = attr.Destructive,
            Idempotent = attr.Idempotent,
            OpenWorld = attr.OpenWorld,
            Sources = attr.Sources ?? [],
            Availability = attr.Availability,
            ApprovalSummary = attr.ApprovalSummary,
            NeedsApproval = attr.NeedsApproval,
            Target = target,
            Method = method,
            Invoker = MethodInvoker.Create(method),
            Parameters = parameters,
            Category = category,
            AsyncShape = shape,
            ValueType = valueType,
            ResultNullable = nullable,
            InputSchema = SchemaGenerator.BuildInputSchema(parameters),
            OutputSchema = output,
            WrapResult = wrap,
            ReturnsToolResult = returnsToolResult,
            Signature = SchemaGenerator.DescribeParameters(parameters),
        };
    }

    private static void RequireOnlyInjected(MethodInfo method, ParameterBinding[] parameters, string what)
    {
        foreach (var p in parameters)
        {
            if (p.Kind == ParameterKind.Argument)
                throw new ArgumentException($"{Where(method)}: {what} methods may only take ToolContext/CancellationToken (found '{p.JsonName}').");
        }
    }

    private static ResourceDescriptor BuildResource(McpResourceAttribute attr, MethodInfo method, object? target, string category)
    {
        if (!Uri.TryCreate(attr.Uri, UriKind.Absolute, out _))
            throw new ArgumentException($"{Where(method)}: resource URI '{attr.Uri}' is not an absolute URI.");
        var parameters = ArgumentBinder.Describe(method);
        RequireOnlyInjected(method, parameters, "resource");
        var (shape, valueType) = AnalyzeReturn(method);
        if (valueType == typeof(void))
            throw new ArgumentException($"{Where(method)}: resource methods must return a value.");

        return new ResourceDescriptor
        {
            Uri = attr.Uri,
            Name = string.IsNullOrWhiteSpace(attr.Name) ? method.Name : attr.Name,
            Description = attr.Description ?? "",
            MimeType = string.IsNullOrWhiteSpace(attr.MimeType) ? "application/json" : attr.MimeType,
            GameThread = attr.GameThread,
            RequiresLogin = attr.RequiresLogin,
            Target = target,
            Method = method,
            Invoker = MethodInvoker.Create(method),
            Parameters = parameters,
            Category = category,
            AsyncShape = shape,
            ValueType = valueType,
        };
    }

    private static ResourceTemplateDescriptor BuildTemplate(McpResourceTemplateAttribute attr, MethodInfo method, object? target, string category)
    {
        var template = UriTemplate.Parse(attr.UriTemplate);
        var parameters = ArgumentBinder.Describe(method);
        foreach (var p in parameters)
        {
            if (p.Kind != ParameterKind.Argument)
                continue;
            var bound = template.Variables.Any(v => string.Equals(v, p.JsonName, StringComparison.OrdinalIgnoreCase));
            if (!bound && p.IsRequired)
                throw new ArgumentException($"{Where(method)}: required parameter '{p.JsonName}' is not a variable of template '{attr.UriTemplate}'.");
        }

        var (shape, valueType) = AnalyzeReturn(method);
        if (valueType == typeof(void))
            throw new ArgumentException($"{Where(method)}: resource template methods must return a value.");

        return new ResourceTemplateDescriptor
        {
            Template = template,
            Name = string.IsNullOrWhiteSpace(attr.Name) ? method.Name : attr.Name,
            Description = attr.Description ?? "",
            MimeType = string.IsNullOrWhiteSpace(attr.MimeType) ? "application/json" : attr.MimeType,
            GameThread = attr.GameThread,
            RequiresLogin = attr.RequiresLogin,
            Target = target,
            Method = method,
            Invoker = MethodInvoker.Create(method),
            Parameters = parameters,
            Category = category,
            AsyncShape = shape,
            ValueType = valueType,
        };
    }

    private static PromptDescriptor BuildPrompt(McpPromptAttribute attr, MethodInfo method, object? target, string category)
    {
        if (string.IsNullOrWhiteSpace(attr.Name))
            throw new ArgumentException($"{Where(method)}: prompt name must not be empty.");
        var parameters = ArgumentBinder.Describe(method);
        var (shape, valueType) = AnalyzeReturn(method);
        var unwrapped = TypeShapes.UnwrapNullable(valueType);
        if (unwrapped != typeof(string) && unwrapped != typeof(PromptResult))
            throw new ArgumentException($"{Where(method)}: prompt methods must return string or PromptResult (or a Task of either).");

        return new PromptDescriptor
        {
            Name = attr.Name,
            Title = attr.Title,
            Description = attr.Description ?? "",
            Target = target,
            Method = method,
            Invoker = MethodInvoker.Create(method),
            Parameters = parameters,
            Category = category,
            AsyncShape = shape,
            ValueType = valueType,
        };
    }
}
