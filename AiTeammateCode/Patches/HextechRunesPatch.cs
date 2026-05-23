using System.Reflection;
using Godot;
using HarmonyLib;
using AiTeammate.AiTeammateCode.Decision;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Patches;

// ---- Foundation patches: make HextechRunes recognize AiTeammate ----

/// <summary>
/// Makes HextechAiTeammateCompat.IsLoopbackHostSession() return true when
/// AiTeammate has an active AI player. This enables the full screen-based
/// Hextech rune selection flow (host-controlled path) which supports rerolls.
/// </summary>
[HarmonyPatch]
public static class HextechIsLoopbackHostSessionPatch
{
    private static MethodInfo? _targetMethod;

    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    public static MethodBase TargetMethod()
    {
        var type = FindCompatType();
        _targetMethod = type?.GetMethod("IsLoopbackHostSession",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (_targetMethod == null)
            throw new InvalidOperationException("Cannot find IsLoopbackHostSession");
        return _targetMethod;
    }

    public static void Postfix(ref bool __result)
    {
        if (__result) return;
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;
        // Check if there's an AI player anywhere in the current run
        try
        {
            var runState = global::MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            if (runState != null)
            {
                foreach (var p in runState.Players)
                {
                    if (p.NetId >= 0xA1AA_A000 && p.NetId <= 0xA1AA_FFFF)
                    {
                        __result = true;
                        return;
                    }
                }
            }
        }
        catch { }
    }

    private static Type? FindCompatType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechAiTeammateCompat", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }
}

/// <summary>
/// Makes HextechAiTeammateCompat.IsAiPlayer(Player) recognize AI players by
/// their NetId range (0xA1AA_A000 - 0xA1AA_FFFF) instead of relying on
/// AiSessionRegistry which doesn't exist in this AiTeammate setup.
/// </summary>
[HarmonyPatch]
public static class HextechIsAiPlayerPatch
{
    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    public static MethodBase TargetMethod()
    {
        var type = FindCompatType();
        var method = type?.GetMethod("IsAiPlayer",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Player) }, null);
        if (method == null)
            throw new InvalidOperationException("Cannot find IsAiPlayer");
        return method;
    }

    public static void Postfix(Player? player, ref bool __result)
    {
        if (__result) return;
        if (player == null) return;
        if (player.NetId >= 0xA1AA_A000 && player.NetId <= 0xA1AA_FFFF)
            __result = true;
    }

    private static Type? FindCompatType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechAiTeammateCompat", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }
}

/// <summary>
/// Provides host player NetId to HextechRunes so it can order player screens
/// correctly (human first, AI last). Falls back to finding the first non-AI
/// player in the run state.
/// </summary>
[HarmonyPatch]
public static class HextechTryGetHostPlayerIdPatch
{
    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    public static MethodBase TargetMethod()
    {
        var type = FindCompatType();
        var method = type?.GetMethod("TryGetHostPlayerId",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException("Cannot find TryGetHostPlayerId");
        return method;
    }

    public static void Postfix(ref bool __result, ref ulong hostPlayerId)
    {
        if (__result) return;
        try
        {
            var runState = global::MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return;
            foreach (var p in runState.Players)
            {
                // Find the first human player (non-AI NetId)
                if (p.NetId < 0xA1AA_A000 || p.NetId > 0xA1AA_FFFF)
                {
                    hostPlayerId = p.NetId;
                    __result = true;
                    return;
                }
            }
        }
        catch { }
    }

    private static Type? FindCompatType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechAiTeammateCompat", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }
}

/// <summary>
/// Provides AI player display name to HextechRunes for the title override
/// shown on the selection screen ("为XXX选择一个海克斯符文").
/// </summary>
[HarmonyPatch]
public static class HextechGetDisplayNamePatch
{
    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    public static MethodBase TargetMethod()
    {
        var type = FindCompatType();
        var method = type?.GetMethod("GetDisplayName",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Player) }, null);
        if (method == null)
            throw new InvalidOperationException("Cannot find GetDisplayName");
        return method;
    }

    public static void Postfix(Player player, ref string __result)
    {
        // Only override if the original returned a fallback name
        if (string.IsNullOrEmpty(__result) || __result.StartsWith("玩家"))
        {
            var manager = AiTeammateManager.Instance;
            if (manager.IsInitialized && manager.PersonalityManager?.Active != null)
            {
                var personalityName = manager.PersonalityManager.Active.Name;
                if (!string.IsNullOrEmpty(personalityName))
                    __result = personalityName;
            }
        }
    }

    private static Type? FindCompatType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechAiTeammateCompat", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }
}

// ---- Decision patches ----

/// <summary>
/// Intercepts HextechAiTeammateCompat.PickRandomRuneIndex to replace random
/// with LLM-based decision. This handles the multiplayer AI-auto-select path
/// where no selection screen is shown.
/// </summary>
[HarmonyPatch]
public static class HextechPickRandomRunePatch
{
    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    /// <summary>
    /// Dynamically target HextechAiTeammateCompat.PickRandomRuneIndex.
    /// </summary>
    public static MethodBase TargetMethod()
    {
        var type = FindCompatType();
        var method = type?.GetMethod("PickRandomRuneIndex",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Player), typeof(IReadOnlyList<RelicModel>) }, null);
        return method ?? throw new InvalidOperationException("Cannot find PickRandomRuneIndex");
    }

    /// <summary>
    /// Prefix: replace the random pick with LLM-based decision.
    /// Returns false to skip original method.
    /// </summary>
    public static bool Prefix(Player player, IReadOnlyList<RelicModel> options, ref int __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null)
            return true;

        try
        {
            var runeInfos = options.Select((r, i) => new RuneOptionInfo
            {
                Index = i,
                Name = PromptTextHelper.ResolveLocString(r.Title),
                Description = CleanDescription(PromptTextHelper.ResolveLocString(r.DynamicDescription)),
                RelicId = (r.CanonicalInstance?.Id ?? r.Id).Entry
            }).ToList();

            var task = System.Threading.Tasks.Task.Run(async () =>
                await HextechRunesDecisionHandler.DecideRuneWithLlmAsync(manager.AiPlayer!, runeInfos, null));
            __result = task.GetAwaiter().GetResult();
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] LLM决策异常，使用随机选择: {ex.Message}");
            return true;
        }
    }

    private static Type? FindCompatType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechAiTeammateCompat", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }

    private static string CleanDescription(string desc)
    {
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"res://\S+", "");
        return desc.Trim();
    }
}

/// <summary>
/// Intercepts HextechRuneSelectionScreen.AfterOverlayOpened to auto-select
/// runes for AI players in the host-controlled path. This path supports rerolls
/// because the full UI is shown. The screen is immediately hidden to prevent
/// it from flashing in front of the human player.
/// </summary>
[HarmonyPatch]
public static class HextechRuneSelectionScreenAutoPatch
{
    private static Type? _screenType;

    public static bool Prepare() => HextechRunesDecisionHandler.IsAvailable;

    public static MethodBase TargetMethod()
    {
        _screenType = FindScreenType();
        var method = _screenType?.GetMethod("AfterOverlayOpened",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return method ?? throw new InvalidOperationException("Cannot find AfterOverlayOpened");
    }

    /// <summary>
    /// Postfix: after the screen is shown, check if it's for an AI player.
    /// If so, hide the screen, reopen the map (closed by HandleActSelection),
    /// and start the auto-decision process in background.
    /// </summary>
    public static void Postfix(object __instance)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        if (!HextechRunesDecisionHandler.IsScreenForAi(__instance)) return;

        // Prevent AfterOverlayHidden from queueing RestoreAfterMapReopenAsync,
        // which would close our map and re-show the hidden overlay.
        var restoreFlagField = _screenType!.GetField("_restoreAfterMapReopenQueued",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        restoreFlagField?.SetValue(__instance, true);

        // Hide the screen so the player doesn't see rune selection UI.
        if (__instance is CanvasItem canvas)
            canvas.Visible = false;

        // HextechRunes closed the map before starting selections; reopen it so
        // the player sees the map (not blackness) while the LLM decides.
        try
        {
            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            if (map != null && !map.IsOpen)
                map.Open();
        }
        catch { }

        MainFile.Logger.Info("[HextechRunes] 检测到AI海克斯符文选择界面，已隐藏并恢复地图");

        _ = AutoSelectForAiAsync(__instance, manager);
    }

    private static async System.Threading.Tasks.Task AutoSelectForAiAsync(
        object screenInstance, AiTeammateManager manager)
    {
        try
        {
            var aiPlayer = manager.AiPlayer;
            if (aiPlayer == null) return;

            var options = HextechRunesDecisionHandler.ExtractOptions(screenInstance);
            var monsterHex = HextechRunesDecisionHandler.ExtractMonsterHex(screenInstance);

            if (options.Count == 0)
            {
                MainFile.Logger.Warn("[HextechRunes] 无法提取符文选项，跳过自动决策");
                return;
            }

            manager.AiLog.Log("AI_THINK",
                $"[海克斯符文] 开始选择 | act={aiPlayer.RunState?.CurrentActIndex} | 选项={options.Count} | monsterHex={monsterHex?.KindName}");

            var choice = await HextechRunesDecisionHandler.DecideRuneWithLlmAsync(
                aiPlayer, options, monsterHex, screenInstance);

            if (choice >= 0 && choice < options.Count)
            {
                var clicked = await HextechRunesDecisionHandler.ClickRuneCard(
                    screenInstance, choice, options);
                if (!clicked)
                    MainFile.Logger.Warn("[HextechRunes] 自动点击卡牌失败");
            }
            else
            {
                MainFile.Logger.Warn($"[HextechRunes] LLM决策返回无效索引 {choice}");
                await HextechRunesDecisionHandler.ClickRuneCard(screenInstance, 0, options);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[HextechRunes] 自动决策异常: {ex.Message}");
        }
    }

    private static Type? FindScreenType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("HextechRunes.HextechRuneSelectionScreen", throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }
}

/// <summary>
/// Install/detect helpers. Called from AiTeammateManager during initialization.
/// </summary>
public static class HextechRunesPatchInstaller
{
    private static bool _installed;

    public static void TryInstall(Harmony harmony)
    {
        if (_installed) return;
        if (!HextechRunesDecisionHandler.IsAvailable) return;

        try
        {
            // Foundation patches: make HextechRunes recognize AiTeammate
            harmony.Patch(
                HextechIsLoopbackHostSessionPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(HextechIsLoopbackHostSessionPatch), "Postfix"));

            harmony.Patch(
                HextechIsAiPlayerPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(HextechIsAiPlayerPatch), "Postfix"));

            harmony.Patch(
                HextechTryGetHostPlayerIdPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(HextechTryGetHostPlayerIdPatch), "Postfix"));

            harmony.Patch(
                HextechGetDisplayNamePatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(HextechGetDisplayNamePatch), "Postfix"));

            // Decision patches
            harmony.Patch(
                HextechPickRandomRunePatch.TargetMethod(),
                prefix: new HarmonyMethod(typeof(HextechPickRandomRunePatch), "Prefix"));

            harmony.Patch(
                HextechRuneSelectionScreenAutoPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(HextechRuneSelectionScreenAutoPatch), "Postfix"));

            _installed = true;
            MainFile.Logger.Info("[HextechRunes] Harmony patches installed successfully (6 patches)");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] Failed to install patches: {ex.Message}");
        }
    }
}
