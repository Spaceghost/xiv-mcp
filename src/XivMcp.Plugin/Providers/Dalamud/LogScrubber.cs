using System.Text.RegularExpressions;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>
/// Removes things from a log line that must not leave the machine: credentials, network addresses, e-mail addresses
/// and the operating-system user name in home paths. Pure; every line a Dalamud tool returns goes through
/// <see cref="Scrub"/>. It errs on the side of removing too much (a four-part number that is not introduced as a
/// version is treated as an IPv4 address, a 40-character commit hash as a secret). Character names are left alone:
/// the log is the player's own and the client already shows them.
/// </summary>
public static partial class LogScrubber
{
    /// <summary>Longest input looked at; the rest is dropped before scrubbing so a regex never runs over megabytes.</summary>
    public const int MaxInput = 8000;

    private const int TimeoutMs = 250;

    public static string Scrub(string? text, string? userName = null)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length > MaxInput)
            text = text[..MaxInput];

        try
        {
            text = AuthorizationHeader().Replace(text, "${name}${sep}<redacted:authorization>");
            text = Bearer().Replace(text, "${scheme} <redacted:token>");
            text = Jwt().Replace(text, "<redacted:token>");
            text = UrlCredentials().Replace(text, "://<redacted:credentials>@");
            text = SecretAssignment().Replace(text, "${name}${sep}<redacted:secret>");
            text = Email().Replace(text, "<redacted:email>");
            text = WindowsHome().Replace(text, "~");
            text = UnixHome().Replace(text, "~");
            text = Ipv4().Replace(text, "<redacted:ipv4>");
            text = Ipv6().Replace(text, "<redacted:ipv6>");
            text = LongHex().Replace(text, "<redacted:hex>");
            text = Base64Candidate().Replace(text, static m => LooksLikeSecret(m.Value) ? "<redacted:base64>" : m.Value);
            if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
                text = Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", "<redacted:user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs));
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // A line that cannot be scrubbed in time is not returned at all.
            return "<redacted:line>";
        }
    }

    /// <summary>A base64-looking run is a secret when a slash-free stretch of 32+ characters mixes digits, upper and lower case (paths do not).</summary>
    internal static bool LooksLikeSecret(string candidate)
    {
        foreach (var segment in candidate.TrimEnd('=').Split('/'))
        {
            if (segment.Length < 32)
                continue;
            bool digit = false, upper = false, lower = false;
            foreach (var c in segment)
            {
                digit |= char.IsAsciiDigit(c);
                upper |= char.IsAsciiLetterUpper(c);
                lower |= char.IsAsciiLetterLower(c);
            }

            if (digit && upper && lower)
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<name>\b(?:proxy-)?authorization\b[""']?)(?<sep>\s*[:=]\s*[""']?)(?:(?:bearer|basic|digest|token|negotiate)\s+)?[^\s,;""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\b(?<scheme>bearer)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"://[^\s/@:]+:[^\s/@]+@", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // name=value, name: value, "name":"value" where the name ends in a credential word. Values end at whitespace or a delimiter.
    [GeneratedRegex(@"(?<name>(?<![A-Za-z0-9])[A-Za-z0-9_.-]*(?:token|secret|password|passwd|pwd|passphrase|api[_-]?key|key|auth|signature|sig|credentials?|cookie|session[_-]?id|otp)[""']?)(?<sep>\s*[=:]\s*[""']?)(?!<redacted:)[^\s&""',;}\])]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Email();

    // C:\Users\name, C:/Users/name, C:\\Users\\name (JSON), Z:\home\name, Z:\var\home\name. A user name with spaces is taken whole when a separator follows.
    [GeneratedRegex(@"\b[A-Za-z]:(?:[\\/]+var)?[\\/]+(?:users|home)[\\/]+(?:[^\\/""'<>:|\r\n]+(?=[\\/])|[^\\/\s""'<>:|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex WindowsHome();

    [GeneratedRegex(@"(?:/var)?/(?:home|Users)/[^/\s""'<>:|]+", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UnixHome();

    // Four dotted octets, unless introduced as a version ("v1.2.3.4", "version 1.2.3.4", "Version=1.2.3.4").
    [GeneratedRegex(@"(?<![\d.])(?<!\bv)(?<!\bver(?:sion)?[ =:]{1,3})(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Ipv4();

    // Eight full groups, or any "::"-compressed form with at least one group.
    [GeneratedRegex(@"(?<![A-Za-z0-9:])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?|::[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})(?:%[A-Za-z0-9]+)?(?![A-Za-z0-9:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9a-f]{32,}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex LongHex();

    [GeneratedRegex(@"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{40,}={0,2}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Base64Candidate();
}
