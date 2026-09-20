using System.Net.Sockets;
using System.Text;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

/// <summary>
/// Randomized requests against a live server: mutated JSON-RPC bodies over HTTP and raw bytes on the socket.
/// Whatever arrives, the server must answer without a 5xx and keep serving. XIVMCP_FUZZ_SECONDS sets the
/// budget (default 2, the nightly workflow raises it); XIVMCP_FUZZ_SEED replays a run. A failure names its seed.
/// </summary>
public sealed class FuzzTests
{
    private static readonly string[] Corpus =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fuzz","version":"0"}}}""",
        """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}""",
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""",
        """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"xiv://nothing"}}""",
        """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""",
        """[{"jsonrpc":"2.0","id":5,"method":"ping"},{"jsonrpc":"2.0","id":6,"method":"ping"}]""",
        """{"jsonrpc":"2.0","id":"s","method":"prompts/get","params":{"name":"x","arguments":{"a":null}}}""",
    ];

    private static readonly string[] Splices =
        ["null", "{}", "[]", "\"\"", "-1", "1e999", "9223372036854775808", "\"\\ud800\"", "true", "{\"a\":{\"a\":{\"a\":{}}}}", "\u0000", "}", "[", "\"", ","];

    [Fact]
    public async Task RandomRequestsNeverBreakTheServer()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("XIVMCP_FUZZ_SEED"), out var s) ? s : Environment.TickCount;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("XIVMCP_FUZZ_SECONDS"), out var b) ? b : 2;
        var rng = new Random(seed);
        await using var server = await TestServer.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var n = 0;
        while (DateTime.UtcNow < deadline)
        {
            n++;
            var body = Mutate(rng);
            if (rng.Next(8) == 0)
            {
                await RawAsync(server.Port, body);
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint) { Content = new ByteArrayContent(body) };
            request.Content!.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (rng.Next(3) == 0)
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", rng.Next(2) == 0 ? TestServer.Modern : Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 12)).Replace('\n', ' ').Replace('\r', ' '));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                using var response = await server.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                Assert.True((int)response.StatusCode < 500, $"seed {seed} request {n}: {(int)response.StatusCode} for {Show(body)}");
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"seed {seed} request {n}: no answer in 15 s for {Show(body)}");
            }
            catch (HttpRequestException)
            {
                // the server may close on a request it refuses; it must still serve the next one
            }
        }

        using var ping = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"" + TestServer.Modern + "\"}}}", Encoding.UTF8, "application/json"),
        };
        ping.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var alive = await server.Http.SendAsync(ping);
        Assert.True((int)alive.StatusCode < 500, $"seed {seed}: the server stopped serving after {n} requests");
    }

    private static byte[] Mutate(Random rng)
    {
        var text = new StringBuilder(Corpus[rng.Next(Corpus.Length)]);
        for (var i = rng.Next(4); i > 0 && text.Length > 0; i--)
        {
            var at = rng.Next(text.Length);
            switch (rng.Next(5))
            {
                case 0: text.Remove(at, Math.Min(rng.Next(1, 12), text.Length - at)); break;
                case 1: text.Insert(at, Splices[rng.Next(Splices.Length)]); break;
                case 2: text[at] = (char)rng.Next(1, 0x250); break;
                case 3: text.Insert(at, text.ToString(at, Math.Min(rng.Next(1, 40), text.Length - at))); break;
                default: text.Length = at; break;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        if (rng.Next(6) == 0 && bytes.Length > 0)
            bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
        return bytes;
    }

    private static async Task RawAsync(int port, byte[] bytes)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        try
        {
            await client.GetStream().WriteAsync(bytes);
        }
        catch (IOException)
        {
            // closed on us: fine
        }
    }

    private static string Show(byte[] body) => Convert.ToBase64String(body);
}
