using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace XivMcp.Core.Net;

/// <summary>A network adapter reduced to what tailnet detection needs. Lets the rules be tested without a real NIC.</summary>
/// <param name="Name">Adapter id/name as the OS reports it (<c>tailscale0</c> on Linux, a GUID under Wine).</param>
/// <param name="Description">Human-readable adapter description.</param>
/// <param name="Up">True when the adapter is operationally up.</param>
/// <param name="Addresses">Unicast addresses assigned to the adapter.</param>
public sealed record NetAdapter(string Name, string Description, bool Up, IReadOnlyList<IPAddress> Addresses);

/// <summary>Where a tailnet address came from, so the UI can say how it was found.</summary>
public enum TailnetSource
{
    /// <summary>Enumerated from <see cref="NetworkInterface"/>. This is the route that works inside the game under Wine.</summary>
    Interface,

    /// <summary>The local endpoint of a connectionless UDP socket routed at the Tailscale resolver.</summary>
    Route,

    /// <summary>Read from the <c>tailscale</c> CLI. Not reachable from inside Wine; native hosts only.</summary>
    Cli,
}

/// <summary>This machine's address on a Tailscale tailnet.</summary>
/// <param name="Address">The tailnet address (CGNAT IPv4, or the Tailscale ULA IPv6).</param>
/// <param name="AdapterName">Adapter the address was found on, or the CLI command that reported it.</param>
/// <param name="MagicDnsName">MagicDNS name without the trailing dot, when it could be read. Never logged.</param>
/// <param name="Source">How it was found.</param>
public sealed record TailnetAddress(IPAddress Address, string AdapterName, string? MagicDnsName, TailnetSource Source)
{
    /// <summary>The address as it goes into a URL host position (IPv6 bracketed).</summary>
    public string HostText => Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();

    /// <summary>The address as it appears in a Host header (never bracketed here; the caller brackets IPv6).</summary>
    public string Literal => Address.ToString();
}

/// <summary>
/// Finds this machine's Tailscale address. Two routes, in order:
/// <list type="number">
/// <item>the adapter list (<see cref="NetworkInterface"/>), which is what a normal Windows/Linux process sees;</item>
/// <item>the <c>tailscale</c> CLI, reached through Wine's <c>Z:</c> drive when the plugin runs inside the game.</item>
/// </list>
/// Everything here is best effort: a machine with no tailnet, or with Tailscale stopped, returns null
/// and the caller falls back to loopback. No detection failure is ever fatal.
/// </summary>
public static class Tailnet
{
    /// <summary>Tailscale hands out IPv4 addresses from the CGNAT range 100.64.0.0/10 (RFC 6598).</summary>
    public const string CgnatRange = "100.64.0.0/10";

    /// <summary>Tailscale's IPv6 ULA prefix.</summary>
    public const string Ipv6Range = "fd7a:115c:a1e0::/48";

    private static readonly byte[] Ipv6Prefix = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    /// <summary>Tailscale's MagicDNS resolver. Only used as a routing target; no packet is ever sent to it.</summary>
    public const string ResolverAddress = "100.100.100.100";

    /// <summary>MagicDNS names live under this domain.</summary>
    public const string MagicDnsSuffix = ".ts.net";

    /// <summary>
    /// Candidate paths for the CLI, tried in order. Only reached on a native host: executing a Linux
    /// binary through Wine's <c>Z:</c> drive with redirected stdio fails with <c>E_HANDLE</c>, which is
    /// why the CLI is the last resort and not the primary route.
    /// </summary>
    public static readonly string[] CliCandidates =
    [
        "/usr/bin/tailscale",
        "/usr/local/bin/tailscale",
        "tailscale",
        @"C:\Program Files\Tailscale\tailscale.exe",
    ];

    /// <summary>True for an address Tailscale would have assigned: 100.64.0.0/10 or fd7a:115c:a1e0::/48.</summary>
    public static bool IsTailnetAddress(IPAddress? address)
    {
        if (address is null)
            return false;
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
            {
                Span<byte> b = stackalloc byte[4];
                if (!address.TryWriteBytes(b, out _))
                    return false;

                // 100.64.0.0/10: first octet 100, second octet 64-127.
                return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
            }

            case AddressFamily.InterNetworkV6:
            {
                if (address.IsIPv4MappedToIPv6)
                    return IsTailnetAddress(address.MapToIPv4());
                Span<byte> b = stackalloc byte[16];
                return address.TryWriteBytes(b, out _) && b[..6].SequenceEqual(Ipv6Prefix);
            }

            default:
                return false;
        }
    }

    /// <summary>True for an adapter whose name or description says Tailscale (<c>tailscale0</c>, <c>ts0</c>, "Tailscale Tunnel").</summary>
    public static bool LooksLikeTailscaleAdapter(string? name, string? description)
    {
        return Matches(name) || Matches(description);

        static bool Matches(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (text.Contains("tailscale", StringComparison.OrdinalIgnoreCase))
                return true;
            var trimmed = text.Trim();
            return trimmed.StartsWith("ts", StringComparison.OrdinalIgnoreCase)
                   && trimmed.Length > 2
                   && trimmed.Skip(2).All(char.IsAsciiDigit);
        }
    }

    /// <summary>
    /// Picks the tailnet address out of an adapter list. An adapter that names Tailscale wins, then an
    /// adapter that is up, then IPv4 over IPv6; ties break on the lowest address so the choice is stable.
    /// Returns null when no adapter carries a tailnet address.
    /// </summary>
    public static TailnetAddress? Select(IEnumerable<NetAdapter>? adapters)
    {
        if (adapters is null)
            return null;

        (NetAdapter Adapter, IPAddress Address)? best = null;
        var bestRank = int.MaxValue;
        foreach (var adapter in adapters)
        {
            if (adapter?.Addresses is null)
                continue;
            foreach (var address in adapter.Addresses)
            {
                if (!IsTailnetAddress(address))
                    continue;
                var rank = (LooksLikeTailscaleAdapter(adapter.Name, adapter.Description) ? 0 : 4)
                           + (adapter.Up ? 0 : 2)
                           + (address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);
                if (rank > bestRank)
                    continue;
                if (rank == bestRank && best is { } current
                    && string.CompareOrdinal(address.ToString(), current.Address.ToString()) >= 0)
                {
                    continue;
                }

                bestRank = rank;
                best = (adapter, address);
            }
        }

        return best is { } picked ? new TailnetAddress(picked.Address, picked.Adapter.Name, null, TailnetSource.Interface) : null;
    }

    /// <summary>Reads the live adapter list. Returns an empty list (never throws) when the platform refuses.</summary>
    public static IReadOnlyList<NetAdapter> EnumerateAdapters(Action<string, Exception?>? log = null)
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            log?.Invoke("could not enumerate network interfaces", ex);
            return [];
        }

        var result = new List<NetAdapter>(interfaces.Length);
        foreach (var nic in interfaces)
        {
            try
            {
                var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray();
                result.Add(new NetAdapter(nic.Name ?? "", nic.Description ?? "", nic.OperationalStatus == OperationalStatus.Up, addresses));
            }
            catch (Exception ex)
            {
                log?.Invoke($"could not read addresses of adapter {nic.Name}", ex);
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the tailnet address. Three routes, cheapest and most portable first:
    /// the adapter list, the local address the kernel picks for the Tailscale resolver, then the CLI.
    /// The first one is what works inside the game (Wine reports <c>tailscale0</c> and its addresses
    /// unchanged); the CLI is unreachable from there. Returns null when nothing was found; never throws.
    /// </summary>
    public static TailnetAddress? Detect(Action<string, Exception?>? log = null, string? cliPath = null, TimeSpan cliTimeout = default)
    {
        var found = Select(EnumerateAdapters(log)) ?? ReadRouteAddress(log) ?? ReadCliAddress(log, cliPath, cliTimeout);
        return found is null ? null : found with { MagicDnsName = ReadMagicDnsName(found.Address, log, cliPath, cliTimeout) };
    }

    /// <summary>
    /// Asks the kernel which local address it would use to reach the Tailscale resolver, by connecting a
    /// UDP socket (connectionless: no packet leaves the machine). Returns null unless that address is a
    /// tailnet address, so a machine without Tailscale reports nothing rather than its LAN address.
    /// </summary>
    public static TailnetAddress? ReadRouteAddress(Action<string, Exception?>? log = null)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse(ResolverAddress), 53));
            if (socket.LocalEndPoint is IPEndPoint { Address: { } local } && IsTailnetAddress(local))
                return new TailnetAddress(local, "route to " + ResolverAddress, null, TailnetSource.Route);
        }
        catch (Exception ex)
        {
            // No route to the tailnet is the normal case when Tailscale is down.
            log?.Invoke("tailnet route probe failed", ex);
        }

        return null;
    }

    /// <summary>Runs <c>tailscale ip -4</c> and returns the first tailnet address it prints, or null.</summary>
    public static TailnetAddress? ReadCliAddress(Action<string, Exception?>? log = null, string? cliPath = null, TimeSpan cliTimeout = default)
    {
        var (exe, output) = RunCli(["ip", "-4"], log, cliPath, cliTimeout);
        if (output is null)
            return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(line, out var ip) && IsTailnetAddress(ip))
                return new TailnetAddress(ip, exe ?? "tailscale", null, TailnetSource.Cli);
        }

        return null;
    }

    /// <summary>
    /// This machine's MagicDNS name, or null. Tried as a reverse lookup of <paramref name="address"/>
    /// first, because MagicDNS answers PTR queries and that works inside Wine, then from
    /// <c>tailscale status --json</c>. Blocking; call it off the UI thread.
    /// </summary>
    public static string? ReadMagicDnsName(IPAddress? address, Action<string, Exception?>? log = null, string? cliPath = null, TimeSpan cliTimeout = default)
    {
        if (address is not null)
        {
            try
            {
                var name = Dns.GetHostEntry(address).HostName?.Trim().TrimEnd('.');
                if (!string.IsNullOrEmpty(name) && name.EndsWith(MagicDnsSuffix, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            catch (Exception ex)
            {
                // No PTR record, no resolver, or MagicDNS is off: fall through to the CLI.
                log?.Invoke("MagicDNS reverse lookup failed", ex);
            }
        }

        var (_, output) = RunCli(["status", "--json"], log, cliPath, cliTimeout);
        return ParseMagicDnsName(output);
    }

    /// <summary>Pulls <c>Self.DNSName</c> out of <c>tailscale status --json</c> output. Null when absent or unparseable.</summary>
    public static string? ParseMagicDnsName(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Self", out var self)
                || self.ValueKind != JsonValueKind.Object
                || !self.TryGetProperty("DNSName", out var dns)
                || dns.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = dns.GetString()?.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string? Exe, string? Output) RunCli(string[] arguments, Action<string, Exception?>? log, string? cliPath, TimeSpan timeout)
    {
        var budget = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(4) : timeout;
        var candidates = cliPath is { Length: > 0 } ? new[] { cliPath } : CliCandidates;
        foreach (var candidate in candidates)
        {
            try
            {
                var info = new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);

                using var process = Process.Start(info);
                if (process is null)
                    continue;
                var stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit((int)budget.TotalMilliseconds))
                {
                    TryKill(process);
                    continue;
                }

                if (process.ExitCode != 0)
                    continue;
                var text = stdout.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(text))
                    return (candidate, text);
            }
            catch (Exception ex)
            {
                // A missing binary is the normal case on a machine without Tailscale; only note the rest.
                if (ex is not (System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException))
                    log?.Invoke($"tailscale CLI probe via {candidate} failed", ex);
            }
        }

        return (null, null);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process is gone or cannot be killed; nothing else to do.
        }
    }
}

/// <summary>
/// Caches <see cref="Tailnet.Detect"/> so the settings window can ask on every frame without paying for a
/// process launch. Thread-safe; <see cref="Refresh"/> forces a fresh probe.
/// </summary>
public sealed class TailnetProbe
{
    private readonly Action<string, Exception?>? log;
    private readonly TimeSpan minInterval;
    private readonly Lock gate = new();
    private long lastProbeTicks = long.MinValue;
    private TailnetAddress? last;
    private bool probed;

    public TailnetProbe(Action<string, Exception?>? log = null, TimeSpan minInterval = default)
    {
        this.log = log;
        this.minInterval = minInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(20) : minInterval;
    }

    /// <summary>Optional override for the CLI path (tests, unusual installs).</summary>
    public string? CliPath { get; set; }

    /// <summary>True once a probe has run, so the UI can tell "not found" from "not looked yet".</summary>
    public bool HasProbed
    {
        get
        {
            lock (gate)
                return probed;
        }
    }

    /// <summary>The cached address, probing when the cache is older than the minimum interval.</summary>
    public TailnetAddress? Current()
    {
        lock (gate)
        {
            if (probed && Environment.TickCount64 - lastProbeTicks < (long)minInterval.TotalMilliseconds)
                return last;
        }

        return Refresh();
    }

    /// <summary>The cached address without probing (null before the first probe).</summary>
    public TailnetAddress? Cached()
    {
        lock (gate)
            return last;
    }

    /// <summary>Probes now and replaces the cache.</summary>
    public TailnetAddress? Refresh()
    {
        TailnetAddress? found;
        try
        {
            found = Tailnet.Detect(log, CliPath);
        }
        catch (Exception ex)
        {
            log?.Invoke("tailnet detection failed", ex);
            found = null;
        }

        lock (gate)
        {
            last = found;
            probed = true;
            lastProbeTicks = Environment.TickCount64;
            return found;
        }
    }
}
