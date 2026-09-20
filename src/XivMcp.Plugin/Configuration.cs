using System.Net;
using System.Security.Cryptography;
using Dalamud.Configuration;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XivMcp.Core;
using XivMcp.Core.Net;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin;

/// <summary>
/// Which addresses the MCP listener binds. Anything but <see cref="Loopback"/> exposes the game to other
/// machines and forces the bearer token on.
/// </summary>
public enum BindMode
{
    /// <summary>127.0.0.1 only: nothing outside this machine can connect. The default.</summary>
    Loopback = 0,

    /// <summary>127.0.0.1 plus this machine's Tailscale address, so tailnet machines can connect too.</summary>
    LoopbackAndTailnet = 1,

    /// <summary>The Tailscale address only; local clients must use it as well.</summary>
    TailnetOnly = 2,

    /// <summary>An address typed by the owner.</summary>
    Custom = 3,
}

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
    /// 3: the single <see cref="Host"/> string became <see cref="BindMode"/> + <see cref="CustomHost"/>.
    ///    <see cref="Host"/> is still read (and still written, so an older build keeps working).
    /// </summary>
    public const int CurrentVersion = 3;

    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 41800;

    public const string DefaultPath = "/mcp";

    public int Version { get; set; } = CurrentVersion;

    // ---- provisioning ----------------------------------------------------------------------
    //
    // While a provision file is present its values win over everything saved here. The stored
    // values below are still what gets written to XivMcp.json, so removing the file restores
    // exactly what the owner had configured in the window.

    private bool enabled = true;
    private BindMode bindMode = BindMode.Loopback;
    private string customHost = DefaultHost;
    private int port = DefaultPort;
    private string path = DefaultPath;
    private string bearerToken = "";
    private bool requireToken = true;
    private List<string> allowedOrigins = [];
    private int callTimeoutSeconds = 30;

    /// <summary>
    /// Settings handed down by an outside provisioning file, or null. Set by <see cref="Services.ProvisionStore"/>;
    /// never serialized and never written back to the file it came from.
    /// </summary>
    [JsonIgnore]
    public ProvisionDocument? Provision { get; set; }

    /// <summary>True when <paramref name="field"/> currently comes from the provision file and the UI must not offer to change it.</summary>
    public bool IsProvisioned(string field) => Provision?.Has(field) == true;

    // ---- server --------------------------------------------------------------------------

    /// <summary>Start the server when the plugin loads.</summary>
    [JsonIgnore]
    public bool Enabled
    {
        get => Provision?.Enabled ?? enabled;
        set => enabled = value;
    }

    /// <summary>Which addresses the listener binds. Replaces the bare <see cref="Host"/> string of schema 2.</summary>
    [JsonIgnore]
    public BindMode BindMode
    {
        get => Provision?.BindMode ?? bindMode;
        set => bindMode = value;
    }

    /// <summary>The address typed by the owner; only used by <see cref="Plugin.BindMode.Custom"/>.</summary>
    [JsonIgnore]
    public string CustomHost
    {
        get => Provision?.CustomHost ?? customHost;
        set => customHost = value;
    }

    /// <summary>
    /// Schema 2's single listen address. Kept readable (and written) so a downgrade still starts, and so
    /// the v2 → v3 migration can see what the owner had. <see cref="BindMode"/> is what actually binds.
    /// </summary>
    [JsonProperty("Host")]
    public string Host { get; set; } = DefaultHost;

    [JsonIgnore]
    public int Port
    {
        get => Provision?.Port ?? port;
        set => port = value;
    }

    /// <summary>Endpoint path, default <c>/mcp</c>.</summary>
    [JsonIgnore]
    public string Path
    {
        get => Provision?.Path ?? path;
        set => path = value;
    }

    /// <summary>256-bit url-safe random token, generated on first run. Never logged.</summary>
    [JsonIgnore]
    public string BearerToken
    {
        get => Provision?.BearerToken ?? bearerToken;
        set => bearerToken = value;
    }

    [JsonIgnore]
    public bool RequireToken
    {
        get => Provision?.RequireToken ?? requireToken;
        set => requireToken = value;
    }

    /// <summary>Extra Origin values accepted (loopback origins are always accepted).</summary>
    [JsonIgnore]
    public List<string> AllowedOrigins
    {
        get => Provision?.AllowedOrigins ?? allowedOrigins;
        set => allowedOrigins = value;
    }

    [JsonIgnore]
    public int CallTimeoutSeconds
    {
        get => Provision?.CallTimeoutSeconds ?? callTimeoutSeconds;
        set => callTimeoutSeconds = value;
    }

    // Serialized members for the overlaid settings above. They hold the owner's own values, which is
    // what must survive in XivMcp.json even while a provision file overrides them in memory.
    [JsonProperty("Enabled")]
    private bool StoredEnabled { get => enabled; set => enabled = value; }

    [JsonProperty("BindMode")]
    private BindMode StoredBindMode { get => bindMode; set => bindMode = value; }

    [JsonProperty("CustomHost")]
    private string StoredCustomHost { get => customHost; set => customHost = value ?? DefaultHost; }

    [JsonProperty("Port")]
    private int StoredPort { get => port; set => port = value; }

    [JsonProperty("Path")]
    private string StoredPath { get => path; set => path = value ?? DefaultPath; }

    [JsonProperty("BearerToken")]
    private string StoredBearerToken { get => bearerToken; set => bearerToken = value ?? ""; }

    [JsonProperty("RequireToken")]
    private bool StoredRequireToken { get => requireToken; set => requireToken = value; }

    [JsonProperty("AllowedOrigins")]
    private List<string> StoredAllowedOrigins { get => allowedOrigins; set => allowedOrigins = value ?? []; }

    [JsonProperty("CallTimeoutSeconds")]
    private int StoredCallTimeoutSeconds { get => callTimeoutSeconds; set => callTimeoutSeconds = value; }

    // ---- permissions -----------------------------------------------------------------------

    public bool AllowRead { get; set; } = true;

    public bool AllowUi { get; set; } = true;

    public bool AllowAction { get; set; }

    public bool AllowChat { get; set; }

    /// <summary>Action/Chat calls wait for an in-game Approve/Deny before running.</summary>
    public bool ConfirmActions { get; set; } = true;

    /// <summary>Seconds a confirmation waits before it is denied automatically.</summary>
    [JsonIgnore]
    public int ConfirmTimeoutSeconds
    {
        get => Provision?.ConfirmTimeoutSeconds ?? confirmTimeoutSeconds;
        set => confirmTimeoutSeconds = value;
    }

    [JsonProperty("ConfirmTimeoutSeconds")]
    private int StoredConfirmTimeoutSeconds { get => confirmTimeoutSeconds; set => confirmTimeoutSeconds = value; }

    private int confirmTimeoutSeconds = 20;

    /// <summary>Length of an "allow everything from this client" approval session, 1-60 minutes. Sessions are never saved.</summary>
    public int ApprovalSessionMinutes { get; set; } = 5;

    /// <summary>Named per-client bearer tokens (SHA-256 only). Identify a client for <see cref="AutoApproveRules"/>.</summary>
    public List<ClientTokenEntry> ClientTokens { get; set; } = [];

    /// <summary>Owner-written pre-approvals for Action/Chat calls from a token-identified client (e.g. CI).</summary>
    public List<AutoApproveRule> AutoApproveRules { get; set; } = [];

    /// <summary>Provider categories switched off (everything else is on).</summary>
    [JsonIgnore]
    public List<string> DisabledCategories
    {
        get => Provision?.DisabledCategories ?? disabledCategories;
        set => disabledCategories = value;
    }

    [JsonProperty("DisabledCategories")]
    private List<string> StoredDisabledCategories { get => disabledCategories; set => disabledCategories = value ?? []; }

    private List<string> disabledCategories = [];

    // ---- providers / UI ----------------------------------------------------------------------

    /// <summary>Lines kept by the chat provider's ring buffer (read_chat).</summary>
    public int ChatBufferSize { get; set; } = 500;

    /// <summary>Tool calls, resource reads and prompt gets allowed per minute for each client; 0 switches the limit off.</summary>
    public int RateLimitPerMinute { get; set; } = 600;

    public ActivityLogLevel ActivityLogLevel { get; set; } = ActivityLogLevel.Failures;

    /// <summary>Show "MCP ● n" in the server info bar.</summary>
    public bool ShowDtrEntry { get; set; } = true;

    /// <summary>Raise a Dalamud notification when an agent posts state done/failed.</summary>
    public bool NotifyAgentCompletion { get; set; } = true;

    /// <summary>Board entries not updated for this many minutes are dropped. 0 = keep until cleared.</summary>
    public int AgentBoardExpiryMinutes { get; set; } = 120;

    // ---- objectives ----------------------------------------------------------------------

    /// <summary>Show the custom objectives overlay (under the game's Duty List).</summary>
    public bool ShowObjectives { get; set; } = true;

    /// <summary>Pin the overlay under the Duty List (_ToDoList); off makes it a movable window.</summary>
    public bool ObjectivesFollowDutyList { get; set; } = true;

    /// <summary>Show a toast when an objective's conditions become ready.</summary>
    public bool NotifyObjectiveReady { get; set; } = true;

    /// <summary>Also list completed objectives in the overlay (greyed) until they are cleared.</summary>
    public bool ShowCompletedObjectives { get; set; }

    // ---- local model (for companion plugins; XivMcp never runs it) ------------------------

    /// <summary>OpenAI-compatible base URL of a local model server (e.g. http://127.0.0.1:11434/v1). "" = not configured.</summary>
    public string LocalModelEndpoint { get; set; } = "";

    /// <summary>Model id at <see cref="LocalModelEndpoint"/>. "" = not configured.</summary>
    public string LocalModelName { get; set; } = "";

    /// <summary>Optional API key for the local server. Never logged and never returned over IPC (only whether one is set).</summary>
    public string LocalModelApiKey { get; set; } = "";

    /// <summary>Other plugins may issue themselves a client token over IPC (XivMcp.ConnectClient).</summary>
    public bool AllowIpcClientTokens { get; set; } = true;

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
    public string AddClientToken(string name, DateTimeOffset now, string? createdVia = null)
    {
        name = name.Trim();
        if (!AutoApprovePolicy.IsValidClientName(name))
            throw new ArgumentException($"Client names are 1-{AutoApprovePolicy.MaxClientNameLength} characters: letters, digits, '-', '_' or '.'.");
        if (ClientTokens.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A token for '{name}' already exists; revoke it first.");
        var token = GenerateToken();
        ClientTokens = [.. ClientTokens, new ClientTokenEntry { Name = name, TokenSha256 = HashToken(token), CreatedAt = now, CreatedVia = createdVia }];
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

    /// <summary>True when the address only accepts connections from this machine.</summary>
    public static bool IsLoopbackHost(string host) => BindPlanner.IsLoopbackHost(host);

    /// <summary>
    /// The bind plan for the current mode. <paramref name="tailnet"/> is the detected tailnet address
    /// (null when Tailscale is down or absent), which is why resolution lives outside this class.
    /// </summary>
    public BindPlan ResolveBindPlan(TailnetAddress? tailnet) => BindPlanner.Resolve(BindMode, CustomHost, tailnet);

    /// <summary>True when the configured mode cannot reach past this machine, ignoring live detection.</summary>
    [JsonIgnore]
    public bool HostIsLoopback => BindMode == BindMode.Loopback || (BindMode == BindMode.Custom && IsLoopbackHost(CustomHost));

    /// <summary>The loopback endpoint. <see cref="Services.ServerHost.Endpoints"/> is the full, mode-aware list.</summary>
    [JsonIgnore]
    public string EndpointUrl => BindPlanner.Endpoint(
        BindMode == BindMode.Custom ? BindPlanner.NormalizeCustomHost(CustomHost) : BindPlanner.Loopback,
        Port,
        Path);

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

        if (Version < 3)
        {
            // v2 -> v3: the single Host string becomes a bind mode. Loopback keeps the default mode;
            // anything else was a deliberate choice, so it is preserved verbatim as a custom address
            // rather than guessed at (a hand-written tailnet address stays exactly that address).
            var previous = string.IsNullOrWhiteSpace(Host) ? DefaultHost : Host.Trim();
            bindMode = IsLoopbackHost(previous) ? BindMode.Loopback : BindMode.Custom;
            customHost = previous;
            Version = 3;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            bearerToken = GenerateToken();
            changed = true;
        }
        else if (bearerToken != bearerToken.Trim())
        {
            bearerToken = bearerToken.Trim();
            changed = true;
        }

        if (!Enum.IsDefined(bindMode))
        {
            bindMode = BindMode.Loopback;
            changed = true;
        }

        var custom = string.IsNullOrWhiteSpace(customHost) ? DefaultHost : customHost.Trim();
        if (custom != customHost)
        {
            customHost = custom;
            changed = true;
        }

        var normalizedPath = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.Trim();
        if (!normalizedPath.StartsWith('/'))
            normalizedPath = "/" + normalizedPath;
        if (normalizedPath.Length > 1)
            normalizedPath = normalizedPath.TrimEnd('/');
        if (normalizedPath != path)
        {
            path = normalizedPath;
            changed = true;
        }

        // Keep the schema-2 field in step with the mode so an older build (or a reader that only knows
        // "Host") still sees a sane address. It is never read again once Version is 3.
        var legacyHost = bindMode == BindMode.Custom ? customHost : DefaultHost;
        if (legacyHost != Host)
        {
            Host = legacyHost;
            changed = true;
        }

        changed |= Clamp(port, 1, 65535, v => port = v);
        changed |= Clamp(callTimeoutSeconds, 5, 600, v => callTimeoutSeconds = v);
        changed |= Clamp(confirmTimeoutSeconds, 5, 300, v => confirmTimeoutSeconds = v);
        changed |= Clamp(ApprovalSessionMinutes, 1, 60, v => ApprovalSessionMinutes = v);
        changed |= Clamp(ChatBufferSize, 50, 5000, v => ChatBufferSize = v);
        changed |= Clamp(AgentBoardExpiryMinutes, 0, 7 * 24 * 60, v => AgentBoardExpiryMinutes = v);

        if (!Enum.IsDefined(ActivityLogLevel))
        {
            ActivityLogLevel = ActivityLogLevel.Failures;
            changed = true;
        }

        // Local model: an endpoint that is not an absolute http(s) URL is dropped rather than guessed at.
        var modelEndpoint = LocalModelProbe.NormalizeEndpoint(LocalModelEndpoint) ?? "";
        if (modelEndpoint != LocalModelEndpoint)
        {
            LocalModelEndpoint = modelEndpoint;
            changed = true;
        }

        var modelName = (LocalModelName ?? "").Trim();
        if (modelName != LocalModelName)
        {
            LocalModelName = modelName;
            changed = true;
        }

        var modelKey = (LocalModelApiKey ?? "").Trim();
        if (modelKey != LocalModelApiKey)
        {
            LocalModelApiKey = modelKey;
            changed = true;
        }

        // The stored fields, not the provision-aware properties: normalising must never write a
        // provisioned value back into the user's own configuration.
        changed |= CleanList(allowedOrigins, v => allowedOrigins = v);
        changed |= CleanList(disabledCategories, v => disabledCategories = v);

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
