namespace AiTeammate.AiTeammateCode.Chat;

public enum ChatMessageType
{
    Normal,
    Suggestion,
    Warning,
    SystemInfo
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SenderName { get; set; } = "";
    public bool IsAi { get; set; }
    public string Content { get; set; } = "";
    public ChatMessageType Type { get; set; }
    public List<HighlightedCard> HighlightedCards { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class HighlightedCard
{
    public string CardInstanceId { get; set; } = "";
    public string BriefReason { get; set; } = "";
}
