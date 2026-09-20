using System.Net;
using System.Net.Sockets;
using XivMcp.Core.Net;

namespace XivMcp.Plugin.Services;

/// <summary>
/// The addresses one <see cref="BindMode"/> resolves to, plus everything the UI, the server options and
/// the IPC payload need to describe them. Produced by <see cref="BindPlanner.Resolve"/> and treated as
/// the single source of truth for "where does this server listen".
/// </summary>
/// <param name="Mode">The mode this plan was resolved from.</param>
/// <param name="Hosts">Addresses to bind, in order. Never empty; the first one is the primary.</param>
/// <param name="PreferredHost">Best address to hand an off-machine client: the tailnet address when one is bound, else loopback.</param>
/// <param name="LoopbackHost">Loopback address when one is bound, else null (tailnet-only).</param>
/// <param name="TailnetHost">The bound tailnet address, or null.</param>
/// <param name="MagicDnsName">MagicDNS name of this machine when it is known and the tailnet is bound.</param>
/// <param name="AllowedHostNames">Extra names the DNS-rebinding check must accept for this plan.</param>
/// <param name="RequiresToken">True when at least one bound address is reachable from another machine.</param>
/// <param name="Notice">A user-visible explanation when the plan is not what the mode asked for (Tailscale down, say).</param>
public sealed record BindPlan(
    BindMode Mode,
    IReadOnlyList<string> Hosts,
    string PreferredHost,
    string? LoopbackHost,
    string? TailnetHost,
    string? MagicDnsName,
    IReadOnlyList<string> AllowedHostNames,
    bool RequiresToken,
    string? Notice)
{
    /// <summary>True when every bound address only accepts connections from this machine.</summary>
    public bool LoopbackOnly => !RequiresToken;
}

/// <summary>
/// Turns a bind mode plus the detected tailnet address into the concrete address list. Pure: the caller
/// supplies the detection result, so the mapping is testable without a network.
/// </summary>
public static class BindPlanner
{
    public const string Loopback = "127.0.0.1";

    /// <summary>
    /// Resolves <paramref name="mode"/>. A tailnet mode without a tailnet address never fails: it falls
    /// back to loopback and says so in <see cref="BindPlan.Notice"/>, because refusing to listen would
    /// take the server away from the local client too.
    /// </summary>
    public static BindPlan Resolve(BindMode mode, string? customHost, TailnetAddress? tailnet)
    {
        var tailnetHost = tailnet?.HostText;
        var magicDns = tailnet?.MagicDnsName;

        // Extra Host header names for a bound tailnet address: the literal (unbracketed, as it appears
        // in a Host header) and the MagicDNS name.
        List<string> TailnetNames()
        {
            var names = new List<string> { tailnet!.Literal };
            if (!string.IsNullOrWhiteSpace(magicDns))
                names.Add(magicDns!.Trim());
            return names;
        }

        switch (mode)
        {
            case BindMode.LoopbackAndTailnet when tailnet is not null:
                return new BindPlan(mode, [Loopback, tailnetHost!], tailnetHost!, Loopback, tailnetHost, magicDns, TailnetNames(), true, null);

            case BindMode.TailnetOnly when tailnet is not null:
                return new BindPlan(mode, [tailnetHost!], tailnetHost!, null, tailnetHost, magicDns, TailnetNames(), true, null);

            case BindMode.LoopbackAndTailnet:
            case BindMode.TailnetOnly:
                return new BindPlan(mode, [Loopback], Loopback, Loopback, null, null, [], false, NoTailnetNotice(mode));

            case BindMode.Custom:
            {
                var host = NormalizeCustomHost(customHost);
                var loopback = IsLoopbackHost(host) ? host : null;
                var isTailnet = IPAddress.TryParse(host.Trim('[', ']'), out var ip) && Tailnet.IsTailnetAddress(ip);
                return new BindPlan(
                    mode,
                    [host],
                    host,
                    loopback,
                    isTailnet ? host : null,
                    isTailnet ? magicDns : null,
                    [host.Trim('[', ']')],
                    loopback is null,
                    null);
            }

            default:
                return new BindPlan(BindMode.Loopback, [Loopback], Loopback, Loopback, null, null, [], false, null);
        }
    }

    /// <summary>Every endpoint URL this plan serves, in bind order.</summary>
    public static IReadOnlyList<string> Endpoints(BindPlan plan, int port, string path) =>
        plan.Hosts.Select(h => Endpoint(h, port, path)).ToArray();

    /// <summary>One endpoint URL. IPv6 hosts are bracketed; <paramref name="path"/> is normalised to start with '/'.</summary>
    public static string Endpoint(string host, int port, string path)
    {
        var h = host.Trim();
        if (h.Contains(':') && !h.StartsWith('['))
            h = $"[{h}]";
        var p = string.IsNullOrWhiteSpace(path) ? "/mcp" : path.Trim();
        if (!p.StartsWith('/'))
            p = "/" + p;
        if (p.Length > 1)
            p = p.TrimEnd('/');
        return $"http://{h}:{port}{p}";
    }

    /// <summary>What the UI says when a tailnet mode had to fall back to loopback.</summary>
    public static string NoTailnetNotice(BindMode mode) =>
        mode == BindMode.TailnetOnly
            ? "No Tailscale address found (is Tailscale running?). Listening on 127.0.0.1 only so the local client keeps working."
            : "No Tailscale address found (is Tailscale running?). Listening on 127.0.0.1 only.";

    /// <summary>Trims a typed address and falls back to loopback when it is empty.</summary>
    public static string NormalizeCustomHost(string? host)
    {
        var trimmed = host?.Trim();
        return string.IsNullOrEmpty(trimmed) ? Loopback : trimmed;
    }

    /// <summary>True when the address only accepts connections from this machine.</summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var trimmed = host.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(trimmed.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// The Host header names the server must accept for this plan: loopback is always accepted by the
    /// server itself, so this adds the bound addresses and the plan's extra names.
    /// </summary>
    public static IReadOnlySet<string> AllowedHostHeaders(BindPlan plan)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in plan.Hosts)
            set.Add(host.Trim().Trim('[', ']'));
        foreach (var name in plan.AllowedHostNames)
            set.Add(name.Trim().Trim('[', ']'));
        return set;
    }

    /// <summary>A short "127.0.0.1 + 100.x.y.z" style summary for status lines.</summary>
    public static string Describe(BindPlan plan) => string.Join(" + ", plan.Hosts);

    /// <summary>True when <paramref name="host"/> parses as an address family this machine can bind.</summary>
    public static bool IsBindableLiteral(string? host) =>
        IPAddress.TryParse((host ?? "").Trim().Trim('[', ']'), out var ip)
        && ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}
