using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// The AI player has no platform account, so the vanilla nameplate / tooltips / power text would
/// print the raw fake NetId. Resolve the mod's display name for the reserved AI NetId range instead.
/// Every caller of <see cref="PlatformUtil.GetPlayerName"/> goes through the raw variant and then
/// escapes BBCode there, so patching the raw method gives plain labels and escaped rich text alike.
/// 用昵称覆盖 AI NetId 的玩家名（名牌、提示、能力文本等全部生效）。
/// </summary>
[HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerNameRaw))]
public static class AiPlayerNamePatch
{
    [HarmonyPostfix]
    public static void Postfix(ulong playerId, ref string __result)
    {
        if (!AiIdentity.IsAi(playerId))
            return;

        var name = AiNames.DisplayName;
        if (!string.IsNullOrWhiteSpace(name))
            __result = name;
    }
}
