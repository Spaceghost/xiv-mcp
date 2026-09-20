using System.Globalization;
using System.Security.Cryptography;
using XivMcp.Core.Net;
using XivMcp.Standalone;

// xiv-mcp-standalone: the MCP endpoint before the game is launched. See docs/STANDALONE.md.

string? game = Environment.GetEnvironmentVariable("XIVMCP_GAME");
string? token = Environment.GetEnvironmentVariable("XIVMCP_TOKEN");
string? tokenFile = null;
string? language = Environment.GetEnvironmentVariable("XIVMCP_LANGUAGE");
string? bind = null;
int? port = null;
string? path = null;
var noToken = false;
var rate = 600;
var printCatalogue = false;

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    try
    {
        switch (args[i])
        {
            case "--game": game = Next(); break;
            case "--language": language = Next(); break;
            case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--path": path = Next(); break;
            case "--bind": bind = Next(); break;
            case "--token-file": tokenFile = Next(); break;
            case "--no-token": noToken = true; break;
            case "--rate-limit": rate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--catalogue": printCatalogue = true; break;
            case "-h":
            case "--help":
                Console.WriteLine("""
                    xiv-mcp-standalone: the xiv-mcp endpoint while FINAL FANTASY XIV is not running.
                    Serves the tools that need only the installed game data; every other tool answers "game_not_running".
                    Uses the plugin's own port, path and tokens, and hands the port to the plugin when the game starts.

                      --game PATH        game install, its "game" directory or the sqpack directory (else $XIVMCP_GAME,
                                         else the XIVLauncher / Steam default locations)
                      --language L       en | ja | de | fr (default en; else $XIVMCP_LANGUAGE)
                      --port N           default: the plugin's configured port, else 41800
                      --path P           default: the plugin's configured path, else /mcp
                      --bind MODE        loopback (default: the plugin's bind mode) | loopback+tailnet | tailnet | an address
                      --token-file F     read the bearer token from F (created with a random token when missing)
                      --no-token         no authentication; refused unless every bound address is loopback
                      --rate-limit N     calls per minute per client (default 600; 0 = unlimited)
                      --catalogue        print the tool catalogue this host would serve as JSON, then exit

                    The bearer token comes from, in order: $XIVMCP_TOKEN, --token-file, the provisioning file
                    (~/.config/xiv-mcp/provision.json), the plugin's saved configuration. It is never printed.
                    """);
                return 0;
            default:
                Console.Error.WriteLine($"unknown argument {args[i]} (try --help)");
                return 2;
        }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

void Log(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

static string? ReadText(string file)
{
    try
    {
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var appData = Environment.GetEnvironmentVariable("APPDATA");

var settings = new SharedSettings();
if (SharedSettings.PluginConfigCandidates(home, appData).FirstOrDefault(File.Exists) is { } pluginConfig)
    settings = settings.Overlay(ReadText(pluginConfig), "the plugin's saved configuration");
settings = settings.Overlay(ReadText(SharedSettings.ProvisionPath(Environment.GetEnvironmentVariable, home)), "the provisioning file");
if (port is { } p)
    settings = settings with { Port = p };
if (SharedSettings.NormalizePath(path) is { } normalized)
    settings = settings with { Path = normalized };

if (tokenFile is not null && token is null)
{
    token = ReadText(tokenFile)?.Trim();
    if (string.IsNullOrEmpty(token))
    {
        token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(tokenFile, token + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

if (!string.IsNullOrEmpty(token))
    settings = settings with { BearerToken = token, RequireToken = true };
if (noToken)
    settings = settings with { RequireToken = false };

// Where to listen: the same modes as the plugin.
var hosts = new List<string>();
var names = new List<string>();
var mode = bind is null ? settings.BindMode : SharedSettings.ParseBindMode(bind) ?? 3;
var custom = bind is not null && SharedSettings.ParseBindMode(bind) is null ? bind : settings.CustomHost;
if (mode is 1 or 2)
{
    var tailnet = Tailnet.Detect((message, _) => Log(message));
    if (tailnet is not null)
    {
        if (mode == 1)
            hosts.Add("127.0.0.1");
        hosts.Add(tailnet.Address.ToString());
        if (!string.IsNullOrEmpty(tailnet.MagicDnsName))
            names.Add(tailnet.MagicDnsName);
    }
    else
    {
        Log("no tailnet address found; listening on loopback only");
    }
}
else if (mode == 3 && !string.IsNullOrWhiteSpace(custom))
{
    hosts.Add(custom.Trim());
}

if (hosts.Count == 0)
    hosts.Add("127.0.0.1");

var offMachine = hosts.Any(h => !(System.Net.IPAddress.TryParse(h, out var a) ? System.Net.IPAddress.IsLoopback(a) : h.Equals("localhost", StringComparison.OrdinalIgnoreCase)));
if (offMachine && (!settings.RequireToken || string.IsNullOrEmpty(settings.BearerToken)))
{
    Console.Error.WriteLine("Refusing to listen off this machine without a bearer token. Set $XIVMCP_TOKEN, use --token-file, or bind loopback.");
    return 3;
}

if (settings.RequireToken && string.IsNullOrEmpty(settings.BearerToken))
{
    Console.Error.WriteLine("No bearer token found (the plugin's configuration and the provisioning file have none). Set $XIVMCP_TOKEN, use --token-file FILE, or pass --no-token for a loopback-only endpoint.");
    return 3;
}

var sqpack = GameLocator.Find(game);
if (sqpack is null)
    Log(game is null ? "no game installation found: static tools will answer 'unavailable' (pass --game PATH)" : $"no game data under {game}: static tools will answer 'unavailable'");

await using var host = new StandaloneHost(new StandaloneHostOptions
{
    Settings = settings,
    Hosts = hosts,
    AllowedHostNames = names,
    SqpackPath = sqpack,
    Language = language ?? "en",
    RateLimitPerMinute = Math.Max(0, rate),
    Log = Log,
});

if (printCatalogue)
{
    Console.WriteLine(host.Server.ExportCatalogue().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

Log($"xiv-mcp-standalone {StandaloneHost.Version}: {host.ServedTools.Count} tools served from game data{(host.GameVersion is { } gv ? $" (game {gv})" : "")}, {host.Server.ListRegisteredTools().Count - host.ServedTools.Count} answer game_not_running");
Log($"endpoint {host.Endpoint} · settings from {(settings.Sources.Count > 0 ? string.Join(" + ", settings.Sources) : "defaults")} · authentication {(settings.RequireToken ? "bearer token" : "off (loopback)")}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

await host.Coordinator.StartAsync();
await host.Coordinator.RunAsync(stop.Token);
await host.Server.StopAsync();
Log("stopped");
return 0;
