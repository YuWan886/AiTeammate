using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Personality;

/// <summary>
/// Post-run review: when a run ends (victory or death) the AI looks back at the whole run and saves
/// one <see cref="MemoryCategory.RunSummary"/> memory, so the next run starts with a concrete lesson
/// instead of the old generic "combat won" notes. 一局结束后复盘胜负原因并写入长期记忆。
///
/// Called from <c>RunReviewPatch</c> on <c>RunManager.OnEnded</c>, which is the single game path for
/// both the final victory and the party wipe. The summary is built synchronously on the main thread
/// (it reads localized titles), the request itself runs on the thread pool like every other backend
/// call.
/// </summary>
internal static class RunReviewService
{
    /// <summary>Guards against the second OnEnded call the victory path produces (WinRun kills the party).</summary>
    private static readonly object Gate = new();
    private static string _lastReviewedRun = "";

    public static void OnRunEnded(RunManager runManager, SerializableRun run, bool isVictory)
    {
        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized || manager.AiPlayer == null)
                return;

            // Abandoning a run kills the party too (OnEnded(false)), but there is no gameplay lesson
            // in it. 主动放弃不算失败复盘。
            if (runManager.IsAbandoned)
                return;

            // dsh defines its own persona and the mod's memories are not injected into its prompts,
            // so writing a review it will never read only burns tokens. dsh 接管时人格记忆不参与提示词。
            if (AgentDecisionBroker.Enabled)
                return;

            var settings = AiSettingsStore.Current;
            if (!settings.EnablePersonalityGrowth || !settings.EnableLongTermMemory)
                return;

            var profile = manager.PersonalityManager.Active;
            if (profile == null)
                return;

            var runKey = BuildRunKey(run);
            lock (Gate)
            {
                if (_lastReviewedRun == runKey)
                    return;
                _lastReviewedRun = runKey;
            }

            var summary = BuildSummary(runManager, run, isVictory, manager.AiPlayer);
            var systemPrompt = new PersonalityPromptBuilder(manager.PersonalityManager)
                .BuildSystemPrompt(requireJson: true);

            _ = GenerateReviewAsync(manager, profile.Id, systemPrompt, summary, isVictory);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunReview] 复盘启动失败: {ex.Message}");
        }
    }

    private static string BuildRunKey(SerializableRun run)
    {
        // StartTime is set once per run and survives the extra OnEnded(false) call after a victory.
        var seed = run.SerializableRng?.Seed ?? "";
        return $"{run.StartTime}|{seed}";
    }

    // ---- Summary ---------------------------------------------------------------------------

    private static string BuildSummary(RunManager runManager, SerializableRun run, bool isVictory, Player player)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【结果】{(isVictory ? "胜利：击败了最终 Boss" : "失败：阵亡")}");
        sb.AppendLine($"【角色】{LocTitle(player.Character.Title, player.Character.Id.Entry)} · 进阶 {run.Ascension}");

        var state = runManager.DebugOnlyGetState();
        if (state != null)
        {
            var actCount = Math.Max(1, state.Acts.Count);
            var act = Math.Clamp(state.CurrentActIndex + 1, 1, actCount);
            sb.AppendLine($"【进度】第 {act}/{actCount} 幕 · 本幕第 {state.ActFloor} 层 · 总计 {state.TotalFloor} 层");
            sb.AppendLine($"【结局地点】{DescribeRoom(state.CurrentRoom)}");
        }

        sb.AppendLine($"【时长】{FormatDuration(run.RunTime)} · 种子 {run.SerializableRng?.Seed ?? "?"}");

        var potions = player.Potions.Select(p => LocTitle(p.Title, p.Id.Entry)).ToList();
        sb.AppendLine($"【最终状态】生命 {player.Creature.CurrentHp}/{player.Creature.MaxHp} · 金币 {player.Gold}"
                      + $" · 卡组 {player.Deck.Cards.Count} 张 · 遗物 {player.Relics.Count} 件"
                      + $" · 药水 {potions.Count} 瓶");

        var deck = player.Deck.Cards
            .GroupBy(CardLabel)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key)
            .ToList();
        sb.AppendLine($"【卡组】{JoinCapped(deck, 40)}");

        var relics = player.Relics.Select(r => LocTitle(r.Title, r.Id.Entry)).ToList();
        if (relics.Count > 0)
            sb.AppendLine($"【遗物】{JoinCapped(relics, 30)}");
        if (potions.Count > 0)
            sb.AppendLine($"【药水】{string.Join("、", potions)}");

        if (state != null)
        {
            var history = state.MapPointHistory.SelectMany(a => a).TakeLast(10).ToList();
            if (history.Count > 0)
            {
                sb.AppendLine("【最近路线】");
                foreach (var entry in history)
                    sb.AppendLine("- " + DescribeHistoryEntry(entry));
            }
        }

        return sb.ToString();
    }

    private static string CardLabel(CardModel card)
    {
        string title;
        try
        {
            title = card.Title;
        }
        catch
        {
            title = "";
        }

        if (string.IsNullOrWhiteSpace(title))
            title = card.Id.Entry;
        return card.IsUpgraded ? title + "+" : title;
    }

    private static string JoinCapped(IReadOnlyList<string> items, int cap)
    {
        if (items.Count == 0)
            return "（无）";
        if (items.Count <= cap)
            return string.Join("、", items);
        return string.Join("、", items.Take(cap)) + $"…（共 {items.Count} 项）";
    }

    private static string DescribeHistoryEntry(MapPointHistoryEntry entry)
    {
        var room = entry.Rooms.LastOrDefault();
        if (room == null)
            return RoomTypeLabel(entry.MapPointType.ToString());

        var label = RoomTypeLabel(room.RoomType);
        var name = room.ModelId is { } id ? ModelLabel(room.RoomType, id) : "";
        var turns = room.TurnsTaken > 0 ? $" · {room.TurnsTaken} 回合" : "";
        return string.IsNullOrEmpty(name) ? label + turns : $"{label} - {name}{turns}";
    }

    private static string RoomTypeLabel(string mapPointType)
    {
        return mapPointType switch
        {
            "Monster" => "小怪",
            "Elite" => "精英",
            "Boss" => "Boss",
            "Treasure" => "宝箱",
            "Shop" => "商店",
            "Event" => "事件",
            "RestSite" => "篝火",
            _ => mapPointType
        };
    }

    private static string RoomTypeLabel(RoomType type) => type switch
    {
        RoomType.Monster => "小怪",
        RoomType.Elite => "精英",
        RoomType.Boss => "Boss",
        RoomType.Treasure => "宝箱",
        RoomType.Shop => "商店",
        RoomType.Event => "事件",
        RoomType.RestSite => "篝火",
        RoomType.Map => "地图",
        _ => "未知"
    };

    private static string ModelLabel(RoomType type, ModelId id)
    {
        try
        {
            if (type is RoomType.Monster or RoomType.Elite or RoomType.Boss)
            {
                var encounter = ModelDb.GetByIdOrNull<EncounterModel>(id);
                if (encounter != null)
                    return LocTitle(encounter.Title, id.Entry);
            }
            else if (type == RoomType.Event)
            {
                var eventModel = ModelDb.GetByIdOrNull<EventModel>(id);
                if (eventModel != null)
                    return LocTitle(eventModel.Title, id.Entry);
            }
        }
        catch
        {
            // ModelDb may not be ready in a replay/test session; the raw id is still useful.
        }

        return id.Entry;
    }

    private static string DescribeRoom(AbstractRoom? room)
    {
        if (room == null)
            return "未知";

        try
        {
            switch (room)
            {
                case CombatRoom combatRoom:
                {
                    var encounter = LocTitle(combatRoom.Encounter.Title, combatRoom.Encounter.Id.Entry);
                    var enemies = combatRoom.Enemies
                        .Where(e => e.IsAlive)
                        .Take(5)
                        .Select(e => $"{e.Name}({e.CurrentHp}/{e.MaxHp})")
                        .ToList();
                    var alive = enemies.Count > 0 ? "，场上敌人：" + string.Join("、", enemies) : "";
                    return $"战斗 - {encounter}{alive}";
                }
                case EventRoom eventRoom:
                    return "事件 - " + LocTitle(eventRoom.CanonicalEvent.Title, eventRoom.CanonicalEvent.Id.Entry);
                case MerchantRoom:
                    return "商店";
                case RestSiteRoom:
                    return "篝火";
                case TreasureRoom:
                    return "宝箱";
                case MapRoom:
                    return "地图";
                default:
                    return RoomTypeLabel(room.RoomType)
                           + (room.ModelId is { } id ? " - " + id.Entry : "");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunReview] 描述结局房间失败: {ex.Message}");
            return RoomTypeLabel(room.RoomType);
        }
    }

    private static string LocTitle(LocString? loc, string fallback)
    {
        try
        {
            return PromptTextHelper.ResolveLocString(loc, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
            return "未知";

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{span.Minutes} 分 {span.Seconds} 秒";
    }

    // ---- Request ---------------------------------------------------------------------------

    private static async Task GenerateReviewAsync(
        AiTeammateManager manager, string profileId, string systemPrompt, string summary, bool isVictory)
    {
        try
        {
            var request = new AiRequest
            {
                SystemPrompt = systemPrompt,
                UserPrompt = BuildReviewPrompt(summary, isVictory),
                Type = AiRequestType.RunReview,
                MaxTokens = 1024,
                Temperature = 0.5f,
                EnableJsonMode = true
            };

            var response = await manager.BackendManager.SendAsync(request, CancellationToken.None);
            if (!response.IsSuccess)
            {
                MainFile.Logger.Warn($"[RunReview] 复盘请求失败: {response.ErrorMessage}");
                manager.AiLog.LogError("RUN_REVIEW", response.ErrorMessage);
                return;
            }

            var (content, importance, trigger) = ParseReview(response.Content, isVictory);
            if (string.IsNullOrWhiteSpace(content))
            {
                MainFile.Logger.Warn("[RunReview] 复盘回复里没有可用内容，已跳过");
                return;
            }

            new MemoryManager().AddEntry(profileId, new MemoryEntry
            {
                Category = MemoryCategory.RunSummary,
                Importance = importance,
                Content = content,
                TriggerContext = trigger,
                Timestamp = DateTime.UtcNow
            });

            manager.AiLog.Log("MEMORY", $"[复盘] {content}");
            MainFile.Logger.Info($"[RunReview] 已写入复盘记忆: {content}");

            // A run summary is a good moment to re-derive the self-reflection as well; the call is
            // still threshold-gated, so this only fires once the memory file is large enough.
            manager.CheckAndTriggerSelfReflection(profileId);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[RunReview] 复盘失败: {ex.Message}");
        }
    }

    private static string BuildReviewPrompt(string summary, bool isVictory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你刚刚打完一局《杀戮尖塔 2》。请复盘这一局，写一条以后会长期记住的经验。");
        sb.AppendLine();
        sb.AppendLine("【本局摘要】");
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine(isVictory
            ? "要求：说明获胜的关键（构筑、遗物、路线或战斗决策），以及下一局可以复用的具体做法。"
            : "要求：找出失败的关键原因（构筑短板、路线选择、资源管理或战斗决策），并给出下一局可执行的改进。");
        sb.AppendLine("只根据摘要中能看到的信息推断，不要编造没有发生的战斗、卡牌或遗物。");
        sb.AppendLine("使用当前游戏语言，90 字以内，具体、可执行，不要空泛鼓励。");
        sb.AppendLine();
        sb.AppendLine("以 JSON 返回："
                      + "{\"should_remember\": true, \"content\": \"复盘与经验\", "
                      + "\"importance\": 1-10, \"trigger_context\": \"什么情况下该回忆这条\"}");
        return sb.ToString();
    }

    private static (string Content, int Importance, string Trigger) ParseReview(string raw, bool isVictory)
    {
        try
        {
            using var doc = JsonDocument.Parse(ResponseParser.ExtractJson(raw));
            var root = doc.RootElement;

            var content = root.TryGetProperty("content", out var contentElement)
                          && contentElement.ValueKind == JsonValueKind.String
                ? (contentElement.GetString() ?? "").Trim()
                : "";

            var importance = root.TryGetProperty("importance", out var importanceElement)
                             && importanceElement.ValueKind == JsonValueKind.Number
                             && importanceElement.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, 1, 10)
                : 7;

            var trigger = root.TryGetProperty("trigger_context", out var triggerElement)
                          && triggerElement.ValueKind == JsonValueKind.String
                ? (triggerElement.GetString() ?? "").Trim()
                : "";

            if (string.IsNullOrWhiteSpace(trigger))
                trigger = isVictory ? "需要借鉴上一局胜利经验时" : "需要避免上一局失败原因时";

            return (content, importance, trigger);
        }
        catch
        {
            return ("", 7, "");
        }
    }
}
