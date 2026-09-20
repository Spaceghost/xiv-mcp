// FROZEN CONTRACT: shared by every provider. Additive changes only.
namespace XivMcp.Core;

/// <summary>
/// Marks a class whose public instance methods carry <see cref="McpToolAttribute"/>,
/// <see cref="McpResourceAttribute"/>, <see cref="McpResourceTemplateAttribute"/> or
/// <see cref="McpPromptAttribute"/>. The host discovers providers by reflection and
/// constructs them itself (see <see cref="McpServer.RegisterProvider"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class McpProviderAttribute : Attribute
{
    /// <summary>Grouping shown in the UI and used for enable/disable, e.g. "character", "inventory".</summary>
    public string Category { get; }

    public McpProviderAttribute(string category) => Category = category;
}

/// <summary>
/// Declares an MCP tool. Method shape:
/// <c>T Name(args..., ToolContext ctx?, CancellationToken ct?)</c> or <c>Task&lt;T&gt;</c> / <c>Task</c>.
/// Arguments bind by name from the JSON arguments object (C# camelCase name == JSON name).
/// Optional C# parameters (with defaults) are optional in the input schema.
/// The return value is serialized as camelCase JSON into both a text content block and
/// <c>structuredContent</c>; returning <see cref="ToolResult"/> gives full control.
/// Throw <see cref="McpToolException"/> for user-facing failures (becomes <c>isError: true</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class McpToolAttribute : Attribute
{
    /// <summary>snake_case tool name, unique across the server, e.g. "get_player".</summary>
    public string Name { get; }

    public McpToolAttribute(string name) => Name = name;

    public string? Title { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Tier gate checked on every call and when listing tools.</summary>
    public ToolPermission Permission { get; init; } = ToolPermission.Read;

    /// <summary>
    /// True (default): the whole method runs on the game's framework thread via
    /// <see cref="IGameThread"/>. Set false for async methods that dispatch only the
    /// game-touching parts themselves through <see cref="ToolContext.Game"/>.
    /// </summary>
    public bool GameThread { get; init; } = true;

    /// <summary>Reject the call with a clear error while no character is logged in.</summary>
    public bool RequiresLogin { get; init; } = true;

    public bool Destructive { get; init; }

    public bool Idempotent { get; init; } = true;

    /// <summary>MCP openWorldHint: interacts with entities outside the local client (other players, servers).</summary>
    public bool OpenWorld { get; init; }

    /// <summary>
    /// Where the answer comes from, as short stable labels: e.g. "lumina:Item" (a game data sheet), "client:InventoryManager"
    /// (game client memory), "dalamud:IPartyList" (a Dalamud service), "ipc:GhosttyDalamud.v1.Call", "http:universalis.app",
    /// "file:dalamud.log". Published as <c>_meta["dev.xivmcp/dataSources"]</c> and in the machine-readable catalogue.
    /// </summary>
    public string[]? Sources { get; init; }

    /// <summary>
    /// <see cref="ToolAvailability.Static"/>: needs only the installed game data (and perhaps the network), so the
    /// standalone host serves it while the game is closed. <see cref="ToolAvailability.Live"/> (default): needs the running game.
    /// </summary>
    public ToolAvailability Availability { get; init; } = ToolAvailability.Live;

    /// <summary>
    /// One sentence telling the player what this call will do, shown in the in-game approval window and written to
    /// the action log. <c>{argumentName}</c> is replaced by that argument's value, verbatim (missing arguments render as
    /// "(default)"). Every tool that <see cref="NeedsApproval"/> should have one; without it the player sees only the tool name and its arguments.
    /// </summary>
    public string? ApprovalSummary { get; init; }

    /// <summary>
    /// Sends a <see cref="ToolPermission.Ui"/> tool through the approval gate too. Action and Chat tools always go
    /// through it; Ui tools only when what they change outlives the call (the map flag, an opened game window).
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>True when the call is put to <see cref="McpServer.Approver"/> before it runs.</summary>
    public bool NeedsApproval => Permission >= ToolPermission.Action || RequiresApproval;
}

/// <summary>Whether a tool needs the running game. See <see cref="McpToolAttribute.Availability"/>.</summary>
public enum ToolAvailability
{
    /// <summary>Reads or changes the running game client.</summary>
    Live = 0,

    /// <summary>Needs only the installed game data (and perhaps the network).</summary>
    Static = 1,
}

/// <summary>Describes a tool/prompt parameter in the generated JSON schema.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class McpParamAttribute : Attribute
{
    public string Description { get; }

    public McpParamAttribute(string description) => Description = description;

    /// <summary>Allowed string values (JSON schema enum). C# enums get this automatically.</summary>
    public string[]? Enum { get; init; }

    /// <summary>Inclusive numeric bounds; NaN means unset.</summary>
    public double Minimum { get; init; } = double.NaN;

    public double Maximum { get; init; } = double.NaN;
}

/// <summary>
/// A fixed resource, e.g. <c>ffxiv://player</c>. Method returns string (text), byte[] (blob),
/// or any object (serialized as application/json). May take ToolContext / CancellationToken.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class McpResourceAttribute : Attribute
{
    public string Uri { get; }

    public McpResourceAttribute(string uri) => Uri = uri;

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string MimeType { get; init; } = "application/json";

    public bool GameThread { get; init; } = true;

    public bool RequiresLogin { get; init; } = true;
}

/// <summary>
/// An RFC 6570 level-1 template, e.g. <c>ffxiv://item/{id}</c>. Template variables bind to
/// method parameters by name (converted from string).
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class McpResourceTemplateAttribute : Attribute
{
    public string UriTemplate { get; }

    public McpResourceTemplateAttribute(string uriTemplate) => UriTemplate = uriTemplate;

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string MimeType { get; init; } = "application/json";

    public bool GameThread { get; init; } = true;

    public bool RequiresLogin { get; init; }
}

/// <summary>
/// A prompt. String parameters become prompt arguments (required unless optional in C#).
/// Return <see cref="PromptResult"/> or a string (becomes a single user message).
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class McpPromptAttribute : Attribute
{
    public string Name { get; }

    public McpPromptAttribute(string name) => Name = name;

    public string? Title { get; init; }

    public string Description { get; init; } = "";
}

/// <summary>
/// Permission tiers. Each is a separate toggle in the plugin configuration.
/// </summary>
public enum ToolPermission
{
    /// <summary>Observes game state or static game data. Default: enabled.</summary>
    Read = 0,

    /// <summary>Local-only visible effects: echo to own chat log, toasts, map flags, opening windows. Default: enabled.</summary>
    Ui = 1,

    /// <summary>Changes local client state: targeting, gearsets, teleport, arbitrary slash commands. Default: disabled.</summary>
    Action = 2,

    /// <summary>Sends text other players can see (say/party/tell/FC...). Default: disabled.</summary>
    Chat = 3,
}
