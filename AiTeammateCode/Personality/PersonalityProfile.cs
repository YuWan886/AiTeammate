namespace AiTeammate.AiTeammateCode.Personality;

public class PersonalityProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPreset { get; set; }
    public bool IsBuiltIn { get; set; }

    // 叙事身份卡
    public string Backstory { get; set; } = "";
    public string CombatPhilosophy { get; set; } = "";
    public string CommunicationStyle { get; set; } = "";
    public string DecisionPrinciples { get; set; } = "";

    // 成长日志
    public string GrowthLog { get; set; } = "";
    public bool EnableGrowth { get; set; } = false;
}
