// Pure presentation helpers (no Dalamud/Umbra types) so they can be unit-tested on the host.
using System.Globalization;
using System.Text;

namespace Umbra.XivMcp;

public sealed record WidgetTextOptions(bool ShowSessionCount, bool ShowAgentCount, bool Compact);

public static class McpFormat
{
    public const string DotRunning = "\u25CF"; // filled circle
    public const string DotStopped = "\u25CB"; // hollow circle
    public const string Dash = "\u2014";       // em dash
    public const string Play = "\u25B6";       // play triangle

    // 0xAARRGGBB, as Una.Drawing.Color(uint) expects.
    public const uint ColorRunning = 0xFF7ED67E;
    public const uint ColorDone = 0xFF8FB8F0;
    public const uint ColorFailed = 0xFFE86B6B;
    public const uint ColorInfo = 0xFFB0B0B0;
    public const uint ColorWarning = 0xFFE8B45A;

    /// <summary>Main toolbar label: "MCP ● 2", "MCP ○", "MCP —", "MCP !".</summary>
    public static string WidgetText(McpSnapshot s, WidgetTextOptions o)
    {
        var sb = new StringBuilder(24);
        if (!o.Compact) sb.Append("MCP ");
        switch (s.State)
        {
            case McpLinkState.Missing:
                sb.Append(Dash);
                break;
            case McpLinkState.VersionMismatch:
            case McpLinkState.Error:
                sb.Append('!');
                break;
            case McpLinkState.Stopped:
                sb.Append(DotStopped);
                break;
            case McpLinkState.Running:
                sb.Append(DotRunning);
                if (o.ShowSessionCount) sb.Append(o.Compact ? "" : " ").Append(s.Sessions.ToString(CultureInfo.InvariantCulture));
                if (o.ShowAgentCount && s.RunningAgents > 0)
                    sb.Append(' ').Append(Play).Append(s.RunningAgents.ToString(CultureInfo.InvariantCulture));
                break;
        }

        return sb.ToString();
    }

    /// <summary>Second line for the two-line widget layout.</summary>
    public static string? WidgetSubText(McpSnapshot s) => s.State switch
    {
        McpLinkState.Missing => "not installed",
        McpLinkState.VersionMismatch => "IPC version mismatch",
        McpLinkState.Error => "IPC error",
        McpLinkState.Stopped => "stopped",
        McpLinkState.Running when s.RunningAgents > 0 =>
            Plural(s.RunningAgents, "agent") + " running",
        McpLinkState.Running => Plural(s.Sessions, "session") + " · " + Plural(s.Status?.TotalRequests ?? 0, "call"),
        _ => null,
    };

    /// <summary>Label for the agents-only widget: "Agents 2 · 45%" / "Agents —".</summary>
    public static string AgentsWidgetText(McpSnapshot s, bool compact)
    {
        var prefix = compact ? "" : "Agents ";
        if (s.State is McpLinkState.Missing or McpLinkState.VersionMismatch or McpLinkState.Error)
            return prefix + Dash;
        var running = s.RunningAgents;
        var avg = AverageRunningProgress(s.Agents);
        var text = prefix + running.ToString(CultureInfo.InvariantCulture);
        if (running > 0 && avg is { } a) text += " · " + Percent(a);
        return text;
    }

    /// <summary>Mean progress (0..1) over running agents that report progress; null if none do.</summary>
    public static double? AverageRunningProgress(IReadOnlyList<McpAgentPost> agents)
    {
        double sum = 0;
        var n = 0;
        foreach (var a in agents)
        {
            if (!a.IsRunning || a.Fraction is not { } f) continue;
            sum += f;
            n++;
        }

        return n == 0 ? null : sum / n;
    }

    public static string StateLabel(McpLinkState state) => state switch
    {
        McpLinkState.Missing => "Not installed",
        McpLinkState.VersionMismatch => "Version mismatch",
        McpLinkState.Error => "IPC error",
        McpLinkState.Stopped => "Stopped",
        McpLinkState.Running => "Running",
        _ => state.ToString(),
    };

    public static uint StateColor(McpLinkState state) => state switch
    {
        McpLinkState.Running => ColorRunning,
        McpLinkState.Stopped => ColorInfo,
        McpLinkState.Missing => ColorInfo,
        _ => ColorWarning,
    };

    public static uint AgentStateColor(string state) => state.ToLowerInvariant() switch
    {
        "running" => ColorRunning,
        "done" => ColorDone,
        "failed" => ColorFailed,
        _ => ColorInfo,
    };

    /// <summary>Multi-line tooltip for the toolbar widget.</summary>
    public static string Tooltip(McpSnapshot s, string? secondaryHint)
    {
        var sb = new StringBuilder();
        sb.Append("XivMcp MCP server: ").Append(StateLabel(s.State));
        if (s.Status is { } st)
        {
            if (st.Endpoint.Length > 0) sb.Append("\nEndpoint: ").Append(st.Endpoint);
            sb.Append("\nSessions: ").Append(st.ActiveSessions.ToString(CultureInfo.InvariantCulture));
            sb.Append("\nClients: ").Append(st.ConnectedClients.Count > 0 ? string.Join(", ", st.ConnectedClients) : "none");
            sb.Append("\nRequests: ").Append(st.TotalRequests.ToString(CultureInfo.InvariantCulture));
            if (st.FailedRequests > 0) sb.Append(" (").Append(st.FailedRequests.ToString(CultureInfo.InvariantCulture)).Append(" failed)");
            if (s.RunningAgents > 0) sb.Append("\nAgents running: ").Append(s.RunningAgents.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(st.LastError)) sb.Append("\nLast error: ").Append(Truncate(st.LastError, 120));
        }

        if (!string.IsNullOrEmpty(s.Problem)) sb.Append('\n').Append(s.Problem);
        sb.Append("\nClick for details");
        if (!string.IsNullOrEmpty(secondaryHint)) sb.Append(" · Right-click: ").Append(secondaryHint);
        return sb.ToString();
    }

    /// <summary>Compact age: "now", "42s", "5m", "3h", "2d".</summary>
    public static string Age(DateTimeOffset? then, DateTimeOffset now)
    {
        if (then is not { } t) return "";
        var d = now - t;
        if (d < TimeSpan.FromSeconds(1)) return "now";
        if (d < TimeSpan.FromMinutes(1)) return ((int)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        if (d < TimeSpan.FromHours(1)) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        if (d < TimeSpan.FromDays(1)) return ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        return ((int)d.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
    }

    /// <summary>Local wall-clock time "HH:mm:ss".</summary>
    public static string ClockTime(DateTimeOffset? t) =>
        t is { } v ? v.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";

    public static string Duration(double ms) => ms switch
    {
        < 0 or double.NaN => "",
        < 10 => ms.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
        < 1000 => ((int)Math.Round(ms, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "ms",
        _ => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s",
    };

    /// <summary>"tools/call get_player" style label for an activity row.</summary>
    public static string ActivityLabel(McpActivity a, int maxLength = 48)
    {
        var label = string.IsNullOrEmpty(a.Target) ? a.Method : a.Method + " " + a.Target;
        return Truncate(label, maxLength);
    }

    /// <summary>0..1 fraction from either a 0..1 fraction or a 0..100 percentage. Null stays null.</summary>
    public static double? NormalizeProgress(double? progress)
    {
        if (progress is not { } p || !double.IsFinite(p)) return null;
        if (p > 1.0) p /= 100.0;
        return Math.Clamp(p, 0.0, 1.0);
    }

    public static string Percent(double fraction) =>
        ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    public static string Plural(long n, string noun) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.ReplaceLineEndings(" ");
        if (max <= 1 || oneLine.Length <= max) return oneLine;
        return oneLine[..(max - 1)] + "…";
    }
}
