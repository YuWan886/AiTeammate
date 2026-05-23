using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Context;

namespace AiTeammate.AiTeammateCode.Decision;

public class ContextBuilder
{
    private readonly ContentQueryService _query;

    public ContextBuilder(ContentQueryService query)
    {
        _query = query;
    }

    public string BuildCombatSnapshot(Player aiPlayer)
    {
        return BuildCombatPromptSnapshot(aiPlayer).RenderAll();
    }

    public PromptSnapshot BuildCombatPromptSnapshot(Player aiPlayer)
    {
        var snapshot = new PromptSnapshot();
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            snapshot.Add("combat_state", "战场", "Combat state unavailable");
            return snapshot;
        }

        snapshot.Add("round", "回合", $"第{state.RoundNumber}回合 | 当前阶段:{state.CurrentSide}");
        snapshot.Add("self", "你", BuildPlayerStateCompact(aiPlayer, includeHand: true));

        var teammateInfos = state.Players
            .Where(p => p != aiPlayer)
            .Select(player =>
            {
                var summary = BuildPlayerStateCompact(player, includeHand: false);
                var hint = BuildCoordinationHintCompact(player);
                return string.IsNullOrWhiteSpace(hint)
                    ? $"{GetCharacterName(player)} {summary}"
                    : $"{GetCharacterName(player)} {summary} | 协同:{hint}";
            })
            .ToList();
        if (teammateInfos.Count > 0)
            snapshot.Add("teammates", "队友", string.Join(" || ", teammateInfos));

        snapshot.Add("enemies", "敌人", BuildEnemyStateLine(state));

        var threat = BuildThreatAssessmentText(state, aiPlayer);
        if (!string.IsNullOrWhiteSpace(threat))
            snapshot.Add("threat", "威胁", threat);

        var potions = BuildPotionSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(potions))
            snapshot.Add("potions", "药水", potions, essential: false);

        var piles = BuildPileSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(piles))
            snapshot.Add("piles", "牌堆", piles, essential: false);

        if (aiPlayer.PlayerCombatState != null)
        {
            var handKnowledge = _query.FormatCardKnowledge(aiPlayer.PlayerCombatState.Hand.Cards, maxCount: 10);
            if (!string.IsNullOrWhiteSpace(handKnowledge))
                snapshot.Add("hand_knowledge", "手牌资料", handKnowledge, essential: false);
        }

        var potionKnowledge = _query.FormatPotionKnowledge(aiPlayer.Potions ?? Enumerable.Empty<PotionModel>(), maxCount: 4);
        if (!string.IsNullOrWhiteSpace(potionKnowledge))
            snapshot.Add("potion_knowledge", "药水资料", potionKnowledge, essential: false);

        // 遗物一般自动触发，信息过多反而干扰判断，已移除

        return snapshot;
    }

    /// <summary>
    /// 构建 [当前关键状态] 的详细内容。
    /// 包含回合、自身/队友的完整手牌与药水、敌人状态、威胁评估。
    /// </summary>
    public string BuildCombatEssentials(Player aiPlayer)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
            return "Combat state unavailable";

        var sb = new StringBuilder();

        // 回合（不需要当前阶段，因为发请求都在玩家回合）
        sb.AppendLine($"回合: 第{state.RoundNumber}回合");

        // 你自己
        sb.AppendLine(BuildDetailedSelfSection(aiPlayer));

        // 队友
        foreach (var teammate in state.Players.Where(p => p != aiPlayer))
            sb.AppendLine(BuildDetailedTeammateSection(teammate));

        // 敌人
        var enemyLine = BuildEnemyStateLine(state);
        sb.AppendLine($"敌人: {enemyLine}");

        // 威胁
        var threat = BuildThreatAssessmentText(state, aiPlayer);
        if (!string.IsNullOrWhiteSpace(threat))
            sb.AppendLine($"威胁: {threat}");

        // 牌堆
        var piles = BuildPileSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(piles))
            sb.AppendLine($"牌堆: {piles}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>构建 "你" 的详细段落：基础状态 + 手牌 + 药水</summary>
    private string BuildDetailedSelfSection(Player aiPlayer)
    {
        var sb = new StringBuilder();
        var combat = aiPlayer.PlayerCombatState;
        var creature = aiPlayer.Creature;
        var currentEnergy = (int)(combat?.Energy ?? 0);
        var maxEnergy = (int)(combat?.MaxEnergy ?? 0);
        var currentStars = (int)(combat?.Stars ?? 0);
        var starsInfo = currentStars > 0 ? $" 辉星:{currentStars}" : "";

        // 基础状态行
        sb.AppendLine($"你: HP{creature.CurrentHp}/{creature.MaxHp} 格挡{creature.Block} 能量{currentEnergy}/{maxEnergy}{starsInfo}");

        // 状态/能力
        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.AppendLine($"  状态: {string.Join(" | ", powers.Select(FormatPowerBrief))}");

        // 手牌详情
        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            if (handCards.Count > 0)
            {
                sb.AppendLine("  手牌:");
                foreach (var card in handCards)
                    sb.AppendLine($"    {FormatCardDetail(card)}");
            }
            else
            {
                sb.AppendLine("  手牌: 空");
            }
        }

        // 药水详情
        AppendDetailedPotions(sb, aiPlayer.Potions);

        return sb.ToString().TrimEnd();
    }

    /// <summary>构建队友的详细段落：玩家名称-角色 | 基础状态 + 手牌 + 药水</summary>
    private static string BuildDetailedTeammateSection(Player teammate)
    {
        var sb = new StringBuilder();
        var combat = teammate.PlayerCombatState;
        var creature = teammate.Creature;
        var charName = GetCharacterName(teammate);
        var starsInfo = combat?.Stars > 0 ? $" 辉星:{combat.Stars}" : "";

        // 获取玩家显示名称
        var playerName = GetPlayerDisplayName(teammate);

        // 基础状态行（玩家名 - 角色 | 状态）
        sb.AppendLine($"队友: {playerName} - {charName} | {creature.CurrentHp}/{creature.MaxHp}HP 格挡{creature.Block} 能量{combat?.Energy}/{combat?.MaxEnergy}{starsInfo}");

        // 状态/能力
        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.AppendLine($"  状态: {string.Join(" | ", powers.Select(FormatPowerBrief))}");

        // 手牌详情
        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            if (handCards.Count > 0)
            {
                sb.AppendLine("  手牌:");
                foreach (var card in handCards)
                    sb.AppendLine($"    {FormatCardDetail(card)}");
            }
            else
            {
                sb.AppendLine("  手牌: 空");
            }
        }

        // 药水详情
        AppendDetailedPotions(sb, teammate.Potions);

        return sb.ToString().TrimEnd();
    }

    /// <summary>格式化单张卡牌的详细信息：名称 | 费用 | 目标 | 效果</summary>
    private static string FormatCardDetail(CardModel card)
    {
        var name = card.Title ?? card.Id.Entry;
        var cost = card.EnergyCost?.Canonical ?? 0;
        var starCost = Math.Max(0, card.GetStarCostWithModifiers());
        var costStr = starCost > 0 ? $"{cost}费{starCost}星" : $"{cost}费";

        // 目标类型
        var targetStr = card.TargetType.ToString();

        // 效果描述
        var desc = PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry);
        // 去除 BBCode 标签
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = desc.Trim();
        if (desc.Length > 120) desc = desc[..120] + "...";

        return $"{name} | {costStr} | 目标:{targetStr} | 效果:{desc}";
    }

    /// <summary>追加药水详细信息到 StringBuilder</summary>
    private static void AppendDetailedPotions(StringBuilder sb, IEnumerable<PotionModel>? potions)
    {
        var potionList = potions?.ToList();
        if (potionList == null || potionList.Count == 0)
        {
            sb.AppendLine("  药水: 无");
            return;
        }

        sb.AppendLine("  药水:");
        foreach (var potion in potionList)
        {
            var name = SafeLocText(potion.Title);
            var desc = SafeLocText(potion.DynamicDescription);
            desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
            desc = desc.Trim();
            if (desc.Length > 80) desc = desc[..80] + "...";

            if (string.IsNullOrEmpty(desc))
                sb.AppendLine($"    {name}");
            else
                sb.AppendLine($"    {name} | 效果:{desc}");
        }
    }


    /// <summary>Compact player state — one line for stats and important powers.</summary>
    private string BuildPlayerStateCompact(Player player, bool includeHand)
    {
        var combat = player.PlayerCombatState;
        var creature = player.Creature;
        var starsInfo = combat?.Stars > 0 ? $" 辉星:{combat.Stars}" : "";

        var sb = new StringBuilder();
        sb.Append($"{creature.CurrentHp}/{creature.MaxHp}HP 格挡{creature.Block} 能量{combat?.Energy}/{combat?.MaxEnergy}{starsInfo}");

        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.Append($" | 状态:{string.Join(" ", powers.Select(FormatPowerBrief))}");

        if (includeHand && combat != null)
        {
            var hand = BuildHandSummary(combat.Hand.Cards.ToList());
            if (!string.IsNullOrWhiteSpace(hand))
                sb.Append($" | 手牌:{hand}");
        }

        return sb.ToString();
    }

    private static string GetCardEffectHint(CardModel card)
    {
        try
        {
            var desc = card.GetDescriptionForPile(PileType.Hand);
            if (string.IsNullOrWhiteSpace(desc)) return "";

            // Strip BBCode tags
            desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");

            // Extract key numbers for common effects
            var parts = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"造成(\d+)点伤害"))
                parts.Add($"伤{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"获得(\d+)点格挡"))
                parts.Add($"甲{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"给予(\d+)层"))
                parts.Add($"层{m.Groups[1].Value}");

            // If no specific numbers found, check for keywords
            if (parts.Count == 0)
            {
                if (desc.Contains("易伤")) parts.Add("易伤");
                if (desc.Contains("虚弱")) parts.Add("虚弱");
                if (desc.Contains("力量")) parts.Add("力量");
                if (desc.Contains("AOE") || desc.Contains("所有敌人")) parts.Add("AOE");
                if (desc.Contains("抽牌") || desc.Contains("抽")) parts.Add("抽");
                if (desc.Contains("辉星")) parts.Add("星");
            }

            return parts.Count > 0 ? $":{string.Join("/", parts.Take(3))}" : "";
        }
        catch { return ""; }
    }

    private string BuildCoordinationHintCompact(Player teammate)
    {
        var c = teammate.PlayerCombatState;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (c == null) return "";
        var hand = c.Hand.Cards.ToList();
        if (hand.Count == 0) return "";

        var hints = new List<string>();
        foreach (var card in hand)
        {
            var title = card.Title ?? card.Id.Entry;
            try
            {
                var desc = card.GetDescriptionForPile(PileType.Hand);
                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                if (desc.Contains("易伤") || desc.Contains("破甲") || desc.Contains("虚弱"))
                    hints.Add($"{title}(上减益)");
                if ((desc.Contains("力量") || desc.Contains("敏捷") || desc.Contains("缓冲")) && card.Type != CardType.Attack)
                    hints.Add($"{title}(增益)");
                if (desc.Contains("所有敌人") || desc.Contains("每个敌人"))
                    hints.Add($"{title}(AOE)");
                if (desc.Contains("格挡") && card.Type == CardType.Skill)
                    hints.Add($"{title}(格挡)");

                if (card.Type == CardType.Attack && state != null)
                {
                    var damageMatch = System.Text.RegularExpressions.Regex.Match(desc, @"造成(\d+)点伤害");
                    if (damageMatch.Success && int.TryParse(damageMatch.Groups[1].Value, out var damage))
                    {
                        var killTarget = state.Enemies
                            .Where(e => e.IsAlive && e.CurrentHp <= damage + 2)
                            .OrderBy(e => e.CurrentHp)
                            .FirstOrDefault();
                        if (killTarget != null)
                            hints.Add($"{title}(可补刀{killTarget.Name})");
                    }
                }
            }
            catch { }
        }
        return hints.Count > 0 ? string.Join(", ", hints.Distinct().Take(4)) : "";
    }

    /// <summary>Format a power as "name:description(amount)". Falls back to name-only if description has unresolved placeholders.</summary>
    private static string FormatPowerBrief(MegaCrit.Sts2.Core.Models.PowerModel p)
    {
        var placeholderRe = new System.Text.RegularExpressions.Regex(@"\{[^}]+\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        var name = SafeLocText(p.Title);
        if (string.IsNullOrEmpty(name) || name == p.Id.Entry)
            name = p.Id.Entry.Replace("_POWER", "").Replace("_", " ").ToLowerInvariant();

        // Try Description first (simpler, fewer SmartFormat conditionals)
        var desc = SafeLocText(p.Description);
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"res://\S+", "").Trim();

        // If unresolved, try SmartDescription
        if (placeholderRe.IsMatch(desc) && p.HasSmartDescription)
        {
            var smartDesc = SafeLocText(p.SmartDescription);
            smartDesc = System.Text.RegularExpressions.Regex.Replace(smartDesc, @"\[.*?\]", "");
            smartDesc = System.Text.RegularExpressions.Regex.Replace(smartDesc, @"res://\S+", "").Trim();
            if (!placeholderRe.IsMatch(smartDesc))
                desc = smartDesc;
        }

        // If STILL has unresolved placeholders, skip description
        if (placeholderRe.IsMatch(desc))
            desc = "";

        if (desc.Length > 30) desc = desc[..30] + "...";

        var amountStr = p.Amount != 1 ? $"({p.Amount})" : "";
        return string.IsNullOrEmpty(desc) ? $"{name}{amountStr}" : $"{name}:{desc}{amountStr}";
    }

    private static string FormatIntentCompact(MoveState move)
    {
        var parts = new List<string>();
        var atkIntents = move.Intents.OfType<AttackIntent>().ToList();
        if (atkIntents.Count > 0)
        {
            // Try to get actual damage values
            var dmgParts = new List<string>();
            try
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                var players = state?.Players.Select(p => p.Creature) ?? Enumerable.Empty<Creature>();
                var firstEnemy = state?.Enemies.FirstOrDefault(e => e.IsAlive);
                foreach (var atk in atkIntents)
                {
                    if (firstEnemy != null)
                    {
                        int dmg = atk.GetSingleDamage(players, firstEnemy);
                        dmgParts.Add(dmg.ToString());
                    }
                }
            }
            catch { dmgParts.Add("?"); }
            parts.Add(dmgParts.Count > 0 ? $"伤害{string.Join("/", dmgParts)}" : $"攻×{atkIntents.Count}");
        }
        var nonAtk = move.Intents.Where(i => i is not AttackIntent)
            .Select(i => i.IntentType switch
            {
                IntentType.Defend => "防御",
                IntentType.Buff => "增益",
                IntentType.Debuff => "减益",
                IntentType.Stun => "眩晕",
                _ => i.IntentType.ToString()
            }).ToList();
        parts.AddRange(nonAtk);
        return string.Join(",", parts);
    }

    [Obsolete] private void AppendPlayerState(StringBuilder sb, string label, Player player)
    {
        var combat = player.PlayerCombatState;
        var creature = player.Creature;

        sb.AppendLine();
        sb.AppendLine(label);
        sb.AppendLine($"角色：{GetCharacterName(player)} | 血量 {creature.CurrentHp}/{creature.MaxHp} | 能量 {combat?.Energy}/{combat?.MaxEnergy} | 格挡 {creature.Block}");

        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.AppendLine($"状态：{string.Join(" | ", powers.Select(p => $"{p.Id.Entry}({p.Amount})"))}");

        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            if (handCards.Count > 0)
                sb.AppendLine($"手牌({handCards.Count}张)：{string.Join(" | ", handCards.Select(c => FormatCardBrief(c)))}");
            else
                sb.AppendLine("手牌：空");
        }
    }

    private string FormatCardBrief(CardModel card)
    {
        var name = card.Title ?? card.Id.Entry;
        var cost = card.EnergyCost?.Canonical ?? 0;
        var oneLiner = GetCardOneLiner(card, 80);

        return $"{name}({cost}费) {oneLiner}";
    }

    private string FormatIntent(MoveState move)
    {
        var parts = new List<string>();
        var state = CombatManager.Instance.DebugOnlyGetState();
        foreach (var intent in move.Intents)
        {
            if (intent is AttackIntent atk)
            {
                try
                {
                    var firstEnemy = state?.Enemies.FirstOrDefault(e => e.IsAlive);
                    if (firstEnemy != null)
                    {
                        int dmg = atk.GetSingleDamage(
                            state?.Players.Select(p => p.Creature) ?? Enumerable.Empty<Creature>(),
                            firstEnemy);
                        parts.Add($"攻击×{dmg}");
                    }
                    else
                    {
                        parts.Add("攻击");
                    }
                }
                catch
                {
                    parts.Add("攻击");
                }
            }
            else
            {
                parts.Add(intent.IntentType.ToString());
            }
        }
        return $"{move.Id}({string.Join(", ", parts)})";
    }

    private static string GetCharacterName(Player player)
    {
        return player.Character?.Id.Entry ?? "Unknown";
    }

    private void AppendCoordinationHints(StringBuilder sb, Player teammate)
    {
        var combat = teammate.PlayerCombatState;
        if (combat == null) return;

        var hand = combat.Hand.Cards.ToList();
        if (hand.Count == 0) return;

        // Find coordination cards in teammate's hand
        var debuffCards = new List<string>();
        var buffCards = new List<string>();
        var aoeCards = new List<string>();

        foreach (var card in hand)
        {
            var title = card.Title ?? card.Id.Entry;
            // Detect coordination cards by effect keywords in title
            if (title.Contains("痛击") || title.Contains("上勾") || title.Contains("震荡")
                || title.Contains("破甲") || title.Contains("易伤") || title.Contains("撕裂"))
                debuffCards.Add(title);
            else if (title.Contains("力量") || title.Contains("强化") || title.Contains("战吼"))
                buffCards.Add(title);
            else if (title.Contains("旋风") || title.Contains("横扫") || title.Contains("全力"))
                aoeCards.Add(title);

            // Also check card description for Vulnerable-related keywords
            try
            {
                var desc = card.GetDescriptionForPile(PileType.Hand);
                if (desc?.Contains("易伤") == true && !debuffCards.Contains(title))
                    debuffCards.Add(title);
            }
            catch { }
        }

        if (debuffCards.Count > 0 || buffCards.Count > 0)
        {
            sb.Append("  [协同提示] ");
            if (debuffCards.Count > 0)
                sb.Append($"队友手中有易伤/破甲牌: {string.Join(", ", debuffCards)}。你应该等队友先上易伤再打攻击牌！");
            if (buffCards.Count > 0)
                sb.Append($"队友有增益牌: {string.Join(", ", buffCards)}。");
            if (aoeCards.Count > 0)
                sb.Append($"队友有AOE: {string.Join(", ", aoeCards)}。");
            sb.AppendLine();
        }
    }

    // ---- Public helpers for DecisionService ----

    public CardIndexEntry? GetCardBrief(string cardId) => _query.GetCardBrief(cardId);

    public string EnrichContext(string text) => _query.EnrichContext(text);

    private string BuildEnemyStateLine(CombatState state)
    {
        var enemyInfos = new List<string>();
        foreach (var enemy in state.Enemies.Where(e => e.IsAlive))
        {
            var intent = enemy.Monster?.NextMove != null ? FormatIntentCompact(enemy.Monster.NextMove) : "?";
            var vuln = HasVulnerable(enemy) ? "[易伤]" : "";
            var weak = HasWeakened(enemy) ? "[虚弱]" : "";
            var buffs = string.Join("", enemy.Powers?
                .Where(p => p.Amount > 0
                    && !p.Id.Entry.Equals("Vulnerable", StringComparison.OrdinalIgnoreCase)
                    && !p.Id.Entry.Equals("Weak", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"[{FormatPowerBrief(p)}]") ?? Enumerable.Empty<string>());
            enemyInfos.Add($"{enemy.Name}({enemy.CurrentHp}/{enemy.MaxHp}) {intent}{vuln}{weak}{buffs}");
        }
        return enemyInfos.Count > 0 ? string.Join(" | ", enemyInfos) : "无存活敌人";
    }

    private string BuildHandSummary(List<CardModel> handCards)
    {
        if (handCards.Count == 0)
            return "空";

        return string.Join(", ", handCards.Select(card =>
        {
            var name = card.Title ?? card.Id.Entry;
            var cost = card.EnergyCost?.Canonical ?? 0;
            var starCost = Math.Max(0, card.GetStarCostWithModifiers());
            var costStr = starCost > 0 ? $"{cost}费{starCost}星" : $"{cost}费";
            var typeTag = card.Type switch { CardType.Attack => "攻", CardType.Skill => "技", CardType.Power => "能", _ => "" };
            var hint = GetCardEffectHint(card);
            return $"[{name}({costStr}){typeTag}{hint}]";
        }));
    }

    private string BuildPotionSummary(Player aiPlayer)
    {
        var potions = aiPlayer.Potions;
        if (potions == null || !potions.Any())
            return "";

        var potionInfos = potions.Select(p =>
        {
            var name = SafeLocText(p.Title);
            var desc = SafeLocText(p.DynamicDescription);
            if (desc.Length > 40) desc = desc[..40] + "...";
            return string.IsNullOrEmpty(desc) ? name : $"{name}:{desc}";
        });
        return string.Join(" | ", potionInfos);
    }

    private string BuildPileSummary(Player aiPlayer)
    {
        var combat = aiPlayer.PlayerCombatState;
        if (combat == null)
            return "";

        var drawCount = combat.DrawPile.Cards.Count;
        var discardCount = combat.DiscardPile.Cards.Count;
        var exhaustCount = combat.ExhaustPile.Cards.Count;
        return exhaustCount > 0
            ? $"抽牌堆:{drawCount}张 弃牌堆:{discardCount}张 消耗堆:{exhaustCount}张"
            : $"抽牌堆:{drawCount}张 弃牌堆:{discardCount}张";
    }

    private bool HasVulnerable(Creature creature)
    {
        return creature.Powers?.Any(p => p.Id.Entry.Equals("Vulnerable", System.StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    private bool HasWeakened(Creature creature)
    {
        return creature.Powers?.Any(p => p.Id.Entry.Equals("Weak", System.StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    /// <summary>Show which enemies are targeting which players, and whether the AI is in danger.</summary>
    private string BuildThreatAssessmentText(CombatState state, Player aiPlayer)
    {
        try
        {
            var aiCreature = aiPlayer.Creature;
            if (aiCreature == null) return "";

            // Find enemies attacking this turn
            var attackingEnemies = state.Enemies
                .Where(e => e.IsAlive && e.Monster?.NextMove?.Intents.Any(i => i is AttackIntent) == true)
                .ToList();

            if (attackingEnemies.Count == 0) return "";

            // Estimate total incoming damage to AI
            int totalIncoming = 0;
            foreach (var enemy in attackingEnemies)
            {
                try
                {
                    var atkIntents = enemy.Monster!.NextMove!.Intents.OfType<AttackIntent>();
                    foreach (var atk in atkIntents)
                    {
                        totalIncoming += atk.GetSingleDamage(
                            state.Players.Select(p => p.Creature),
                            enemy);
                    }
                }
                catch { /* skip if damage calc fails */ }
            }

            var block = aiCreature.Block;
            var hp = aiCreature.CurrentHp;
            var netDmg = Math.Max(0, totalIncoming - block);

            if (totalIncoming > 0)
            {
                var dangerLabel = netDmg >= hp ? "!!致死!!" :
                                  netDmg > hp / 2 ? "危险" :
                                  block >= totalIncoming ? "安全" : "";
                var threat = $"预计受到{totalIncoming}点伤害";
                if (block > 0) threat += $" 格挡{block}";
                threat += $" 净伤害{netDmg}/{hp}HP";
                if (!string.IsNullOrEmpty(dangerLabel)) threat += $" {dangerLabel}";
                return threat;
            }
        }
        catch { }

        return "";
    }

    public string BuildChatContext(IReadOnlyList<ChatMessage> recentMessages, int recentWindow = 4)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[聊天上下文]");
        if (recentMessages.Count == 0)
        {
            sb.AppendLine("(无)");
            return sb.ToString();
        }

        var older = recentMessages.Take(Math.Max(0, recentMessages.Count - recentWindow)).TakeLast(6).ToList();
        if (older.Count > 0)
        {
            sb.AppendLine("较早摘要:");
            sb.AppendLine(string.Join(" | ", older.Select(msg =>
                $"{(msg.IsAi ? "你" : msg.SenderName)}:{PromptTextHelper.Truncate(PromptTextHelper.CleanText(msg.Content), 24)}")));
            sb.AppendLine();
        }

        sb.AppendLine("最近对话:");
        foreach (var msg in recentMessages.TakeLast(recentWindow))
            sb.AppendLine($"{(msg.IsAi ? "你" : msg.SenderName)}: {PromptTextHelper.Truncate(PromptTextHelper.CleanText(msg.Content), 80)}");

        return sb.ToString();
    }

    public string BuildSuggestionContext(Player player)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine("[请求卡牌建议 - 玩家手牌分析]");
        sb.AppendLine();

        var combat = player.PlayerCombatState;
        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            sb.AppendLine($"玩家当前手牌({handCards.Count}张)：");
            for (int i = 0; i < handCards.Count; i++)
                sb.AppendLine($"{i + 1}. {FormatCardBrief(handCards[i])}");
        }

        sb.AppendLine();
        sb.AppendLine("场上敌人：");
        foreach (var enemy in state.Enemies.Where(e => e.IsAlive))
            sb.AppendLine($"- {enemy.Name}({enemy.CurrentHp}血)");

        sb.AppendLine();
        sb.AppendLine("请选出2-3张最适合当前局势的卡牌，并给出简短理由。");

        if (combat != null)
        {
            var handKnowledge = _query.FormatCardKnowledge(combat.Hand.Cards, maxCount: 10);
            if (!string.IsNullOrWhiteSpace(handKnowledge))
            {
                sb.AppendLine();
                sb.AppendLine("[手牌详细资料]");
                sb.AppendLine(handKnowledge);
            }
        }

        return sb.ToString();
    }

    // ---- Non-combat contexts ----

    public string BuildCardRewardContext(Player aiPlayer, List<CardModel> choices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[卡牌奖励]");
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        for (int i = 0; i < choices.Count; i++)
        {
            var card = choices[i];
            if (card == null) continue;
            sb.AppendLine($"{i}. {FormatCardDetail(card)}");
        }
        sb.AppendLine("-1=跳过");

        return sb.ToString();
    }

    public string BuildMapContext(Player aiPlayer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[地图导航]");
        AppendPlayerBrief(sb, aiPlayer);
        return sb.ToString();
    }

    public string BuildRestSiteContext(Player aiPlayer, int currentHp, int maxHp, IReadOnlyList<RestSiteOption> options)
    {
        var sb = new StringBuilder();
        var hpPct = maxHp > 0 ? (currentHp * 100 / maxHp) : 0;
        var hpLabel = hpPct switch
        {
            <= 20 => "【危重】",
            <= 40 => "【危险】",
            <= 60 => "【偏低】",
            _ => "【健康】"
        };
        sb.AppendLine($"[休息站] {hpLabel}{GetCharacterName(aiPlayer)} HP:{currentHp}/{maxHp}({hpPct}%) Gold:{aiPlayer.Gold}");

        // Teammate HP status — helps decide MEND, HEAL, etc.
        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
            {
                var teammates = runState.Players.Where(p => p != aiPlayer).ToList();
                if (teammates.Count > 0)
                {
                    var teammateInfos = teammates.Select(t =>
                    {
                        var alive = t.Creature?.IsAlive ?? false;
                        var tHp = t.Creature?.CurrentHp ?? 0;
                        var tMax = t.Creature?.MaxHp ?? 1;
                        var tPct = tMax > 0 ? (tHp * 100 / tMax) : 0;
                        var tLabel = !alive ? "【阵亡】" :
                                     tPct <= 40 ? "⚠" :
                                     tPct >= 100 ? "满血" : "";
                        return $"{GetCharacterName(t)} HP:{tHp}/{tMax}({tPct}%){tLabel}";
                    });
                    sb.AppendLine($"队友: {string.Join(" | ", teammateInfos)}");

                    // Warn if MEND has no valid target
                    var aliveInjured = teammates.Any(t => t.Creature?.IsAlive == true
                        && (t.Creature?.CurrentHp ?? 0) < (t.Creature?.MaxHp ?? 1));
                    if (!aliveInjured)
                        sb.AppendLine("⚠ 当前没有受伤的队友，MEND（治疗队友）将无效！");
                }
            }
        }
        catch { }

        // Show deck + act info
        var deck = aiPlayer.Deck.Cards;
        var upgradable = deck.Where(c => c.IsUpgradable).Take(3).Select(c => c.Title).ToList();
        var totalUpgradable = deck.Count(c => c.IsUpgradable);
        sb.Append($"牌组:{deck.Count}张 可升级:{totalUpgradable}张");
        if (upgradable.Count > 0)
        {
            var sample = string.Join(",", upgradable);
            if (totalUpgradable > 3) sample += $"...等{totalUpgradable}张";
            sb.Append($" ({sample})");
        }
        sb.AppendLine();

        // Add act/floor context for better decision making
        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
                sb.AppendLine($"当前: 第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层");
        }
        catch { }

        // Relics that affect rest decisions
        var relics = aiPlayer.Relics;
        if (relics != null && relics.Any())
        {
            var keyRelics = relics.Where(r =>
            {
                var id = r.Id.Entry.ToLowerInvariant();
                return id.Contains("coffee") || id.Contains("dripper") || id.Contains("fusion")
                    || id.Contains("hammer") || id.Contains("shovel") || id.Contains("peace")
                    || id.Contains("girya") || id.Contains("pipe") || id.Contains("dream");
            }).ToList();
            if (keyRelics.Count > 0)
                sb.AppendLine($"[关键遗物] {string.Join(", ", keyRelics.Select(r => r.Id.Entry))} (可能影响休息站选择)");
        }

        sb.AppendLine();
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var title = SafeLocText(option.Title);
            var desc = SafeLocText(option.Description);
            // Truncate description to keep prompt compact
            desc = PromptTextHelper.Truncate(desc, 60);
            var enabledTag = option.IsEnabled ? "" : " [不可用]";
            sb.AppendLine($"{i}. {title}{enabledTag} — {desc}");
        }

        var smithOption = options.OfType<SmithRestSiteOption>().FirstOrDefault();
        if (smithOption?.IsEnabled == true)
        {
            var upgradableCards = aiPlayer.Deck.Cards
                .Where(c => c.IsUpgradable)
                .ToList();

            sb.AppendLine();
            sb.AppendLine($"如果选择 SMITH，本次需要从以下可升级卡牌中选择 1~{smithOption.SmithCount} 张，并返回对应索引：");
            for (int i = 0; i < upgradableCards.Count; i++)
            {
                var card = upgradableCards[i];
                var cost = card.EnergyCost?.Canonical ?? 0;
                var brief = GetCardOneLiner(card, 60);
                sb.AppendLine($"- {i}. {card.Title}({cost}费) {brief}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Resolve a LocString for prompts, preferring formatted text with concrete values.</summary>
    private static string SafeLocText(LocString? loc)
    {
        return PromptTextHelper.ResolveLocString(loc);
    }

    public string BuildShopContext(Player aiPlayer, List<ShopDecisionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[商店]");
        sb.Append(GetHpStatusLabel(aiPlayer));
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        for (int i = 0; i < entries.Count; i++)
        {
            sb.AppendLine($"{i}. {entries[i].DisplayText}");
        }
        sb.AppendLine("（返回空 choices 数组表示离开商店）");

        var cardKnowledge = _query.FormatCardKnowledge(entries.Where(e => e.Card != null).Select(e => e.Card!), maxCount: 8);
        if (!string.IsNullOrWhiteSpace(cardKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选卡牌资料]");
            sb.AppendLine(cardKnowledge);
        }

        var relicKnowledge = _query.FormatRelicKnowledge(
            entries.Where(e => e.Relic != null).Select(e => e.Relic!)
                .Concat(entries.SelectMany(e => e.ModifyingRelics)),
            maxCount: 8);
        if (!string.IsNullOrWhiteSpace(relicKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选遗物资料]");
            sb.AppendLine(relicKnowledge);
        }

        var potionKnowledge = _query.FormatPotionKnowledge(entries.Where(e => e.Potion != null).Select(e => e.Potion!), maxCount: 4);
        if (!string.IsNullOrWhiteSpace(potionKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选药水资料]");
            sb.AppendLine(potionKnowledge);
        }

        return sb.ToString();
    }

    public string BuildEventContext(Player aiPlayer, List<string> optionDescs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[事件决策]");
        sb.Append(GetHpStatusLabel(aiPlayer));
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        sb.AppendLine("事件选项：");
        for (int i = 0; i < optionDescs.Count; i++)
            sb.AppendLine($"{i}. {PromptTextHelper.CleanText(optionDescs[i])}");

        var enrichment = _query.FormatMentionKnowledge(optionDescs, maxCount: 8);
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    public string BuildHextechRuneContext(Player aiPlayer, List<RuneOptionInfo> options, MonsterHexInfo? monsterHex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[海克斯符文选择]");

        AppendPlayerBrief(sb, aiPlayer);

        var strategy = BuildRunStrategyLabel(aiPlayer);
        if (!string.IsNullOrWhiteSpace(strategy))
            sb.AppendLine($"构筑策略: {strategy}");

        var relics = aiPlayer.Relics;
        if (relics != null && relics.Any())
        {
            var keyRelics = relics
                .Where(r =>
                {
                    var id = r.Id.Entry.ToLowerInvariant();
                    return id.Contains("strength") || id.Contains("dexterity")
                        || id.Contains("energy") || id.Contains("draw")
                        || id.Contains("block") || id.Contains("damage")
                        || id.Contains("vulnerable") || id.Contains("weak")
                        || id.Contains("poison") || id.Contains("orb");
                })
                .Take(5)
                .Select(r => r.Id.Entry)
                .ToList();
            if (keyRelics.Count > 0)
                sb.AppendLine($"关键遗物: {string.Join(", ", keyRelics)}");
        }

        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
                sb.AppendLine($"当前: 第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层");
        }
        catch { }

        sb.AppendLine();

        if (monsterHex != null && !monsterHex.IsRemoved)
        {
            sb.AppendLine($"[敌人Hex] {monsterHex.Title}");
            if (!string.IsNullOrWhiteSpace(monsterHex.Description))
                sb.AppendLine($"  效果: {monsterHex.Description}");
        }
        else
        {
            sb.AppendLine("[敌人Hex] 已移除或无");
        }
        sb.AppendLine();

        sb.AppendLine("符文选项（三选一）：");
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            var desc = PromptTextHelper.Truncate(opt.Description, 120);
            sb.AppendLine($"{i}. {opt.Name}");
            if (!string.IsNullOrWhiteSpace(desc))
                sb.AppendLine($"   {desc}");
        }

        var allNames = string.Join(" ", options.Select(o => o.Name));
        var enrichment = _query.EnrichContext(allNames);
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.Append("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    /// <summary>Compact one-line player state for non-combat contexts.</summary>
    private void AppendPlayerBrief(StringBuilder sb, Player p)
    {
        var c = p.Creature;
        var deck = p.Deck?.Cards;
        var hpPct = c.MaxHp > 0 ? (c.CurrentHp * 100 / c.MaxHp) : 0;
        sb.Append($"{GetCharacterName(p)} | HP:{c.CurrentHp}/{c.MaxHp}({hpPct}%) | Gold:{p.Gold}");
        if (deck != null)
        {
            var basicCount = deck.Count(c => c.Rarity == CardRarity.Basic);
            sb.Append($" | 牌组:{deck.Count}张");
        }
        sb.AppendLine();
    }

    /// <summary>Returns a Chinese HP status label for the given player.</summary>
    public static string GetHpStatusLabel(Player p)
    {
        var c = p.Creature;
        if (c == null || c.MaxHp <= 0) return "";
        var pct = c.CurrentHp * 100 / c.MaxHp;
        return pct switch
        {
            <= 20 => $"【血量危重 {c.CurrentHp}/{c.MaxHp}】 ",
            <= 40 => $"【血量危险 {c.CurrentHp}/{c.MaxHp}】 ",
            <= 60 => $"【血量偏低 {c.CurrentHp}/{c.MaxHp}】 ",
            _ => ""
        };
    }

    public string BuildRunStrategicSummary(Player aiPlayer)
    {
        var runState = aiPlayer.RunState;
        var deck = aiPlayer.Deck?.Cards ?? new List<CardModel>();
        var hpLabel = GetHpStatusLabel(aiPlayer).Trim();
        var strategy = BuildRunStrategyLabel(aiPlayer);
        var stage = runState?.TotalFloor switch
        {
            null => "阶段未知",
            <= 10 => "前期",
            <= 30 => "中期",
            _ => "后期"
        };

        var sb = new StringBuilder();
        if (runState != null)
            sb.AppendLine($"第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层 | {stage} | {hpLabel}".Trim(' ', '|'));

        if (!string.IsNullOrWhiteSpace(strategy))
            sb.AppendLine($"构筑判断: {strategy}");

        if (deck.Count > 0)
        {
            var avgCost = deck.Average(card => card.EnergyCost?.Canonical ?? 0);
            sb.AppendLine($"资源概况: 牌组{deck.Count}张 | 金币{aiPlayer.Gold} | 平均费用{avgCost:F1}");
        }

        var keyCards = deck
            .GroupBy(card => card.Title ?? card.Id.Entry)
            .OrderByDescending(group => ScoreCardForStrategy(group.First()) + group.Count())
            .Take(4)
            .Select(group => group.Key)
            .ToList();
        if (keyCards.Count > 0)
            sb.AppendLine($"关键牌线索: {string.Join(" / ", keyCards)}");

        return sb.ToString().TrimEnd();
    }

    public string BuildRunStrategyLabel(Player aiPlayer)
    {
        var scores = AnalyzeDeckStrategy(aiPlayer);
        if (scores.Count == 0)
            return "方向尚未成型";

        var top = scores.OrderByDescending(kvp => kvp.Value).Take(2).Where(kvp => kvp.Value > 0).ToList();
        if (top.Count == 0)
            return "方向尚未成型";

        return string.Join(" + ", top.Select(kvp => kvp.Key));
    }

    private Dictionary<string, int> AnalyzeDeckStrategy(Player aiPlayer)
    {
        var scores = new Dictionary<string, int>
        {
            ["叠甲防御"] = 0,
            ["力量爆发"] = 0,
            ["消耗循环"] = 0,
            ["抽牌运转"] = 0,
            ["易伤压制"] = 0,
            ["中毒持续"] = 0
        };

        foreach (var card in aiPlayer.Deck?.Cards ?? Enumerable.Empty<CardModel>())
        {
            var text = $"{card.Title} {PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry)}";
            AddIfContains(text, scores, "叠甲防御", "格挡", "护甲", "壁垒", "防御");
            AddIfContains(text, scores, "力量爆发", "力量", "伤害", "攻击");
            AddIfContains(text, scores, "消耗循环", "消耗", "耗尽", "烧毁");
            AddIfContains(text, scores, "抽牌运转", "抽", "检索", "弃");
            AddIfContains(text, scores, "易伤压制", "易伤", "破甲");
            AddIfContains(text, scores, "中毒持续", "中毒", "毒");
        }

        return scores;
    }

    private static void AddIfContains(string text, Dictionary<string, int> scores, string key, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                scores[key]++;
        }
    }

    private int ScoreCardForStrategy(CardModel card)
    {
        var text = $"{card.Title} {PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry)}";
        var score = 0;
        if (text.Contains("格挡")) score += 2;
        if (text.Contains("力量")) score += 2;
        if (text.Contains("消耗")) score += 2;
        if (text.Contains("易伤")) score += 2;
        if (text.Contains("抽")) score += 1;
        return score;
    }

    public string GetCardOneLiner(CardModel card, int maxLen = 80)
    {
        var desc = PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry);
        desc = PromptTextHelper.CleanText(desc);
        var periodIdx = desc.IndexOf('.');
        var oneLiner = periodIdx > 0 ? desc[..(periodIdx + 1)] : desc;
        return PromptTextHelper.Truncate(oneLiner, maxLen);
    }

    /// <summary>获取玩家的显示名称，优先使用平台名称，回退到"玩家X"</summary>
    private static string GetPlayerDisplayName(Player player)
    {
        try
        {
            var platform = RunManager.Instance?.NetService?.Platform;
            if (platform.HasValue)
            {
                var name = PlatformUtil.GetPlayerName(platform.Value, player.NetId);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }

        // 回退：使用 NetId 后4位生成名称
        return $"玩家{(int)(player.NetId & 0xFFFF)}";
    }
}
