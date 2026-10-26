namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>
/// All player-editable settings. Persisted by RitsuLib at <c>SaveScope.Global</c> (one set of
/// values shared by every save slot), which matches the old <c>config.json</c> behaviour.
/// </summary>
public sealed class AiSettings
{
    // ---- AI backend ----

    /// <summary>Active backend name: DeepSeek / OpenAI / Ollama. 当前使用的 AI 后端。</summary>
    public string ActiveBackend { get; set; } = "DeepSeek";

    // Stored as-is in settings.json; the settings page only ever displays them masked.
    public string DeepSeekApiKey { get; set; } = "";
    public string DeepSeekModel { get; set; } = "deepseek-v4-flash";
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o";
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string OllamaModel { get; set; } = "llama3";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ask DeepSeek-compatible models to emit reasoning. 启用模型思考模式（更慢、更贵）。</summary>
    public bool EnableModelThinking { get; set; }

    // ---- Decision ----

    public int DecisionTimeoutMs { get; set; } = 20000;
    public int ChatTimeoutMs { get; set; } = 10000;
    public int NonCombatRetryCount { get; set; } = 2;

    /// <summary>Put the draw / discard / exhaust pile contents into combat prompts. 战斗提示词中包含牌堆内容。</summary>
    public bool EnablePileDetail { get; set; } = true;

    /// <summary>Let the AI call read-only tools (read_pile / card_info / read_relics …) during combat decisions. 允许 AI 主动读取信息。</summary>
    public bool EnableReadTools { get; set; } = true;

    /// <summary>Same tool access for out-of-combat decisions (map / reward / shop / rest / event). 战斗外决策也允许调用工具。</summary>
    public bool EnableOutOfCombatReadTools { get; set; } = true;

    // ---- Personality / memory ----

    public string ActivePersonalityId { get; set; } = "tactician";
    public bool EnablePersonalityGrowth { get; set; } = true;
    public bool EnableLongTermMemory { get; set; } = true;
    public int MaxMemoriesPerPrompt { get; set; } = 5;
    public int MemoryMergeThreshold { get; set; } = 200;

    // ---- Chat ----

    public bool EnableAIChat { get; set; } = true;
    public bool EnableSpeechBubble { get; set; } = true;
    public int ChatCooldownMs { get; set; } = 15000;
    public int MaxChatHistoryForPrompt { get; set; } = 10;

    // ---- Suggestions ----

    public bool EnableSuggestions { get; set; } = true;
    public bool AutoSuggestOnTurnStart { get; set; }
    public int MaxSuggestionsPerTurn { get; set; } = 3;

    // ---- Solo climb ----

    /// <summary>Lets the AI drive the local player in singleplayer runs. 单人模式由 AI 接管。</summary>
    public bool EnableSoloClimb { get; set; }

    /// <summary>Character id used by the "start AI solo climb" button; empty = random.</summary>
    public string SoloCharacterId { get; set; } = "";

    /// <summary>Ascension level for a new AI solo climb; 0 = no ascension. 单人爬塔的进阶难度。</summary>
    public int SoloAscensionLevel { get; set; }

    // ---- Maintenance ----

    public bool AutoRefreshCache { get; set; } = true;
    public bool EnableDecisionLog { get; set; } = true;
    public bool EnableTrafficLog { get; set; }
    public int LogRetention { get; set; } = 5;

    public void ResetToDefaults()
    {
        var defaults = new AiSettings();

        ActiveBackend = defaults.ActiveBackend;
        DeepSeekApiKey = defaults.DeepSeekApiKey;
        DeepSeekModel = defaults.DeepSeekModel;
        DeepSeekBaseUrl = defaults.DeepSeekBaseUrl;
        OpenAiApiKey = defaults.OpenAiApiKey;
        OpenAiModel = defaults.OpenAiModel;
        OpenAiBaseUrl = defaults.OpenAiBaseUrl;
        OllamaModel = defaults.OllamaModel;
        OllamaBaseUrl = defaults.OllamaBaseUrl;
        EnableModelThinking = defaults.EnableModelThinking;

        DecisionTimeoutMs = defaults.DecisionTimeoutMs;
        ChatTimeoutMs = defaults.ChatTimeoutMs;
        NonCombatRetryCount = defaults.NonCombatRetryCount;
        EnablePileDetail = defaults.EnablePileDetail;
        EnableReadTools = defaults.EnableReadTools;
        EnableOutOfCombatReadTools = defaults.EnableOutOfCombatReadTools;

        ActivePersonalityId = defaults.ActivePersonalityId;
        EnablePersonalityGrowth = defaults.EnablePersonalityGrowth;
        EnableLongTermMemory = defaults.EnableLongTermMemory;
        MaxMemoriesPerPrompt = defaults.MaxMemoriesPerPrompt;
        MemoryMergeThreshold = defaults.MemoryMergeThreshold;

        EnableAIChat = defaults.EnableAIChat;
        EnableSpeechBubble = defaults.EnableSpeechBubble;
        ChatCooldownMs = defaults.ChatCooldownMs;
        MaxChatHistoryForPrompt = defaults.MaxChatHistoryForPrompt;

        EnableSuggestions = defaults.EnableSuggestions;
        AutoSuggestOnTurnStart = defaults.AutoSuggestOnTurnStart;
        MaxSuggestionsPerTurn = defaults.MaxSuggestionsPerTurn;

        EnableSoloClimb = defaults.EnableSoloClimb;
        SoloCharacterId = defaults.SoloCharacterId;
        SoloAscensionLevel = defaults.SoloAscensionLevel;

        AutoRefreshCache = defaults.AutoRefreshCache;
        EnableDecisionLog = defaults.EnableDecisionLog;
        EnableTrafficLog = defaults.EnableTrafficLog;
        LogRetention = defaults.LogRetention;
    }
}
