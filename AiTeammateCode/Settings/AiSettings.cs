namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>Scrolling direction of the live-danmaku overlay. 弹幕滚动方向。</summary>
public enum DanmakuDirection
{
    /// <summary>Text enters from the left and moves right. 从左向右。</summary>
    LeftToRight = 0,

    /// <summary>Text enters from the right and moves left (the usual video-danmaku direction). 从右向左。</summary>
    RightToLeft = 1
}

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

    /// <summary>
    /// Lets the AI capture the current game screen and attach it to the follow-up request.
    /// Only models with image input support can use it. 允许 AI 截取当前游戏画面（仅视觉模型有效）。
    /// </summary>
    public bool EnableScreenshotTool { get; set; }

    // ---- Bilibili live danmaku ----

    /// <summary>Shows the live-room danmaku overlay in game. 在游戏内显示 B 站直播弹幕。</summary>
    public bool EnableBilibiliDanmaku { get; set; }

    /// <summary>Live room id or URL, for example <c>21452505</c>. 直播间房间号或链接。</summary>
    public string BilibiliRoomId { get; set; } = "";

    /// <summary>Longest danmaku text shown on screen (characters). 屏幕上弹幕文字的最大字数。</summary>
    public int DanmakuMaxChars { get; set; } = 50;

    /// <summary>Whether danmaku text enters from the left or the right. 弹幕滚动方向。</summary>
    public DanmakuDirection DanmakuMoveDirection { get; set; } = DanmakuDirection.LeftToRight;

    /// <summary>Scroll speed in pixels per second at a 1080p viewport height. 弹幕速度（1080p 下的像素/秒）。</summary>
    public int DanmakuSpeed { get; set; } = 220;

    /// <summary>Danmaku font size in pixels. 弹幕字号。</summary>
    public int DanmakuFontSize { get; set; } = 20;

    /// <summary>Danmaku text opacity in percent. 弹幕不透明度（百分比）。</summary>
    public int DanmakuOpacity { get; set; } = 100;

    /// <summary>Shows the sender's Bilibili avatar next to the text. 弹幕前显示发送者头像。</summary>
    public bool DanmakuShowAvatar { get; set; } = true;

    /// <summary>In multiplayer, only the host's screen shows the danmaku. 多人模式下只在房主屏幕上显示。</summary>
    public bool DanmakuHostOnly { get; set; } = true;

    /// <summary>Percentage of the screen height used by the danmaku lanes. 弹幕区域占屏幕高度百分比。</summary>
    public int DanmakuAreaPercent { get; set; } = 55;

    // Stored as-is; the settings page only ever displays them masked. 登录 Cookie，仅存储不回显。
    public string BilibiliSessData { get; set; } = "";
    public string BilibiliBiliJct { get; set; } = "";

    /// <summary>Lets the AI reply to live-room danmaku (requires a logged-in Bilibili account). 允许 AI 回复弹幕。</summary>
    public bool EnableDanmakuAiChat { get; set; } = true;

    /// <summary>Minimum time between two AI danmaku replies. 两次 AI 弹幕回复的最小间隔。</summary>
    public int DanmakuAiChatCooldownMs { get; set; } = 30000;

    /// <summary>How many recent danmaku are shown to the AI in one reply request. 每次回复参考的弹幕条数。</summary>
    public int DanmakuAiChatBatchSize { get; set; } = 6;

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
        EnableScreenshotTool = defaults.EnableScreenshotTool;

        EnableBilibiliDanmaku = defaults.EnableBilibiliDanmaku;
        BilibiliRoomId = defaults.BilibiliRoomId;
        DanmakuMaxChars = defaults.DanmakuMaxChars;
        DanmakuMoveDirection = defaults.DanmakuMoveDirection;
        DanmakuSpeed = defaults.DanmakuSpeed;
        DanmakuFontSize = defaults.DanmakuFontSize;
        DanmakuOpacity = defaults.DanmakuOpacity;
        DanmakuShowAvatar = defaults.DanmakuShowAvatar;
        DanmakuHostOnly = defaults.DanmakuHostOnly;
        DanmakuAreaPercent = defaults.DanmakuAreaPercent;
        BilibiliSessData = defaults.BilibiliSessData;
        BilibiliBiliJct = defaults.BilibiliBiliJct;
        EnableDanmakuAiChat = defaults.EnableDanmakuAiChat;
        DanmakuAiChatCooldownMs = defaults.DanmakuAiChatCooldownMs;
        DanmakuAiChatBatchSize = defaults.DanmakuAiChatBatchSize;

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
