using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AiTeammate.AiTeammateCode.Suggestion;

public class SituationAnalyzer
{
    public struct SituationSummary
    {
        public bool PlayerLowHp;
        public bool PlayerHighBlock;
        public bool EnemyVulnerable;
        public bool EnemyLowHp;
        public bool BossFight;
        public int EnemyCount;
        public int AllyCount;
    }

    public SituationSummary Analyze(Player localPlayer)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null) return new();

        var creature = localPlayer.Creature;
        var enemies = state.Enemies.Where(e => e.IsAlive).ToList();

        return new SituationSummary
        {
            PlayerLowHp = creature.CurrentHp < creature.MaxHp * 0.3f,
            PlayerHighBlock = creature.Block > 15,
            EnemyVulnerable = enemies.Any(e => (e.Powers != null && e.Powers.Any(p => (p.Id?.Entry ?? "").Contains("Vulnerable", System.StringComparison.OrdinalIgnoreCase)))),
            EnemyLowHp = enemies.Any(e => e.CurrentHp < 10),
            BossFight = enemies.Any(e => e.IsPrimaryEnemy && e.MaxHp > 100),
            EnemyCount = enemies.Count,
            AllyCount = state.Players.Count
        };
    }
}
