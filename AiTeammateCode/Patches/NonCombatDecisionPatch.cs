using HarmonyLib;
using AiTeammate.AiTeammateCode;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Patches;

/// Resolve event text for AI prompts, preferring formatted text with actual values.
internal static class EventTextHelper
{
    public static string SafeText(this LocString? loc, EventModel? eventModel = null)
    {
        if (loc != null && eventModel != null)
        {
            try
            {
                eventModel.DynamicVars.AddTo(loc);
            }
            catch
            {
                // Best-effort only. Fall back to the existing LocString state if
                // this event's DynamicVars cannot be materialized in the prompt path.
            }
        }

        return PromptTextHelper.ResolveLocString(loc);
    }

    public static string DescribeOption(EventModel eventModel, EventOption option)
    {
        var title = option.Title.SafeText(eventModel);
        var description = option.Description.SafeText(eventModel);
        return $"{title} — {description}";
    }
}

// ---- Log event details when an event begins ----
[HarmonyPatch(typeof(EventSynchronizer), "BeginEvent")]
public static class EventBeginLogPatch
{
    [HarmonyPostfix]
    public static void AfterBeginEvent(EventModel canonicalEvent)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        SoloRunController.TryBind(manager);
        if (manager.AiPlayer == null)
        {
            try { manager.DetectAndBindAiPlayer(); }
            catch (System.Exception ex) { MainFile.Logger.Warn($"AI detect in BeginEvent failed: {ex.Message}"); }
        }
        if (manager.AiPlayer == null) return;

        try
        {
            var eventSync = RunManager.Instance?.EventSynchronizer;
            if (eventSync == null) return;
            var aiEvent = eventSync.GetEventForPlayer(manager.AiPlayer);
            var options = aiEvent.CurrentOptions;
            var optionDescs = string.Join(" | ", options.Select((o, i) =>
                $"{i}. {EventTextHelper.DescribeOption(aiEvent, o)}"));
            manager.AiLog.Log("EVENT", $"进入事件: {canonicalEvent.Id}");
            manager.AiLog.Log("EVENT", $"  选项({options.Count}个): {optionDescs}");
            manager.AiLog.Log("AI_THINK", $"[事件] 进入{canonicalEvent.Id} | {options.Count}个选项: {optionDescs}");
            MainFile.Logger.Info($"[事件] {canonicalEvent.Id}: {options.Count}个选项");

            // Non-shared event: AI decides immediately (each player's event is independent)
            // Solo climb: the AI *is* the local player, so it must drive shared events too.
            if (!eventSync.IsShared || SoloRunController.IsActive)
            {
                if (SoloRunController.IsActive && AiFlowControl.IsPaused)
                {
                    MainFile.Logger.Info("[Solo] event paused by player; will decide on resume");
                    return;
                }

                if (options.Count == 1)
                {
                    EventAutoChoicePatch.AutoResolveSingleOption(manager.AiPlayer, eventSync, reason: "初始页单选自动继续");
                }
                else
                {
                    var optionDescList = options.Select(o => EventTextHelper.DescribeOption(aiEvent, o)).ToList();
                    EventAutoChoicePatch.DecideAndApplyAsync(manager.AiPlayer, eventSync, optionDescList);
                }
            }
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"Event begin log failed: {ex.Message}");
        }
    }
}

// ---- Events: AI decides independently for non-shared events ----
[HarmonyPatch(typeof(EventSynchronizer), "ChooseLocalOption")]
public static class EventAutoChoicePatch
{
    private static int _decisionInFlight;

    public static void AutoResolveSingleOption(Player aiPlayer, EventSynchronizer eventSync, int resumeDepth = 0, string reason = "单选自动继续")
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        var solo = SoloRunController.IsActive;
        if (solo && AiFlowControl.IsPaused)
            return;

        try
        {
            var aiEvent = eventSync.GetEventForPlayer(aiPlayer);
            if (aiEvent.IsFinished) return;

            var options = aiEvent.CurrentOptions;
            if (options == null || options.Count != 1) return;

            var title = options[0].Title.SafeText(aiEvent);
            if (solo)
            {
                // Solo: the AI is the local player — use the same path as the event UI.
                eventSync.ChooseLocalOption(0);
            }
            else
            {
                var method = AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent");
                method?.Invoke(eventSync, new object[] { aiPlayer, 0 });
            }

            MainFile.Logger.Info($"AI auto-resolving single-option event page: {title}");
            manager.AiLog.Log("AI_ACTION", $"事件继续: {title} | {reason}");

            ContinueEventResolution(eventSync, aiPlayer, manager, resumeDepth);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"AI auto-resolve single option failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called from AfterBeginEvent to immediately decide and apply AI's choice
    /// for non-shared events. Each player's event is independent so AI doesn't
    /// need to wait for the human.
    /// </summary>
    /// <param name="resumeDepth">Recursion depth for multi-page event resolution. 0 for initial call.</param>
    public static void DecideAndApplyAsync(Player aiPlayer, EventSynchronizer eventSync, List<string> optionDescs, int resumeDepth = 0)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        // One decision per event at a time; a resume nudge must not stack a second choice.
        if (Interlocked.CompareExchange(ref _decisionInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            var handedOff = false;
            try
            {
                // Solo climb: wait out an interruption before spending an LLM call.
                if (SoloRunController.IsActive)
                    await AiFlowControl.WaitWhilePausedAsync();

                var (choice, reason) = await manager.DecisionService.DecideEventAsync(
                    aiPlayer, optionDescs, CancellationToken.None);

                // Validate against captured option count, clamp if needed
                if (choice < 0 || choice >= optionDescs.Count) choice = 0;

                var finalChoice = choice;
                var finalReason = reason;
                if (!string.IsNullOrWhiteSpace(finalReason))
                    manager.AiLog.Log("AI_THINK", $"[事件] {finalReason}");
                MainFile.Logger.Info($"AI event choice {finalChoice} for {aiPlayer.NetId}: {finalReason}");

                // Apply on main thread to avoid race with event state changes
                Compat.MainThread.Post(() =>
                {
                    try
                    {
                        var aiEvent = eventSync.GetEventForPlayer(aiPlayer);
                        if (aiEvent.IsFinished) return;
                        var options = aiEvent.CurrentOptions;
                        if (options == null || options.Count == 0) return;
                        var safeChoice = finalChoice >= options.Count ? 0 : finalChoice;

                        // Capture title BEFORE calling ChooseOptionForEvent (it may modify the options list)
                        var title = options[safeChoice].Title.SafeText(aiEvent);

                        if (SoloRunController.IsActive)
                        {
                            // Local-player path handles both shared and non-shared events.
                            eventSync.ChooseLocalOption(safeChoice);
                        }
                        else
                        {
                            var method = AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent");
                            method?.Invoke(eventSync, new object[] { aiPlayer, safeChoice });
                        }

                        manager.AiLog.LogEvent("事件选择", $"选择选项 {safeChoice} [{title}] | {finalReason}");
                        manager.AiLog.Log("AI_ACTION", $"事件: 选择选项 {safeChoice} [{title}] | NetId={aiPlayer.NetId}");

                        // Release the in-flight guard before chaining the next page, otherwise the
                        // next DecideAndApplyAsync call would be rejected as a duplicate.
                        Interlocked.Exchange(ref _decisionInFlight, 0);
                        ContinueEventResolution(eventSync, aiPlayer, manager, resumeDepth);
                    }
                    catch (System.Exception ex)
                    {
                        MainFile.Logger.Warn($"Event apply failed: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _decisionInFlight, 0);
                    }
                });
                handedOff = true;
            }
            catch (System.Exception ex)
            {
                MainFile.Logger.Warn($"AI event async decision failed: {ex.Message}");
            }
            finally
            {
                if (!handedOff)
                    Interlocked.Exchange(ref _decisionInFlight, 0);
            }
        });
    }

    [HarmonyPostfix]
    public static void AfterLocalChoice(int index)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Solo: the AI is the local player; this postfix (human choice → AI follows) does not apply.
        if (SoloRunController.IsActive) return;

        var eventSync = RunManager.Instance?.EventSynchronizer;
        if (eventSync == null) return;
        var aiPlayer = manager.AiPlayer!;

        try
        {
            if (!eventSync.IsShared) return; // non-shared handled by DecideAndApplyAsync

            // Shared event: vote same as human for consensus
            var aiEvent = eventSync.GetEventForPlayer(aiPlayer);
            if (aiEvent.IsFinished) return;
            var options = aiEvent.CurrentOptions;
            if (options == null || options.Count == 0) return;
            if (index < 0 || index >= options.Count) return;

            var optionTitle = options[index].Title.SafeText(aiEvent);
            var desc = $"选择选项 {index} [{optionTitle}] | NetId={aiPlayer.NetId} (合作投票)";

            var traverse = Traverse.Create(eventSync);
            var pageIndex = traverse.Field("_pageIndex").GetValue<uint>();
            traverse.Method("PlayerVotedForSharedOptionIndex", aiPlayer, (uint)index, pageIndex).GetValue();

            manager.AiLog.LogEvent("事件选择", desc);
            manager.AiLog.Log("AI_ACTION", $"事件: {desc}");

            ContinueEventResolution(eventSync, aiPlayer, manager, 0);
        }
        catch (System.Exception ex)
        {
            var innerMsg = ex.InnerException?.Message ?? ex.Message;
            MainFile.Logger.Warn($"Event shared choice failed: {innerMsg}");
            manager.AiLog.LogError("事件选择", innerMsg);
        }
    }

    /// <summary>Re-runs an unfinished event decision after the human lets the AI continue (solo).</summary>
    public static void Nudge(Player aiPlayer, EventSynchronizer eventSync)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || !SoloRunController.IsActive || AiFlowControl.IsPaused)
            return;

        try
        {
            var aiEvent = eventSync.GetEventForPlayer(aiPlayer);
            if (aiEvent.IsFinished)
                return;

            var options = aiEvent.CurrentOptions;
            if (options == null || options.Count == 0)
                return;

            if (options.Count == 1)
            {
                AutoResolveSingleOption(aiPlayer, eventSync, 0, "恢复后单选继续");
                return;
            }

            var descs = options.Select(o => EventTextHelper.DescribeOption(aiEvent, o)).ToList();
            DecideAndApplyAsync(aiPlayer, eventSync, descs);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] event nudge failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Continues resolving event pages after an option has been chosen.
    /// - Single-option pages: auto-select and continue immediately (up to 5 in a row).
    /// - Multi-option pages: trigger another async AI decision via DecideAndApplyAsync.
    /// - Finished events or empty options: stop.
    /// - Shared events: do nothing — the human player drives the event, AI follows via AfterLocalChoice.
    /// </summary>
    /// <param name="resumeDepth">Tracks recursion depth for multi-page events. Capped at 10 to prevent infinite loops.</param>
    private static void ContinueEventResolution(EventSynchronizer eventSync, Player aiPlayer, AiTeammateManager manager, int resumeDepth)
    {
        // In multiplayer shared events the AI follows the human player. In solo the AI *is* the
        // local player, so it keeps driving the event itself.
        var solo = SoloRunController.IsActive;
        if (eventSync.IsShared && !solo)
            return;

        const int maxResumeDepth = 10;
        if (resumeDepth >= maxResumeDepth)
        {
            MainFile.Logger.Warn($"Event resolution depth {resumeDepth} exceeded max {maxResumeDepth}, stopping");
            return;
        }

        var chooseMethod = AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent");
        if (chooseMethod == null) return;

        int maxLoops = 5;
        for (int i = 0; i < maxLoops; i++)
        {
            EventModel aiEvent;
            try { aiEvent = eventSync.GetEventForPlayer(aiPlayer); }
            catch { return; }

            if (aiEvent.IsFinished) return;

            var opts = aiEvent.CurrentOptions;
            if (opts == null || opts.Count == 0) return;

            if (opts.Count == 1)
            {
                // Auto-select single option and continue loop
                AutoResolveSingleOption(aiPlayer, eventSync, resumeDepth, "续页单选自动继续");
                // Continue loop to check if next page is also single-option
            }
            else
            {
                // For shared events, do NOT make independent decisions on multi-option pages.
                // The AI follows the human player; AfterLocalChoice will be triggered
                // when the human picks, and the AI votes the same.
                if (eventSync.IsShared && !solo)
                    return;

                // Non-shared event with multiple options: trigger async AI decision for this page.
                // The decision will call back to ContinueEventResolution when done,
                // forming a clean async chain (each step goes through background LLM → main thread).
                var optionDescs = opts.Select(o => EventTextHelper.DescribeOption(aiEvent, o)).ToList();
                MainFile.Logger.Info($"AI triggering decision for multi-option event page: {opts.Count} options (depth={resumeDepth})");
                manager.AiLog.Log("AI_THINK", $"[事件] 进入下一页 | {opts.Count}个选项: {string.Join(" | ", opts.Select((o, idx) => $"{idx}. {EventTextHelper.DescribeOption(aiEvent, o)}"))}");
                DecideAndApplyAsync(aiPlayer, eventSync, optionDescs, resumeDepth + 1);
                return; // Async decision will continue resolution
            }
        }
    }
}

// ---- Architect event: AI also needs to signal ready for next act ----
// TheArchitect.WinRun() only calls ActChangeSynchronizer.SetLocalPlayerReady()
// for LocalContext.IsMe(owner). AI teammates are never "local", so their event
// can finish visually while the host still waits for that player's ready signal.
[HarmonyPatch(typeof(TheArchitect), "WinRun")]
public static class TheArchitectWinRunAiReadyPatch
{
    [HarmonyPostfix]
    public static void AfterWinRun(TheArchitect __instance)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Solo: the game already treats the local player as the human and signals ready itself.
        if (SoloRunController.IsActive) return;

        var aiPlayer = manager.AiPlayer;
        if (__instance.Owner?.NetId != aiPlayer.NetId) return;

        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                Compat.GameCompat.CreateVoteToMoveToNextAct(aiPlayer));
            manager.AiLog.Log("AI_ACTION", "建筑师事件完成，AI已投票进入下一幕");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Architect AI ready signal failed: {ex.Message}");
            manager.AiLog.LogError("建筑师事件", $"进入下一幕投票失败: {ex.Message}");
        }
    }
}

// ---- Map: auto-vote for AI when any non-AI player votes ----
[HarmonyPatch(typeof(MapSelectionSynchronizer), "PlayerVotedForMapCoord")]
public static class MapAutoVotePatch
{
    [HarmonyPostfix]
    public static void AfterPlayerVoted(Player player, MapLocation source, MapVote? destination, MapSelectionSynchronizer __instance)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        // Solo: the local player votes for itself; no follower vote needed.
        if (SoloRunController.IsActive) return;

        if (manager.AiPlayer == null)
            manager.DetectAndBindAiPlayer();

        if (manager.AiPlayer == null) return;
        if (player == manager.AiPlayer) return;
        if (destination == null) return;

        // Verify AI player is in the RunState players list before voting.
        // GetPlayerSlotIndex returns -1 if the player isn't registered,
        // which causes an IndexOutOfRangeException inside PlayerVotedForMapCoord.
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null) return;
        int aiSlotIndex = runState.GetPlayerSlotIndex(manager.AiPlayer.NetId);
        if (aiSlotIndex < 0)
        {
            MainFile.Logger.Info("AI map vote skipped: AI player not in RunState.Players (may have been removed/re-added)");
            return;
        }

        // Verify source matches the synchronizer's accepting source.
        // If the source doesn't match, the vote will be silently ignored
        // but we avoid triggering a warning log from the game.
        var acceptingSourceField = AccessTools.Field(typeof(MapSelectionSynchronizer), "_acceptingVotesFromSource");
        var acceptingSource = (MapLocation?)acceptingSourceField?.GetValue(__instance);
        if (acceptingSource != null && !acceptingSource.Value.Equals(source))
        {
            MainFile.Logger.Info($"AI map vote skipped: source mismatch (vote from {source}, accepting {acceptingSource.Value})");
            return;
        }

        // Let the AI decide independently; the human's vote is only the fallback so the host never
        // waits forever when the LLM call fails or times out.
        AiMapVoter.Request(manager.AiPlayer, __instance, source, destination?.coord);
    }
}
