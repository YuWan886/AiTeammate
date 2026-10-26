using System.Text;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Context;

public sealed class ConversationContextManager
{
    private readonly object _sync = new();
    private readonly List<string> _recentCombatSummaries = new();

    public void ResetAll()
    {
        lock (_sync)
        {
            _recentCombatSummaries.Clear();
        }
    }

    public void ResetCombat()
    {
        lock (_sync)
        {
            _recentCombatSummaries.Clear();
        }
    }

    public string BuildCombatContext(Player aiPlayer, ContextBuilder contextBuilder)
    {
        // Snapshot the shared history under the lock, then format outside it: building the card /
        // enemy text marshals to the main thread, and holding this lock while waiting on the main
        // thread would stall it on the next RecordCombatDecision call.
        string recentCombat;
        lock (_sync)
        {
            recentCombat = FormatSection("最近关键回合", _recentCombatSummaries, 4);
        }

        var sb = new StringBuilder();
        // [局势理解] 已由 BuildContextPrelude → BuildRunPrelude 提供，此处不再重复
        // [跨回合摘要] 已移除 — 非战斗房间不需要，战斗房间由 [战斗记忆] 提供

        if (!string.IsNullOrWhiteSpace(recentCombat))
        {
            sb.AppendLine("[战斗记忆]");
            sb.AppendLine(recentCombat);
            sb.AppendLine();
        }

        sb.AppendLine("[当前关键状态]");
        sb.AppendLine(contextBuilder.BuildCombatEssentials(aiPlayer));

        return sb.ToString().TrimEnd();
    }

    public string BuildRunPrelude(Player aiPlayer, ContextBuilder contextBuilder)
    {
        var strategic = contextBuilder.BuildRunStrategicSummary(aiPlayer);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(strategic))
        {
            sb.AppendLine("[局势理解]");
            sb.AppendLine(strategic);
        }

        // [最近战斗摘要] 仅在战斗房间有意义，非战斗房间已移除；
        // 战斗房间由 BuildCombatContext 中的 [战斗记忆] 提供。

        return sb.ToString().TrimEnd();
    }

    public void RecordCombatDecision(Player aiPlayer, ContextBuilder contextBuilder, DecisionResult result)
    {
        var summary = BuildCombatDecisionSummary(aiPlayer, contextBuilder, result);
        if (string.IsNullOrWhiteSpace(summary))
            return;

        lock (_sync)
        {
            AppendBounded(_recentCombatSummaries, summary, 6);
        }
    }

    private static string BuildCombatDecisionSummary(Player aiPlayer, ContextBuilder contextBuilder, DecisionResult result)
    {
        var runState = aiPlayer.RunState;
        var floor = runState != null ? $"第{runState.TotalFloor}层" : "未知层";
        var strategy = contextBuilder.BuildRunStrategyLabel(aiPlayer);

        var actions = result.Actions
            .Where(a => a.Type == "play_card" && !string.IsNullOrWhiteSpace(a.CardName))
            .Take(3)
            .Select(a => string.IsNullOrWhiteSpace(a.Target) ? a.CardName : $"{a.CardName}->{a.Target}")
            .ToList();

        if (result.DeferredActions.Count > 0)
        {
            actions.AddRange(result.DeferredActions
                .Where(a => !string.IsNullOrWhiteSpace(a.CardName))
                .Take(2)
                .Select(a => $"{a.CardName}(等待{a.WaitFor})"));
        }

        var summaryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(strategy))
            summaryParts.Add(strategy);
        if (!string.IsNullOrWhiteSpace(result.Thinking))
            summaryParts.Add(TrimInline(result.Thinking, 40));
        if (actions.Count > 0)
            summaryParts.Add($"动作:{string.Join(", ", actions)}");

        return summaryParts.Count == 0
            ? string.Empty
            : $"{floor} | {string.Join(" | ", summaryParts)}";
    }

    private static string FormatSection(string title, IReadOnlyList<string> items, int maxCount)
    {
        if (items.Count == 0)
            return string.Empty;

        var lines = items.TakeLast(maxCount).Select(item => $"- {item}");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendBounded(List<string> target, string value, int maxCount)
    {
        target.Add(value);
        while (target.Count > maxCount)
            target.RemoveAt(0);
    }

    private static string TrimInline(string text, int maxChars)
    {
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        return normalized[..maxChars] + "...";
    }
}
