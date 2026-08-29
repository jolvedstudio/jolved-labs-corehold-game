using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Static definition of a level: its wave sequence and economy/rules parameters
    /// (GDD §12.2). Difficulty is applied as a struct over this at run start rather
    /// than duplicating the asset set.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Level Definition", fileName = "Level_")]
    public class LevelDefinition : ScriptableObject
    {
        [Header("Waves")]
        [Tooltip("Ordered wave sequence for this level (GDD §8.1).")]
        public WaveDefinition[] waves;

        [Header("Roster (R-UI-2 — per-level turret introductions)")]
        [Tooltip("Turrets buildable ON THIS LEVEL, in menu order. EMPTY = the full roster (every " +
                 "buildable TowerDefinition), which is the shipped behaviour. Written by the Campaign " +
                 "Builder from the stage's roster count so early campaign levels introduce turrets " +
                 "one at a time (the PvZ model).")]
        public TowerDefinition[] roster;

        [Header("Economy & rules")]
        [Tooltip("Salvage the player starts the run with.")]
        public int startingSalvage;

        [Tooltip("Starting core integrity (GDD §3.3).")]
        public int coreIntegrity;

        [Tooltip("Enemy HP growth applied per wave (GDD §8.2).")]
        public float hpGrowthPerWave;

        [Tooltip("Chain bonus salvage per live enemy when the next wave is chained (GDD §8.4).")]
        public int chainBonusPerLiveEnemy;

        [Tooltip("Maximum chain bonus that can be earned in one chain.")]
        public int chainBonusCap;

        [Tooltip("Hard cap on concurrently live enemies (GDD §8.1).")]
        public int maxLiveEnemies;

        [Tooltip("Chaining locks once this fraction of maxLiveEnemies is committed — alive plus queued " +
                 "(GDD §8.4, bounded). 0.75 of a 14-cap locks at 11. 0 uses the WaveManager default. " +
                 "The bound is on how full the field is, NOT on how many waves are on it: a wave with four " +
                 "stragglers is not a full field, and counting waves locks the button while it looks empty.")]
        [Range(0f, 1f)] public float chainLockFieldLoad;

        [Tooltip("Deal each wave's GROUND groups out across every ground spawner in the scene, ignoring the " +
                 "spawner index the group asks for. Set by the generator on siege maps (R40): the wave tables " +
                 "address two ground spawners, and a map with four approaches would otherwise leave half of " +
                 "them silent. Air groups are never redirected. Off for the shipped map.")]
        public bool spreadGroundGroupsAcrossSpawners;

        [Header("Certified tuning (the generator's adopt flow writes these)")]

        [Tooltip("Damage multiplier applied to EVERY turret on this level (1 = as authored). Level-scoped: " +
                 "the Tower_*.asset tiers stay untouched. Written by the balance gate's Adopt when the fix " +
                 "was 'raise turret firepower' rather than 'gut the waves'; the model certifies with the " +
                 "same value, so hand edits here re-certify on the next Verify.")]
        public float towerDamageMultiplier = 1f;

        [Tooltip("Range multiplier applied to EVERY turret on this level (1 = as authored). Level-scoped, " +
                 "same contract as the damage multiplier.")]
        public float towerRangeMultiplier = 1f;
    }
}
