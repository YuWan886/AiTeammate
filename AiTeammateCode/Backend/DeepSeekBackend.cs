using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Backend;

/// <summary>
/// DeepSeek API 后端，专门针对 DeepSeek 大模型优化：
/// - 多轮对话：自动拼接 conversation history，保持上下文连贯
/// - JSON Output：使用 response_format json_object，自动注入 json 关键词
/// - 空内容重试：DeepSeek JSON 模式偶发空返回，自动重试
/// - 额外参数：frequency_penalty, presence_penalty, top_p
/// </summary>
public class DeepSeekBackend : IAiBackend
{
    public string BackendName => "DeepSeek";
    public string BackendVersion => "v4-flash";

    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly bool _enableThinkingMode;
    private const int MaxRetryOnEmpty = 2;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    public DeepSeekBackend(string apiKey, string model = "deepseek-v4-flash", string baseUrl = "https://api.deepseek.com/v1", bool enableThinkingMode = false)
    {
        _apiKey = apiKey;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _enableThinkingMode = enableThinkingMode;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct)
    {
        // 对 JSON 模式请求进行重试（DeepSeek 偶发返回空 content）
        int maxAttempts = request.EnableJsonMode ? MaxRetryOnEmpty + 1 : 1;
        AiResponse? lastResponse = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // 重试前等待一小段时间
                await Task.Delay(200 * attempt, ct);
            }

            var response = await SendSingleAsync(request, ct);
            lastResponse = response;

            if (!response.IsSuccess)
                return response; // 失败不重试，直接返回错误

            // JSON 模式：检查是否空内容，空则重试
            if (request.EnableJsonMode && string.IsNullOrWhiteSpace(response.Content))
                continue;

            return response;
        }

        return lastResponse ?? new AiResponse { IsSuccess = false, ErrorMessage = "All retry attempts failed" };
    }

    private async Task<AiResponse> SendSingleAsync(AiRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // === 多轮对话：构建 messages 数组 ===
            var messages = new List<object>();

            // 系统提示 — 如果启用 JSON 模式，确保包含 "json" 关键词
            var systemPrompt = request.SystemPrompt ?? "";
            if (request.EnableJsonMode && !HasJsonKeyword(systemPrompt))
            {
                // DeepSeek 要求 system/user prompt 中必须包含 "json" 字样
                systemPrompt = systemPrompt.TrimEnd() + "\n\n请严格以 JSON 格式输出。";
            }

            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });

            // 多轮对话历史 — 保留完整的 user/assistant 交替记录
            foreach (var msg in request.ConversationHistory)
            {
                if (string.IsNullOrWhiteSpace(msg.Content)) continue;
                messages.Add(new { role = msg.Role, content = msg.Content });
            }

            // 当前用户提示 — 如果启用 JSON 模式，确保包含 "json" 关键词
            var userPrompt = request.UserPrompt ?? "";
            if (request.EnableJsonMode && !HasJsonKeyword(userPrompt) && !HasJsonKeyword(systemPrompt))
            {
                // system 和 user 都没有 "json"，在 user prompt 中追加
                userPrompt += "\n\n请以 JSON 格式返回结果。";
            }

            messages.Add(new { role = "user", content = userPrompt });

            // === JSON Output 模式 ===
            object? responseFormat = null;
            if (request.EnableJsonMode)
            {
                responseFormat = new { type = "json_object" };
            }

            // === 构建请求体 ===
            var bodyDict = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = messages,
                ["max_tokens"] = request.MaxTokens,
                ["temperature"] = request.Temperature
            };

            if (request.TopP > 0)
                bodyDict["top_p"] = request.TopP;

            if (request.FrequencyPenalty != 0)
                bodyDict["frequency_penalty"] = request.FrequencyPenalty;

            if (request.PresencePenalty != 0)
                bodyDict["presence_penalty"] = request.PresencePenalty;

            if (responseFormat != null)
                bodyDict["response_format"] = responseFormat;

            if (!(request.EnableThinkingMode || _enableThinkingMode) && SupportsThinkingToggle(_model))
                bodyDict["thinking"] = new { type = "disabled" };

            // === 发送 HTTP 请求 ===
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            var json = JsonSerializer.Serialize(bodyDict);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(httpRequest, ct);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                return new AiResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {TruncateError(responseJson)}",
                    Latency = sw.Elapsed
                };
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // 安全提取 content
            var choices = root.GetProperty("choices");
            string content = "";
            if (choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                if (message.TryGetProperty("content", out var contentElem))
                    content = contentElem.GetString() ?? "";
            }

            // 安全提取 usage
            int tokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("total_tokens", out var totalTokens))
                    tokens = totalTokens.GetInt32();
            }

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
        // 构建 messages（与 SendSingleAsync 逻辑一致）
        var messages = new List<object>();

        var systemPrompt = request.SystemPrompt ?? "";
        if (request.EnableJsonMode && !HasJsonKeyword(systemPrompt))
            systemPrompt = systemPrompt.TrimEnd() + "\n\n请严格以 JSON 格式输出。";

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });

        foreach (var msg in request.ConversationHistory)
        {
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;
            messages.Add(new { role = msg.Role, content = msg.Content });
        }

        var userPrompt = request.UserPrompt ?? "";
        if (request.EnableJsonMode && !HasJsonKeyword(userPrompt) && !HasJsonKeyword(systemPrompt))
            userPrompt += "\n\n请以 JSON 格式返回结果。";

        messages.Add(new { role = "user", content = userPrompt });

        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = true
        };

        if (request.TopP > 0)
            bodyDict["top_p"] = request.TopP;

        // 流式模式下一般不启用 json_object（流式 JSON 难以解析）
        // 但如果显式要求了，仍然传递

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");
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
            MaxTokens = 4
        };
        var response = await SendAsync(request, ct);
        return response.IsSuccess;
    }

    // === 辅助方法 ===

    /// <summary>
    /// 检查文本中是否包含 "json" 关键词（不区分大小写）。
    /// DeepSeek JSON Output 功能要求 system 或 user prompt 中必须出现 "json" 字样。
    /// </summary>
    private static bool HasJsonKeyword(string text)
    {
        return text.Contains("json", StringComparison.OrdinalIgnoreCase)
            || text.Contains("JSON", StringComparison.Ordinal);
    }

    /// <summary>
    /// 截断过长的错误信息，避免日志膨胀。
    /// </summary>
    private static string TruncateError(string error, int maxLen = 300)
    {
        if (string.IsNullOrWhiteSpace(error)) return error;
        return error.Length <= maxLen ? error : error.Substring(0, maxLen) + "...";
    }

    private static bool SupportsThinkingToggle(string model)
    {
        return model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }
}
