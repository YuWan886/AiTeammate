using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Backend;

public class OpenAiBackend : IAiBackend
{
    public string BackendName => "OpenAI";
    public string BackendVersion => "GPT-4o";

    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    public OpenAiBackend(string apiKey, string model = "gpt-4o", string baseUrl = "https://api.openai.com/v1", bool enableThinkingMode = false)
    {
        _apiKey = apiKey;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                messages.Add(new { role = "system", content = request.SystemPrompt });

            foreach (var msg in request.ConversationHistory)
                messages.Add(new { role = msg.Role, content = msg.Content });

            messages.Add(BuildUserMessage(request.UserPrompt, request.UserImageDataUrl));

            object? responseFormat = null;
            if (request.EnableJsonMode)
                responseFormat = new { type = "json_object" };

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = messages,
                ["max_tokens"] = request.MaxTokens,
                ["temperature"] = request.Temperature
            };

            if (request.TopP > 0)
                body["top_p"] = request.TopP;
            if (request.FrequencyPenalty != 0)
                body["frequency_penalty"] = request.FrequencyPenalty;
            if (request.PresencePenalty != 0)
                body["presence_penalty"] = request.PresencePenalty;

            if (responseFormat != null)
                body["response_format"] = responseFormat;

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            var json = JsonSerializer.Serialize(body);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(httpRequest, ct);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                return new AiResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {responseJson}",
                    Latency = sw.Elapsed
                };
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            var usage = root.GetProperty("usage");
            var tokens = usage.GetProperty("total_tokens").GetInt32();

            sw.Stop();
            return new AiResponse
            {
                Content = content,
                TokensUsed = tokens,
                Latency = sw.Elapsed,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new AiResponse { IsSuccess = false, ErrorMessage = "Request timed out", Latency = sw.Elapsed };
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new AiResponse { IsSuccess = false, ErrorMessage = $"Network error: {ex.Message}", Latency = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AiResponse { IsSuccess = false, ErrorMessage = ex.Message, Latency = sw.Elapsed };
        }
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct)
    {
        var request = new AiRequest
        {
            SystemPrompt = "Reply with just 'OK'.",
            UserPrompt = "Test connection.",
            Type = AiRequestType.Decision,
            MaxTokens = 4
        };
        var response = await SendAsync(request, ct);
        return response.IsSuccess;
    }

    public Task<ModelListResult> ListModelsAsync(CancellationToken ct) =>
        ModelListClient.FetchOpenAiCompatibleAsync(_baseUrl, _apiKey, ct);

    /// <summary>Builds the final user message, using OpenAI's multi-part content when an image is attached.</summary>
    private static object BuildUserMessage(string text, string? imageDataUrl)
    {
        if (string.IsNullOrWhiteSpace(imageDataUrl))
            return new { role = "user", content = text };

        var parts = new List<object>
        {
            new { type = "text", text },
            new { type = "image_url", image_url = new { url = imageDataUrl } }
        };
        return new { role = "user", content = parts };
    }

    public void Dispose() => _client.Dispose();
}
