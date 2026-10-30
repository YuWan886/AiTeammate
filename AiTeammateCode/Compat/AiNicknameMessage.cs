using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Carries the AI teammate's display nickname to the other multiplayer clients. The AI player has
/// no network peer of its own, and the vanilla nameplate reads the platform name for that NetId,
/// so the nickname has to travel as an ordinary mod message.
/// 把 AI 昵称同步给其他联机客户端（AI 没有自己的网络对端）。
/// </summary>
public struct AiNicknameMessage : INetMessage
{
    /// <summary>Nickname to display; an empty string means "fall back to the personality name".</summary>
    public string nickname;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(nickname ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        nickname = reader.ReadString();
    }
}
