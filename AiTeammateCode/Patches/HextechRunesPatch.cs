using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Compatibility with HextechRunes (ARAM: Mayhem).
///
/// That mod parks a <c>PlayerChoiceSynchronizer</c> transaction for every player during act rune
/// selection, forge/relic picks and the act-selection ack. An AiTeammate AI player has no network peer,
/// so those waits never complete and the act transition soft-locks. These patches answer the waits for AI
/// players only; real players keep the stock flow.
///
/// Targets are resolved by name at install time (the Hextech build is a multi-version bundle and its
/// coordinator is <c>internal</c>); a missing member only skips that one patch.
/// </summary>
internal static class HextechPatchTargets
{
    public const string CoordinatorType = "HextechRunes.HextechRuneSelectionCoordinator";
    public const string SyncedRelicChoiceType = "HextechRunes.HextechSyncedRelicChoice";
    public const string RuneGrantHelperType = "HextechRunes.HextechRuneGrantHelper";
    public const string ScreenType = "HextechRunes.HextechRuneSelectionScreen";

    private const BindingFlags StaticFlags =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static MethodInfo? Find(string typeName, string methodName, params Type[] parameters)
    {
        var type = HextechRunesDecisionHandler.FindHextechType(typeName);
        if (type == null) return null;

        if (parameters.Length == 0)
        {
            var matches = type.GetMethods(StaticFlags).Where(m => m.Name == methodName).ToList();
            if (matches.Count == 0) return null;
            return matches.OrderByDescending(m => m.GetParameters().Length).First();
        }

        return type.GetMethod(methodName, StaticFlags, null, parameters, null);
    }

    public static MethodInfo? FindInstance(string typeName, string methodName, params Type[] parameters)
    {
        var type = HextechRunesDecisionHandler.FindHextechType(typeName);
        if (type == null) return null;
        return type.GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, parameters, null);
    }
}

/// <summary>
/// Act rune selection (<c>SelectRuneMultiplayer</c>): for an AI player, schedule a decision and feed the
/// resulting payload into the local synchronizer. The original method keeps waiting and resolves it through
/// Hextech's normal remote branch (journal, seen history, relic grant), so nothing is re-implemented.
/// </summary>
internal static class HextechSelectRunePatch
{
    public static MethodInfo? Target() => HextechPatchTargets.Find(
        HextechPatchTargets.CoordinatorType, "SelectRuneMultiplayer");

    public static bool Prefix(object[] __args)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return true;

        try
        {
            if (__args.Length < 5) return true;

            var selection = __args[1];
            if (selection == null) return true;

            var aiPlayer = ReadMember<Player>(selection, "Player");
            if (aiPlayer == null || !AiIdentity.IsAi(aiPlayer.NetId)) return true;
            if (ReadMember<bool>(selection, "IsLocal")) return true;

            var options = ReadMember<IReadOnlyList<RelicModel>>(selection, "Options");
            var choiceId = ReadMember<uint>(selection, "ChoiceId");
            if (options == null || options.Count == 0) return true;

            var synchronizer = __args[2] as PlayerChoiceSynchronizer;
            int actIndex = __args[3] is int act ? act : -1;
            int choiceOrdinal = __args[4] is int ordinal ? ordinal : -1;

            HextechRunesDecisionHandler.AnswerRuneChoiceForAi(
                aiPlayer, options, actIndex, choiceOrdinal, choiceId, synchronizer);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 符文选择拦截失败: {ex.Message}");
        }

        return true;
    }

    private static T ReadMember<T>(object instance, string name)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(instance) is T value) return value;

        var field = type.GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(instance) is T fieldValue ? fieldValue : default!;
    }
}

/// <summary>
/// Forge / relic-option choices (<c>HextechSyncedRelicChoice.SelectAsync</c>): an AI player picks locally.
/// Every peer computes the same seed-stable pick, so the transaction is skipped instead of waiting for a
/// peer that does not exist.
/// </summary>
internal static class HextechSyncedRelicChoicePatch
{
    public static MethodInfo? Target() => HextechPatchTargets.Find(
        HextechPatchTargets.SyncedRelicChoiceType, "SelectAsync");

    public static bool Prefix(
        Player player,
        IReadOnlyList<RelicModel> options,
        string context,
        bool syncMultiplayerChoice,
        ref Task<RelicModel?> __result)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return true;
        if (player == null || !AiIdentity.IsAi(player.NetId)) return true;
        if (!syncMultiplayerChoice) return true;

        var netType = RunManager.Instance?.NetService?.Type;
        if (netType is null or NetGameType.Singleplayer or NetGameType.None) return true;

        __result = HextechRunesDecisionHandler.SelectRelicForAiAsync(player, options, context);
        return false;
    }
}

/// <summary>
/// Act-selection ack (<c>SynchronizeActSelectionApplied</c>): it waits for an "applied" message from every
/// player. The AI applies its choice locally, so treat its ack as already delivered.
/// </summary>
internal static class HextechActSelectionAppliedPatch
{
    public static MethodInfo? Target()
    {
        var type = HextechRunesDecisionHandler.FindHextechType(HextechPatchTargets.CoordinatorType);
        return type?.GetMethod("WaitForRemoteActSelectionApplied",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    public static bool Prefix(Player player, ref Task __result)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return true;
        if (player == null || !AiIdentity.IsAi(player.NetId)) return true;

        __result = Task.CompletedTask;
        MainFile.Logger.Info($"[HextechRunes] 跳过 AI 的幕选择确认等待 player={player.NetId}");
        return false;
    }
}

/// <summary>
/// Act-roll authority (<c>GetActRollAuthorityPlayer</c>): clients wait for the host's act roll. If the
/// returned player is the AI (it can be the first entry in the player list), every client would wait for a
/// payload nobody sends; fall back to the first real player instead.
/// </summary>
internal static class HextechActRollAuthorityPatch
{
    public static MethodInfo? Target() => HextechPatchTargets.Find(
        HextechPatchTargets.CoordinatorType, "GetActRollAuthorityPlayer");

    public static void Postfix(ref Player? __result)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return;
        if (__result == null || !AiIdentity.IsAi(__result.NetId)) return;

        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var replacement = runState?.Players.FirstOrDefault(p => !AiIdentity.IsAi(p.NetId));
            if (replacement != null)
            {
                __result = replacement;
                MainFile.Logger.Info($"[HextechRunes] 幕抽签权威改为非 AI 玩家 {replacement.NetId}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 修正幕抽签权威失败: {ex.Message}");
        }
    }
}

/// <summary>
/// Random rune grants (<c>HextechRuneGrantHelper.TryObtainRandomRunesMultiplayer</c>): letting it return
/// false makes the public caller fall back to its singleplayer path, which picks with Hextech's seed-stable
/// RNG and obtains locally — identical on every client, no peer needed.
/// </summary>
internal static class HextechRandomRuneGrantPatch
{
    public static MethodInfo? Target() => HextechPatchTargets.Find(
        HextechPatchTargets.RuneGrantHelperType, "TryObtainRandomRunesMultiplayer");

    public static bool Prefix(Player player, ref Task<bool> __result)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return true;
        if (player == null || !AiIdentity.IsAi(player.NetId)) return true;

        __result = Task.FromResult(false);
        return false;
    }
}

/// <summary>
/// Solo climb: Hextech shows a modal selection screen and waits for a click. While the AI drives the local
/// player nobody clicks, so the run soft-locks. The handler answers the screen (LLM or stable pick) and
/// leaves it alone when the player paused the AI.
/// </summary>
internal static class HextechSoloScreenPatch
{
    public static MethodInfo? Target() => HextechPatchTargets.FindInstance(
        HextechPatchTargets.ScreenType, "AfterOverlayOpened");

    public static void Postfix(object __instance)
    {
        if (!HextechRunesDecisionHandler.IsAvailable) return;
        if (__instance == null) return;
        if (!SoloRunController.IsActive) return;
        if (AiFlowControl.IsPaused) return;

        HextechRunesDecisionHandler.AutoAnswerSoloScreen(__instance);
    }
}

/// <summary>
/// Installs the HextechRunes patches. Waits for the Hextech assembly when it has not been loaded yet
/// (mod initialization order is not guaranteed), and installs each patch independently.
/// </summary>
public static class HextechRunesPatchInstaller
{
    private static bool _installed;
    private static bool _waitingForAssembly;

    public static void Initialize()
    {
        TryInstall();
        if (_installed || _waitingForAssembly) return;

        _waitingForAssembly = true;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != "HextechRunes") return;

        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        _waitingForAssembly = false;
        MainFile.Logger.Info("[HextechRunes] 检测到 HextechRunes 程序集加载，安装集成补丁");
        TryInstall();
    }

    private static void TryInstall()
    {
        if (_installed) return;
        if (!HextechRunesDecisionHandler.IsAvailable) return;

        var harmony = new HarmonyLib.Harmony(MainFile.ModId + ".HextechRunes");
        int installed = 0;

        installed += TryPatch(harmony, "符文三选一", HextechSelectRunePatch.Target(),
            typeof(HextechSelectRunePatch), "Prefix", isPrefix: true);
        installed += TryPatch(harmony, "锻造/遗物选择", HextechSyncedRelicChoicePatch.Target(),
            typeof(HextechSyncedRelicChoicePatch), "Prefix", isPrefix: true);
        installed += TryPatch(harmony, "幕选择确认", HextechActSelectionAppliedPatch.Target(),
            typeof(HextechActSelectionAppliedPatch), "Prefix", isPrefix: true);
        installed += TryPatch(harmony, "幕抽签权威", HextechActRollAuthorityPatch.Target(),
            typeof(HextechActRollAuthorityPatch), "Postfix", isPrefix: false);
        installed += TryPatch(harmony, "随机符文发放", HextechRandomRuneGrantPatch.Target(),
            typeof(HextechRandomRuneGrantPatch), "Prefix", isPrefix: true);
        installed += TryPatch(harmony, "单人界面自动选择", HextechSoloScreenPatch.Target(),
            typeof(HextechSoloScreenPatch), "Postfix", isPrefix: false);

        _installed = installed > 0;
        MainFile.Logger.Info($"[HextechRunes] 集成补丁安装完成: {installed}/6");
    }

    private static int TryPatch(
        HarmonyLib.Harmony harmony,
        string label,
        MethodInfo? target,
        Type patchType,
        string patchMethodName,
        bool isPrefix)
    {
        if (target == null)
        {
            MainFile.Logger.Info($"[HextechRunes] 跳过「{label}」: 未找到目标方法（海克斯版本不匹配）");
            return 0;
        }

        try
        {
            var method = patchType.GetMethod(patchMethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                MainFile.Logger.Warn($"[HextechRunes] 跳过「{label}」: 找不到补丁方法 {patchType.Name}.{patchMethodName}");
                return 0;
            }

            var harmonyMethod = new HarmonyMethod(method);
            harmony.Patch(target,
                prefix: isPrefix ? harmonyMethod : null,
                postfix: isPrefix ? null : harmonyMethod);

            MainFile.Logger.Info($"[HextechRunes] 已安装「{label}」→ {target.DeclaringType?.Name}.{target.Name}");
            return 1;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 安装「{label}」失败: {ex.Message}");
            return 0;
        }
    }
}
