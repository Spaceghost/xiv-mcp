using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Plugin.Services;

/// <summary>A local OpenAI-compatible server found by <see cref="LocalModelProbe.DetectAsync"/> and the models it lists.</summary>
public sealed record LocalModelServer(string BaseUrl, IReadOnlyList<string> Models);

/// <summary>Outcome of <see cref="LocalModelProbe.TestAsync"/>. <see cref="Message"/> never contains the API key.</summary>
public sealed record LocalModelTestResult(bool Ok, string Message, long? LatencyMs);

/// <summary>
/// Talks to a local OpenAI-compatible model server (Ollama, LM Studio, llama.cpp, KoboldCpp): lists models and sends a
/// one-line chat completion to prove the configured endpoint and model work. XivMcp never runs the model; this only
/// checks the settings companion plugins read over IPC. Pure apart from the injected <see cref="HttpClient"/>.
/// </summary>
public sealed class LocalModelProbe(HttpClient http)
{
    /// <summary>Default base URLs of the common local servers (Ollama, LM Studio, llama.cpp server, KoboldCpp).</summary>
    public static readonly IReadOnlyList<string> CommonEndpoints =
    [
        "http://127.0.0.1:11434/v1",
        "http://127.0.0.1:1234/v1",
        "http://127.0.0.1:8080/v1",
        "http://127.0.0.1:5001/v1",
    ];

    public static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(1.5);

    private const int MaxErrorBody = 200;

    /// <summary>
    /// Trimmed absolute http(s) URL without trailing '/', "" for blank input, or null when the text is not such a URL.
    /// </summary>
    public static string? NormalizeEndpoint(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// Model ids from an OpenAI-style <c>{"data":[{"id":...}]}</c> list, or Ollama's <c>/api/tags</c>
    /// <c>{"models":[{"name":...}]}</c>. Null when the text is neither.
    /// </summary>
    public static IReadOnlyList<string>? ParseModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
            return null;
        if (obj["data"] is JsonArray data)
            return Ids(data, "id");
        if (obj["models"] is JsonArray models)
            return Ids(models, "name", "model");
        return null;

        static IReadOnlyList<string> Ids(JsonArray items, params string[] keys) => items
            .OfType<JsonObject>()
            .Select(o => keys.Select(k => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).FirstOrDefault(s => !string.IsNullOrEmpty(s)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The text of <c>choices[0].message.content</c> (or <c>choices[0].text</c>), or null.</summary>
    public static string? ParseCompletion(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject obj || obj["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject first)
                return null;
            var content = first["message"] is JsonObject message ? message["content"] : first["text"];
            return content is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// GET {baseUrl}/models; when that fails or is not a model list and the base ends in /v1, Ollama's {root}/api/tags.
    /// Null when the server does not answer with a model list within <paramref name="timeout"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(string baseUrl, string? apiKey, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var (models, _) = await TryListModelsAsync(baseUrl, apiKey, timeout, cancellationToken).ConfigureAwait(false);
        return models;
    }

    /// <summary>Probes each candidate concurrently and returns the ones that answered, in candidate order.</summary>
    public async Task<IReadOnlyList<LocalModelServer>> DetectAsync(IEnumerable<string>? candidates = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var bases = (candidates ?? CommonEndpoints).Select(NormalizeEndpoint).OfType<string>().Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = await Task.WhenAll(bases.Select(async b =>
        {
            var models = await ListModelsAsync(b, null, timeout ?? DetectTimeout, cancellationToken).ConfigureAwait(false);
            return models == null ? null : new LocalModelServer(b, models);
        })).ConfigureAwait(false);
        return results.OfType<LocalModelServer>().ToList();
    }

    /// <summary>
    /// Lists the endpoint's models, checks <paramref name="model"/> is among them, then sends a tiny chat completion
    /// (max_tokens 8, "Reply with OK"). Latency is that of the completion. Never throws for network or server errors.
    /// </summary>
    public async Task<LocalModelTestResult> TestAsync(string endpoint, string model, string? apiKey, TimeSpan listTimeout, TimeSpan chatTimeout, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeEndpoint(endpoint);
        if (string.IsNullOrEmpty(baseUrl))
            return new(false, "Endpoint is not an http(s) URL.", null);
        model = (model ?? "").Trim();
        if (model.Length == 0)
            return new(false, "No model name set.", null);

        var (models, listError) = await TryListModelsAsync(baseUrl, apiKey, listTimeout, cancellationToken).ConfigureAwait(false);
        if (models == null)
            return new(false, $"{baseUrl}/models: {listError}", null);
        if (!models.Contains(model, StringComparer.Ordinal))
        {
            var shown = string.Join(", ", models.Take(8)) + (models.Count > 8 ? ", …" : "");
            return new(false, models.Count == 0 ? $"The server lists no models; '{model}' is not available." : $"'{model}' is not listed. Available: {shown}", null);
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8,
            ["temperature"] = 0,
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with OK" }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, apiKey);

        var stopwatch = Stopwatch.StartNew();
        var (status, text, error) = await SendAsync(request, chatTimeout, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        if (error != null)
            return new(false, $"chat/completions: {error}", null);
        if (status is < 200 or > 299)
            return new(false, $"chat/completions: HTTP {status} {Snippet(text)}", stopwatch.ElapsedMilliseconds);
        var reply = ParseCompletion(text);
        if (reply == null)
            return new(false, $"chat/completions: not an OpenAI-style completion: {Snippet(text)}", stopwatch.ElapsedMilliseconds);
        return new(true, $"OK: {model} replied \"{Snippet(reply.Trim(), 40)}\"", stopwatch.ElapsedMilliseconds);
    }

    private async Task<(IReadOnlyList<string>? Models, string Error)> TryListModelsAsync(string baseUrl, string? apiKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEndpoint(baseUrl);
        if (string.IsNullOrEmpty(normalized))
            return (null, "not an http(s) URL");

        var (models, error) = await GetModelsAsync($"{normalized}/models", apiKey, timeout, cancellationToken).ConfigureAwait(false);
        if (models != null)
            return (models, "");

        // Ollama before its OpenAI layer (or with it disabled) still answers /api/tags at the server root.
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var (tags, _) = await GetModelsAsync($"{normalized[..^3]}/api/tags", apiKey, timeout, cancellationToken).ConfigureAwait(false);
            if (tags != null)
                return (tags, "");
        }

        return (null, error);
    }

    private async Task<(IReadOnlyList<string>? Models, string Error)> GetModelsAsync(string url, string? apiKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request, apiKey);
        var (status, text, error) = await SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        if (error != null)
            return (null, error);
        if (status is < 200 or > 299)
            return (null, $"HTTP {status} {Snippet(text)}");
        var models = ParseModels(text);
        return models == null ? (null, $"not a model list: {Snippet(text)}") : (models, "");
    }

    private async Task<(int Status, string Text, string? Error)> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, text, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, "", $"no answer within {timeout.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException ex)
        {
            return (0, "", ex.HttpRequestError == HttpRequestError.ConnectionError ? "connection refused (is the server running?)" : ex.Message);
        }
    }

    private static void Authorize(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static string Snippet(string? text, int max = MaxErrorBody)
    {
        var flat = (text ?? "").ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
