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

            return result;
        }
        catch (JsonException)
        {
            return DecisionResult.Fallback();
        }
    }

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
