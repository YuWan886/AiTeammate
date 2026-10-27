using Godot;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Ui.Toast;
using STS2RitsuLib.Utils.Persistence;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Live;

namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>
/// RitsuLib settings page for every user-editable option. All controls bind directly to the
/// live <see cref="AiSettings"/> / <see cref="PersonalityData"/> instances, so changes apply
/// immediately and are persisted by RitsuLib.
/// </summary>
public static class AiSettingsPage
{
    private const string LogDir = ModPaths.LogDir;

    private static readonly string[] BackendNames = ["DeepSeek", "OpenAI", "Ollama"];

    // ---- AiSettings bindings ----

    private static ModSettingsValueBinding<AiSettings, string> B(Func<AiSettings, string> get, Action<AiSettings, string> set) =>
        new(MainFile.ModId, AiSettingsStore.DataKey, SaveScope.Global, get, set);

    private static ModSettingsValueBinding<AiSettings, bool> B(Func<AiSettings, bool> get, Action<AiSettings, bool> set) =>
        new(MainFile.ModId, AiSettingsStore.DataKey, SaveScope.Global, get, set);

    private static ModSettingsValueBinding<AiSettings, int> B(Func<AiSettings, int> get, Action<AiSettings, int> set) =>
        new(MainFile.ModId, AiSettingsStore.DataKey, SaveScope.Global, get, set);

    private static ModSettingsValueBinding<AiSettings, DanmakuDirection> B(
        Func<AiSettings, DanmakuDirection> get, Action<AiSettings, DanmakuDirection> set) =>
        new(MainFile.ModId, AiSettingsStore.DataKey, SaveScope.Global, get, set);

    private static readonly ModSettingsValueBinding<AiSettings, string> ActiveBackend =
        B(s => s.ActiveBackend, (s, v) => s.ActiveBackend = v);

    // API keys are never read back into the settings UI: the field always starts empty, so the stored
    // key is not shown on screen. Committing a value replaces the key; an empty commit is ignored
    // (focus loss without typing) — use the "clear key" rows to remove a key.
    private static readonly ModSettingsValueBinding<AiSettings, string> DeepSeekApiKey =
        B(_ => "",
            (s, v) => { if (!string.IsNullOrEmpty(v)) s.DeepSeekApiKey = v; });
    private static readonly ModSettingsValueBinding<AiSettings, string> DeepSeekModel =
        B(s => s.DeepSeekModel, (s, v) => s.DeepSeekModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> DeepSeekModelPick =
        B(s => s.DeepSeekModel, (s, v) => s.DeepSeekModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> DeepSeekBaseUrl =
        B(s => s.DeepSeekBaseUrl, (s, v) => s.DeepSeekBaseUrl = v);

    private static readonly ModSettingsValueBinding<AiSettings, string> OpenAiApiKey =
        B(_ => "",
            (s, v) => { if (!string.IsNullOrEmpty(v)) s.OpenAiApiKey = v; });
    private static readonly ModSettingsValueBinding<AiSettings, string> OpenAiModel =
        B(s => s.OpenAiModel, (s, v) => s.OpenAiModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> OpenAiModelPick =
        B(s => s.OpenAiModel, (s, v) => s.OpenAiModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> OpenAiBaseUrl =
        B(s => s.OpenAiBaseUrl, (s, v) => s.OpenAiBaseUrl = v);

    private static readonly ModSettingsValueBinding<AiSettings, string> OllamaModel =
        B(s => s.OllamaModel, (s, v) => s.OllamaModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> OllamaModelPick =
        B(s => s.OllamaModel, (s, v) => s.OllamaModel = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> OllamaBaseUrl =
        B(s => s.OllamaBaseUrl, (s, v) => s.OllamaBaseUrl = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableModelThinking =
        B(s => s.EnableModelThinking, (s, v) => s.EnableModelThinking = v);

    private static readonly ModSettingsValueBinding<AiSettings, int> DecisionTimeoutMs =
        B(s => s.DecisionTimeoutMs, (s, v) => s.DecisionTimeoutMs = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> ChatTimeoutMs =
        B(s => s.ChatTimeoutMs, (s, v) => s.ChatTimeoutMs = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> NonCombatRetryCount =
        B(s => s.NonCombatRetryCount, (s, v) => s.NonCombatRetryCount = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnablePileDetail =
        B(s => s.EnablePileDetail, (s, v) => s.EnablePileDetail = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableReadTools =
        B(s => s.EnableReadTools, (s, v) => s.EnableReadTools = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableOutOfCombatReadTools =
        B(s => s.EnableOutOfCombatReadTools, (s, v) => s.EnableOutOfCombatReadTools = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableScreenshotTool =
        B(s => s.EnableScreenshotTool, (s, v) => s.EnableScreenshotTool = v);

    // ---- Bilibili live danmaku bindings ----

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableBilibiliDanmaku =
        B(s => s.EnableBilibiliDanmaku, (s, v) => s.EnableBilibiliDanmaku = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> BilibiliRoomId =
        B(s => s.BilibiliRoomId, (s, v) => s.BilibiliRoomId = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuMaxChars =
        B(s => s.DanmakuMaxChars, (s, v) => s.DanmakuMaxChars = v);
    private static readonly ModSettingsValueBinding<AiSettings, DanmakuDirection> DanmakuMoveDirection =
        B(s => s.DanmakuMoveDirection, (s, v) => s.DanmakuMoveDirection = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuSpeed =
        B(s => s.DanmakuSpeed, (s, v) => s.DanmakuSpeed = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuFontSize =
        B(s => s.DanmakuFontSize, (s, v) => s.DanmakuFontSize = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuOpacity =
        B(s => s.DanmakuOpacity, (s, v) => s.DanmakuOpacity = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> DanmakuShowAvatar =
        B(s => s.DanmakuShowAvatar, (s, v) => s.DanmakuShowAvatar = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> DanmakuHostOnly =
        B(s => s.DanmakuHostOnly, (s, v) => s.DanmakuHostOnly = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuAreaPercent =
        B(s => s.DanmakuAreaPercent, (s, v) => s.DanmakuAreaPercent = v);

    // Cookies are write-only in the UI, like the API keys.
    private static readonly ModSettingsValueBinding<AiSettings, string> BilibiliSessData =
        B(_ => "",
            (s, v) => { if (!string.IsNullOrEmpty(v)) s.BilibiliSessData = v; });
    private static readonly ModSettingsValueBinding<AiSettings, string> BilibiliBiliJct =
        B(_ => "",
            (s, v) => { if (!string.IsNullOrEmpty(v)) s.BilibiliBiliJct = v; });

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableDanmakuAiChat =
        B(s => s.EnableDanmakuAiChat, (s, v) => s.EnableDanmakuAiChat = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuAiChatCooldownMs =
        B(s => s.DanmakuAiChatCooldownMs, (s, v) => s.DanmakuAiChatCooldownMs = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> DanmakuAiChatBatchSize =
        B(s => s.DanmakuAiChatBatchSize, (s, v) => s.DanmakuAiChatBatchSize = v);

    private static readonly ModSettingsValueBinding<AiSettings, string> ActivePersonality =
        B(s => s.ActivePersonalityId, (s, v) => s.ActivePersonalityId = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnablePersonalityGrowth =
        B(s => s.EnablePersonalityGrowth, (s, v) => s.EnablePersonalityGrowth = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableLongTermMemory =
        B(s => s.EnableLongTermMemory, (s, v) => s.EnableLongTermMemory = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> MaxMemoriesPerPrompt =
        B(s => s.MaxMemoriesPerPrompt, (s, v) => s.MaxMemoriesPerPrompt = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> MemoryMergeThreshold =
        B(s => s.MemoryMergeThreshold, (s, v) => s.MemoryMergeThreshold = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableAIChat =
        B(s => s.EnableAIChat, (s, v) => s.EnableAIChat = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableSpeechBubble =
        B(s => s.EnableSpeechBubble, (s, v) => s.EnableSpeechBubble = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> ChatCooldownMs =
        B(s => s.ChatCooldownMs, (s, v) => s.ChatCooldownMs = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> MaxChatHistoryForPrompt =
        B(s => s.MaxChatHistoryForPrompt, (s, v) => s.MaxChatHistoryForPrompt = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableSuggestions =
        B(s => s.EnableSuggestions, (s, v) => s.EnableSuggestions = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> AutoSuggestOnTurnStart =
        B(s => s.AutoSuggestOnTurnStart, (s, v) => s.AutoSuggestOnTurnStart = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> MaxSuggestionsPerTurn =
        B(s => s.MaxSuggestionsPerTurn, (s, v) => s.MaxSuggestionsPerTurn = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableSoloClimb =
        B(s => s.EnableSoloClimb, (s, v) => s.EnableSoloClimb = v);
    private static readonly ModSettingsValueBinding<AiSettings, string> SoloCharacter =
        B(s => s.SoloCharacterId, (s, v) => s.SoloCharacterId = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> SoloAscension =
        B(s => s.SoloAscensionLevel, (s, v) => s.SoloAscensionLevel = v);

    private static readonly ModSettingsValueBinding<AiSettings, bool> AutoRefreshCache =
        B(s => s.AutoRefreshCache, (s, v) => s.AutoRefreshCache = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableDecisionLog =
        B(s => s.EnableDecisionLog, (s, v) => s.EnableDecisionLog = v);
    private static readonly ModSettingsValueBinding<AiSettings, bool> EnableTrafficLog =
        B(s => s.EnableTrafficLog, (s, v) => s.EnableTrafficLog = v);
    private static readonly ModSettingsValueBinding<AiSettings, int> LogRetention =
        B(s => s.LogRetention, (s, v) => s.LogRetention = v);

    // ---- Personality profile bindings (bound to the currently active profile) ----

    private static ModSettingsValueBinding<PersonalityData, string> P(
        Func<PersonalityProfile, string> get, Action<PersonalityProfile, string> set) =>
        new(MainFile.ModId, PersonalityStore.DataKey, SaveScope.Global,
            data => ResolveActive(data) is { } profile ? get(profile) : "",
            (data, value) =>
            {
                if (ResolveActive(data) is { } profile)
                    set(profile, value);
            });

    private static readonly ModSettingsValueBinding<PersonalityData, string> Backstory =
        P(p => p.Backstory, (p, v) => p.Backstory = v);
    private static readonly ModSettingsValueBinding<PersonalityData, string> CombatPhilosophy =
        P(p => p.CombatPhilosophy, (p, v) => p.CombatPhilosophy = v);
    private static readonly ModSettingsValueBinding<PersonalityData, string> CommunicationStyle =
        P(p => p.CommunicationStyle, (p, v) => p.CommunicationStyle = v);
    private static readonly ModSettingsValueBinding<PersonalityData, string> DecisionPrinciples =
        P(p => p.DecisionPrinciples, (p, v) => p.DecisionPrinciples = v);

    private static PersonalityProfile? ResolveActive(PersonalityData data)
    {
        var activeId = AiSettingsStore.Current.ActivePersonalityId;
        return data.Profiles.FirstOrDefault(p => p.Id == activeId)
               ?? data.Profiles.FirstOrDefault();
    }

    public static void Register()
    {
        RitsuLibFramework.RegisterModSettings(MainFile.ModId, page => page
            .WithTitle(S("settings.page.title", "AI Teammate"))
            .WithModDisplayName(S("settings.page.title", "AI Teammate"))
            .WithVisibleOnHostSurfaces(ModSettingsHostSurface.All)
            .AddSection("engine", section => section
                .WithTitle(S("settings.section.engine", "AI Engine"))
                .AddChoice("active_backend", S("settings.backend.active", "Active backend"), ActiveBackend,
                    BackendNames.Select(name => new ModSettingsChoiceOption<string>(name, ModSettingsText.Literal(name))),
                    description: S("settings.backend.active.desc", "The LLM service used for decisions and chat."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                .AddString("deepseek_api_key", S("settings.backend.deepseek_key", "DeepSeek API Key"), DeepSeekApiKey,
                    placeholder: ApiKeyPlaceholder(s => s.DeepSeekApiKey),
                    description: S("settings.backend.deepseek_key.desc",
                        "The saved key is never shown. Type a new key to replace it, or use the clear row below."))
                    .WithEntryVisibleWhen("deepseek_api_key", () => IsBackend("DeepSeek"))
                .AddString("deepseek_model", S("settings.backend.deepseek_model", "DeepSeek model"), DeepSeekModel,
                    description: S("settings.backend.deepseek_model.desc", "For example, deepseek-v4-flash."))
                    .WithEntryVisibleWhen("deepseek_model", () => IsBackend("DeepSeek"))
                .AddDynamicChoice("deepseek_model_pick", S("settings.backend.deepseek_model.pick", "DeepSeek model list"),
                    DeepSeekModelPick,
                    () => BuildModelOptions("DeepSeek", AiSettingsStore.Current.DeepSeekModel),
                    description: S("settings.backend.model_pick.desc",
                        "Models reported by the endpoint. Pick one to write it into the model field above; “Fetch models” refreshes this list."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                    .WithEntryVisibleWhen("deepseek_model_pick", () => IsBackend("DeepSeek"))
                .AddString("deepseek_base_url", S("settings.backend.deepseek_url", "DeepSeek Base URL"), DeepSeekBaseUrl)
                    .WithEntryVisibleWhen("deepseek_base_url", () => IsBackend("DeepSeek"))
                .AddButton("deepseek_api_key_clear", S("settings.backend.deepseek_key.clear", "Clear DeepSeek key"),
                    S("settings.backend.deepseek_key.clear.button", "Clear key"), ClearDeepSeekApiKey,
                    ModSettingsButtonTone.Normal,
                    S("settings.backend.deepseek_key.clear.desc", "Remove the saved DeepSeek key from this PC."))
                    .WithEntryVisibleWhen("deepseek_api_key_clear", () => IsBackend("DeepSeek"))
                .AddString("openai_api_key", S("settings.backend.openai_key", "OpenAI API Key"), OpenAiApiKey,
                    placeholder: ApiKeyPlaceholder(s => s.OpenAiApiKey),
                    description: S("settings.backend.openai_key.desc",
                        "The saved key is never shown. Type a new key to replace it, or use the clear row below."))
                    .WithEntryVisibleWhen("openai_api_key", () => IsBackend("OpenAI"))
                .AddString("openai_model", S("settings.backend.openai_model", "OpenAI model"), OpenAiModel)
                    .WithEntryVisibleWhen("openai_model", () => IsBackend("OpenAI"))
                .AddDynamicChoice("openai_model_pick", S("settings.backend.openai_model.pick", "OpenAI model list"),
                    OpenAiModelPick,
                    () => BuildModelOptions("OpenAI", AiSettingsStore.Current.OpenAiModel),
                    description: S("settings.backend.model_pick.desc",
                        "Models reported by the endpoint. Pick one to write it into the model field above; “Fetch models” refreshes this list."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                    .WithEntryVisibleWhen("openai_model_pick", () => IsBackend("OpenAI"))
                .AddString("openai_base_url", S("settings.backend.openai_url", "OpenAI Base URL"), OpenAiBaseUrl)
                    .WithEntryVisibleWhen("openai_base_url", () => IsBackend("OpenAI"))
                .AddButton("openai_api_key_clear", S("settings.backend.openai_key.clear", "Clear OpenAI key"),
                    S("settings.backend.openai_key.clear.button", "Clear key"), ClearOpenAiApiKey,
                    ModSettingsButtonTone.Normal,
                    S("settings.backend.openai_key.clear.desc", "Remove the saved OpenAI key from this PC."))
                    .WithEntryVisibleWhen("openai_api_key_clear", () => IsBackend("OpenAI"))
                .AddString("ollama_model", S("settings.backend.ollama_model", "Ollama model"), OllamaModel)
                    .WithEntryVisibleWhen("ollama_model", () => IsBackend("Ollama"))
                .AddDynamicChoice("ollama_model_pick", S("settings.backend.ollama_model.pick", "Ollama model list"),
                    OllamaModelPick,
                    () => BuildModelOptions("Ollama", AiSettingsStore.Current.OllamaModel),
                    description: S("settings.backend.model_pick.desc",
                        "Models reported by the endpoint. Pick one to write it into the model field above; “Fetch models” refreshes this list."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                    .WithEntryVisibleWhen("ollama_model_pick", () => IsBackend("Ollama"))
                .AddString("ollama_base_url", S("settings.backend.ollama_url", "Ollama Base URL"), OllamaBaseUrl)
                    .WithEntryVisibleWhen("ollama_base_url", () => IsBackend("Ollama"))
                .AddToggle("thinking", S("settings.backend.thinking", "Enable model thinking"), EnableModelThinking,
                    description: S("settings.backend.thinking.desc", "The model produces more reasoning, at the cost of slower responses and higher usage."))
                .AddButton("fetch_models", S("settings.backend.fetch.label", "Available models"),
                    S("settings.backend.fetch.button", "Fetch models"), FetchModels,
                    ModSettingsButtonTone.Normal,
                    S("settings.backend.fetch.desc",
                        "Asks the active backend which models it serves (GET /models, or Ollama /api/tags) and fills the model list above."))
                .AddButton("test_connection", S("settings.backend.test.label", "Test connection"),
                    S("settings.backend.test.button", "Test backend"), TestConnection,
                    ModSettingsButtonTone.Accent,
                    S("settings.backend.test.desc", "Send a minimal request to the active backend."))
            )
            .AddSection("decision", section => section
                .WithTitle(S("settings.section.decision", "Decisions"))
                .AddIntSlider("decision_timeout", S("settings.decision.timeout", "Combat decision timeout (ms)"), DecisionTimeoutMs,
                    5000, 120000, 1000,
                    description: S("settings.decision.timeout.desc", "Maximum wait for a single combat decision."))
                .AddIntSlider("chat_timeout", S("settings.chat.timeout", "Chat timeout (ms)"), ChatTimeoutMs,
                    2000, 60000, 500,
                    description: S("settings.chat.timeout.desc", "Maximum wait for a chat request."))
                .AddIntSlider("non_combat_retry", S("settings.decision.retry", "Out-of-combat retries"), NonCombatRetryCount,
                    0, 5, 1,
                    description: S("settings.decision.retry.desc", "Retries after a failed card, event, or shop decision."))
                .AddToggle("pile_detail", S("settings.decision.pile_detail", "Show pile contents to the AI"), EnablePileDetail,
                    description: S("settings.decision.pile_detail.desc",
                        "Include the draw, discard and exhaust pile contents in combat prompts. Turn off for small-context local models."))
                .AddToggle("read_tools", S("settings.decision.read_tools", "Tools in combat decisions"), EnableReadTools,
                    description: S("settings.decision.read_tools.desc",
                        "Allow one extra request per combat decision so the AI can call read tools (piles, relics, potions, teammates) before acting. Costs extra tokens and latency."))
                .AddToggle("read_tools_out_of_combat", S("settings.decision.read_tools_out_of_combat", "Tools in out-of-combat decisions"),
                    EnableOutOfCombatReadTools,
                    description: S("settings.decision.read_tools_out_of_combat.desc",
                        "Allow up to two extra requests for map / reward / shop / rest / event decisions so the AI can inspect the map, relics, potions and deck on demand."))
                .AddToggle("screenshot_tool", S("settings.decision.screenshot", "Let the AI see the game screen"),
                    EnableScreenshotTool,
                    description: S("settings.decision.screenshot.desc",
                        "Adds a screenshot tool to decisions. The current frame is attached to the follow-up request as an image; only vision-capable models can use it, and it costs extra tokens."))
                .AddParagraph("screenshot_model_hint", ModSettingsText.Dynamic(() => DescribeActiveModelVision()))
                    .WithEntryVisibleWhen("screenshot_model_hint", () => AiSettingsStore.Current.EnableScreenshotTool)
            )
            .AddSection("personality", section => section
                .WithTitle(S("settings.section.personality", "Personality"))
                .AddDynamicChoice("active_personality", S("settings.personality.active", "Active personality"), ActivePersonality,
                    BuildPersonalityOptions,
                    description: S("settings.personality.active.desc", "The personality used by the AI teammate."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                .AddToggle("personality_growth", S("settings.personality.growth", "Allow personality growth"), EnablePersonalityGrowth,
                    description: S("settings.personality.growth.desc", "Let the AI summarize and remember experiences after combat."))
                .AddToggle("long_term_memory", S("settings.personality.memory", "Enable long-term memory"), EnableLongTermMemory,
                    description: S("settings.personality.memory.desc", "Inject recent high-importance memories into prompts."))
                .AddIntSlider("max_memories", S("settings.personality.memory_count", "Memories per prompt"), MaxMemoriesPerPrompt,
                    0, 20, 1)
                    .WithEntryVisibleWhen("max_memories", () => AiSettingsStore.Current.EnableLongTermMemory)
                .AddIntSlider("memory_merge_threshold", S("settings.personality.memory_threshold", "Memory compaction threshold"), MemoryMergeThreshold,
                    20, 2000, 10,
                    description: S("settings.personality.memory_threshold.desc", "Triggers self-reflection once memory entries reach this count."))
                    .WithEntryVisibleWhen("memory_merge_threshold", () => AiSettingsStore.Current.EnableLongTermMemory)
                .AddMultilineString("backstory", S("settings.personality.backstory", "Backstory"), Backstory)
                .AddMultilineString("philosophy", S("settings.personality.philosophy", "Combat philosophy"), CombatPhilosophy)
                .AddMultilineString("style", S("settings.personality.style", "Communication style"), CommunicationStyle)
                .AddMultilineString("principles", S("settings.personality.principles", "Decision principles"), DecisionPrinciples)
                .AddButton("new_personality", S("settings.personality.create.label", "New personality"),
                    S("settings.personality.create.button", "Create"), CreatePersonality,
                    ModSettingsButtonTone.Accent)
                .AddButton("delete_personality", S("settings.personality.delete.label", "Delete active personality"),
                    S("settings.personality.delete.button", "Delete"), DeletePersonality,
                    ModSettingsButtonTone.Danger,
                    S("settings.personality.delete.desc", "Built-in personalities can't be deleted."))
                .AddButton("restore_personality", S("settings.personality.restore.label", "Restore built-in personality"),
                    S("settings.personality.restore.button", "Restore"), RestorePersonality)
            )
            .AddSection("chat", section => section
                .WithTitle(S("settings.section.chat", "Chat & Suggestions"))
                .AddToggle("ai_chat", S("settings.chat.enabled", "Enable AI chat"), EnableAIChat)
                .AddToggle("speech_bubble", S("settings.chat.bubble", "Show speech bubbles"), EnableSpeechBubble)
                .AddIntSlider("chat_cooldown", S("settings.chat.cooldown", "Auto-chat cooldown (ms)"), ChatCooldownMs,
                    0, 120000, 1000)
                    .WithEntryVisibleWhen("chat_cooldown", () => AiSettingsStore.Current.EnableAIChat)
                .AddIntSlider("chat_history", S("settings.chat.history", "Chat history entries in prompt"), MaxChatHistoryForPrompt,
                    0, 30, 1)
                    .WithEntryVisibleWhen("chat_history", () => AiSettingsStore.Current.EnableAIChat)
                .AddToggle("suggestions", S("settings.suggestions.enabled", "Enable card suggestions"), EnableSuggestions)
                .AddToggle("auto_suggest", S("settings.suggestions.auto", "Auto-suggest at turn start"), AutoSuggestOnTurnStart,
                    description: S("settings.suggestions.auto.desc", "Requests suggestions and highlights cards each turn, consuming extra tokens."))
                    .WithEntryVisibleWhen("auto_suggest", () => AiSettingsStore.Current.EnableSuggestions)
                .AddIntSlider("max_suggestions", S("settings.suggestions.max", "Max suggestions per turn"), MaxSuggestionsPerTurn,
                    1, 10, 1)
                    .WithEntryVisibleWhen("max_suggestions", () => AiSettingsStore.Current.EnableSuggestions)
            )
            .AddSection("live", section => section
                .WithTitle(S("settings.section.live", "Bilibili Live Danmaku"))
                .AddToggle("live_enabled", S("settings.live.enabled", "Show live-room danmaku"), EnableBilibiliDanmaku,
                    description: S("settings.live.enabled.desc",
                        "Connects to the room and scrolls its danmaku across the game screen. Receiving danmaku needs no Bilibili login; sending (and AI replies) does."))
                .AddParagraph("live_status",
                    ModSettingsText.Dynamic(() => AiLoc.T("settings.live.status", "Connection: {Status}",
                        ("Status", LiveDanmakuService.CurrentStatus))))
                    .WithEntryVisibleWhen("live_status", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddParagraph("live_login_status",
                    ModSettingsText.Dynamic(() => AiLoc.T("settings.live.login_status", "Bilibili account: {Status}",
                        ("Status", LiveDanmakuService.CurrentLogin))))
                    .WithEntryVisibleWhen("live_login_status", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddString("live_room", S("settings.live.room", "Live room id or URL"), BilibiliRoomId,
                    placeholder: ModSettingsText.Literal("21452505"),
                    description: S("settings.live.room.desc", "The number after live.bilibili.com/, or paste the whole room URL."))
                    .WithEntryVisibleWhen("live_room", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddButton("live_reconnect", S("settings.live.reconnect.label", "Connection"),
                    S("settings.live.reconnect.button", "Reconnect"), ReconnectLiveDanmaku,
                    ModSettingsButtonTone.Normal,
                    S("settings.live.reconnect.desc",
                        "Reconnects now. Room id and cookie changes are also picked up automatically within a second."))
                    .WithEntryVisibleWhen("live_reconnect", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_max_chars", S("settings.live.max_chars", "Max characters per danmaku"),
                    DanmakuMaxChars, 5, 200, 5,
                    description: S("settings.live.max_chars.desc", "Longer lines are trimmed with an ellipsis before they are drawn."))
                    .WithEntryVisibleWhen("live_max_chars", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddEnumChoice("live_direction", S("settings.live.direction", "Scroll direction"),
                    DanmakuMoveDirection,
                    value => value == DanmakuDirection.LeftToRight
                        ? S("settings.live.direction.ltr", "Left to right")
                        : S("settings.live.direction.rtl", "Right to left"),
                    description: S("settings.live.direction.desc",
                        "Video danmaku usually move right to left. This mod defaults to left to right; change it here if you prefer the classic direction."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                    .WithEntryVisibleWhen("live_direction", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_speed", S("settings.live.speed", "Scroll speed (px/s at 1080p)"),
                    DanmakuSpeed, 40, 800, 10,
                    description: S("settings.live.speed.desc", "Scales with the viewport height."))
                    .WithEntryVisibleWhen("live_speed", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_font", S("settings.live.font", "Font size"), DanmakuFontSize, 10, 48, 1)
                    .WithEntryVisibleWhen("live_font", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_opacity", S("settings.live.opacity", "Text opacity (%)"), DanmakuOpacity, 20, 100, 5)
                    .WithEntryVisibleWhen("live_opacity", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_area", S("settings.live.area", "Vertical area used (%)"), DanmakuAreaPercent, 10, 100, 5,
                    description: S("settings.live.area.desc", "Percentage of the screen height the danmaku lanes may occupy, starting near the top."))
                    .WithEntryVisibleWhen("live_area", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddToggle("live_avatar", S("settings.live.avatar", "Show sender avatars"), DanmakuShowAvatar)
                    .WithEntryVisibleWhen("live_avatar", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddToggle("live_host_only", S("settings.live.host_only", "Only on the host's screen"), DanmakuHostOnly,
                    description: S("settings.live.host_only.desc",
                        "When off, every multiplayer client fetches and shows the same room independently."))
                    .WithEntryVisibleWhen("live_host_only", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddString("live_sessdata", S("settings.live.sessdata", "Bilibili SESSDATA cookie"), BilibiliSessData,
                    placeholder: CookiePlaceholder(s => s.BilibiliSessData),
                    description: S("settings.live.sessdata.desc",
                        "Optional. Needed only to send danmaku and let the AI chat with viewers. The saved value is never shown."))
                    .WithEntryVisibleWhen("live_sessdata", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddString("live_bili_jct", S("settings.live.bili_jct", "Bilibili bili_jct cookie"), BilibiliBiliJct,
                    placeholder: CookiePlaceholder(s => s.BilibiliBiliJct),
                    description: S("settings.live.bili_jct.desc",
                        "Optional. The CSRF token from the same browser session; required together with SESSDATA."))
                    .WithEntryVisibleWhen("live_bili_jct", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddButton("live_verify_login", S("settings.live.verify.label", "Verify Bilibili login"),
                    S("settings.live.verify.button", "Verify"), VerifyLiveLogin,
                    ModSettingsButtonTone.Accent,
                    S("settings.live.verify.desc", "Checks the pasted cookies against Bilibili and shows the account name."))
                    .WithEntryVisibleWhen("live_verify_login", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddButton("live_clear_login", S("settings.live.clear_login.label", "Clear Bilibili login"),
                    S("settings.live.clear_login.button", "Clear"), ClearBilibiliLogin,
                    ModSettingsButtonTone.Danger,
                    S("settings.live.clear_login.desc", "Removes SESSDATA and bili_jct from this PC."))
                    .WithEntryVisibleWhen("live_clear_login", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddToggle("live_ai_chat", S("settings.live.ai_chat", "Let the AI reply to live danmaku"),
                    EnableDanmakuAiChat,
                    description: S("settings.live.ai_chat.desc",
                        "Requires a logged-in Bilibili account. The AI reads recent viewer danmaku and sends one short reply, respecting the cooldown below."))
                    .WithEntryVisibleWhen("live_ai_chat", () => AiSettingsStore.Current.EnableBilibiliDanmaku)
                .AddIntSlider("live_ai_cooldown", S("settings.live.ai_cooldown", "AI reply cooldown (ms)"),
                    DanmakuAiChatCooldownMs, 5000, 300000, 1000)
                    .WithEntryVisibleWhen("live_ai_cooldown",
                        () => AiSettingsStore.Current.EnableBilibiliDanmaku && AiSettingsStore.Current.EnableDanmakuAiChat)
                .AddIntSlider("live_ai_batch", S("settings.live.ai_batch", "Danmaku per AI reply"),
                    DanmakuAiChatBatchSize, 1, 20, 1)
                    .WithEntryVisibleWhen("live_ai_batch",
                        () => AiSettingsStore.Current.EnableBilibiliDanmaku && AiSettingsStore.Current.EnableDanmakuAiChat)
            )
            .AddSection("solo", section => section
                .WithTitle(S("settings.section.solo", "Solo Climb"))
                .AddToggle("solo_mode", S("settings.solo.enabled", "AI climbs alone (singleplayer)"), EnableSoloClimb,
                    description: S("settings.solo.enabled.desc", "When enabled, the AI takes over singleplayer runs; interrupt or pause it from the chat box at any time."))
                .AddDynamicChoice("solo_character", S("settings.solo.character", "AI character"), SoloCharacter,
                    BuildSoloCharacterOptions,
                    description: S("settings.solo.character.desc", "Character used by “Start AI Climb”; in-combat behavior is decided by the personality and LLM."),
                    presentation: ModSettingsChoicePresentation.Dropdown)
                .AddIntSlider("solo_ascension", S("settings.solo.ascension", "Ascension level"), SoloAscension,
                    0, MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager.maxAscensionAllowed, 1,
                    valueFormatter: level => level <= 0
                        ? AiLoc.T("settings.solo.ascension.none", "None")
                        : level.ToString(),
                    description: S("settings.solo.ascension.desc", "Ascension level used by “Start AI Climb”. Higher levels apply the usual ascension rules to the AI's run."))
                .AddButton("start_solo_run", S("settings.solo.start.label", "Start AI solo climb"),
                    S("settings.solo.start.button", "Start new run"), StartSoloRun,
                    ModSettingsButtonTone.Accent,
                    S("settings.solo.start.desc", "Immediately start a singleplayer run controlled by the AI from the main menu."))
                .AddButton("solo_pause", S("settings.solo.pause.label", "Pause / Resume AI"),
                    S("settings.solo.pause.button", "Toggle"), ToggleSoloPause,
                    ModSettingsButtonTone.Normal,
                    S("settings.solo.pause.desc", "Pause or resume the AI's automation now (also available from the chat panel)."))
            )
            .AddSection("maintenance", section => section
                .WithTitle(S("settings.section.maintenance", "Maintenance"))
                .AddToggle("auto_cache", S("settings.maintenance.auto_cache", "Auto-refresh content cache"), AutoRefreshCache,
                    description: S("settings.maintenance.auto_cache.desc", "Regenerate the card, relic, and potion index when the game version changes."))
                .AddButton("regen_cache", S("settings.maintenance.cache.label", "Content cache"),
                    S("settings.maintenance.cache.button", "Regenerate now"), RegenerateCache)
                .AddToggle("decision_log", S("settings.maintenance.decision_log", "Decision log"), EnableDecisionLog,
                    description: S("settings.maintenance.decision_log.desc", "Record AI thoughts and actions to user://ai_teammate/logs."))
                .AddToggle("traffic_log", S("settings.maintenance.traffic_log", "Request/response log"), EnableTrafficLog,
                    description: S("settings.maintenance.traffic_log.desc", "Record full LLM requests and responses; large files, off by default."))
                .AddIntSlider("log_retention", S("settings.maintenance.retention", "Log retention count"), LogRetention, 1, 20, 1)
                .AddButton("open_log_dir", S("settings.maintenance.log_dir", "Log directory"),
                    S("settings.maintenance.log_dir.button", "Open"), OpenLogDirectory)
                .AddButton("reset_defaults", S("settings.maintenance.reset.label", "Reset all settings"),
                    S("settings.maintenance.reset.button", "Reset"), ResetToDefaults,
                    ModSettingsButtonTone.Danger,
                    S("settings.maintenance.reset.desc", "Restore all settings to defaults (including API keys); personality data is kept."))
            ));
    }

    private static bool IsBackend(string name) =>
        string.Equals(AiSettingsStore.Current.ActiveBackend, name, StringComparison.Ordinal);

    private static IReadOnlyList<ModSettingsChoiceOption<string>> BuildPersonalityOptions()
    {
        // Locale is only reliable once LocManager is up; re-sync built-ins when the page is built
        // (covers opening settings from the main menu before the first run).
        PersonalityStore.EnsureSeeded();

        var options = PersonalityStore.All
            .Select(p => new ModSettingsChoiceOption<string>(p.Id,
                p.IsBuiltIn
                    ? ModSettingsText.Dynamic(() => AiLoc.T("settings.personality.builtin_suffix",
                        "{Name} (built-in)", ("Name", p.Name)))
                    : ModSettingsText.Literal(p.Name)))
            .ToList();

        if (options.Count == 0)
            options.Add(new ModSettingsChoiceOption<string>("tactician",
                ModSettingsText.Dynamic(() => AiLoc.T("settings.personality.builtin_default", "Tactician (built-in)"))));

        return options;
    }

    private static IReadOnlyList<ModSettingsChoiceOption<string>> BuildSoloCharacterOptions()
    {
        return SoloRunController.GetSoloCharacterOptions()
            .Select(o => new ModSettingsChoiceOption<string>(o.Id, ModSettingsText.Literal(o.Name)))
            .ToList();
    }

    private static void StartSoloRun(IModSettingsUiActionHost host)
    {
        _ = StartSoloRunAsync();
    }

    private static async Task StartSoloRunAsync()
    {
        var (ok, message) = await SoloRunController.StartNewSoloRunAsync();
        if (ok)
            RitsuToastService.ShowInfo(message);
        else
            RitsuToastService.ShowError(message);
    }

    private static void ToggleSoloPause(IModSettingsUiActionHost host)
    {
        AiFlowControl.ToggleManualPause();
        if (AiFlowControl.IsPaused)
            RitsuToastService.ShowInfo(AiLoc.T("toast.solo_paused", "AI paused. Press again when you're ready to resume."));
        else
            RitsuToastService.ShowInfo(AiLoc.T("toast.solo_resumed", "AI resumed the climb."));
    }

    private static void TestConnection(IModSettingsUiActionHost host)
    {
        var backend = AiSettingsStore.Current.ActiveBackend;
        RitsuToastService.ShowInfo(AiLoc.T("toast.testing_backend", "Testing {Backend}…", ("Backend", backend)));
        _ = Task.Run(async () =>
        {
            var (success, message) = await BackendFactory.TestConnectionAsync(backend, CancellationToken.None);
            MainThread.Post(() =>
            {
                if (success)
                    RitsuToastService.ShowInfo(message);
                else
                    RitsuToastService.ShowError(message);
            });
        });
    }

    /// <summary>Model drop-down options: the fetched list, always including the currently typed model.</summary>
    private static IReadOnlyList<ModSettingsChoiceOption<string>> BuildModelOptions(string backend, string current)
    {
        var models = ModelCatalog.Get(backend).ToList();
        if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current, StringComparer.Ordinal))
            models.Insert(0, current);
        if (models.Count == 0)
            models.Add("");

        return models
            .Select(model => new ModSettingsChoiceOption<string>(model,
                ModSettingsText.Literal(string.IsNullOrEmpty(model)
                    ? AiLoc.T("settings.backend.model_pick.empty", "No models fetched yet")
                    : model)))
            .ToList();
    }

    private static string ActiveModelName()
    {
        var settings = AiSettingsStore.Current;
        return settings.ActiveBackend switch
        {
            "OpenAI" => settings.OpenAiModel,
            "Ollama" => settings.OllamaModel,
            _ => settings.DeepSeekModel
        };
    }

    /// <summary>Heuristic hint (never a hard block): is the active model likely to accept images?</summary>
    private static string DescribeActiveModelVision()
    {
        var model = ActiveModelName();
        if (string.IsNullOrWhiteSpace(model))
            return AiLoc.T("settings.decision.screenshot.model_missing",
                "No model is configured for the active backend.");

        return ModelCatalog.LooksVisionCapable(model)
            ? AiLoc.T("settings.decision.screenshot.model_ok",
                "Active model {Model} looks vision-capable.", ("Model", model))
            : AiLoc.T("settings.decision.screenshot.model_warn",
                "Active model {Model} does not look like a vision model; the screenshot tool will likely fail.",
                ("Model", model));
    }

    private static void FetchModels(IModSettingsUiActionHost host)
    {
        var backend = AiSettingsStore.Current.ActiveBackend;
        RitsuToastService.ShowInfo(AiLoc.T("toast.models_fetching", "Fetching the model list from {Backend}…",
            ("Backend", backend)));

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await ModelCatalog.RefreshAsync(backend, cts.Token);
            MainThread.Post(() =>
            {
                if (result.IsSuccess)
                {
                    RitsuToastService.ShowInfo(AiLoc.T("toast.models_fetched",
                        "{Count} models fetched for {Backend}.", ("Count", result.Models.Count.ToString()),
                        ("Backend", backend)));
                    RequestRefresh(host);
                }
                else
                {
                    RitsuToastService.ShowError(AiLoc.T("toast.models_fetch_failed",
                        "Could not fetch models: {Message}", ("Message", result.ErrorMessage)));
                }
            });
        });
    }

    private static void ReconnectLiveDanmaku(IModSettingsUiActionHost host)
    {
        LiveDanmakuService.RequestReconnect();
        RitsuToastService.ShowInfo(AiLoc.T("toast.live_reconnecting", "Reconnecting to the live room…"));
        RequestRefresh(host);
    }

    private static void VerifyLiveLogin(IModSettingsUiActionHost host)
    {
        RitsuToastService.ShowInfo(AiLoc.T("toast.live_verifying", "Checking Bilibili login…"));
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (success, message) = await LiveDanmakuService.VerifyLoginAsync(cts.Token);
            MainThread.Post(() =>
            {
                if (success)
                    RitsuToastService.ShowInfo(message);
                else
                    RitsuToastService.ShowError(message);
                RequestRefresh(host);
            });
        });
    }

    private static void ClearBilibiliLogin(IModSettingsUiActionHost host)
    {
        AiSettingsStore.Current.BilibiliSessData = "";
        AiSettingsStore.Current.BilibiliBiliJct = "";
        AiSettingsStore.Save();
        LiveDanmakuService.RequestReconnect();
        RequestRefresh(host);
        RitsuToastService.ShowInfo(AiLoc.T("toast.live_login_cleared", "Bilibili login removed."));
    }

    private static void RequestRefresh(IModSettingsUiActionHost host)
    {
        try { host.RequestRefresh(); }
        catch { /* the settings page may already be closed */ }
    }

    /// <summary>Placeholder for a write-only cookie field, mirroring the API-key hint.</summary>
    private static ModSettingsText CookiePlaceholder(Func<AiSettings, string> read) =>
        ModSettingsText.Dynamic(() => string.IsNullOrEmpty(read(AiSettingsStore.Current))
            ? AiLoc.T("settings.live.cookie.empty", "Not set")
            : AiLoc.T("settings.live.cookie.saved", "Saved — type a new value to replace it"));

    /// <summary>
    /// Placeholder for an API key field. The field never displays the saved key, so the placeholder
    /// is the only hint whether one is configured.
    /// </summary>
    private static ModSettingsText ApiKeyPlaceholder(Func<AiSettings, string> readKey) =>
        ModSettingsText.Dynamic(() => string.IsNullOrEmpty(readKey(AiSettingsStore.Current))
            ? AiLoc.T("settings.backend.key.empty", "Not set — paste your API key")
            : AiLoc.T("settings.backend.key.saved", "Saved — type a new key to replace it"));

    private static void ClearDeepSeekApiKey(IModSettingsUiActionHost host) =>
        ClearApiKey(host, DeepSeekApiKey, s => s.DeepSeekApiKey = "");

    private static void ClearOpenAiApiKey(IModSettingsUiActionHost host) =>
        ClearApiKey(host, OpenAiApiKey, s => s.OpenAiApiKey = "");

    private static void ClearApiKey(IModSettingsUiActionHost host,
        ModSettingsValueBinding<AiSettings, string> binding, Action<AiSettings> clear)
    {
        try
        {
            clear(AiSettingsStore.Current);
            host.MarkDirty(binding);
            host.RequestRefresh();
            AiSettingsStore.Save();
            RitsuToastService.ShowInfo(AiLoc.T("toast.api_key_cleared", "API key removed."));
        }
        catch (Exception ex)
        {
            RitsuToastService.ShowError(AiLoc.T("toast.api_key_clear_failed",
                "Could not remove the API key: {Error}", ("Error", ex.Message)));
        }
    }

    private static void CreatePersonality(IModSettingsUiActionHost host)
    {
        var customName = AiLoc.T("settings.personality.custom_name", "Custom Personality {Number}",
            ("Number", (PersonalityStore.All.Count + 1).ToString()));
        var profile = PersonalityStore.Create(customName);
        AiSettingsStore.Save();
        host.RequestRefresh();
        RitsuToastService.ShowInfo(AiLoc.T("toast.personality_created", "Created personality: {Name}",
            ("Name", profile.Name)));
    }

    private static void DeletePersonality(IModSettingsUiActionHost host)
    {
        var active = PersonalityStore.Active;
        if (active == null)
            return;

        if (!PersonalityStore.Delete(active.Id))
        {
            RitsuToastService.ShowWarning(AiLoc.T("toast.builtin_undeletable", "Built-in personalities can't be deleted."));
            return;
        }

        AiSettingsStore.Save();
        host.RequestRefresh();
        RitsuToastService.ShowInfo(AiLoc.T("toast.personality_deleted", "Deleted personality: {Name}",
            ("Name", active.Name)));
    }

    private static void RestorePersonality(IModSettingsUiActionHost host)
    {
        var active = PersonalityStore.Active;
        if (active == null)
            return;

        if (!PersonalityStore.RestoreBuiltIn(active.Id))
        {
            RitsuToastService.ShowWarning(AiLoc.T("toast.builtin_only_restore", "Only built-in personalities can be restored."));
            return;
        }

        host.RequestRefresh();
        RitsuToastService.ShowInfo(AiLoc.T("toast.personality_restored", "Restored built-in personality: {Name}",
            ("Name", active.Name)));
    }

    private static void RegenerateCache(IModSettingsUiActionHost host)
    {
        try
        {
            var cache = AiTeammateManager.Instance.CacheService ?? new ContentCacheService();
            cache.RegenerateCache();
            RitsuToastService.ShowInfo(AiLoc.T("toast.cache_regenerated", "Content cache regenerated."));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Settings] 重新生成缓存失败: {ex.Message}");
            RitsuToastService.ShowError(AiLoc.T("toast.cache_regen_failed", "Failed to regenerate the cache: {Message}",
                ("Message", ex.Message)));
        }
    }

    private static void OpenLogDirectory(IModSettingsUiActionHost host)
    {
        try
        {
            if (!ModPaths.EnsureDir(LogDir))
                return;

            OS.ShellOpen(ProjectSettings.GlobalizePath(LogDir));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Settings] 打开日志目录失败: {ex.Message}");
        }
    }

    private static void ResetToDefaults(IModSettingsUiActionHost host)
    {
        AiSettingsStore.Current.ResetToDefaults();
        AiSettingsStore.Save();
        host.RequestRefresh();
        RitsuToastService.ShowInfo(AiLoc.T("toast.defaults_restored", "Settings restored to defaults."));
    }

    private static ModSettingsText S(string key, string fallback) => AiLoc.Settings(key, fallback);
}
