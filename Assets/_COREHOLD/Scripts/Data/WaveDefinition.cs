using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Optional per-wave mutators (R20). Flags — mutators compose freely on one
    /// wave. Applied at SPAWN by WaveManager/EnemyMover from `[TUNE]` values on
    /// the WaveManager; None restores exact vanilla behaviour. This field is the
    /// foundation the weekly rotation (R36) writes into.
    /// </summary>
    [System.Flags]
    public enum WaveMutator
    {
        None = 0,
        /// <summary>Air units of this wave fly faster (WaveManager [TUNE], default +30%).</summary>
        Storm = 1 << 0,
        /// <summary>Ground units run single-file in one lane on one approach.</summary>
        Convoy = 1 << 1,
        /// <summary>More HP, bigger bounty (defaults +30% / +50%).</summary>
        Overcharge = 1 << 2,
        /// <summary>Unlit units count distance double at turret acquisition — towers
        /// see them at half range until a Floodlight (R24) lights them.</summary>
        Blackout = 1 << 3
    }

    /// <summary>
    /// One group of identical enemies spawned within a wave (GDD §12.2, §8.1).
    /// </summary>
    [System.Serializable]
    public struct SpawnGroup
    {
        [Tooltip("The enemy type to spawn.")]
        public EnemyDefinition enemy;

        [Tooltip("Number of units to spawn.")]
        public int count;

        [Tooltip("Seconds between successive spawns in this group.")]
        public float spawnGap;

        [Tooltip("Seconds after the wave begins before this group starts spawning.")]
        public float startOffset;

        [Tooltip("Spawner: 0 = west ground entrance, 1 = north ground entrance, 2 = air corridor (GDD §12.2).")]
        public int spawnerIndex;
    }

    /// <summary>
    /// A single wave: its spawn groups plus the clear bonus awarded when resolved (GDD §12.2, §8.1).
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Wave Definition", fileName = "Wave_")]
    public class WaveDefinition : ScriptableObject
    {
        [Tooltip("The groups that make up this wave.")]
        public SpawnGroup[] groups;

        [Tooltip("Salvage awarded when the wave is cleared.")]
        public int clearBonus;

        [Tooltip("Optional mutators for this wave (R20). None = vanilla. Applied at spawn; values tuned on the WaveManager.")]
        public WaveMutator mutators = WaveMutator.None;
    }
}
