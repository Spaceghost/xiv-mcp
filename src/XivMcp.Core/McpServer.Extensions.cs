using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XivMcp.Core.Registry;

namespace XivMcp.Core;

/// <summary>
/// A tool described by data rather than by an attributed method: the standalone host uses it to answer, under the
/// plugin's own tool names and schemas, for tools that need the running game.
/// </summary>
public sealed class ExternalTool
{
    public required string Name { get; init; }

    public string? Title { get; init; }

    public string Description { get; init; } = "";

    public string Category { get; init; } = "general";

    public ToolPermission Permission { get; init; } = ToolPermission.Read;

    public bool RequiresApproval { get; init; }

    public bool Destructive { get; init; }

    public bool Idempotent { get; init; } = true;

    public bool OpenWorld { get; init; }

    public ToolAvailability Availability { get; init; } = ToolAvailability.Live;

    public IReadOnlyList<string> Sources { get; init; } = [];

    public string? ApprovalSummary { get; init; }

    /// <summary>JSON schema of the arguments object. Not validated by the server: the handler gets the raw arguments.</summary>
    public JsonObject InputSchema { get; init; } = new() { ["type"] = "object" };

    public JsonObject? OutputSchema { get; init; }

    /// <summary>Runs the call. Throw <see cref="McpToolException"/> for an expected failure.</summary>
    public required Func<JsonObject?, ToolContext, Task<ToolResult>> Handler { get; init; }
}

/// <summary>An authenticated request to a path below the MCP endpoint, e.g. <c>/mcp/host</c>. See <see cref="McpServer.ControlHandler"/>.</summary>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="SubPath">The part after the endpoint path, without the leading slash, e.g. "host".</param>
/// <param name="AuthenticatedClient">Per-client token name, or null for the main token (or no token when none is required).</param>
/// <param name="FromLoopback">The TCP peer is a loopback address.</param>
/// <param name="Body">Request body parsed as JSON when there was one and it parsed; otherwise null.</param>
public sealed record ControlRequest(string Method, string SubPath, string? AuthenticatedClient, bool FromLoopback, JsonNode? Body);

/// <summary>What a <see cref="McpServer.ControlHandler"/> answers.</summary>
public sealed record ControlResponse(int Status, JsonNode Body);

public sealed partial class McpServer
{
    /// <summary>
    /// Version of the tool catalogue's shape and naming rules (docs/tools.json <c>catalogueVersion</c>). Bump it when a tool
    /// is renamed or removed, or when the catalogue document changes shape; adding tools does not need a bump.
    /// </summary>
    public const int CatalogueVersion = 2;

    private readonly ConcurrentDictionary<string, RateBucket> _rateBuckets = new(StringComparer.Ordinal);

    /// <summary>
    /// Handles requests to paths below the MCP endpoint (<c>{Path}/{SubPath}</c>) after the same Host, Origin and bearer
    /// checks as the endpoint itself. Null result or no handler: 404. Used for the host identity and hand-off routes.
    /// </summary>
    public Func<ControlRequest, Task<ControlResponse?>>? ControlHandler { get; set; }

    /// <summary>
    /// Raised on a thread-pool thread after a tool that goes through the approval gate ran or failed (not when it was
    /// denied or never started). Subscribers must not throw.
    /// </summary>
    public event Action<GatedToolExecution>? GatedToolExecuted;

    /// <summary>Clock used for rate limiting; tests replace it.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Registers tools described by data. Duplicate names throw. See <see cref="ExternalTool"/>.</summary>
    public void RegisterExternalTools(IReadOnlyList<ExternalTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var change = _registry.RegisterExternal(tools);
        if (_http is not null && change.HasFlag(RegistryChange.Tools))
            _notifier.ToolListChanged();
    }

    /// <summary>
    /// The whole registered surface as one JSON document, regardless of which tiers and categories are switched on:
    /// <c>{catalogueVersion, tools[], resources[], resourceTemplates[], prompts[]}</c>. docs/tools.json is this document.
    /// </summary>
    public JsonObject ExportCatalogue()
    {
        var snapshot = _registry.Snapshot;
        var tools = new JsonArray();
        foreach (var t in snapshot.Tools)
        {
            var json = new JsonObject
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["category"] = t.Category,
                ["permission"] = JsonNamingPolicy.CamelCase.ConvertName(t.Permission.ToString()),
                ["availability"] = JsonNamingPolicy.CamelCase.ConvertName(t.Availability.ToString()),
                ["requiresLogin"] = t.RequiresLogin,
                ["needsApproval"] = t.NeedsApproval,
                ["approvalSummary"] = t.ApprovalSummary,
                ["readOnlyHint"] = t.Permission == ToolPermission.Read,
                ["destructiveHint"] = t.Destructive,
                ["idempotentHint"] = t.Idempotent,
                ["openWorldHint"] = t.OpenWorld,
                ["dataSources"] = new JsonArray(t.Sources.Select(s => (JsonNode?)s).ToArray()),
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            };
            if (t.OutputSchema is not null)
                json["outputSchema"] = t.OutputSchema.DeepClone();
            tools.Add(json);
        }

        var resources = new JsonArray();
        foreach (var r in snapshot.Resources)
            resources.Add(new JsonObject { ["uri"] = r.Uri, ["name"] = r.Name, ["category"] = r.Category, ["mimeType"] = r.MimeType, ["requiresLogin"] = r.RequiresLogin, ["description"] = r.Description });

        var templates = new JsonArray();
        foreach (var r in snapshot.Templates)
            templates.Add(new JsonObject { ["uriTemplate"] = r.Template.Template, ["name"] = r.Name, ["category"] = r.Category, ["mimeType"] = r.MimeType, ["requiresLogin"] = r.RequiresLogin, ["description"] = r.Description });

        var prompts = new JsonArray();
        foreach (var p in snapshot.Prompts)
        {
            var arguments = new JsonArray();
            foreach (var a in p.Parameters.Where(a => a.Kind == ParameterKind.Argument))
                arguments.Add(new JsonObject { ["name"] = a.JsonName, ["required"] = a.IsRequired });
            prompts.Add(new JsonObject { ["name"] = p.Name, ["title"] = p.Title, ["category"] = p.Category, ["arguments"] = arguments, ["description"] = p.Description });
        }

        return new JsonObject
        {
            ["catalogueVersion"] = CatalogueVersion,
            ["tools"] = tools,
            ["resources"] = resources,
            ["resourceTemplates"] = templates,
            ["prompts"] = prompts,
        };
    }

    /// <summary>
    /// Fills <c>{argument}</c> placeholders of an approval summary with the call's argument values, verbatim but with
    /// control and format characters shown as \uXXXX and each value capped at 500 characters.
    /// </summary>
    public static string RenderApprovalSummary(string? template, string toolName, JsonObject? arguments)
    {
        if (string.IsNullOrWhiteSpace(template))
            return $"Run {toolName} with the arguments shown.";
        return PlaceholderPattern().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (arguments is null || !arguments.TryGetPropertyValue(key, out var node) || node is null)
                return "(default)";
            var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString(McpJson.Options);
            return Visible(text.Length > 500 ? text[..500] + "…" : text);
        });
    }

    private static string Visible(string text)
    {
        if (!text.Any(static c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
            return text;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Token bucket per caller (<see cref="McpServerOptions.RateLimitPerMinute"/> refill, <see cref="McpServerOptions.RateLimitBurst"/>
    /// capacity). Returns 0 when the call may run, otherwise the seconds until a token is available.
    /// </summary>
    internal double RateLimitDelaySeconds(string? authenticatedClient, string? sessionId)
    {
        var perMinute = Options.RateLimitPerMinute;
        if (perMinute <= 0)
            return 0;
        var burst = Math.Max(1, Options.RateLimitBurst > 0 ? Options.RateLimitBurst : perMinute / 4);
        var key = authenticatedClient is not null ? "c:" + authenticatedClient : sessionId is not null ? "s:" + sessionId : "main";
        if (_rateBuckets.Count > 4096)
            _rateBuckets.Clear();
        var bucket = _rateBuckets.GetOrAdd(key, static (_, b) => new RateBucket(b), burst);
        return bucket.TryTake(Clock.GetTimestamp(), Clock.TimestampFrequency, perMinute / 60.0, burst);
    }

    private void RaiseGatedToolExecuted(GatedToolExecution execution)
    {
        var handler = GatedToolExecuted;
        if (handler is null)
            return;
        try
        {
            handler(execution);
        }
        catch (Exception ex)
        {
            LogSink("a GatedToolExecuted subscriber threw", ex);
        }
    }

    private sealed class RateBucket(int burst)
    {
        private readonly object _lock = new();
        private double _tokens = burst;
        private long _last = -1;

        public double TryTake(long now, long frequency, double perSecond, int capacity)
        {
            lock (_lock)
            {
                if (_last >= 0)
                    _tokens = Math.Min(capacity, _tokens + ((now - _last) / (double)frequency * perSecond));
                _last = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return 0;
                }

                return Math.Max(0.05, (1 - _tokens) / perSecond);
            }
        }
    }
}
