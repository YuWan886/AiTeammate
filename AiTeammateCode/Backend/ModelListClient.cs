using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Backend;

/// <summary>Result of a "list the models this endpoint exposes" request. 模型列表请求结果。</summary>
public sealed class ModelListResult
{
    public bool IsSuccess { get; init; }
    public string ErrorMessage { get; init; } = "";
    public List<string> Models { get; init; } = new();

    public static ModelListResult Fail(string error) => new() { IsSuccess = false, ErrorMessage = error };

    public static ModelListResult Ok(IEnumerable<string> models) =>
        new() { IsSuccess = true, Models = models.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList() };
}

/// <summary>
/// Shared HTTP plumbing for the OpenAI-compatible <c>GET /models</c> endpoint and Ollama's
/// <c>GET /api/tags</c>. Keeping it out of the backends lets the settings page fetch a model list
/// without instantiating a chat backend. 通过 API 端点获取可用模型列表。
/// </summary>
public static class ModelListClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>Fetches <c>{baseUrl}/models</c> (OpenAI, DeepSeek and most compatible gateways).</summary>
    public static async Task<ModelListResult> FetchOpenAiCompatibleAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return ModelListResult.Fail("Base URL is empty");

        var url = baseUrl.TrimEnd('/') + "/models";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return ModelListResult.Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

            return ParseOpenAiModelList(body);
        }
        catch (OperationCanceledException)
        {
            return ModelListResult.Fail("Request timed out");
        }
        catch (Exception ex)
        {
            return ModelListResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Fetches Ollama's native <c>/api/tags</c>; falls back to the OpenAI-compatible <c>/v1/models</c>
    /// so an OpenAI-compatible proxy in front of Ollama also works.
    /// </summary>
    public static async Task<ModelListResult> FetchOllamaAsync(string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return ModelListResult.Fail("Base URL is empty");

        var root = baseUrl.TrimEnd('/');
        var hasV1 = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
        var nativeRoot = hasV1 ? root[..^3].TrimEnd('/') : root;

        var native = await FetchJsonModelsAsync(nativeRoot + "/api/tags", "models", "name", ct);
        if (native.IsSuccess && native.Models.Count > 0)
            return native;

        var compatibleUrl = hasV1 ? root + "/models" : root + "/v1/models";
        var compatible = await FetchJsonModelsAsync(compatibleUrl, "data", "id", ct);
        return compatible.IsSuccess ? compatible : native;
    }

    private static async Task<ModelListResult> FetchJsonModelsAsync(
        string url, string arrayProperty, string nameProperty, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return ModelListResult.Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(arrayProperty, out var array)
                || array.ValueKind != JsonValueKind.Array)
                return ModelListResult.Fail($"Response has no \"{arrayProperty}\" array");

            var models = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var direct = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(direct))
                        models.Add(direct.Trim());
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty(nameProperty, out var name)
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    models.Add(name.GetString()!.Trim());
                }
            }

            return ModelListResult.Ok(models);
        }
        catch (OperationCanceledException)
        {
            return ModelListResult.Fail("Request timed out");
        }
        catch (Exception ex)
        {
            return ModelListResult.Fail(ex.Message);
        }
    }

    /// <summary>Parses <c>{"data":[{"id":"model-name"}, ...]}</c> and tolerates plain string arrays.</summary>
    private static ModelListResult ParseOpenAiModelList(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
                array = root;
            else if (!root.TryGetProperty("data", out array) || array.ValueKind != JsonValueKind.Array)
                return ModelListResult.Fail("Response has no \"data\" array");

            var models = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var direct = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(direct))
                        models.Add(direct.Trim());
                }
                else if (entry.ValueKind == JsonValueKind.Object
                         && entry.TryGetProperty("id", out var id)
                         && id.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    models.Add(id.GetString()!.Trim());
                }
            }

            return models.Count == 0
                ? ModelListResult.Fail("The endpoint returned no models")
                : ModelListResult.Ok(models);
        }
        catch (JsonException ex)
        {
            return ModelListResult.Fail($"Invalid JSON: {ex.Message}");
        }
    }

    private static string Truncate(string text, int maxLength = 300) =>
        string.IsNullOrEmpty(text) ? text
            : text.Length <= maxLength ? text
            : text[..maxLength] + "…";
}
