using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Personality;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Run end (victory or party wipe) is the single place where a full-run review makes sense.
/// <c>RunManager.OnEnded</c> is called by both paths: <c>WinRun</c> with <c>isVictory: true</c> and
/// <c>CreatureCmd.Kill</c> when every player is dead with <c>isVictory: false</c>.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
public static class RunReviewPatch
{
    [HarmonyPostfix]
    public static void AfterOnEnded(RunManager __instance, bool isVictory, SerializableRun __result)
    {
        RunReviewService.OnRunEnded(__instance, __result, isVictory);

        // Close the auto-takeover window so the next run starts from a clean invitation.
        AgentTakeoverInvite.OnRunEnded();
    }
}
