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

        [Tooltip("How many waves may be on the field at once (GDD §8.4, bounded). 2 = you may call the next " +
                 "wave while one runs, but not a third until the first clears. 0 uses the WaveManager default. " +
                 "Unbounded chaining pays the bonus once per call and lands every wave as a single pile.")]
        public int maxWavesInFlight;
    }
}
