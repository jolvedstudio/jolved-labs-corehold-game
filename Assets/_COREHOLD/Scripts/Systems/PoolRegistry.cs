using System.Collections.Generic;
using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Owns the enemy pools and hands out one pool per prefab, created lazily.
    /// EnemyDefinition does not exist yet (GDD §11), so pools are keyed on the
    /// prefab directly for now.
    /// </summary>
    [DisallowMultipleComponent]
    public class PoolRegistry : MonoBehaviour
    {
        [Tooltip("Prewarm count used when a new enemy pool is created.")]
        [SerializeField] private int enemyPrewarm = 4;

        private readonly Dictionary<Enemy, CoreholdPool<Enemy>> _enemyPools =
            new Dictionary<Enemy, CoreholdPool<Enemy>>();

        // Keeps pooled instances tidy under a per-prefab parent in the hierarchy.
        private readonly Dictionary<Enemy, Transform> _enemyParents =
            new Dictionary<Enemy, Transform>();

        /// <summary>
        /// Get (or lazily create) the enemy pool for the given prefab.
        /// One pool per prefab.
        /// </summary>
        public CoreholdPool<Enemy> GetEnemyPool(Enemy prefab)
        {
            if (prefab == null)
                return null;

            if (_enemyPools.TryGetValue(prefab, out CoreholdPool<Enemy> pool))
                return pool;

            var parentGo = new GameObject($"Pool_{prefab.name}");
            parentGo.transform.SetParent(transform, false);

            pool = new CoreholdPool<Enemy>(prefab, parentGo.transform, enemyPrewarm);
            _enemyPools.Add(prefab, pool);
            _enemyParents.Add(prefab, parentGo.transform);
            return pool;
        }
    }
}
