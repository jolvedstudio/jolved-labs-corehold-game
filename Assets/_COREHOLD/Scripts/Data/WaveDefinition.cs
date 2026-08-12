using UnityEngine;

namespace Corehold.Data
{
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
    }
}
