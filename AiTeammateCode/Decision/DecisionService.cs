using System.Text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Personality;

namespace AiTeammate.AiTeammateCode.Decision;

public class DecisionService
{
    private readonly BackendManager _backend;
    private readonly PersonalityPromptBuilder _promptBuilder;
    private readonly ContextBuilder _contextBuilder;
    private readonly PersonalityManager _personalityManager;
    private readonly MemoryManager _memoryManager;
    private readonly ConversationContextManager _conversationContext;
    private readonly int _timeoutMs;
    private readonly int _nonCombatRetryCount;

    public DecisionService(
        BackendManager backend,
        PersonalityManager personality,
        ContentQueryService query,
        ConversationContextManager conversationContext,
        int timeoutMs = 8000,
        int nonCombatRetryCount = 2)
    {
        _backend = backend;
        _promptBuilder = new PersonalityPromptBuilder(personality);
        _contextBuilder = new ContextBuilder(query);
        _timeoutMs = Math.Max(timeoutMs, 15000);
        _nonCombatRetryCount = nonCombatRetryCount;
        _personalityManager = personality;
        _memoryManager = new MemoryManager();
        _conversationContext = conversationContext;
    }

    private string BuildSystemPrompt(bool includeTeamPrinciples)
    {
        var prompt = _promptBuilder.BuildSystemPrompt(requireJson: true, includeTeamPrinciples: includeTeamPrinciples);
        return includeTeamPrinciples
            ? _promptBuilder.AppendTeamPrinciples(prompt)
            : prompt;
    }

    /// <summary>
    /// 发送 AI 请求（支持 JSON 模式和多轮对话）。
    /// 所有决策类请求默认启用 json_object 模式。
    /// </summary>
    private async Task<AiResponse> SendDecisionAsync(
        string systemPrompt, string userPrompt,
        int maxTokens, float temp,
        List<AiMessage>? history = null,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(_timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await _backend.SendAsync(new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Decision,
            MaxTokens = maxTokens,
            Temperature = temp,
            EnableJsonMode = true,       // DeepSeek JSON Output
            TopP = 0.9f,                 // DeepSeek 推荐与低温配合使用
            FrequencyPenalty = 0.1f,     // 轻微抑制重复
            ConversationHistory = history ?? new List<AiMessage>()
        }, linkedCts.Token);
    }

    /// <summary>
    /// 发送非战斗 AI 请求，带重试机制。
    /// 当 AI 后端返回错误（超时、网络异常等）时自动重试。
    /// </summary>
    private async Task<AiResponse> SendNonCombatDecisionWithRetryAsync(
        string systemPrompt, string userPrompt,
        int maxTokens, float temp,
        List<AiMessage>? history = null,
        CancellationToken ct = default)
    {
        int maxAttempts = _nonCombatRetryCount + 1; // 首次尝试 + 重试次数
        AiResponse? lastResponse = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // 重试前等待，递增延迟
                await Task.Delay(500 * attempt, ct);
                MainFile.Logger.Info($"[DecisionService] 非战斗决策重试 ({attempt}/{_nonCombatRetryCount})");
            }

            lastResponse = await SendDecisionAsync(systemPrompt, userPrompt, maxTokens, temp, history, ct);

            if (lastResponse.IsSuccess)
                return lastResponse;

            // 如果是因为外部取消（非超时），不重试
            if (ct.IsCancellationRequested)
                return lastResponse;
        }

        MainFile.Logger.Warn($"[DecisionService] 非战斗决策 {_nonCombatRetryCount} 次重试均失败");
        return lastResponse!;
    }

    public void ResetCombatHistory(string? combatId = null)
    {
        _conversationContext.ResetCombat();
    }

    // ---- Combat ----

    public async Task<DecisionResult> DecideCombatAsync(Player aiPlayer, CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: true);
        var prelude = BuildContextPrelude(aiPlayer);
        var snapshot = _conversationContext.BuildCombatContext(aiPlayer, _contextBuilder);
        var directiveContext = AiTeammateManager.Instance.DirectiveManager.BuildCombatDirectivePrompt(aiPlayer);
        var userPrompt = BuildCombatDecisionPrompt(prelude, snapshot, directiveContext);

        var response = await SendDecisionAsync(systemPrompt, userPrompt, 1024, 0.3f, history: null, ct);

        if (!response.IsSuccess) return DecisionResult.Fallback();

        var result = ResponseParser.ParseDecision(response.Content);
        if (!result.IsFallback)
            _conversationContext.RecordCombatDecision(aiPlayer, _contextBuilder, result);
        return result;
    }

    private string BuildCombatDecisionPrompt(string prelude, string snapshot, string directiveContext)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(directiveContext))
        {
            sb.AppendLine(directiveContext);
            sb.AppendLine();
        }
        sb.AppendLine(snapshot);
        sb.AppendLine();
        sb.AppendLine("请以 JSON 格式返回你的战斗决策（json output required）：");
        sb.AppendLine("```json");
        sb.AppendLine("{\"thinking\":\"战术分析\",\"chat_message\":\"简短沟通(可选)\",\"actions\":[{\"type\":\"play_card\",\"card_name\":\"卡名\",\"target\":\"敌人名或空\"},{\"type\":\"use_potion\",\"potion_name\":\"药水名\",\"target\":\"目标\"},{\"type\":\"end_turn\"}],\"deferred_actions\":[{\"type\":\"deferred_attack\",\"card_name\":\"攻击牌名\",\"target\":\"敌人名\",\"wait_for\":\"等待队友某种行为后打出\"}]}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("注意: card_name必须精确匹配手牌中的卡名。攻击牌通常必须指定敌人名；若你要给队友打支援牌或给队友用药水，target 可以写玩家名、角色名或“玩家”。");
        sb.AppendLine("【药水使用】药水不消耗能量，是免费行动！每回合都应该考虑使用。potion_name必须精确匹配你拥有的药水名称，target 写 Self 表示给自己用。");
        sb.AppendLine("【能量约束】actions 中所有 play_card 的合计费用不能超过你当前的可用能量（含辉星）。deferred_actions 中的攻击牌也会消耗能量，请确保 actions + deferred_actions 总费用 ≤ 当前能量。");
        sb.AppendLine("chat_message: 用角色语气简短沟通，不超过20字。");
        sb.AppendLine("若队友手里有易伤/破甲/力量/补刀机会，优先先出防御、力量、过牌等铺垫牌，把会受益的攻击放进 deferred_actions。");
        sb.AppendLine("若敌人已被队友压到残血，除非你能稳定完成击杀，否则不要抢斩杀；用 chat_message 明确集火目标。");
        sb.AppendLine("若上方存在[玩家战术指令]，应优先考虑执行；尤其是集火目标和给队友上药水。若你认为不该执行，请在 thinking 和 chat_message 中体现理由。");
        return sb.ToString();
    }

    private string BuildContextPrelude(Player aiPlayer)
    {
        var sb = new StringBuilder();
        var profile = _personalityManager.Active;
        if (profile != null)
        {
            var memory = _memoryManager.Load(profile.Id);
            var memories = _memoryManager.GetRelevantMemories(profile.Id, 3);
            var memoryContext = _promptBuilder.BuildMemoryContext(memories, memory.SelfReflection);
            if (!string.IsNullOrWhiteSpace(memoryContext))
            {
                sb.AppendLine(memoryContext);
                sb.AppendLine();
            }
        }

        var runPrelude = _conversationContext.BuildRunPrelude(aiPlayer, _contextBuilder);
        if (!string.IsNullOrWhiteSpace(runPrelude))
            sb.Append(runPrelude);

        return sb.ToString().TrimEnd();
    }

    private string BuildTaskPrompt(Player aiPlayer, string context, string instruction)
    {
        var sb = new StringBuilder();
        var prelude = BuildContextPrelude(aiPlayer);
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }

        sb.AppendLine(context);
        sb.AppendLine(instruction);
        return sb.ToString();
    }

    // ---- Card Reward ----

    public async Task<(int choice, string reason)> DecideCardRewardAsync(Player aiPlayer, List<CardModel> choices, CancellationToken ct)
    {
        int count = choices.Count > 0 ? choices.Count : 3;
        var context = _contextBuilder.BuildCardRewardContext(aiPlayer, choices);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var userPrompt = BuildTaskPrompt(aiPlayer, context, "请以 JSON 格式返回选牌决策：{\"choice\":0~2选一张, \"reason\":\"理由\"} 或 {\"choice\":-1, \"reason\":\"跳过理由\"}");

        var response = await SendNonCombatDecisionWithRetryAsync(systemPrompt, userPrompt, 256, 0.4f, ct: ct);

        if (!response.IsSuccess) return (0, "AI不可用，默认选第一张");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (choice >= 0 && choice < count ? choice : -1, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? 0;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            return (fallbackChoice >= 0 && fallbackChoice < count ? fallbackChoice : -1, fallbackReason);
        }
    }

    // ---- Map Navigation ----

    public async Task<(int choice, string reason)> DecideMapNavigationAsync(Player aiPlayer, CancellationToken ct)
    {
        var context = _contextBuilder.BuildMapContext(aiPlayer);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var userPrompt = BuildTaskPrompt(aiPlayer, context, "请以 JSON 格式返回选路决策：{\"choice\": 目标索引, \"reason\": \"简短理由\"}");

        var response = await SendNonCombatDecisionWithRetryAsync(systemPrompt, userPrompt, 256, 0.4f, ct: ct);

        if (!response.IsSuccess) return (0, "AI不可用，默认选第一个");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (choice, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? 0;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            return (fallbackChoice, fallbackReason);
        }
    }

    // ---- Rest Site ----

    public async Task<RestSiteDecisionResult> DecideRestSiteAsync(Player aiPlayer, int currentHp, int maxHp, IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options, CancellationToken ct)
    {
        var optionIds = options.Select(o => o.OptionId).ToList();
        var context = _contextBuilder.BuildRestSiteContext(aiPlayer, currentHp, maxHp, options);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var smithOption = options.OfType<MegaCrit.Sts2.Core.Entities.RestSite.SmithRestSiteOption>().FirstOrDefault();
        var upgradableCards = aiPlayer.Deck.Cards.Where(c => c.IsUpgradable).ToList();
        var smithCount = smithOption?.SmithCount ?? 0;

        var instruction = new StringBuilder();
        instruction.AppendLine($"请以 JSON 格式返回休息决策：{{\"choice\":0~{optionIds.Count - 1}, \"reason\":\"...\", \"upgrade_indices\":[仅在选择SMITH时填写升级卡牌索引]}}");
        if (smithOption?.IsEnabled == true)
            instruction.AppendLine($"如果 choice 对应 SMITH，upgrade_indices 必须从上面的可升级卡牌列表中选择 1~{smithCount} 个不重复索引；否则返回空数组。");

        var response = await SendNonCombatDecisionWithRetryAsync(systemPrompt, BuildTaskPrompt(aiPlayer, context, instruction.ToString().TrimEnd()), 256, 0.3f, ct: ct);

        int healIdx = optionIds.IndexOf("HEAL");
        int smithIdx = optionIds.IndexOf("SMITH");
        int digIdx = optionIds.IndexOf("DIG");
        int mendIdx = optionIds.IndexOf("MEND");
        int liftIdx = optionIds.IndexOf("LIFT");

        if (healIdx < 0) healIdx = 0;
        if (smithIdx < 0) smithIdx = optionIds.Count > 1 ? 1 : 0;

        var hpPct = maxHp > 0 ? (currentHp * 100 / maxHp) : 100;
        int safeFallback;
        if (hpPct <= 20) safeFallback = healIdx;
        else if (hpPct <= 35) safeFallback = mendIdx >= 0 ? mendIdx : healIdx;
        else if (hpPct <= 50) safeFallback = healIdx;
        else safeFallback = smithIdx;

        if (!response.IsSuccess)
            return new RestSiteDecisionResult
            {
                Choice = safeFallback,
                Reason = $"AI不可用，HP{hpPct}%使用安全策略"
            };

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var upgradeIndices = ParseUpgradeIndices(doc.RootElement, upgradableCards.Count, smithCount);

            if (choice < 0 || choice >= optionIds.Count) choice = safeFallback;

            // If AI chose MEND but no alive injured teammate, fall back to safe choice
            if (choice == mendIdx)
            {
                var teammates = aiPlayer.RunState?.Players.Where(p => p != aiPlayer).ToList() ?? new();
                var aliveInjured = teammates.Any(t => t.Creature?.IsAlive == true
                    && (t.Creature?.CurrentHp ?? 0) < (t.Creature?.MaxHp ?? 1));
                if (!aliveInjured)
                {
                    MainFile.Logger.Info("AI chose MEND but no injured teammates, falling back to safe option");
                    return new RestSiteDecisionResult { Choice = safeFallback, Reason = "没有需要治疗的队友，使用安全策略" };
                }
            }

            // Only override SMITH when HP is critically low (≤20%) — at 20-35% LLM can still
            // reasonably choose SMITH, especially with good upgrade targets.
            if (hpPct <= 20 && choice != healIdx && (choice == smithIdx || choice == digIdx || choice == liftIdx))
                return new RestSiteDecisionResult { Choice = healIdx, Reason = $"HP仅{hpPct}%危急，强制休息保命" };

            // Removed: overriding SMITH→MEND at ≤35% HP. MEND heals allies, not the AI player,
            // so it's worse than SMITH when the AI itself is dying.

            if (choice != smithIdx)
                upgradeIndices.Clear();

            var choiceLabel = choice >= 0 && choice < options.Count ? SafeOptionLabel(options[choice]) : $"索引{choice}";

            return new RestSiteDecisionResult
            {
                Choice = choice,
                Reason = reason,
                UpgradeIndices = upgradeIndices
            };
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content);
            if (fallbackChoice.HasValue && fallbackChoice.Value >= 0 && fallbackChoice.Value < optionIds.Count)
            {
                var fallbackReason = ResponseParser.TryExtractReason(response.Content);
                return new RestSiteDecisionResult
                {
                    Choice = fallbackChoice.Value,
                    Reason = fallbackReason.Length > 0 ? fallbackReason : "LLM解析回退"
                };
            }
            return new RestSiteDecisionResult
            {
                Choice = safeFallback,
                Reason = "解析失败，使用HP安全策略"
            };
        }
    }

    private static List<int> ParseUpgradeIndices(System.Text.Json.JsonElement root, int cardCount, int smithCount)
    {
        var indices = new List<int>();
        if (smithCount <= 0 || cardCount <= 0) return indices;
        if (!root.TryGetProperty("upgrade_indices", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return indices;

        foreach (var elem in arr.EnumerateArray())
        {
            if (elem.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            var idx = elem.GetInt32();
            if (idx >= 0 && idx < cardCount && !indices.Contains(idx))
                indices.Add(idx);
        }

        if (indices.Count > smithCount)
            indices = indices.Take(smithCount).ToList();

        return indices;
    }

    private static string SafeOptionLabel(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption option)
    {
        var title = PromptTextHelper.ResolveLocString(option.Title);
        return string.IsNullOrWhiteSpace(title) ? option.OptionId : title;
    }

    // ---- Shop ----

    public async Task<(List<int> choices, string reason)> DecideShopAsync(Player aiPlayer, List<ShopDecisionEntry> entries, CancellationToken ct)
    {
        var context = _contextBuilder.BuildShopContext(aiPlayer, entries);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var instruction = $"请以 JSON 格式返回购物决策：{{\"choices\":[索引列表], \"reason\":\"理由\"}}\nchoices 为要购买的物品索引数组（可多选），留空 [] 表示离开商店。索引范围 0~{entries.Count - 1}。";

        var response = await SendNonCombatDecisionWithRetryAsync(systemPrompt, BuildTaskPrompt(aiPlayer, context, instruction), 384, 0.3f, ct: ct);

        if (!response.IsSuccess) return (new List<int>(), "AI不可用，跳过购物");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

            var choices = new List<int>();
            if (doc.RootElement.TryGetProperty("choices", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var elem in arr.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var idx = elem.GetInt32();
                        if (idx >= 0 && idx < entries.Count && !choices.Contains(idx))
                            choices.Add(idx);
                    }
                }
            }

            return (choices, reason);
        }
        catch
        {
            // Fallback: try old single-choice format
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content);
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            if (fallbackChoice.HasValue && fallbackChoice.Value >= 0 && fallbackChoice.Value < entries.Count)
                return (new List<int> { fallbackChoice.Value }, fallbackReason);
            if (fallbackChoice.HasValue && fallbackChoice.Value < 0)
                return (new List<int>(), fallbackReason);
            return (new List<int>(), "解析失败");
        }
    }

    // ---- Generic Card Selection (enchant, remove, transform, upgrade) ----

    public async Task<(List<int> indices, string reason)> DecideCardSelectionAsync(
        Player aiPlayer, List<CardModel> cards, int minSelect, int maxSelect,
        string selectionType, CancellationToken ct)
    {
        if (cards.Count == 0) return (new List<int>(), "无牌可选");

        var context = BuildCardSelectionContext(aiPlayer, cards, selectionType);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var response = await SendNonCombatDecisionWithRetryAsync(
            systemPrompt,
            BuildTaskPrompt(aiPlayer, context, $"请以 JSON 格式返回选牌决策：{{\"indices\":[...], \"reason\":\"理由\"}} 选{minSelect}~{maxSelect}张"),
            1024,
            0.4f,
            ct: ct);

        if (!response.IsSuccess)
            return (new List<int>(), "AI不可用");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var indicesJson = doc.RootElement.GetProperty("indices");
            var indices = new List<int>();
            foreach (var elem in indicesJson.EnumerateArray())
            {
                var idx = elem.GetInt32();
                if (idx >= 0 && idx < cards.Count && !indices.Contains(idx))
                    indices.Add(idx);
            }
            // Clamp to min/max
            if (indices.Count < minSelect)
            {
                for (int i = 0; i < cards.Count && indices.Count < minSelect; i++)
                    if (!indices.Contains(i)) indices.Add(i);
            }
            if (indices.Count > maxSelect)
                indices = indices.Take(maxSelect).ToList();

            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (indices, reason);
        }
        catch
        {
            // Regex fallback: extract first valid index from "indices"
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            var fallbackIndices = new List<int>();
            // Try to extract individual indices from malformed JSON
            var matches = System.Text.RegularExpressions.Regex.Matches(
                ResponseParser.ExtractJson(response.Content), @"(-?\d+)");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 0 && idx < cards.Count && !fallbackIndices.Contains(idx))
                    fallbackIndices.Add(idx);
            }
            if (fallbackIndices.Count < minSelect)
            {
                for (int i = 0; i < cards.Count && fallbackIndices.Count < minSelect; i++)
                    if (!fallbackIndices.Contains(i)) fallbackIndices.Add(i);
            }
            if (fallbackIndices.Count > maxSelect)
                fallbackIndices = fallbackIndices.Take(maxSelect).ToList();
            return (fallbackIndices, fallbackReason.Length > 0 ? fallbackReason : "解析失败，选择前几张");
        }
    }

    private string BuildCardSelectionContext(Player aiPlayer, List<CardModel> cards, string selectionType)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{selectionType}] 选{cards.Count}张");

        var hp = aiPlayer.Creature?.CurrentHp ?? 0;
        var maxHp = aiPlayer.Creature?.MaxHp ?? 0;
        sb.Append($"{hp}/{maxHp}HP Gold:{aiPlayer.Gold}");

        var deckCards = aiPlayer.Deck?.Cards;
        if (deckCards != null && deckCards.Any())
            sb.Append($" | 牌组:{deckCards.Count}张");

        sb.AppendLine();
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var ol = _contextBuilder.GetCardOneLiner(c, 50);
            sb.AppendLine($"{i}. {c.Title}({c.Rarity}) {ol}");
        }

        var exactKnowledge = _contextBuilder.EnrichContext(selectionType);
        var cardKnowledge = cards.Count <= 10 ? string.Join(Environment.NewLine, cards.Select(c => _contextBuilder.GetCardOneLiner(c, 80))) : "";
        if (!string.IsNullOrWhiteSpace(cardKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选卡牌资料]");
            for (int i = 0; i < cards.Count; i++)
                sb.AppendLine($"{i}. {_contextBuilder.GetCardOneLiner(cards[i], 80)}");
        }

        // Keep mention-based lookup for selection types that explicitly reference relics or enchantments.
        var enrichment = exactKnowledge;
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.Append("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    // ---- Event ----

    public async Task<(int choice, string reason)> DecideEventAsync(Player aiPlayer, List<string> optionDescs, CancellationToken ct)
    {
        var context = _contextBuilder.BuildEventContext(aiPlayer, optionDescs);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var response = await SendNonCombatDecisionWithRetryAsync(
            systemPrompt,
            BuildTaskPrompt(aiPlayer, context, $"请以 JSON 格式返回事件决策：{{\"choice\":0~{optionDescs.Count - 1}, \"reason\":\"理由\"}}"),
            1024,
            0.3f,
            ct: ct);

        if (!response.IsSuccess) return (0, "AI不可用，默认选第一个");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var valid = choice >= 0 && choice < optionDescs.Count ? choice : 0;
            return (valid, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? 0;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            var valid = fallbackChoice >= 0 && fallbackChoice < optionDescs.Count ? fallbackChoice : 0;
            return (valid, fallbackReason);
        }
    }
}

public class DecisionResult
{
    public string Thinking { get; set; } = "";
    public string ChatMessage { get; set; } = "";
    public List<DecisionAction> Actions { get; set; } = new();
    public List<DecisionAction> DeferredActions { get; set; } = new();
    public bool IsFallback { get; set; }

    public static DecisionResult Fallback()
    {
        return new DecisionResult
        {
            Thinking = "Fallback: 无法获取 AI 决策，使用默认行为",
            Actions = new List<DecisionAction> { new() { Type = "end_turn" } },
            IsFallback = true
        };
    }
}

public class RestSiteDecisionResult
{
    public int Choice { get; set; }
    public string Reason { get; set; } = "";
    public List<int> UpgradeIndices { get; set; } = new();
}

public class DecisionAction
{
    public string Type { get; set; } = "";
    public string CardName { get; set; } = "";
    public string PotionName { get; set; } = "";
    public string Target { get; set; } = "";
    public string Player { get; set; } = "";
    public string Reason { get; set; } = "";
    public string WaitFor { get; set; } = "";
}
