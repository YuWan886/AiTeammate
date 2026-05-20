namespace AiTeammate.AiTeammateCode.Backend;

public enum AiRequestType
{
    Decision,
    Chat,
    Suggestion,
    Memory,
    SelfReflection
}

public class AiRequest
{
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public AiRequestType Type { get; set; }
    public int MaxTokens { get; set; } = 2048;
    public float Temperature { get; set; } = 0.7f;

    /// <summary>多轮对话历史（user/assistant 交替）。DeepSeek 无状态 API 每次需携带完整历史。</summary>
    public List<AiMessage> ConversationHistory { get; set; } = new();

    // === DeepSeek 专属优化参数 ===

    /// <summary>是否启用 JSON Output 模式（response_format: json_object）。
    /// 启用后 DeepSeekBackend 会自动确保 prompt 中包含 "json" 关键词。</summary>
    public bool EnableJsonMode { get; set; }

    /// <summary>Top-P 采样（0~1），0 表示不使用。DeepSeek 推荐与 temperature 二选一。</summary>
    public float TopP { get; set; }

    /// <summary>频率惩罚（-2.0~2.0），正值降低重复。DeepSeek 支持。</summary>
    public float FrequencyPenalty { get; set; }

    /// <summary>存在惩罚（-2.0~2.0），正值鼓励新话题。DeepSeek 支持。</summary>
    public float PresencePenalty { get; set; }

    /// <summary>是否启用模型思考模式。默认 false，优先减少响应延迟。</summary>
    public bool EnableThinkingMode { get; set; }
}

public class AiResponse
{
    public string Content { get; set; } = "";
    public int TokensUsed { get; set; }
    public TimeSpan Latency { get; set; }
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; } = "";
}

public class AiMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}
