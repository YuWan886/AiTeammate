using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Config;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Suggestion;
using AiTeammate.AiTeammateCode.Teamwork;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode;

public class AiTeammateManager
{
    private const int MaxCombatDecisionPassesPerTurn = 3;
    private static readonly Lazy<AiTeammateManager> _instance = new(() => new());
    public static AiTeammateManager Instance => _instance.Value;

    public ConfigManager ConfigManager { get; private set; } = null!;
    public BackendManager BackendManager { get; private set; } = null!;
    public PersonalityManager PersonalityManager { get; private set; } = null!;
    public ContentCacheService CacheService { get; private set; } = null!;
    public ContentQueryService QueryService { get; private set; } = null!;
    public DecisionService DecisionService { get; private set; } = null!;
    public ChatManager ChatManager { get; private set; } = null!;
    public SuggestionService SuggestionService { get; private set; } = null!;
    public ConversationContextManager ConversationContext { get; private set; } = null!;
    public PlayerDirectiveManager DirectiveManager { get; private set; } = null!;
    public CombatDecisionExecutor CombatExecutor { get; private set; } = null!;
    public OutOfCombatExecutor OutOfCombatExecutor { get; private set; } = null!;
    public CardHighlighter CardHighlighter { get; private set; } = null!;
    public DecisionValidator Validator { get; private set; } = null!;

    public AiLogger AiLog { get; private set; } = null!;
    public Player? AiPlayer { get; private set; }
    public bool IsInitialized { get; private set; }
    public bool IsAiTurn { get; private set; }

    private readonly Lock _turnLock = new();
    private readonly Lock _restSiteUpgradeLock = new();
    private CancellationTokenSource? _turnCts;
    private List<int>? _pendingRestSiteUpgradeIndices;
    private bool _hasTriggeredExtraPlayerTurnCombatDecision;
    private bool _pendingExtraPlayerTurnCombatDecision;

    private AiTeammateManager() { }

    public void Initialize()
    {
        ConfigManager = new ConfigManager();
        var config = ConfigManager.Load();

        // Backend
        BackendManager = new BackendManager();
        foreach (var (name, beConfig) in config.Backends)
        {
            IAiBackend? backend = name switch
            {
                "DeepSeek" => new DeepSeekBackend(beConfig.ApiKey, beConfig.Model, beConfig.BaseUrl, config.EnableModelThinking),
                "OpenAI" => new OpenAiBackend(beConfig.ApiKey, beConfig.Model, beConfig.BaseUrl, config.EnableModelThinking),
                "Ollama" => new OllamaBackend(beConfig.ApiKey, beConfig.Model, beConfig.BaseUrl, config.EnableModelThinking),
                _ => null
            };
            if (backend != null)
                BackendManager.Register(backend);
        }
        BackendManager.SwitchTo(config.ActiveBackend);

        // Cache
        CacheService = new ContentCacheService();
        CacheService.Initialize();
        QueryService = new ContentQueryService(CacheService);

        // Personality
        PersonalityManager = new PersonalityManager();
        PersonalityManager.Load(config.ActivePersonalityId);

        // Logger
        AiLog = new AiLogger();
        AiLog.Initialize();

        ConversationContext = new ConversationContextManager();
        DirectiveManager = new PlayerDirectiveManager();

        // Services
        DecisionService = new DecisionService(BackendManager, PersonalityManager, QueryService, ConversationContext, config.DecisionTimeoutMs, config.NonCombatRetryCount);
        ChatManager = new ChatManager(BackendManager, PersonalityManager, QueryService, ConversationContext, DirectiveManager, config.ChatCooldownMs);
        SuggestionService = new SuggestionService(BackendManager, PersonalityManager, QueryService, ConversationContext);
        CombatExecutor = new CombatDecisionExecutor(AiLog);
        OutOfCombatExecutor = new OutOfCombatExecutor(DecisionService);
        CardHighlighter = new CardHighlighter();
        Validator = new DecisionValidator();

        IsInitialized = true;
        MainFile.Logger.Info("AI 队友管理器初始化完成");
        AiLog.Log("SYSTEM", "AI 队友管理器初始化完成");
    }

    public void OnAiPlayerJoined(Player aiPlayer)
    {
        AiPlayer = aiPlayer;
        ConversationContext.ResetAll();
        DirectiveManager.ResetAll();
        ResetExtraPlayerTurnCombatDecisionState();
        ChatManager.AddSystemMessage($"{PersonalityManager.Active?.Name ?? "AI 队友"} 加入了队伍");
        Patches.NGamePatch.RefreshChatUi();
        MainFile.Logger.Info($"AI 玩家已加入: {aiPlayer.Character?.Id.Entry}");
        AiLog.Log("SYSTEM", $"AI 玩家加入: {aiPlayer.Character?.Id.Entry} (NetId={aiPlayer.NetId})");
    }

    public void ResetExtraPlayerTurnCombatDecisionState()
    {
        lock (_turnLock)
        {
            _hasTriggeredExtraPlayerTurnCombatDecision = false;
            _pendingExtraPlayerTurnCombatDecision = false;
        }
    }

    public bool HasExtraPlayerTurnCombatDecisionOpportunity()
    {
        var aiPlayer = AiPlayer;
        var combat = aiPlayer?.PlayerCombatState;
        if (!IsInitialized || aiPlayer?.Creature == null || combat == null)
            return false;

        if (!aiPlayer.Creature.IsAlive || combat.Energy <= 0 || combat.Hand.Cards.Count == 0)
            return false;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (CombatManager.Instance.IsOverOrEnding || state?.CurrentSide != CombatSide.Player)
            return false;

        return combat.Hand.Cards.Any(card =>
            !card.Keywords.Contains(CardKeyword.Unplayable)
            && combat.HasEnoughResourcesFor(card, out _));
    }

    public bool TryPrepareExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            if (_hasTriggeredExtraPlayerTurnCombatDecision || _pendingExtraPlayerTurnCombatDecision)
                return false;

            _hasTriggeredExtraPlayerTurnCombatDecision = true;
            _pendingExtraPlayerTurnCombatDecision = true;
            return true;
        }
    }

    public void CancelPendingExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            _hasTriggeredExtraPlayerTurnCombatDecision = false;
            _pendingExtraPlayerTurnCombatDecision = false;
        }
    }

    public bool ConsumePendingExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            if (!_pendingExtraPlayerTurnCombatDecision)
                return false;

            _pendingExtraPlayerTurnCombatDecision = false;
            return true;
        }
    }

    /// <summary>
    /// Add AI-controlled LobbyPlayer to the multiplayer lobby's Players list.
    /// </summary>
    public LobbyPlayer? AddAiToLobby(StartRunLobby lobby, CharacterModel? character = null)
    {
        if (!IsInitialized) return null;

        // Reset AI player for a new run (old reference is stale)
        AiPlayer = null;

        if (lobby.Players.Count >= lobby.MaxPlayers)
        {
            MainFile.Logger.Warn($"Lobby is full ({lobby.Players.Count}/{lobby.MaxPlayers})");
            return null;
        }

        int slot = -1;
        for (int i = 0; i < lobby.MaxPlayers; i++)
        {
            if (!lobby.Players.Any(p => p.slotId == i))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return null;

        ulong aiNetId = 0xA1AA_A000 + (ulong)slot;

        var chars = GetAiCharacterOptions();
        if (chars.Count == 0) return null;
        character ??= chars[0];

        var hostPlayer = lobby.Players.FirstOrDefault();
        var aiLobbyPlayer = new LobbyPlayer
        {
            id = aiNetId,
            slotId = slot,
            character = character,
            unlockState = hostPlayer.unlockState,
            maxMultiplayerAscensionUnlocked = hostPlayer.maxMultiplayerAscensionUnlocked,
            isReady = false
        };

        lobby.Players.Add(aiLobbyPlayer);
        MainFile.Logger.Info($"AI added to lobby: slot {slot}, char {character.Id.Entry}");
        return aiLobbyPlayer;
    }

    public void RemoveAiFromLobby(StartRunLobby lobby)
    {
        var aiPlayer = lobby.Players.FirstOrDefault(p =>
            p.id >= 0xA1AA_A000 && p.id <= 0xA1AA_FFFF);
        if (aiPlayer.id != 0)
        {
            lobby.Players.Remove(aiPlayer);
            MainFile.Logger.Info("AI removed from lobby");
        }
    }

    public void SetAiCharacter(StartRunLobby lobby, CharacterModel character)
    {
        for (int i = 0; i < lobby.Players.Count; i++)
        {
            if (lobby.Players[i].id >= 0xA1AA_A000 && lobby.Players[i].id <= 0xA1AA_FFFF)
            {
                var p = lobby.Players[i];
                p.character = character;
                lobby.Players[i] = p;
                return;
            }
        }
    }

    public void SetAiReady(StartRunLobby lobby, bool ready)
    {
        for (int i = 0; i < lobby.Players.Count; i++)
        {
            if (lobby.Players[i].id >= 0xA1AA_A000 && lobby.Players[i].id <= 0xA1AA_FFFF)
            {
                var p = lobby.Players[i];
                p.isReady = ready;
                lobby.Players[i] = p;
                return;
            }
        }
    }

    public bool IsAiInLobby(StartRunLobby lobby)
    {
        return lobby.Players.Any(p => p.id >= 0xA1AA_A000 && p.id <= 0xA1AA_FFFF);
    }

    public LobbyPlayer? GetAiLobbyPlayer(StartRunLobby lobby)
    {
        var p = lobby.Players.FirstOrDefault(p => p.id >= 0xA1AA_A000 && p.id <= 0xA1AA_FFFF);
        return p.id != 0 ? p : null;
    }

    public List<CharacterModel> GetAiCharacterOptions()
    {
        return ModelDb.AllCharacters
            .Where(c => c is not RandomCharacter && !c.Id.Entry.Contains("Random", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Detect the AI player in a combat state after the run has started.
    /// </summary>
    public void DetectAndBindAiPlayer(CombatState state)
    {
        if (AiPlayer != null) return;

        foreach (var player in state.Players)
        {
            if (player.NetId >= 0xA1AA_A000 && player.NetId <= 0xA1AA_FFFF)
            {
                OnAiPlayerJoined(player);
                return;
            }
        }
    }

    /// <summary>
    /// Detect the AI player from RunState (for non-combat scenarios like Neow event).
    /// </summary>
    public void DetectAndBindAiPlayer()
    {
        if (AiPlayer != null) return;
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null) return;
        foreach (var player in state.Players)
        {
            if (player.NetId >= 0xA1AA_A000 && player.NetId <= 0xA1AA_FFFF)
            {
                OnAiPlayerJoined(player);
                return;
            }
        }
    }

    public async Task ProcessCombatTurnAsync()
    {
        if (AiPlayer == null || !IsInitialized) return;

        // Don't process turns if combat is already over or ending.
        // TurnStarted can fire during reward processing when combat
        // state hasn't been fully cleaned up yet.
        if (CombatManager.Instance.IsOverOrEnding) return;

        // Cancel any in-flight turn processing from previous invocations
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var ct = _turnCts.Token;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null) return;

        // === CRITICAL: AI death detection ===
        // If the AI died during the enemy turn, immediately end turn
        // so we don't deadlock the game waiting for a dead player.
        if (!AiPlayer.Creature.IsAlive)
        {
            AiLog.Log("SYSTEM", "AI已阵亡，跳过回合处理");
            try
            {
                CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
                if (state.CurrentSide == CombatSide.Player && CombatManager.Instance.IsInProgress)
                    CombatManager.Instance.SetReadyToBeginEnemyTurn(AiPlayer);
            }
            catch { }
            return;
        }

        // Additional guard: don't process if the AI has neither cards nor potions.
        // Potion-only turns are still meaningful, especially when a human explicitly
        // asked the AI to buff or heal them.
        var combat = AiPlayer.PlayerCombatState;
        var hasPotions = AiPlayer.Potions?.Any() == true;
        if (combat == null || (combat.Hand.Cards.Count == 0 && !hasPotions))
        {
            // Try to end turn immediately so we don't stall.
            // Must call both SetReadyToEndTurn AND SetReadyToBeginEnemyTurn,
            // otherwise the game hangs when the human player is dead and can't signal.
            try
            {
                CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
                var curState = CombatManager.Instance.DebugOnlyGetState();
                if (curState?.CurrentSide == CombatSide.Player && CombatManager.Instance.IsInProgress)
                    CombatManager.Instance.SetReadyToBeginEnemyTurn(AiPlayer);
            }
            catch { }
            return;
        }

        bool isAiTurn;
        lock (_turnLock)
        {
            isAiTurn = state.Players.Contains(AiPlayer) && state.CurrentSide == CombatSide.Player;
        }

        if (!isAiTurn) return;

        // Track whether we should force-end the turn in the finally block.
        // Set to true once we've confirmed this is the AI's turn to process.
        bool turnInProgress = true;
        bool shouldAdvanceDirectives = true;
        try
        {
            ct.ThrowIfCancellationRequested();

            // === Hard safety timeout: 45s for entire turn processing ===
            // This is a last-resort safety net. The LLM call has its own 15s timeout,
            // card execution should take <5s. 45s gives ample headroom but prevents
            // permanent deadlocks from network or game-state hangs.
            using var hardTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, hardTimeoutCts.Token);
            var effectiveCt = linkedCts.Token;

            for (var decisionPass = 1; decisionPass <= MaxCombatDecisionPassesPerTurn; decisionPass++)
            {
                effectiveCt.ThrowIfCancellationRequested();

                if (decisionPass > 1)
                {
                    if (!HasExtraPlayerTurnCombatDecisionOpportunity())
                        break;

                    AiLog.Log("COMBAT", $"检测到AI仍有能量和可打牌，继续发起第{decisionPass}次战斗决策");
                }

                // Run AI backend decision on background thread (HTTP call with timeout)
                var result = await Task.Run(async () =>
                    await DecisionService.DecideCombatAsync(AiPlayer, effectiveCt), effectiveCt);

                // Log the AI's decision
                if (result.IsFallback)
                {
                    AiLog.LogDecisionFailure("AI 后端返回无效响应或超时");
                }
                AiLog.LogDecision(result.Thinking, result.Actions);
                if (result.DeferredActions.Count > 0)
                    AiLog.LogDecision("", result.DeferredActions);

                // Push AI thinking to chat panel
                ChatManager.AddAiThought(result.Thinking);

                if (!string.IsNullOrWhiteSpace(result.ChatMessage))
                {
                    AiLog.LogChat(PersonalityManager.Active?.Name ?? "AI", result.ChatMessage);
                    ChatManager.AddAiMessage(result.ChatMessage);

                    // Let the speech bubble render before card autoplay starts,
                    // otherwise the visual effect lands after the first card play.
                    await Task.Delay(ChatManager.GetSpeechLeadInDelayMs(result.ChatMessage), effectiveCt);
                }
                else if (decisionPass == 1)
                {
                    var autoMessage = await ChatManager.TryAutoMessage(AiPlayer, "AiTurnStart", effectiveCt);
                    if (autoMessage != null)
                    {
                        await Task.Delay(ChatManager.GetSpeechLeadInDelayMs(autoMessage.Content), effectiveCt);
                    }
                }

                effectiveCt.ThrowIfCancellationRequested();

                // Back on main thread — validate and execute card actions
                var validActions = result.IsFallback
                    ? Validator.ApplyFallback(AiPlayer)
                    : Validator.Validate(result.Actions, AiPlayer);
                var validDeferred = result.IsFallback
                    ? new List<DecisionAction>()
                    : Validator.ValidateDeferred(result.DeferredActions, AiPlayer);
                await CombatExecutor.Execute(validActions, validDeferred, AiPlayer, effectiveCt, endTurn: false);
            }

            if (HasExtraPlayerTurnCombatDecisionOpportunity())
            {
                AiLog.Log("COMBAT", $"达到单回合最多 {MaxCombatDecisionPassesPerTurn} 次战斗决策上限，结束本次AI行动");
            }

            if (CombatManager.Instance.IsInProgress
                && CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player)
            {
                CombatExecutor.TryEndTurn(AiPlayer);
            }

            // Turn completed normally — finally block doesn't need to force-end.
            turnInProgress = false;
        }
        catch (OperationCanceledException)
        {
            AiLog.Log("SYSTEM", "回合处理被新回合中断");
            turnInProgress = false; // New turn started, don't force-end in finally
            shouldAdvanceDirectives = false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"AI turn error: {ex.Message}");
            AiLog.LogError("回合处理", ex.Message);
        }
        finally
        {
            if (shouldAdvanceDirectives)
            {
                try
                {
                    DirectiveManager.AdvanceAfterAiTurn();
                }
                catch (Exception directiveEx)
                {
                    MainFile.Logger.Warn($"Directive advance error: {directiveEx.Message}");
                }
            }

            // Safety net: if combat is still active and it's still the player's turn,
            // force-end the AI's turn so we never deadlock the game.
            // This handles cases where card execution throws mid-turn (e.g., mod conflicts)
            // or when the human player has died and the game is waiting for AI to signal.
            if (turnInProgress)
            {
                try
                {
                    // Use IsInProgress instead of !IsOverOrEnding: when all enemies are dead,
                    // IsEnding is true but IsInProgress is still true until EndCombatInternal.
                    // We must still signal end-turn so AllPlayersReadyToEndTurn can trigger
                    // the final CheckWinCondition → EndCombatInternal transition.
                    if (CombatManager.Instance.IsInProgress)
                    {
                        var curState = CombatManager.Instance.DebugOnlyGetState();
                        if (curState?.CurrentSide == CombatSide.Player
                            && curState.Players.Contains(AiPlayer))
                        {
                            AiLog.Log("SYSTEM", "安全网：强制结束AI回合");
                            CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
                            CombatManager.Instance.SetReadyToBeginEnemyTurn(AiPlayer);
                        }
                    }
                }
                catch (Exception finalEx)
                {
                    MainFile.Logger.Warn($"AI force-end-turn error: {finalEx.Message}");
                }
            }
        }
    }

    public async Task OnPlayerTurnStarted(Player player)
    {
        if (!IsInitialized || SuggestionService == null) return;

        try
        {
            var suggestions = await SuggestionService.AnalyzeAsync(player, CancellationToken.None);
            if (suggestions.Count > 0)
            {
                CardHighlighter.ApplyHighlights(suggestions, player);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Suggestion error: {ex.Message}");
        }
    }

    public void SetPendingRestSiteUpgradeIndices(List<int>? indices)
    {
        lock (_restSiteUpgradeLock)
        {
            _pendingRestSiteUpgradeIndices = indices is { Count: > 0 } ? new List<int>(indices) : null;
        }
    }

    public List<int>? ConsumePendingRestSiteUpgradeIndices()
    {
        lock (_restSiteUpgradeLock)
        {
            var result = _pendingRestSiteUpgradeIndices;
            _pendingRestSiteUpgradeIndices = null;
            return result is { Count: > 0 } ? new List<int>(result) : null;
        }
    }

    public void OnPlayerHpChanged(Player player)
    {
        if (player?.Creature == null || AiPlayer == null) return;
        if (player.Creature.CurrentHp < player.Creature.MaxHp * 0.3f)
        {
            _ = ChatManager.TryAutoMessage(AiPlayer, "PlayerLowHp", CancellationToken.None);
        }
    }

    public async Task GenerateMemory(MemoryCategory category, string context, CancellationToken ct)
    {
        if (PersonalityManager.Active == null) return;

        var promptBuilder = new PersonalityPromptBuilder(PersonalityManager);
        var systemPrompt = promptBuilder.BuildSystemPrompt(requireJson: true);

        var userPrompt = $"基于以下事件，判断是否值得记住。如果是，生成一条记忆。\n\n事件类型：{category}\n触发上下文：{context}\n\n请以 JSON 格式返回（json output required）：{{\"should_remember\": true/false, \"content\": \"记忆内容（中文，50字以内）\", \"importance\": 1-10, \"trigger_context\": \"什么情况下回忆这条\"}}";

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Memory,
            MaxTokens = 2048,
            Temperature = 0.5f,
            EnableJsonMode = true
        };

        var response = await BackendManager.SendAsync(request, ct);
        if (!response.IsSuccess) return;

        try
        {
            using var doc = JsonDocument.Parse(response.Content);
            var root = doc.RootElement;
            if (root.TryGetProperty("should_remember", out var shouldRemember) && shouldRemember.GetBoolean())
            {
                var memoryManager = new MemoryManager();
                memoryManager.AddEntry(PersonalityManager.Active.Id, new MemoryEntry
                {
                    Category = category,
                    Importance = root.TryGetProperty("importance", out var imp) ? imp.GetInt32() : 5,
                    Content = root.TryGetProperty("content", out var cnt) ? cnt.GetString() ?? "" : "",
                    TriggerContext = root.TryGetProperty("trigger_context", out var tc) ? tc.GetString() ?? "" : "",
                    Timestamp = DateTime.UtcNow
                });
            }
        }
        catch { /* parsing failure - skip this memory */ }
    }

    public async Task OnCombatWon()
    {
        if (AiPlayer == null || PersonalityManager.Active == null) return;
        await GenerateMemory(MemoryCategory.CombatLesson, "战斗胜利", CancellationToken.None);
        CheckAndTriggerSelfReflection(PersonalityManager.Active.Id);
    }

    public void CheckAndTriggerSelfReflection(string profileId)
    {
        var memoryManager = new MemoryManager();
        var memory = memoryManager.Load(profileId);
        var threshold = ConfigManager.Load().MemoryMergeThreshold;

        if (memory.Entries.Count < threshold) return;

        _ = TriggerSelfReflectionAsync(profileId);
    }

    private async Task TriggerSelfReflectionAsync(string profileId)
    {
        var promptBuilder = new PersonalityPromptBuilder(PersonalityManager);
        var systemPrompt = promptBuilder.BuildSystemPrompt(requireJson: true);
        var memoryManager = new MemoryManager();
        var memory = memoryManager.Load(profileId);

        var allMemories = string.Join("\n", memory.Entries.Select(m => $"- {m.Content}"));
        if (string.IsNullOrWhiteSpace(allMemories)) return;

        var userPrompt = $"你的长期记忆已积累 {memory.Entries.Count} 条。请总结这些记忆，提炼出关于你自己的2-3句话自我认知：\n\n记忆列表：\n{allMemories}\n\n返回JSON：{{\"self_reflection\": \"你的自我总结（中文，2-3句话）\"}}";

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.SelfReflection,
            MaxTokens = 2048,
            Temperature = 0.9f,
            EnableJsonMode = true
        };

        var response = await BackendManager.SendAsync(request, CancellationToken.None);
        if (response.IsSuccess)
        {
            try
            {
                using var doc = JsonDocument.Parse(response.Content);
                var root = doc.RootElement;
                var reflection = root.TryGetProperty("self_reflection", out var sr) ? sr.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(reflection))
                    memoryManager.MergeMemories(profileId, reflection);
            }
            catch { }
        }
    }
}
