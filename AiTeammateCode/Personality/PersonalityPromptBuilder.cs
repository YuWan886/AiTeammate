using System.Text;

namespace AiTeammate.AiTeammateCode.Personality;

public class PersonalityPromptBuilder
{
    private readonly PersonalityManager _personalityManager;

    public string ActivePersonalityName => _personalityManager.Active?.Name ?? "AI 队友";

    public PersonalityPromptBuilder(PersonalityManager personalityManager)
    {
        _personalityManager = personalityManager;
    }

    public string BuildSystemPrompt(bool requireJson = false, bool includeTeamPrinciples = false, bool chatMode = false)
    {
        var profile = _personalityManager.Active;
        if (profile == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"你是多人模式中的 AI 队友玩家“{profile.Name}”，始终保持该人格。");
        sb.AppendLine($"背景: {Normalize(profile.Backstory, 180)}");
        sb.AppendLine($"战斗风格: {Normalize(profile.CombatPhilosophy, 180)}");
        sb.AppendLine($"交流风格: {Normalize(profile.CommunicationStyle, chatMode ? 140 : 180)}");
        sb.AppendLine($"决策原则: {Normalize(profile.DecisionPrinciples, 180)}");

        if (includeTeamPrinciples)
            sb.AppendLine("协作要求: 先看队友状态和协同机会，优先保护低血队友，不抢斩杀，参考[相关资料]后再决定。");

        sb.AppendLine(requireJson
            ? "输出要求: 只返回合法 JSON，不要输出 JSON 之外的内容。"
            : "输出要求: 中文、简短、像真实玩家，不要用空泛分析腔。");

        return sb.ToString();
    }

    public string AppendTeamPrinciples(string systemPrompt)
    {
        var sb = new StringBuilder(systemPrompt);
        sb.AppendLine();
        sb.AppendLine("## 团队协作原则");
        sb.AppendLine("1. 始终考虑队友的手牌和状态，协同作战。");
        sb.AppendLine("2. 优先保护血量最低的队友。");
        sb.AppendLine("3. 不抢队友的斩杀目标，帮队友创造机会。");
        sb.AppendLine("4. 共享资源，药水优先给更需要的人。");
        sb.AppendLine("5. 聊天时直接用角色语气说话像一个真实的游戏玩家。最多20字。");
        sb.AppendLine("6. 若提示中有[相关资料]，参考其中的相关描述做决策。");
        sb.AppendLine("7. 【重要】你的回答必须是合法 JSON 格式，不要输出任何 JSON 之外的内容。");
        return sb.ToString();
    }

    public string InjectMemories(string systemPrompt, List<MemoryEntry> memories)
    {
        if (memories.Count == 0) return systemPrompt;

        var sb = new StringBuilder(systemPrompt);
        sb.AppendLine();
        sb.AppendLine("## 你的长期记忆");
        foreach (var mem in memories)
            sb.AppendLine($"- (重要度{mem.Importance}) {mem.Content}");

        return sb.ToString();
    }

    public string BuildMemoryContext(List<MemoryEntry> memories, string selfReflection)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(selfReflection))
        {
            sb.AppendLine("[自我认知]");
            sb.AppendLine(Normalize(selfReflection, 180));
        }

        if (memories.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("[长期记忆]");
            foreach (var mem in memories.Take(3))
                sb.AppendLine($"- {Normalize(mem.Content, 80)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Normalize(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

        if (normalized.Length <= maxChars)
            return normalized;
        return normalized[..maxChars] + "...";
    }
}
