using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>A named per-client bearer token. Only the SHA-256 of the token is stored; the token is shown once.</summary>
[Serializable]
public sealed class ClientTokenEntry
{
    public string Name { get; set; } = "";

    /// <summary>Upper-case hex SHA-256 of the token.</summary>
    public string TokenSha256 { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Owner-configured pre-approval: calls of <see cref="Tool"/> from the client that authenticated with the per-client token
/// named <see cref="Client"/> run without a prompt when their <see cref="Argument"/> matches one of <see cref="Prefixes"/>.
/// </summary>
[Serializable]
public sealed class AutoApproveRule
{
    public bool Enabled { get; set; } = true;

    /// <summary>Name of a per-client token (never the self-reported client name).</summary>
    public string Client { get; set; } = "";

    /// <summary>Exact tool name, e.g. "execute_command".</summary>
    public string Tool { get; set; } = "";

    /// <summary>String argument the prefixes apply to. Default "command" (execute_command).</summary>
    public string Argument { get; set; } = "command";

    /// <summary>
    /// Allowed values: the value equals a prefix, or continues after it with a space and only plain ASCII arguments. Empty
    /// means any arguments, except for execute_command, where an empty list never matches.
    /// </summary>
    public List<string> Prefixes { get; set; } = [];

    /// <summary>Also pre-approve calls that post chat other players see. Default off.</summary>
    public bool IncludeChat { get; set; }

    public AutoApproveRule Clone() => new()
    {
        Enabled = Enabled,
        Client = Client,
        Tool = Tool,
        Argument = Argument,
        Prefixes = [.. Prefixes],
        IncludeChat = IncludeChat,
    };
}

/// <summary>Outcome of a policy match, for logging (the matched prefix is owner-written, never client data).</summary>
public sealed record AutoApproveMatch(AutoApproveRule Rule, string? Prefix)
{
    public string Describe() => $"rule {Rule.Client}/{Rule.Tool}: {(Prefix is null ? "any arguments" : $"\"{Prefix}\"")}";
}

/// <summary>
/// Pure matching for <see cref="AutoApproveRule"/>. Identity comes only from a per-client token, so a client cannot match a
/// rule by choosing its clientInfo name. Argument matching is deliberately narrow: an exact prefix, then either nothing or
/// a space and plain printable ASCII without separators or shell-like metacharacters, so "/term selftest; /say hi",
/// "/term selftest\n/say hi" or "/term selftestX" never match "/term selftest".
/// </summary>
public static class AutoApprovePolicy
{
    public const int MaxClientNameLength = 64;

    /// <summary>Characters allowed after a prefix (besides ASCII letters and digits).</summary>
    private const string SafePunctuation = " -_.,:=/+@#%()[]{}!?~*'\"";

    public static AutoApproveMatch? Match(IReadOnlyList<AutoApproveRule> rules, string? authenticatedClient, string toolName, ToolPermission tier, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(authenticatedClient) || tier < ToolPermission.Action)
            return null;

        JsonObject? arguments = null;
        foreach (var rule in rules)
        {
            if (rule is not { Enabled: true } || !string.Equals(rule.Client, authenticatedClient, StringComparison.Ordinal) || !string.Equals(rule.Tool, toolName, StringComparison.Ordinal))
                continue;
            if (tier == ToolPermission.Chat && !rule.IncludeChat)
                continue;

            var prefixes = rule.Prefixes.Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (prefixes.Length == 0)
            {
                if (toolName == ConfirmationService.ExecuteCommandTool)
                    continue; // "any slash command" is never pre-approved
                return new AutoApproveMatch(rule, null);
            }

            arguments ??= ParseArguments(argumentsJson);
            var name = string.IsNullOrWhiteSpace(rule.Argument) ? "command" : rule.Argument;
            if (arguments?[name] is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                continue;
            var value = v.GetValue<string>();
            foreach (var prefix in prefixes)
            {
                if (MatchesPrefix(value, prefix))
                    return new AutoApproveMatch(rule, prefix);
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="value"/> is <paramref name="prefix"/>, or the prefix followed by a space and only ASCII
    /// letters, digits and <see cref="SafePunctuation"/>. Case-sensitive, no trimming, no normalization.
    /// </summary>
    public static bool MatchesPrefix(string value, string prefix)
    {
        if (!IsValidPrefix(prefix) || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var tail = value.AsSpan(prefix.Length);
        if (tail.IsEmpty)
            return true;
        if (tail[0] != ' ' && !prefix.EndsWith(' '))
            return false;
        foreach (var ch in tail)
        {
            if (!IsSafeChar(ch))
                return false;
        }

        return true;
    }

    /// <summary>An owner-written prefix: non-empty, printable ASCII only, no leading space.</summary>
    public static bool IsValidPrefix(string? prefix) =>
        !string.IsNullOrEmpty(prefix) && prefix[0] != ' ' && prefix.All(c => c is >= ' ' and <= '~');

    public static bool IsValidClientName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxClientNameLength && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsSafeChar(char ch) => char.IsAsciiLetterOrDigit(ch) || SafePunctuation.Contains(ch);

    private static JsonObject? ParseArguments(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
