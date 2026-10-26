using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTeammate.AiTeammateCode.Decision;

public static class ResponseParser
{
    /// <summary>
    /// 从 AI 原始回复中提取 JSON 内容。
    /// 处理常见的格式问题：markdown 代码块、额外文本、思考过程等。
    /// </summary>
    /// <param name="rawContent">AI 返回的原始文本</param>
    /// <returns>提取出的纯 JSON 字符串；如果无法提取则返回原始内容</returns>
    public static string ExtractJson(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return rawContent;

        var content = rawContent.Trim();

        // 1. 尝试移除 markdown 代码块标记：```json ... ``` 或 ``` ... ```
        const string fenceJson = "```json";
        const string fenceEnd = "```";

        int fenceStartIdx = content.IndexOf(fenceJson, StringComparison.OrdinalIgnoreCase);
        if (fenceStartIdx >= 0)
        {
            // 找到 ```json 之后的内容
            int jsonStart = fenceStartIdx + fenceJson.Length;
            int fenceEndIdx = content.IndexOf(fenceEnd, jsonStart, StringComparison.Ordinal);
            if (fenceEndIdx > jsonStart)
            {
                content = content.Substring(jsonStart, fenceEndIdx - jsonStart).Trim();
            }
            else
            {
                // 没有结束标记，取 ```json 之后的所有内容
                content = content.Substring(jsonStart).Trim();
            }
        }
        else
        {
            // 尝试 ``` (不带 json 标记)
            fenceStartIdx = content.IndexOf(fenceEnd, StringComparison.Ordinal);
            if (fenceStartIdx >= 0)
            {
                int jsonStart = fenceStartIdx + fenceEnd.Length;
                // 跳过语言标识（如 "json"）
                var afterFence = content.Substring(jsonStart).TrimStart();
                if (afterFence.Length > 0)
                {
                    // 找到下一个 ```
                    int fence2Idx = afterFence.IndexOf(fenceEnd, StringComparison.Ordinal);
                    if (fence2Idx > 0)
                    {
                        content = afterFence.Substring(0, fence2Idx).Trim();
                    }
                    else
                    {
                        content = afterFence;
                    }
                }
            }
        }

        // 2. 尝试找到 JSON 对象的边界 { ... }
        int firstBrace = content.IndexOf('{');
        int lastBrace = content.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            // 提取从第一个 { 到最后一个 } 的内容
            content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
        }
        else
        {
            // 3. 尝试找到 JSON 数组的边界 [ ... ]
            int firstBracket = content.IndexOf('[');
            int lastBracket = content.LastIndexOf(']');
            if (firstBracket >= 0 && lastBracket > firstBracket)
            {
                content = content.Substring(firstBracket, lastBracket - firstBracket + 1);
            }
        }

        return content;
    }

    /// <summary>
    /// 使用正则表达式从（可能格式错误的）JSON 中提取 "choice" 整数值。
    /// 比 JsonDocument.Parse 更宽容，能处理尾部逗号、多余字段等问题。
    /// </summary>
    /// <returns>提取到的 choice 值；如果无法提取则返回 null</returns>
    public static int? TryExtractChoice(string rawContent)
    {
        var json = ExtractJson(rawContent);
        // 匹配 "choice": 数字 (支持正负号)
        var match = Regex.Match(json, @"""choice""\s*:\s*(-?\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var choice))
            return choice;
        return null;
    }

    /// <summary>
    /// 使用正则表达式从（可能格式错误的）JSON 中提取 "reason" 字符串值。
    /// </summary>
    /// <returns>提取到的 reason；如果无法提取则返回 ""</returns>
    public static string TryExtractReason(string rawContent)
    {
        var json = ExtractJson(rawContent);
        // 匹配 "reason": "..." (支持转义引号)
        var match = Regex.Match(json, @"""reason""\s*:\s*""((?:[^""\\]|\\.)*)""");
        if (match.Success)
            return Regex.Unescape(match.Groups[1].Value);
        return "";
    }

    public static DecisionResult ParseDecision(string content)
    {
        try
        {
            var json = ExtractJson(content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new DecisionResult
            {
                Thinking = GetStringProperty(root, "thinking"),
                ChatMessage = GetStringProperty(root, "chat_message")
            };

            if (root.TryGetProperty("actions", out var actions))
            {
                foreach (var action in actions.EnumerateArray())
                    result.Actions.Add(ParseAction(action));
            }

            if (root.TryGetProperty("deferred_actions", out var deferred))
            {
                foreach (var action in deferred.EnumerateArray())
                    result.DeferredActions.Add(ParseAction(action));
            }

            ParseToolCalls(root, result.ToolCalls);

            return result;
        }
        catch (JsonException)
        {
            return DecisionResult.Fallback();
        }
    }

    /// <summary>
    /// Extracts tool calls from a raw response. Returns an empty list when the response has none or
    /// is not valid JSON, so callers can use this as a cheap "did the model ask for a tool?" check.
    /// </summary>
    public static List<ToolCall> ExtractToolCalls(string content)
    {
        var calls = new List<ToolCall>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(content));
            ParseToolCalls(doc.RootElement, calls);
        }
        catch (JsonException)
        {
            // Not JSON: no tools to run.
        }

        return calls;
    }

    /// <summary>
    /// Reads <c>tool_calls</c> (array) or <c>tool_call</c> (single object/string). Also accepts a bare
    /// string entry, so a sloppy model can emit <c>{"tool_calls":["read_pile"]}</c>.
    /// </summary>
    private static void ParseToolCalls(JsonElement root, List<ToolCall> sink)
    {
        if (root.TryGetProperty("tool_calls", out var calls))
        {
            if (calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (ParseToolCall(call) is { } parsed)
                        sink.Add(parsed);
                }
            }
            else if (ParseToolCall(calls) is { } single)
            {
                sink.Add(single);
            }

            return;
        }

        if (root.TryGetProperty("tool_call", out var toolCall) && ParseToolCall(toolCall) is { } one)
            sink.Add(one);
    }

    private static ToolCall? ParseToolCall(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var bareName = element.GetString()?.Trim() ?? "";
            return bareName.Length > 0 ? new ToolCall { Name = bareName } : null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var name = GetStringProperty(element, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = GetStringProperty(element, "tool");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var call = new ToolCall { Name = name.Trim() };

        if (element.TryGetProperty("args", out var args) || element.TryGetProperty("arguments", out args))
            FillToolArgs(call, args);

        return call;
    }

    private static void FillToolArgs(ToolCall call, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
                call.Args[prop.Name] = ArgValue(prop.Value);
            return;
        }

        if (args.ValueKind != JsonValueKind.String)
            return;

        // Some models serialize the argument object as a JSON string.
        var raw = args.GetString() ?? "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                call.Args["input"] = raw;
                return;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
                call.Args[prop.Name] = ArgValue(prop.Value);
        }
        catch (JsonException)
        {
            call.Args["input"] = raw;
        }
    }

    private static string ArgValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => ""
    };

    private static DecisionAction ParseAction(JsonElement action)
    {
        return new DecisionAction
        {
            Type = GetStringProperty(action, "type"),
            CardName = GetStringProperty(action, "card_name"),
            PotionName = GetStringProperty(action, "potion_name"),
            Target = GetStringProperty(action, "target"),
            Player = GetStringProperty(action, "player"),
            Reason = GetStringProperty(action, "reason"),
            WaitFor = GetStringProperty(action, "wait_for")
        };
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
            return "";

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? "",
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }
}
