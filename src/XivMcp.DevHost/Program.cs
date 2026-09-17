using System.Globalization;
using System.Security.Cryptography;
using XivMcp.Core;
using XivMcp.DevHost;

var options = new McpServerOptions
{
    Port = 41811,
    ServerTitle = "FINAL FANTASY XIV (dev host, simulated)",
    Instructions = "Dev host with fake game data. Tools mirror the in-game plugin's conventions.",
};
string? tokenFile = null;
string? token = null;
var noToken = false;

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--host": options.Host = Next(); break;
        case "--port": options.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--path": options.Path = Next(); break;
        case "--token": token = Next(); break;
        case "--token-file": tokenFile = Next(); break;
        case "--no-token": noToken = true; break;
        case "--allowed-origin": options.AllowedOrigins.Add(Next()); break;
        case "--page-size": options.ListPageSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--call-timeout": options.CallTimeout = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
        case "--idle-timeout": options.SessionIdleTimeout = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
        case "--keepalive": options.SseKeepAliveInterval = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
        case "-h":
        case "--help":
            Console.WriteLine("""
                xiv-mcp-devhost: MCP server with a simulated framework thread and fake providers.
                  --host ADDR          bind address (default 127.0.0.1)
                  --port N             port (default 41811; 0 = ephemeral)
                  --path P             endpoint path (default /mcp)
                  --token T            bearer token
                  --token-file F       read the token from F, or create F with a random token
                  --no-token           disable authentication
                  --allowed-origin O   extra allowed Origin (repeatable)
                  --page-size N        list page size (default 250)
                  --call-timeout S     per-call timeout in seconds (default 30)
                  --idle-timeout S     legacy session idle timeout in seconds (default 1800)
                  --keepalive S        SSE keep-alive interval in seconds (default 15)
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]} (try --help)");
            return 2;
    }
}

if (!noToken)
{
    if (token is null && tokenFile is not null && File.Exists(tokenFile))
        token = File.ReadAllText(tokenFile).Trim();
    token ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    if (tokenFile is not null && !File.Exists(tokenFile))
    {
        File.WriteAllText(tokenFile, token + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    options.BearerToken = token;
}

using var framework = new SimulatedFrameworkThread();
var host = new DevHostState();
await using var server = new McpServer(options, framework, host, (message, ex) =>
    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}{(ex is null ? "" : " :: " + ex)}"));

server.RegisterProviders(typeof(SimulatedFrameworkThread).Assembly, type =>
{
    var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
    var services = new Dictionary<Type, object> { [typeof(SimulatedFrameworkThread)] = framework, [typeof(DevHostState)] = host, [typeof(IMcpNotifier)] = server.Notifier, [typeof(IGameThread)] = framework };
    return ctor.Invoke(ctor.GetParameters().Select(p => services[p.ParameterType]).ToArray());
});

await server.StartAsync();
Console.WriteLine($"xiv-mcp dev host listening on {server.GetStatus().Endpoint}");
Console.WriteLine(noToken ? "authentication: disabled" : tokenFile is not null ? $"bearer token in {tokenFile}" : $"bearer token: {token}");
Console.WriteLine($"{server.ListRegisteredTools().Count} tools registered. Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
await server.StopAsync();
Console.WriteLine("stopped");
return 0;
