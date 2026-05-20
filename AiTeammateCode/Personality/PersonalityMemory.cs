namespace AiTeammate.AiTeammateCode.Personality;

public enum MemoryCategory
{
    CombatLesson,
    PlayerPreference,
    ChatMoment,
    RunSummary
}

public class MemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public MemoryCategory Category { get; set; }
    public int Importance { get; set; } = 5;
    public string Content { get; set; } = "";
    public string TriggerContext { get; set; } = "";
}

public class PersonalityMemory
{
    public string ProfileId { get; set; } = "";
    public List<MemoryEntry> Entries { get; set; } = new();
    public string SelfReflection { get; set; } = "";
}
