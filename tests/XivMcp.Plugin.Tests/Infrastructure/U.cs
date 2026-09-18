using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XivMcp.Plugin.Tests.Infrastructure;

/// <summary>
/// Test strings with special characters spelled as &lt;XXXX&gt; UTF-16 code units (e.g. "a&lt;2028&gt;b"), so the
/// sources stay plain ASCII and every invisible character in a test case is visible when reading it.
/// </summary>
public static partial class U
{
    public static string S(string spelled) => CodeUnit().Replace(spelled, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    public static string Repeat(string spelled, int count) => new StringBuilder().Insert(0, S(spelled), count).ToString();

    [GeneratedRegex("<([0-9A-Fa-f]{4})>")]
    private static partial Regex CodeUnit();
}
