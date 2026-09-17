using System.Net;
using System.Security.Cryptography;
using Dalamud.Configuration;
using Dalamud.Plugin;
using XivMcp.Core;

namespace XivMcp.Plugin;

/// <summary>What the plugin writes to the Dalamud log for each handled MCP request.</summary>
public enum ActivityLogLevel
{
    /// <summary>Nothing (the in-game Activity tab still shows everything).</summary>
    Off = 0,

    /// <summary>Failed requests only.</summary>
    Failures = 1,

    /// <summary>Failures plus tools/call, resources/read and prompts/get.</summary>
    Calls = 2,

    /// <summary>Every request, including initialize, list and ping traffic.</summary>
    All = 3,
}

/// <summary>
/// Persisted by Dalamud to pluginConfigs/XivMcp.json (Newtonsoft, TypeNameHandling.Objects).
/// Keep collection defaults empty: Newtonsoft appends into pre-populated collections on load.
/// Written only from the framework/UI thread; server threads read it. Collections are replaced
/// wholesale (never mutated in place) so readers see a consistent reference.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 41800;

    public int Version { get; set; } = CurrentVersion;

    // ---- server --------------------------------------------------------------------------

    /// <summary>Start the server when the plugin loads.</summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = DefaultHost;

    public int Port { get; set; } = DefaultPort;

    /// <summary>256-bit url-safe random token, generated on first run. Never logged.</summary>
    public string BearerToken { get; set; } = "";

    public bool RequireToken { get; set; } = true;

    /// <summary>Extra Origin values accepted (loopback origins are always accepted).</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    public int CallTimeoutSeconds { get; set; } = 30;

    // ---- permissions -----------------------------------------------------------------------

    public bool AllowRead { get; set; } = true;

    public bool AllowUi { get; set; } = true;

    public bool AllowAction { get; set; }

    public bool AllowChat { get; set; }

    /// <summary>Action/Chat calls wait for an in-game Approve/Deny before running.</summary>
    public bool ConfirmActions { get; set; } = true;

    /// <summary>Seconds a confirmation waits before it is denied automatically.</summary>
    public int ConfirmTimeoutSeconds { get; set; } = 20;

    /// <summary>Provider categories switched off (everything else is on).</summary>
    public List<string> DisabledCategories { get; set; } = [];

    // ---- providers / UI ----------------------------------------------------------------------

    /// <summary>Lines kept by the chat provider's ring buffer (read_chat).</summary>
    public int ChatBufferSize { get; set; } = 500;

    public ActivityLogLevel ActivityLogLevel { get; set; } = ActivityLogLevel.Failures;

    /// <summary>Show "MCP ● n" in the server info bar.</summary>
    public bool ShowDtrEntry { get; set; } = true;

    /// <summary>Raise a Dalamud notification when an agent posts state done/failed.</summary>
    public bool NotifyAgentCompletion { get; set; } = true;

    /// <summary>Board entries not updated for this many minutes are dropped. 0 = keep until cleared.</summary>
    public int AgentBoardExpiryMinutes { get; set; } = 120;

    // ---- helpers (not persisted state) -------------------------------------------------------

    public bool IsPermitted(ToolPermission permission) => permission switch
    {
        ToolPermission.Read => AllowRead,
        ToolPermission.Ui => AllowUi,
        ToolPermission.Action => AllowAction,
        ToolPermission.Chat => AllowChat,
        _ => false,
    };

    public void SetPermitted(ToolPermission permission, bool value)
    {
        switch (permission)
        {
            case ToolPermission.Read: AllowRead = value; break;
            case ToolPermission.Ui: AllowUi = value; break;
            case ToolPermission.Action: AllowAction = value; break;
            case ToolPermission.Chat: AllowChat = value; break;
        }
    }

    public bool IsCategoryEnabled(string category)
    {
        var disabled = DisabledCategories;
        foreach (var c in disabled)
        {
            if (string.Equals(c, category, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public void SetCategoryEnabled(string category, bool enabled)
    {
        var next = DisabledCategories.Where(c => !string.Equals(c, category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!enabled)
            next.Add(category);
        DisabledCategories = next;
    }

    /// <summary>True when <see cref="Host"/> only accepts connections from this machine.</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    public bool HostIsLoopback => IsLoopbackHost(Host);

    public string EndpointUrl
    {
        get
        {
            var host = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
            return $"http://{host}:{Port}/mcp";
        }
    }

    /// <summary>Returns a new 256-bit token encoded as unpadded base64url (43 chars).</summary>
    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Loads (or creates) the configuration, migrates it, and fills invariants.</summary>
    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        Configuration config;
        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch
        {
            // A corrupt file must not stop the plugin loading; the old token is lost in that case.
            config = new Configuration();
        }

        var dirty = config.Normalize();
        if (dirty)
            pluginInterface.SavePluginConfig(config);
        return config;
    }

    /// <summary>Clamps values and generates the token. Returns true when anything changed.</summary>
    public bool Normalize()
    {
        var changed = false;
        if (Version < CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(BearerToken))
        {
            BearerToken = GenerateToken();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            Host = DefaultHost;
            changed = true;
        }

        changed |= Clamp(Port, 1, 65535, v => Port = v);
        changed |= Clamp(CallTimeoutSeconds, 5, 600, v => CallTimeoutSeconds = v);
        changed |= Clamp(ConfirmTimeoutSeconds, 5, 300, v => ConfirmTimeoutSeconds = v);
        changed |= Clamp(ChatBufferSize, 50, 5000, v => ChatBufferSize = v);
        changed |= Clamp(AgentBoardExpiryMinutes, 0, 7 * 24 * 60, v => AgentBoardExpiryMinutes = v);

        AllowedOrigins ??= [];
        DisabledCategories ??= [];
        return changed;
    }

    private static bool Clamp(int value, int min, int max, Action<int> set)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped == value)
            return false;
        set(clamped);
        return true;
    }
}
