using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AiTeammate.AiTeammateCode.Patches;

[HarmonyPatch(typeof(CombatManager))]
public static class CombatManagerPatch
{
    private static bool _turnStartedSubscribed;

    [HarmonyPatch(nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void AfterSetUpCombat(CombatState state)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        // 新战斗开始，重置多轮对话历史
        manager.DecisionService.ResetCombatHistory();
        manager.DirectiveManager.ResetCombat();
        manager.ResetExtraPlayerTurnCombatDecisionState();

        // Subscribe to TurnStarted event once per session
        if (!_turnStartedSubscribed)
        {
            CombatManager.Instance.TurnStarted += OnTurnStarted;
            CombatManager.Instance.TurnEnded += OnTurnEnded;
            _turnStartedSubscribed = true;
        }

        // Detect and bind the AI player from the combat state
        if (manager.AiPlayer == null)
        {
            manager.DetectAndBindAiPlayer(state);
        }
    }

    [HarmonyPatch(nameof(CombatManager.SetReadyToEndTurn))]
    [HarmonyPrefix]
    public static void BeforeSetReadyToEndTurn(Player player)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        if (player == manager.AiPlayer) return;

        if (CombatManager.Instance.IsOverOrEnding) return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player) return;

        if (!CombatManager.Instance.IsPlayerReadyToEndTurn(manager.AiPlayer)) return;
        if (!manager.HasExtraPlayerTurnCombatDecisionOpportunity()) return;
        if (!manager.TryPrepareExtraPlayerTurnCombatDecision()) return;

        try
        {
            CombatManager.Instance.UndoReadyToEndTurn(manager.AiPlayer);
            manager.AiLog.Log("COMBAT", "玩家结束回合时检测到AI仍有能量和可打牌，撤销ready并补发一次战斗决策");
        }
        catch (Exception ex)
        {
            manager.CancelPendingExtraPlayerTurnCombatDecision();
            MainFile.Logger.Warn($"Failed to prepare extra AI combat decision: {ex.Message}");
        }
    }

    [HarmonyPatch(nameof(CombatManager.SetReadyToEndTurn))]
    [HarmonyPostfix]
    public static void AfterSetReadyToEndTurn(Player player)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Don't signal enemy turns if combat is already over
        if (CombatManager.Instance.IsOverOrEnding) return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player) return;

        if (player == manager.AiPlayer)
        {
            // AI just ended its own turn. Check if all human teammates are dead.
            // If so, the game may be waiting for the dead player to signal
            // SetReadyToBeginEnemyTurn — do it on their behalf.
            bool allHumansDead = state.Players
                .Where(p => p != manager.AiPlayer)
                .All(p => !p.Creature.IsAlive);
            if (allHumansDead)
            {
                CombatManager.Instance.SetReadyToBeginEnemyTurn(manager.AiPlayer);
            }
            return;
        }

        if (manager.ConsumePendingExtraPlayerTurnCombatDecision())
        {
            TriggerExtraCombatDecision(manager);
            return;
        }

        // Human player ended turn. Only signal BeginEnemyTurn if the AI has
        // already ended its own turn.
        if (!CombatManager.Instance.IsPlayerReadyToEndTurn(manager.AiPlayer)) return;

        CombatManager.Instance.SetReadyToBeginEnemyTurn(manager.AiPlayer);
    }

    private static async void OnTurnStarted(CombatState state)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Don't process turns if combat is already over or ending.
        // TurnStarted can fire during reward processing when combat
        // state hasn't been fully cleaned up yet.
        if (CombatManager.Instance.IsOverOrEnding) return;

        if (state.CurrentSide == CombatSide.Player)
        {
            manager.ResetExtraPlayerTurnCombatDecisionState();
        }

        try
        {
            await manager.ProcessCombatTurnAsync();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"AI turn error: {ex.Message}");
        }
    }

    private static void OnTurnEnded(CombatState state)
    {
        // Future: cleanup or state tracking after turn ends
    }

    private static async void TriggerExtraCombatDecision(AiTeammateManager manager)
    {
        try
        {
            await manager.ProcessCombatTurnAsync();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Extra AI combat decision error: {ex.Message}");
        }
    }
}
