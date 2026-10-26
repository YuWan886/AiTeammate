using MegaCrit.Sts2.Core.Entities.Players;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Reserved NetId range for AI players. AI players have no network peer; every sync/patch path
/// uses this range to tell them apart from real clients.
/// </summary>
internal static class AiIdentity
{
    public const ulong NetIdBase = 0xA1AA_A000;
    public const ulong NetIdMax = 0xA1AA_FFFF;

    public static bool IsAi(ulong netId) => netId is >= NetIdBase and <= NetIdMax;

    public static bool IsAi(Player? player) => player != null && IsAi(player.NetId);
}
