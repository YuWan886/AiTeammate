using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Teamwork;
using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Chat;

public class ChatManager
{
    private readonly BackendManager _backend;
    private readonly PersonalityPromptBuilder _promptBuilder;
    private readonly ContextBuilder _contextBuilder;
    private readonly ConversationContextManager _conversationContext;
    private readonly PlayerDirectiveManager _directiveManager;
    private readonly int _cooldownMs;
    private DateTime _lastAutoMessage = DateTime.MinValue;

    public List<ChatMessage> RecentMessages { get; } = new();
    public event Action<ChatMessage>? OnMessageReceived;

    public ChatManager(BackendManager backend, PersonalityManager personality, ContentQueryService query, ConversationContextManager conversationContext, PlayerDirectiveManager directiveManager, int cooldownMs = 15000)
    {
        _backend = backend;
        _promptBuilder = new PersonalityPromptBuilder(personality);
        _contextBuilder = new ContextBuilder(query);
        _conversationContext = conversationContext;
        _directiveManager = directiveManager;
        _cooldownMs = cooldownMs;
    }

    public void AddSystemMessage(string content)
    {
        var msg = new ChatMessage
        {
            SenderName = "系统",
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
                SenderName = "AI 思考",
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
        if (IsSuggestionRequest(content))
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
        var userPrompt = BuildInteractiveChatPrompt(
            runPrelude,
            combatContext,
            directiveContext,
            chatContext,
            $"玩家 {playerName} 说：\"{content}\"");

        var parsed = await SendInteractiveChatAsync(userPrompt, $"玩家 {playerName} 说：\"{content}\"", ct);
        if (parsed.Directives.Count > 0)
        {
            _directiveManager.RecordAcceptedDirectives(playerName, parsed.Directives);
            foreach (var directive in parsed.Directives.Where(d => d.Accepted))
                MainFile.Logger.Info($"[ChatDirective] {playerName} -> {directive.Type}: {directive.Summary}");
        }

        var replyContent = !string.IsNullOrWhiteSpace(parsed.Reply)
            ? parsed.Reply
            : "（收到，我会看情况执行。）";

        var aiMsg = new ChatMessage
        {
            SenderName = _promptBuilder.ActivePersonalityName,
            IsAi = true,
            Content = replyContent,
            Type = ChatMessageType.Normal
        };
        RecentMessages.Add(aiMsg);
        OnMessageReceived?.Invoke(aiMsg);

        return aiMsg;
    }

    public async Task<ChatMessage?> TryAutoMessage(Player aiPlayer, string triggerEvent, CancellationToken ct)
    {
        var (probability, _) = GetTriggerProbability(triggerEvent);
        if (new Random().NextDouble() > probability) return null;

        var elapsed = DateTime.UtcNow - _lastAutoMessage;
        if (elapsed.TotalMilliseconds < _cooldownMs) return null;

        _lastAutoMessage = DateTime.UtcNow;

        var systemPrompt = _promptBuilder.BuildSystemPrompt(chatMode: true);
        var runPrelude = _conversationContext.BuildRunPrelude(aiPlayer, _contextBuilder);
        var snapshot = _contextBuilder.BuildCombatPromptSnapshot(aiPlayer).RenderEssentials();
        var chatContext = _contextBuilder.BuildChatContext(RecentMessages, recentWindow: 4);
        var userPrompt = BuildChatPrompt(runPrelude, $"{snapshot}\n\n{chatContext}", $"触发事件：{triggerEvent}", "请用角色语气说一句简短的话（15字以内），禁止重复最近的发言，禁止使用\"让我分析\"\"当前局势\"等套话，要像一个真实的玩家在打字沟通。");

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

    private (double probability, ChatMessageType type) GetTriggerProbability(string triggerEvent)
    {
        return triggerEvent switch
        {
            "AiTurnStart" => (0.3, ChatMessageType.Suggestion),
            "AiKeyCard" => (0.8, ChatMessageType.Normal),
            "AiLowHp" => (0.7, ChatMessageType.Warning),
            "PlayerLowHp" => (0.6, ChatMessageType.Warning),
            "PlayerKeyCard" => (0.4, ChatMessageType.Normal),
            "BossStart" => (0.9, ChatMessageType.Normal),
            "CombatWon" => (0.8, ChatMessageType.Normal),
            "EnemyIntent" => (0.5, ChatMessageType.Suggestion),
            _ => (0.1, ChatMessageType.Normal)
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
            sb.AppendLine("[当前战斗状态]");
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
        sb.AppendLine("请先判断这句话是否包含需要你后续执行的团队指令，再决定是否答应。");
        sb.AppendLine("请以 JSON 格式返回（json output required）：");
        sb.AppendLine("```json");
        sb.AppendLine("{\"reply\":\"40字以内回复\",\"directives\":[{\"type\":\"focus_target|use_potion_on_teammate|protect_teammate|support_teammate|general_tactic\",\"accepted\":true,\"summary\":\"你理解到的执行要求\",\"target\":\"目标怪物或玩家名，没有则空\",\"item_hint\":\"药水名或效果关键词，没有则空\",\"duration_turns\":1,\"reason\":\"接受或拒绝原因\"}]}");
        sb.AppendLine("```");
        sb.AppendLine("规则:");
        sb.AppendLine("1. 没有明确行动请求时，directives 返回空数组。");
        sb.AppendLine("2. “给我上药水/奶我/给我加 buff”优先使用 use_potion_on_teammate 或 support_teammate。");
        sb.AppendLine("3. “下回合先集火某只怪/先打某个目标”使用 focus_target，并写清目标。");
        sb.AppendLine("4. 只有当你愿意尝试执行时 accepted=true；如果当前没有资源、不值得或太危险，可以 accepted=false。");
        sb.AppendLine("5. reply 要像真实队友在打字沟通，不能是脚本说明文。");
        return sb.ToString();
    }

    private List<AiMessage> BuildHistory()
    {
        var history = new List<AiMessage>();
        foreach (var msg in RecentMessages.TakeLast(4))
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

        var response = await _backend.SendAsync(request, ct);
        if (!response.IsSuccess)
            return new InteractiveChatResult { Reply = "（思考中...）" };

        var parsed = TryParseInteractiveChatResponse(response.Content);
        if (parsed != null)
            return parsed;

        var fallbackPrompt = BuildChatPrompt("", _contextBuilder.BuildChatContext(RecentMessages, recentWindow: 4), latestInput, "请用你的角色风格简短回复（40字以内）。");
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
            Reply = fallbackResponse.IsSuccess ? fallbackResponse.Content.Trim() : "（收到。）"
        };
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
                manager.CardHighlighter?.ApplyHighlights(suggestions, humanPlayer);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Suggestion request error: {ex.Message}");
        }
    }
}
