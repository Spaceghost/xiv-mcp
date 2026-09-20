using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

/// <summary>
/// The two bridges that exist on the developer's machine: GhosttyDalamud (in-game terminal panels) and
/// XivDesktop (the host desktop's apps and windows). Queries are Read; anything that opens, focuses, closes
/// or launches is Action and goes through the in-game approval path like every other Action tool.
/// </summary>
[McpProvider("bridges")]
public sealed class DesktopBridgeProvider
{
    private const string GhosttyCall = "GhosttyDalamud.v1.Call";

    /// <summary>Ghostty verbs that only read a snapshot.</summary>
    private static readonly string[] GhosttyQueries = ["window.list", "agent.status", "agent.windows", "agent.apps", "focus.get", "status"];

    /// <summary>Ghostty verbs that change something and therefore need approval.</summary>
    private static readonly string[] GhosttyCommands =
        ["window.open", "window.close", "window.focus", "window.hide", "window.toggle_pet", "window.place", "terminal.new", "focus.cycle", "agent.windows.refresh"];

    private static readonly string[] DesktopQueries = ["status", "windows", "apps", "palette"];

    private readonly BridgeRegistry registry;

    public DesktopBridgeProvider(IDalamudPluginInterface pluginInterface, IPluginLog log) =>
        registry = new BridgeRegistry(pluginInterface, log);

    public sealed record BridgeJsonDto(string Bridge, string Method, bool Ok, JsonNode? Result, string? Error, string Raw);

    [McpTool("ghostty_query",
        Sources = ["ipc:GhosttyDalamud.v1.Call"],
        Title = "Query the in-game terminal bridge",
        Description =
            "Reads GhosttyDalamud's state over IPC. method is one of: window.list (every terminal panel with id, title, app, size, " +
            "state, kind, anchor, hidden, focused, plus the last request results), agent.status, agent.windows, agent.apps, " +
            "focus.get (what has the keyboard) or status (one-line text). Read-only: these verbs answer from a snapshot at most one " +
            "frame old and change nothing, so no approval is needed. Fails when GhosttyDalamud is not loaded — check list_bridges. " +
            "Use ghostty_command to open or focus a panel.")]
    public BridgeJsonDto GhosttyQuery(
        [McpParam("Read verb.", Enum = ["window.list", "agent.status", "agent.windows", "agent.apps", "focus.get", "status"])]
        string method = "window.list")
    {
        var normalized = Normalize(method, GhosttyQueries, "ghostty_query");
        registry.Require("ghostty");
        return CallGhostty(normalized, null);
    }

    [McpTool("ghostty_command",
        Sources = ["ipc:GhosttyDalamud.v1.Call"],
        ApprovalSummary = "Ghostty terminal panels: {method} {parameters}",
        Title = "Command the in-game terminal bridge",
        Permission = ToolPermission.Action,
        Idempotent = false,
        Description =
            "Changes a GhosttyDalamud terminal panel. method is one of: window.open (params: run | match | wid, optional pin such as " +
            "\"here\", \"me 2 1.7\", \"target\", \"orbit 3.5\", \"hud X Y\", \"pet\"), window.close {id}, window.focus {id}, " +
            "window.hide {id, hidden}, window.toggle_pet {id}, window.place {id, pin}, terminal.new {profile, pin}, focus.cycle {dir}, " +
            "agent.windows.refresh. params is a JSON object as text. The bridge validates and queues the change and answers " +
            "{queued: true, request: N}; the outcome shows up in ghostty_query window.list under requests. " +
            "This changes the player's screen, so it goes through the normal in-game approval.")]
    public BridgeJsonDto GhosttyCommand(
        [McpParam("Command verb.", Enum = ["window.open", "window.close", "window.focus", "window.hide", "window.toggle_pet", "window.place", "terminal.new", "focus.cycle", "agent.windows.refresh"])]
        string method,
        [McpParam("Parameters as a JSON object, e.g. {\"id\": 3} or {\"run\": \"btop\", \"pin\": \"orbit 3\"}. Omit when the verb takes none.")]
        string? parameters = null)
    {
        var normalized = Normalize(method, GhosttyCommands, "ghostty_command");
        registry.Require("ghostty");

        JsonObject? payload = null;
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            try
            {
                payload = JsonNode.Parse(parameters) as JsonObject
                          ?? throw new McpToolException("parameters must be a JSON object, e.g. {\"id\": 3}.");
            }
            catch (JsonException ex)
            {
                throw new McpToolException($"parameters is not valid JSON: {ex.Message}");
            }
        }

        return CallGhostty(normalized, payload);
    }

    [McpTool("desktop_query",
        Sources = ["ipc:XivDesktop.v1"],
        Title = "Query the host desktop bridge",
        RequiresLogin = false,
        Description =
            "Reads XivDesktop's view of the host desktop. method is status (bridge health), windows (open windows with id, title, app, " +
            "workspace, focused), apps (launchable applications) or palette (ranked command-palette entries for query). Read-only; " +
            "nothing is launched or focused. Fails when XivDesktop is not loaded — check list_bridges. " +
            "Use desktop_command to act on a window or launch an application.")]
    public BridgeJsonDto DesktopQuery(
        [McpParam("What to read.", Enum = ["status", "windows", "apps", "palette"])] string method = "status",
        [McpParam("Search text, only used by palette.")] string? query = null)
    {
        var normalized = Normalize(method, DesktopQueries, "desktop_query");
        registry.Require("xivdesktop");

        var raw = normalized switch
        {
            "status" => registry.Invoke("XivDesktop.v1.Status"),
            "windows" => registry.Invoke("XivDesktop.v1.Windows"),
            "apps" => registry.Invoke("XivDesktop.v1.ListApps"),
            _ => registry.Invoke("XivDesktop.v1.Palette", query ?? ""),
        };

        return Parse("xivdesktop", normalized, raw);
    }

    [McpTool("desktop_command",
        Sources = ["ipc:XivDesktop.v1"],
        ApprovalSummary = "XivDesktop: {method} {argument}",
        Title = "Command the host desktop bridge",
        Permission = ToolPermission.Action,
        Idempotent = false,
        RequiresLogin = false,
        Description =
            "Acts on the host desktop through XivDesktop. method launch starts an application (argument: the app id or a search text); " +
            "method window sends a window action (argument: JSON such as {\"action\":\"focus\",\"id\":12} — actions are focus, close, " +
            "pet, pin, toggle, place, move); method workspace switches to workspace N (argument: \"1\"-\"9\", \"0\" only reports). " +
            "The bridge answers \"ok: ...\" or \"error: ...\". This starts programs on the player's computer, so it always goes through " +
            "the in-game approval path.")]
    public BridgeJsonDto DesktopCommand(
        [McpParam("What to do.", Enum = ["launch", "window", "workspace"])] string method,
        [McpParam("Argument for the method: an app id or search text, a window-action JSON object, or a workspace number.")]
        string argument)
    {
        var normalized = Normalize(method, ["launch", "window", "workspace"], "desktop_command");
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new McpToolException("argument is required.");
        }

        registry.Require("xivdesktop");
        var trimmed = argument.Trim();
        string raw;
        if (normalized == "workspace")
        {
            if (!int.TryParse(trimmed, out var workspace) || workspace is < 0 or > 9)
            {
                throw new McpToolException("workspace takes a number from 0 to 9 (0 only reports the current one).");
            }

            raw = InvokeWorkspace(workspace);
        }
        else
        {
            raw = registry.Invoke(normalized == "launch" ? "XivDesktop.v1.Launch" : "XivDesktop.v1.WindowAction", trimmed);
        }

        var ok = !raw.StartsWith("error", StringComparison.OrdinalIgnoreCase);
        return new BridgeJsonDto("xivdesktop", normalized, ok, null, ok ? null : raw, raw);
    }

    private string InvokeWorkspace(int workspace)
    {
        try
        {
            return registry.Invoke("XivDesktop.v1.Workspace", workspace);
        }
        catch (BridgeCallException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    private BridgeJsonDto CallGhostty(string method, JsonObject? parameters)
    {
        var request = new JsonObject
        {
            ["method"] = method,
            ["caller"] = "XivMcp",
        };
        if (parameters != null)
        {
            request["params"] = parameters.DeepClone();
        }

        string raw;
        try
        {
            raw = registry.Invoke(GhosttyCall, request.ToJsonString());
        }
        catch (BridgeCallException ex)
        {
            throw new McpToolException(ex.Message);
        }

        return Parse("ghostty", method, raw);
    }

    private static BridgeJsonDto Parse(string bridge, string method, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new BridgeJsonDto(bridge, method, false, null, "The bridge returned an empty answer.", "");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            // Some gates answer with plain text ("ghostty 3", "ok: launched ...").
            return new BridgeJsonDto(bridge, method, !raw.StartsWith("error", StringComparison.OrdinalIgnoreCase), null, null, Truncate(raw));
        }

        if (node is JsonObject envelope && envelope["ok"] is { } okNode)
        {
            var ok = okNode.GetValueKind() == JsonValueKind.True;
            return new BridgeJsonDto(
                bridge,
                method,
                ok,
                ok ? envelope["result"]?.DeepClone() : null,
                ok ? null : envelope["error"]?.ToString(),
                Truncate(raw));
        }

        return new BridgeJsonDto(bridge, method, true, node?.DeepClone(), null, Truncate(raw));
    }

    private static string Truncate(string raw) => raw.Length <= 8000 ? raw : raw[..8000] + "...";

    private static string Normalize(string? method, IReadOnlyList<string> allowed, string tool)
    {
        var trimmed = method?.Trim() ?? "";
        var match = allowed.FirstOrDefault(m => m.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new McpToolException(
            $"{tool} does not accept method \"{method}\". Use one of: {string.Join(", ", allowed)}.");
    }
}
