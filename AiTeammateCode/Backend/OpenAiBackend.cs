using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

            messages.Add(new { role = "user", content = request.UserPrompt });

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

    public async IAsyncEnumerable<string> SendStreamingAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var msg in request.ConversationHistory)
            messages.Add(new { role = msg.Role, content = msg.Content });
        messages.Add(new { role = "user", content = request.UserPrompt });

        var body = new
        {
            model = _model,
            messages,
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            stream = true
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;

            string? pendingText = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                        pendingText = text;
                }
            }
            catch (JsonException) { }

            if (pendingText != null)
                yield return pendingText;
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
}
