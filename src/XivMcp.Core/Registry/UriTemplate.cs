using System.Text;
using System.Text.RegularExpressions;

namespace XivMcp.Core.Registry;

/// <summary>
/// RFC 6570 URI template matching for level 1 (<c>{var}</c>) plus reserved expansion (<c>{+var}</c>).
/// Simple variables match one path segment (no '/', '?', '#'); reserved variables match the rest.
/// </summary>
internal sealed class UriTemplate
{
    private readonly Regex _regex;

    private UriTemplate(string template, IReadOnlyList<string> variables, Regex regex)
    {
        Template = template;
        Variables = variables;
        _regex = regex;
    }

    public string Template { get; }

    public IReadOnlyList<string> Variables { get; }

    public static UriTemplate Parse(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var pattern = new StringBuilder("^");
        var variables = new List<string>();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            var close = template.IndexOf('}', i);
            if (open < 0)
            {
                if (close >= 0)
                    throw new ArgumentException($"URI template '{template}' has an unmatched '}}'.");
                pattern.Append(Regex.Escape(template[i..]));
                break;
            }

            if (close >= 0 && close < open)
                throw new ArgumentException($"URI template '{template}' has an unmatched '}}'.");
            pattern.Append(Regex.Escape(template[i..open]));
            close = template.IndexOf('}', open);
            if (close < 0)
                throw new ArgumentException($"URI template '{template}' has an unmatched '{{'.");

            var expression = template[(open + 1)..close];
            var reserved = expression.StartsWith('+');
            var name = reserved ? expression[1..] : expression;
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.'))
            {
                throw new ArgumentException(
                    $"URI template '{template}': expression '{{{expression}}}' is not supported (use {{name}} or {{+name}}).");
            }

            if (variables.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException($"URI template '{template}' repeats variable '{name}'.");

            pattern.Append("(?<v").Append(variables.Count).Append('>').Append(reserved ? ".+" : "[^/?#]+").Append(')');
            variables.Add(name);
            i = close + 1;
        }

        pattern.Append('$');
        var regex = new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        return new UriTemplate(template, variables, regex);
    }

    public bool TryMatch(string uri, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        Match match;
        try
        {
            match = _regex.Match(uri);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
            return false;

        for (var i = 0; i < Variables.Count; i++)
        {
            var raw = match.Groups["v" + i].Value;
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(raw);
            }
            catch (UriFormatException)
            {
                decoded = raw;
            }

            values[Variables[i]] = decoded;
        }

        return true;
    }
}
