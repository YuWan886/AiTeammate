using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace AiTeammate.AiTeammateCode;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "AiTeammate";
    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll();
    }
}
