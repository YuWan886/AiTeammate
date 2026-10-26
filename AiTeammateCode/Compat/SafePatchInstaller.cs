using System.Reflection;
using HarmonyLib;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Installs every <see cref="HarmonyPatch"/> class individually so one stale target (a private
/// method another game version removed) cannot abort the rest of the mod's patches.
/// </summary>
internal static class SafePatchInstaller
{
    public static void InstallAll(Harmony harmony)
    {
        var assembly = typeof(SafePatchInstaller).Assembly;
        var installed = 0;
        var skipped = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<HarmonyPatch>(inherit: false) == null)
                continue;

            try
            {
                harmony.CreateClassProcessor(type).Patch();
                installed++;
            }
            catch (Exception ex)
            {
                skipped++;
                MainFile.Logger.Warn($"[Patch] 跳过 {type.Name}: {Unwrap(ex).Message}");
            }
        }

        MainFile.Logger.Info($"[Patch] 已安装 {installed} 个补丁，跳过 {skipped} 个");
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException or TypeInitializationException
               && exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }
}
