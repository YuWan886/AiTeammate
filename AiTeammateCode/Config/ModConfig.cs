namespace AiTeammate.AiTeammateCode.Config;

public class ModConfig
{
    // 多人模式
    public bool AutoJoinMultiplayer { get; set; } = true;

    // 人格
    public string ActivePersonalityId { get; set; } = "tactician";
    public bool EnablePersonalityGrowth { get; set; } = true;
    public bool EnableLongTermMemory { get; set; } = true;
    public int MaxMemoriesPerPrompt { get; set; } = 5;
    public int MemoryMergeThreshold { get; set; } = 200;

    // AI 引擎
    public string ActiveBackend { get; set; } = "DeepSeek";
    public Dictionary<string, BackendConfigEntry> Backends { get; set; } = new()
    {
        ["DeepSeek"] = new() { ApiKey = "", Model = "deepseek-v4-flash", BaseUrl = "https://api.deepseek.com/v1" },
        ["OpenAI"] = new() { ApiKey = "", Model = "gpt-4o", BaseUrl = "https://api.openai.com/v1" },
        ["Ollama"] = new() { ApiKey = "", Model = "llama3", BaseUrl = "http://localhost:11434" },
    };
    public bool EnableModelThinking { get; set; } = false;

    // 决策
    public int DecisionTimeoutMs { get; set; } = 8000;
    public int ChatTimeoutMs { get; set; } = 10000;
    public int NonCombatRetryCount { get; set; } = 2;

    // 聊天
    public bool EnableAIChat { get; set; } = true;
    public int ChatCooldownMs { get; set; } = 15000;
    public int MaxChatHistoryForPrompt { get; set; } = 10;
    public bool SaveChatLogs { get; set; } = true;

    // 建议
    public bool EnableSuggestions { get; set; } = true;
    public int MaxSuggestionsPerTurn { get; set; } = 3;

    // 缓存
    public bool AutoRefreshCache { get; set; } = true;
}

public class BackendConfigEntry
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}
