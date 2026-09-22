using System.Net.Http;
using System.Text;
using System.Text.Json;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Ai;

/// <summary>
/// Thin read-only bridge to a local Laya/JEV typed-decision server. Laya proposes
/// classifications; it never receives authority to execute FFXIV actions.
/// </summary>
[McpProvider("ai")]
public sealed class LayaProvider
{
    private const string DefaultEndpoint = "http://127.0.0.1:8080/v1/systemone";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [McpTool("laya_decide",
        Title = "Ask local Laya",
        Description = "Ask the local Laya typed-decision model one or more typed questions about supplied state. Read-only: the result is advisory and never executes a game action.",
        GameThread = false,
        RequiresLogin = false,
        Availability = ToolAvailability.Static,
        Sources = ["http:local-laya"])]
    public async Task<object> Decide(
        [McpParam("State/context Laya should reason over.")] string state,
        [McpParam("JSON object of JEV/Laya questions, e.g. {\"safe\":{\"type\":\"noul\",\"instructions\":\"Is this safe?\"}}.")] string questions,
        [McpParam("Optional Laya /v1/systemone endpoint. Loopback is the default.")] string? endpoint = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new McpToolException("state is required");

        JsonElement parsedQuestions;
        try
        {
            using var q = JsonDocument.Parse(questions);
            if (q.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("questions must be an object");
            parsedQuestions = q.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new McpToolException($"questions must be valid JSON object: {ex.Message}");
        }

        var uri = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttp ||
            !(target.IsLoopback || target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new McpToolException("Laya endpoint must be loopback HTTP (127.0.0.1/localhost).");

        var payload = JsonSerializer.Serialize(new { state, questions = parsedQuestions });
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new McpToolException($"Laya returned HTTP {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            return new
            {
                advisory = true,
                model = "laya",
                endpoint = target.ToString(),
                result = doc.RootElement.Clone(),
            };
        }
        catch (HttpRequestException ex)
        {
            throw new McpToolException($"Local Laya is unavailable at {target}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpToolException($"Local Laya timed out at {target}.");
        }
    }
}
