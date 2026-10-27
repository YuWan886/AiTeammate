namespace AiTeammate.AiTeammateCode.Live;

/// <summary>What kind of live-room event a <see cref="DanmakuMessage"/> represents.</summary>
public enum DanmakuKind
{
    /// <summary>A normal chat line. 普通弹幕。</summary>
    Chat = 0,

    /// <summary>A paid Super Chat. 醒目留言。</summary>
    SuperChat = 1,

    /// <summary>A gift notification shown in the same stream. 礼物。</summary>
    Gift = 2,

    /// <summary>A local line (for example the AI's own outgoing danmaku). 本地消息。</summary>
    Local = 3
}

/// <summary>One live-room danmaku / event, normalized from Bilibili's websocket payload.</summary>
public sealed class DanmakuMessage
{
    public long Uid { get; init; }
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public string FaceUrl { get; init; } = "";

    /// <summary>0xRRGGBB colour picked by the viewer; 0 means default white. 弹幕颜色。</summary>
    public int Color { get; init; }

    public DanmakuKind Kind { get; init; }

    /// <summary>True when this line was sent by the logged-in account (or generated locally). 自己发送的。</summary>
    public bool IsSelf { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
