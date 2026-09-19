using System.Net;
using System.Security.Cryptography;
using Dalamud.Configuration;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XivMcp.Core;
using XivMcp.Plugin.Services;

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
    /// <summary>
    /// 1: initial schema (also wrote the computed HostIsLoopback/EndpointUrl by mistake).
    /// 2: computed properties are no longer written; values unchanged.
    /// </summary>
    public const int CurrentVersion = 2;

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

    /// <summary>Length of an "allow everything from this client" approval session, 1-60 minutes. Sessions are never saved.</summary>
    public int ApprovalSessionMinutes { get; set; } = 5;

    /// <summary>Named per-client bearer tokens (SHA-256 only). Identify a client for <see cref="AutoApproveRules"/>.</summary>
    public List<ClientTokenEntry> ClientTokens { get; set; } = [];

    /// <summary>Owner-written pre-approvals for Action/Chat calls from a token-identified client (e.g. CI).</summary>
    public List<AutoApproveRule> AutoApproveRules { get; set; } = [];

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

    /// <summary>
    /// Creates a per-client token for <paramref name="name"/> and returns it. Only its SHA-256 is kept, so the caller must
    /// show it now; it cannot be shown again. Throws <see cref="ArgumentException"/> for an invalid or duplicate name.
    /// </summary>
    public string AddClientToken(string name, DateTimeOffset now)
    {
        name = name.Trim();
        if (!AutoApprovePolicy.IsValidClientName(name))
            throw new ArgumentException($"Client names are 1-{AutoApprovePolicy.MaxClientNameLength} characters: letters, digits, '-', '_' or '.'.");
        if (ClientTokens.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A token for '{name}' already exists; revoke it first.");
        var token = GenerateToken();
        ClientTokens = [.. ClientTokens, new ClientTokenEntry { Name = name, TokenSha256 = HashToken(token), CreatedAt = now }];
        return token;
    }

    /// <summary>Removes the token; its client can no longer connect, and rules naming it stop matching.</summary>
    public bool RevokeClientToken(string name)
    {
        var next = ClientTokens.Where(t => !string.Equals(t.Name, name, StringComparison.Ordinal)).ToList();
        if (next.Count == ClientTokens.Count)
            return false;
        ClientTokens = next;
        return true;
    }

    /// <summary>The per-client tokens as the server wants them.</summary>
    public IReadOnlyList<ClientToken> ClientTokenHashes() =>
        ClientTokens.Select(t => new ClientToken(t.Name, t.TokenSha256)).ToArray();

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>True when <see cref="Host"/> only accepts connections from this machine.</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    [JsonIgnore]
    public bool HostIsLoopback => IsLoopbackHost(Host);

    [JsonIgnore]
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

    /// <summary>
    /// Loads (or creates) the configuration, migrates it, and fills invariants. A file Dalamud cannot
    /// deserialize is backed up and every readable value (above all BearerToken) is recovered from it,
    /// so an existing token is never silently replaced.
    /// </summary>
    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        Configuration? config = null;
        var dirty = false;
        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration;
        }
        catch
        {
            // Handled below: recover from the raw file.
        }

        if (config == null)
        {
            var file = pluginInterface.ConfigFile;
            string? text = null;
            try
            {
                if (file.Exists && file.Length > 0)
                    text = File.ReadAllText(file.FullName);
            }
            catch
            {
                // Unreadable: start from defaults.
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    File.Copy(file.FullName, $"{file.FullName}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
                }
                catch
                {
                    // The backup is a courtesy; recovery below does not depend on it.
                }
            }

            config = text == null ? new Configuration() : Recover(text);
            dirty = true;
        }

        dirty |= config.Normalize();
        if (dirty)
            pluginInterface.SavePluginConfig(config);
        return config;
    }

    /// <summary>
    /// Best-effort read of a damaged config: ignores type annotations and per-member errors, and
    /// falls back to pulling the token out of text that is not valid JSON at all.
    /// </summary>
    public static Configuration Recover(string text)
    {
        try
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                Error = (_, e) => e.ErrorContext.Handled = true,
            };
            if (JToken.Parse(text) is JObject)
            {
                var recovered = JsonConvert.DeserializeObject<Configuration>(text, settings);
                if (recovered != null)
                    return recovered;
            }
        }
        catch
        {
            // Not JSON (e.g. truncated): fall through.
        }

        var config = new Configuration();
        var match = Regex.Match(text, "\"BearerToken\"\\s*:\\s*\"([A-Za-z0-9_\\-]{16,})\"");
        if (match.Success)
            config.BearerToken = match.Groups[1].Value;
        return config;
    }

    /// <summary>Clamps values and generates the token. Returns true when anything changed.</summary>
    public bool Normalize()
    {
        var changed = false;

        // Migrations. Never touch BearerToken or the permission tiers here.
        if (Version < 2)
        {
            // v1 -> v2: nothing to transform; the computed properties v1 wrote are ignored on load
            // and disappear from the file on this save.
            Version = 2;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(BearerToken))
        {
            BearerToken = GenerateToken();
            changed = true;
        }
        else if (BearerToken != BearerToken.Trim())
        {
            BearerToken = BearerToken.Trim();
            changed = true;
        }

        var host = string.IsNullOrWhiteSpace(Host) ? DefaultHost : Host.Trim();
        if (host != Host)
        {
            Host = host;
            changed = true;
        }

        changed |= Clamp(Port, 1, 65535, v => Port = v);
        changed |= Clamp(CallTimeoutSeconds, 5, 600, v => CallTimeoutSeconds = v);
        changed |= Clamp(ConfirmTimeoutSeconds, 5, 300, v => ConfirmTimeoutSeconds = v);
        changed |= Clamp(ApprovalSessionMinutes, 1, 60, v => ApprovalSessionMinutes = v);
        changed |= Clamp(ChatBufferSize, 50, 5000, v => ChatBufferSize = v);
        changed |= Clamp(AgentBoardExpiryMinutes, 0, 7 * 24 * 60, v => AgentBoardExpiryMinutes = v);

        if (!Enum.IsDefined(ActivityLogLevel))
        {
            ActivityLogLevel = ActivityLogLevel.Failures;
            changed = true;
        }

        changed |= CleanList(AllowedOrigins, v => AllowedOrigins = v);
        changed |= CleanList(DisabledCategories, v => DisabledCategories = v);

        // Tokens with a bad name or hash can never authenticate; drop them (and duplicates) rather than guess.
        var tokens = (ClientTokens ?? [])
            .Where(t => t != null && AutoApprovePolicy.IsValidClientName(t.Name) && t.TokenSha256 is { Length: 64 } h && h.All(char.IsAsciiHexDigit))
            .DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ClientTokens == null || tokens.Count != ClientTokens.Count)
        {
            ClientTokens = tokens;
            changed = true;
        }

        // Rules: drop empty ones and prefixes that could never match; everything else stays as the owner wrote it.
        var rules = (AutoApproveRules ?? [])
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Client) && !string.IsNullOrWhiteSpace(r.Tool))
            .ToList();
        foreach (var rule in rules)
        {
            var prefixes = (rule.Prefixes ?? []).Where(AutoApprovePolicy.IsValidPrefix).Distinct(StringComparer.Ordinal).ToList();
            if (rule.Prefixes == null || !prefixes.SequenceEqual(rule.Prefixes, StringComparer.Ordinal))
            {
                rule.Prefixes = prefixes;
                changed = true;
            }
        }

        if (AutoApproveRules == null || rules.Count != AutoApproveRules.Count)
        {
            AutoApproveRules = rules;
            changed = true;
        }

        return changed;
    }

    private static bool CleanList(List<string>? list, Action<List<string>> set)
    {
        var clean = (list ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list != null && clean.SequenceEqual(list, StringComparer.Ordinal))
            return false;
        set(clean);
        return true;
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
