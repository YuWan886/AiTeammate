using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Teamwork;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Localization;
using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Chat;

public class ChatManager
{
    private readonly BackendManager _backend;
    private readonly PersonalityPromptBuilder _promptBuilder;
    private readonly ContextBuilder _contextBuilder;
    private readonly ConversationContextManager _conversationContext;
    private readonly PlayerDirectiveManager _directiveManager;
    private DateTime _lastAutoMessage = DateTime.MinValue;

    public List<ChatMessage> RecentMessages { get; } = new();
    public event Action<ChatMessage>? OnMessageReceived;

    public ChatManager(BackendManager backend, PersonalityManager personality, ContentQueryService query, ConversationContextManager conversationContext, PlayerDirectiveManager directiveManager)
    {
        _backend = backend;
        _promptBuilder = new PersonalityPromptBuilder(personality);
        _contextBuilder = new ContextBuilder(query);
        _conversationContext = conversationContext;
        _directiveManager = directiveManager;
    }

    public void AddSystemMessage(string content)
    {
        var msg = new ChatMessage
        {
            SenderName = AiLoc.T("chat.system_sender", "System"),
            IsAi = false,
            Content = content,
            Type = ChatMessageType.SystemInfo
        };
        RecentMessages.Add(msg);
        OnMessageReceived?.Invoke(msg);
    }

    public void AddAiMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        var msg = new ChatMessage
        {
            SenderName = _promptBuilder.ActivePersonalityName,
            IsAi = true,
            Content = content.Trim(),
            Type = ChatMessageType.Normal
        };
        RecentMessages.Add(msg);
        OnMessageReceived?.Invoke(msg);
    }

    public int GetSpeechLeadInDelayMs(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        var visibleChars = content.Count(c => !char.IsWhiteSpace(c));
        return Math.Clamp(visibleChars * 90, 450, 1200);
    }

    public void AddAiThought(string thinking)
    {
        if (!string.IsNullOrWhiteSpace(thinking))
        {
            var thinkMsg = new ChatMessage
            {
                SenderName = AiLoc.T("chat.thought_sender", "AI thoughts"),
                IsAi = true,
                Content = thinking,
                Type = ChatMessageType.Suggestion
            };
            RecentMessages.Add(thinkMsg);
            OnMessageReceived?.Invoke(thinkMsg);
        }
    }

    public async Task<ChatMessage> HandlePlayerMessage(string playerName, string content, CancellationToken ct)
    {
        // Solo climb: a message is also an interrupt — hold the automation while we talk.
        if (SoloRunController.IsActive)
            AiFlowControl.BeginConversationPause(TimeSpan.FromSeconds(60));

        var playerMsg = new ChatMessage
        {
            SenderName = playerName,
            IsAi = false,
            Content = content,
            Type = ChatMessageType.Normal
        };
        RecentMessages.Add(playerMsg);
        OnMessageReceived?.Invoke(playerMsg);

        // Check if player is requesting card suggestions
        if (AiSettingsStore.Current.EnableSuggestions && IsSuggestionRequest(content))
        {
            _ = Task.Run(async () =>
            {
                try { await TriggerSuggestionForHumanPlayerAsync(); }
                catch { /* fire-and-forget, errors logged internally */ }
            });
        }

        var aiPlayer = AiTeammateManager.Instance.AiPlayer;
        var runPrelude = aiPlayer != null ? _conversationContext.BuildRunPrelude(aiPlayer, _contextBuilder) : "";
        var combatContext = BuildCombatChatContext(aiPlayer);
        var chatContext = _contextBuilder.BuildChatContext(RecentMessages, recentWindow: 4);
        var directiveContext = _directiveManager.BuildChatDirectiveContext();
        var latestInput = AiLoc.T("prompt.chat.player_said", "Player {Name} says: \"{Text}\"",
            ("Name", playerName), ("Text", content));
        var userPrompt = BuildInteractiveChatPrompt(
            runPrelude,
            combatContext,
            directiveContext,
            chatContext,
            latestInput);

        var parsed = await SendInteractiveChatAsync(userPrompt, latestInput, ct);
        if (parsed.Directives.Count > 0)
        {
            _directiveManager.RecordAcceptedDirectives(playerName, parsed.Directives);
            foreach (var directive in parsed.Directives.Where(d => d.Accepted))
                MainFile.Logger.Info($"[ChatDirective] {playerName} -> {directive.Type}: {directive.Summary}");
        }

        var replyContent = !string.IsNullOrWhiteSpace(parsed.Reply)
            ? parsed.Reply
            : AiLoc.T("chat.reply.ack", "(Got it — I'll act if it makes sense.)");

        var aiMsg = new ChatMessage
        {
            SenderName = _promptBuilder.ActivePersonalityName,
            IsAi = true,
            Content = replyContent,
            Type = ChatMessageType.Normal
        };
        RecentMessages.Add(aiMsg);
        OnMessageReceived?.Invoke(aiMsg);

        // Let the AI answer, then give the player a moment to keep talking before resuming.
        if (SoloRunController.IsActive)
            AiFlowControl.EndConversationPauseSoon(TimeSpan.FromSeconds(3));

        return aiMsg;
    }

    public async Task<ChatMessage?> TryAutoMessage(Player aiPlayer, string triggerEvent, CancellationToken ct)
    {
        var settings = AiSettingsStore.Current;
        if (!settings.EnableAIChat)
            return null;

        // Don't chatter while the human has interrupted the AI.
        if (AiFlowControl.IsPaused)
            return null;

        var probability = GetTriggerProbability(triggerEvent);
        if (Random.Shared.NextDouble() > probability) return null;

        var elapsed = DateTime.UtcNow - _lastAutoMessage;
        if (elapsed.TotalMilliseconds < Math.Max(0, settings.ChatCooldownMs)) return null;

        _lastAutoMessage = DateTime.UtcNow;

        var systemPrompt = _promptBuilder.BuildSystemPrompt(chatMode: true);
        var runPrelude = _conversationContext.BuildRunPrelude(aiPlayer, _contextBuilder);
        var snapshot = _contextBuilder.BuildCombatPromptSnapshot(aiPlayer).RenderEssentials();
        var chatContext = _contextBuilder.BuildChatContext(RecentMessages, recentWindow: 4);
        var autoTrigger = AiLoc.T("prompt.chat.auto_trigger", "Trigger event: {Event}", ("Event", triggerEvent));
        var autoInstruction = AiLoc.T("prompt.chat.auto_instruction",
            "Say one short line in character (under 15 words). Don't repeat recent lines or use filler like \"let me analyze\" or \"the current situation\"; sound like a real player typing.");
        var userPrompt = BuildChatPrompt(runPrelude, $"{snapshot}\n\n{chatContext}", autoTrigger, autoInstruction);

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Chat,
            MaxTokens = 1024,
            Temperature = 0.8f,
            FrequencyPenalty = 0.3f,           // 抑制重复用语
            ConversationHistory = BuildHistory()
        };

        var response = await _backend.SendAsync(request, ct);
        if (!response.IsSuccess) return null;

        var msg = new ChatMessage
        {
            SenderName = _promptBuilder.ActivePersonalityName,
            IsAi = true,
            Content = response.Content.Trim(),
            Type = ChatMessageType.Normal
        };
        RecentMessages.Add(msg);
        OnMessageReceived?.Invoke(msg);
        return msg;
    }

    private static double GetTriggerProbability(string triggerEvent)
    {
        return triggerEvent switch
        {
            "AiTurnStart" => 0.3,
            "AiKeyCard" => 0.8,
            "AiLowHp" => 0.7,
            "PlayerLowHp" => 0.6,
            "PlayerKeyCard" => 0.4,
            "BossStart" => 0.9,
            "CombatWon" => 0.8,
            "EnemyIntent" => 0.5,
            _ => 0.1
        };
    }

    private static string BuildChatPrompt(string prelude, string context, string latestInput, string instruction)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }

        sb.AppendLine(context);
        sb.AppendLine();
        sb.AppendLine(latestInput);
        sb.AppendLine(instruction);
        return sb.ToString();
    }

    private static string BuildInteractiveChatPrompt(string prelude, string combatContext, string directiveContext, string chatContext, string latestInput)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(combatContext))
        {
            sb.AppendLine(AiLoc.T("prompt.chat.combat_state", "[Current combat state]"));
            sb.AppendLine(combatContext);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(directiveContext))
        {
            sb.AppendLine(directiveContext);
            sb.AppendLine();
        }

        sb.AppendLine(chatContext);
        sb.AppendLine();
        sb.AppendLine(latestInput);
        sb.AppendLine(AiLoc.T("prompt.chat.judge",
            "First decide whether the message contains a team directive you should carry out later, then decide whether to accept it."));
        sb.AppendLine(AiLoc.T("prompt.chat.json_prefix", "Return JSON (json output required):"));
        sb.AppendLine("```json");
        sb.AppendLine(AiLoc.T("prompt.chat.json_schema",
            "{\"reply\":\"reply under 40 words\",\"directives\":[{\"type\":\"focus_target|use_potion_on_teammate|protect_teammate|support_teammate|general_tactic\",\"accepted\":true,\"summary\":\"what you understood\",\"target\":\"target enemy or player, empty if none\",\"item_hint\":\"potion name or effect keyword, empty if none\",\"duration_turns\":1,\"reason\":\"why you accepted or rejected\"}]}"));
        sb.AppendLine("```");
        sb.AppendLine(AiLoc.T("prompt.chat.rules_header", "Rules:"));
        sb.AppendLine(AiLoc.T("prompt.chat.rule.1", "1. Return an empty array for directives when there is no explicit action request."));
        sb.AppendLine(AiLoc.T("prompt.chat.rule.2", "2. For \"use a potion on me / heal me / buff me\", prefer use_potion_on_teammate or support_teammate."));
        sb.AppendLine(AiLoc.T("prompt.chat.rule.3", "3. For \"focus that enemy next turn / hit that target first\", use focus_target and name the target."));
        sb.AppendLine(AiLoc.T("prompt.chat.rule.4", "4. Set accepted=true only when you're willing to try; set accepted=false if you lack resources, it isn't worth it, or it's too dangerous."));
        sb.AppendLine(AiLoc.T("prompt.chat.rule.5", "5. The reply must read like a real teammate typing, not a scripted explanation."));
        if (AiSettingsStore.Current.EnableScreenshotTool)
        {
            sb.AppendLine(AiLoc.T("prompt.chat.screenshot_tool",
                "If you genuinely need to see the current screen before answering, return only {\"tool_calls\":[{\"name\":\"screenshot\"}]}; the system will attach a captured frame to your next message, then you must return the final chat JSON."));
        }
        return sb.ToString();
    }

    private List<AiMessage> BuildHistory()
    {
        var history = new List<AiMessage>();
        var take = Math.Clamp(AiSettingsStore.Current.MaxChatHistoryForPrompt, 0, 30);
        if (take == 0)
            return history;

        foreach (var msg in RecentMessages.TakeLast(take))
        {
            history.Add(new AiMessage
            {
                Role = msg.IsAi ? "assistant" : "user",
                Content = PromptTextHelper.Truncate(PromptTextHelper.CleanText(msg.Content), 80)
            });
        }
        return history;
    }

    private async Task<InteractiveChatResult> SendInteractiveChatAsync(string userPrompt, string latestInput, CancellationToken ct)
    {
        var request = new AiRequest
        {
            SystemPrompt = _promptBuilder.BuildSystemPrompt(requireJson: true, chatMode: true),
            UserPrompt = userPrompt,
            Type = AiRequestType.Chat,
            MaxTokens = 1024,
            Temperature = 0.7f,
            EnableJsonMode = true,
            FrequencyPenalty = 0.3f,
            PresencePenalty = 0.2f,
            ConversationHistory = BuildHistory()
        };

        using var timeoutCts = new CancellationTokenSource(
            Math.Clamp(AiSettingsStore.Current.ChatTimeoutMs, 2000, 60000));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var response = await _backend.SendAsync(request, linkedCts.Token);
        if (!response.IsSuccess)
            return new InteractiveChatResult { Reply = AiLoc.T("chat.reply.thinking", "(Thinking...)") };

        response = await TryRunChatScreenshotAsync(response, userPrompt, ct);

        var parsed = TryParseInteractiveChatResponse(response.Content);
        if (parsed != null)
            return parsed;

        var fallbackPrompt = BuildChatPrompt("", _contextBuilder.BuildChatContext(RecentMessages, recentWindow: 4), latestInput,
            AiLoc.T("prompt.chat.fallback_instruction", "Reply briefly in your character's style (under 40 words)."));
        var fallbackRequest = new AiRequest
        {
            SystemPrompt = _promptBuilder.BuildSystemPrompt(chatMode: true),
            UserPrompt = fallbackPrompt,
            Type = AiRequestType.Chat,
            MaxTokens = 256,
            Temperature = 0.8f,
            FrequencyPenalty = 0.3f,
            PresencePenalty = 0.2f,
            ConversationHistory = BuildHistory()
        };
        var fallbackResponse = await _backend.SendAsync(fallbackRequest, ct);
        return new InteractiveChatResult
        {
            Reply = fallbackResponse.IsSuccess ? fallbackResponse.Content.Trim() : AiLoc.T("chat.reply.received", "(Got it.)")
        };
    }

    /// <summary>
    /// Chat variant of the vision tool: when the model asks for a screenshot instead of answering,
    /// capture the frame and re-ask with the image attached. One extra round only.
    /// </summary>
    private async Task<AiResponse> TryRunChatScreenshotAsync(
        AiResponse first, string userPrompt, CancellationToken ct)
    {
        if (!AiSettingsStore.Current.EnableScreenshotTool)
            return first;

        var calls = ResponseParser.ExtractToolCalls(first.Content);
        if (calls.Count == 0 || !calls.Any(call => call.Name.Trim().ToLowerInvariant()
                is "screenshot" or "capture_screen" or "see_screen"))
            return first;

        // A response that already contains a usable reply wins over the tool call.
        var early = TryParseInteractiveChatResponse(first.Content);
        if (early != null && (!string.IsNullOrWhiteSpace(early.Reply) || early.Directives.Count > 0))
            return first;

        ScreenshotTool.Result shot;
        try
        {
            shot = Compat.MainThread.Invoke(ScreenshotTool.Capture);
        }
        catch (Exception ex)
        {
            shot = new ScreenshotTool.Result { IsSuccess = false, Error = ex.Message };
        }

        var toolText = shot.IsSuccess
            ? AiLoc.T("prompt.chat.screenshot_ok", "Captured the current screen ({Width}×{Height}); the image is attached to this message.",
                ("Width", shot.Width.ToString()), ("Height", shot.Height.ToString()))
            : AiLoc.T("prompt.chat.screenshot_failed", "Screenshot failed: {Message}", ("Message", shot.Error));

        var history = BuildHistory();
        history.Add(new AiMessage { Role = "user", Content = userPrompt });
        history.Add(new AiMessage { Role = "assistant", Content = first.Content });

        var request = new AiRequest
        {
            SystemPrompt = _promptBuilder.BuildSystemPrompt(requireJson: true, chatMode: true),
            UserPrompt = AiLoc.T("prompt.chat.screenshot_followup",
                "Tool result: {Result}\n\nReturn the final chat JSON (reply / directives) now. Do not call another tool.",
                ("Result", toolText)),
            Type = AiRequestType.Chat,
            MaxTokens = 1024,
            Temperature = 0.7f,
            EnableJsonMode = true,
            FrequencyPenalty = 0.3f,
            PresencePenalty = 0.2f,
            ConversationHistory = history,
            UserImageDataUrl = shot.IsSuccess ? shot.DataUrl : null
        };

        using var timeoutCts = new CancellationTokenSource(
            Math.Clamp(AiSettingsStore.Current.ChatTimeoutMs, 2000, 30000));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var followUp = await _backend.SendAsync(request, linkedCts.Token);
        return followUp.IsSuccess ? followUp : first;
    }

    private InteractiveChatResult? TryParseInteractiveChatResponse(string rawContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(ResponseParser.ExtractJson(rawContent));
            var root = doc.RootElement;
            var result = new InteractiveChatResult
            {
                Reply = GetStringProperty(root, "reply")
            };

            if (root.TryGetProperty("directives", out var directives) && directives.ValueKind == JsonValueKind.Array)
            {
                foreach (var directive in directives.EnumerateArray())
                {
                    if (!TryParseDirectiveProposal(directive, out var proposal))
                        continue;
                    result.Directives.Add(proposal);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseDirectiveProposal(JsonElement element, out PlayerDirectiveProposal proposal)
    {
        proposal = default!;
        var typeText = GetStringProperty(element, "type");
        if (!TryMapDirectiveType(typeText, out var directiveType))
            return false;

        var summary = GetStringProperty(element, "summary");
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        proposal = new PlayerDirectiveProposal
        {
            Type = directiveType,
            Accepted = element.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True,
            Summary = summary,
            Target = GetStringProperty(element, "target"),
            ItemHint = GetStringProperty(element, "item_hint"),
            Reason = GetStringProperty(element, "reason"),
            DurationTurns = Math.Clamp(GetIntProperty(element, "duration_turns", 1), 1, 3)
        };
        return true;
    }

    private static bool TryMapDirectiveType(string raw, out PlayerDirectiveType directiveType)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        directiveType = normalized switch
        {
            "focus_target" => PlayerDirectiveType.FocusTarget,
            "use_potion_on_teammate" => PlayerDirectiveType.UsePotionOnTeammate,
            "protect_teammate" => PlayerDirectiveType.ProtectTeammate,
            "support_teammate" => PlayerDirectiveType.SupportTeammate,
            "general_tactic" => PlayerDirectiveType.GeneralTactic,
            _ => default
        };
        return normalized is "focus_target"
            or "use_potion_on_teammate"
            or "protect_teammate"
            or "support_teammate"
            or "general_tactic";
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

    private static int GetIntProperty(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out var prop))
            return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number))
            return number;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out number))
            return number;
        return fallback;
    }

    private string BuildCombatChatContext(Player? aiPlayer)
    {
        if (aiPlayer == null)
            return "";

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || state.CurrentSide != CombatSide.Player)
            return "";

        try
        {
            return _contextBuilder.BuildCombatEssentials(aiPlayer);
        }
        catch
        {
            return "";
        }
    }

    private sealed class InteractiveChatResult
    {
        public string Reply { get; init; } = "";
        public List<PlayerDirectiveProposal> Directives { get; } = new();
    }

    /// <summary>检测玩家消息是否在请求卡牌建议。</summary>
    private static bool IsSuggestionRequest(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var lower = content.ToLowerInvariant();
        // 中文关键词
        string[] cnKeywords = { "建议", "推荐", "怎么打", "怎么出", "选哪张", "出什么", "高亮", "推荐卡牌", "帮我看看" };
        foreach (var kw in cnKeywords)
            if (content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        // 英文关键词
        string[] enKeywords = { "suggest", "recommend", "highlight", "which card", "what card" };
        foreach (var kw in enKeywords)
            if (lower.Contains(kw))
                return true;
        return false;
    }

    /// <summary>异步触发对非AI玩家的卡牌建议。</summary>
    private static async Task TriggerSuggestionForHumanPlayerAsync()
    {
        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized || manager.SuggestionService == null) return;

            var state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null) return;

            var humanPlayer = state.Players.FirstOrDefault(p => p != manager.AiPlayer);
            if (humanPlayer == null) return;

            var suggestions = await manager.SuggestionService.AnalyzeAsync(humanPlayer, System.Threading.CancellationToken.None);
            if (suggestions.Count > 0)
            {
                Compat.MainThread.Post(() => manager.CardHighlighter?.ApplyHighlights(suggestions, humanPlayer));
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Suggestion request error: {ex.Message}");
        }
    }
}
