using System.Text;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Personality;

public class PersonalityPromptBuilder
{
    private readonly PersonalityManager _personalityManager;

    public string ActivePersonalityName =>
        _personalityManager.Active?.Name ?? AiLoc.T("chat.ai_default_name", "AI Teammate");

    public PersonalityPromptBuilder(PersonalityManager personalityManager)
    {
        _personalityManager = personalityManager;
    }

    public string BuildSystemPrompt(bool requireJson = false, bool includeTeamPrinciples = false, bool chatMode = false)
    {
        var profile = _personalityManager.Active;
        if (profile == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine(AiLoc.T("prompt.system.intro",
            "You are the AI teammate “{Name}” in a multiplayer run. Always stay in character.",
            ("Name", AiNames.DisplayName)));
        sb.AppendLine(AiLoc.T("prompt.system.backstory", "Backstory: {Text}", ("Text", Normalize(profile.Backstory, 180))));
        sb.AppendLine(AiLoc.T("prompt.system.combat_style", "Combat style: {Text}", ("Text", Normalize(profile.CombatPhilosophy, 180))));
        sb.AppendLine(AiLoc.T("prompt.system.communication_style", "Communication style: {Text}",
            ("Text", Normalize(profile.CommunicationStyle, chatMode ? 140 : 180))));
        sb.AppendLine(AiLoc.T("prompt.system.principles", "Decision principles: {Text}", ("Text", Normalize(profile.DecisionPrinciples, 180))));

        if (includeTeamPrinciples)
            sb.AppendLine(AiLoc.T("prompt.system.team",
                "Teamwork: check teammates and synergy first, protect low-HP teammates, don't steal kills, and consult [reference material] before deciding."));

        sb.AppendLine(requireJson
            ? AiLoc.T("prompt.system.output_json", "Output requirement: return valid JSON only; output nothing else.")
            : AiLoc.T("prompt.system.output_text", "Output requirement: English, concise, like a real player; avoid generic analysis tone."));
        sb.AppendLine(AiLoc.T("prompt.system.output_language",
            "Output language: all player-facing text (chat, suggestions, thinking summaries) must be in English."));

        return sb.ToString();
    }

    /// <summary>
    /// System prompt for a run an external agent (dsh) drives: that agent brings its own persona, so
    /// the mod must not layer its personality profile on top of it. 外接 Agent 接管时不再注入人格设定。
    /// </summary>
    public static string BuildExternalAgentSystemPrompt(bool requireJson, bool includeTeamPrinciples)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AiLoc.T("prompt.system.external_agent",
            "An external agent drives this run and defines the character's persona, voice and tactics; this mod injects no personality."));

        if (includeTeamPrinciples)
            sb.AppendLine(AiLoc.T("prompt.system.team",
                "Teamwork: check teammates and synergy first, protect low-HP teammates, don't steal kills, and consult [reference material] before deciding."));

        sb.AppendLine(requireJson
            ? AiLoc.T("prompt.system.output_json", "Output requirement: return valid JSON only; output nothing else.")
            : AiLoc.T("prompt.system.output_text", "Output requirement: English, concise, like a real player; avoid generic analysis tone."));
        sb.AppendLine(AiLoc.T("prompt.system.output_language",
            "Output language: all player-facing text (chat, suggestions, thinking summaries) must be in English."));

        return sb.ToString();
    }

    public string AppendTeamPrinciples(string systemPrompt)
    {
        var sb = new StringBuilder(systemPrompt);
        sb.AppendLine();
        sb.AppendLine(AiLoc.T("prompt.system.team_header", "## Teamwork principles"));
        sb.AppendLine(AiLoc.T("prompt.system.team.1", "1. Always consider teammates' hands and state; coordinate with them."));
        sb.AppendLine(AiLoc.T("prompt.system.team.2", "2. Protect the lowest-HP teammate first."));
        sb.AppendLine(AiLoc.T("prompt.system.team.3", "3. Don't steal a teammate's kill target; create opportunities for them."));
        sb.AppendLine(AiLoc.T("prompt.system.team.4", "4. Share resources; give potions to whoever needs them most."));
        sb.AppendLine(AiLoc.T("prompt.system.team.5", "5. Chat in character like a real player; keep it under 20 words."));
        sb.AppendLine(AiLoc.T("prompt.system.team.6", "6. If the prompt includes [reference material], use it when deciding."));
        sb.AppendLine(AiLoc.T("prompt.system.team.7", "7. [Important] Your answer must be valid JSON with nothing outside it."));
        return sb.ToString();
    }

    public string BuildMemoryContext(List<MemoryEntry> memories, string selfReflection)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(selfReflection))
        {
            sb.AppendLine(AiLoc.T("prompt.memory.self", "[Self-awareness]"));
            sb.AppendLine(Normalize(selfReflection, 180));
        }

        if (memories.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(AiLoc.T("prompt.memory.long", "[Long-term memory]"));
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
