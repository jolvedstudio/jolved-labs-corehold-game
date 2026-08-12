using UnityEngine;

namespace Corehold.Core
{
    /// <summary>
    /// A single spawn point (GDD §5.2, §12.2). Its <see cref="index"/> matches the
    /// <c>spawnerIndex</c> a <see cref="Corehold.Data.SpawnGroup"/> selects:
    ///   0 = west ground entrance,
    ///   1 = north ground entrance,
    ///   2 = air corridor.
    ///
    /// Ground spawners hand the enemy the ground <see cref="PathRoute"/> to walk.
    /// The air spawner places the enemy at this transform's XZ; <see cref="EnemyMover"/>
    /// then flies it straight to the Core at the flight altitude, ignoring the route.
    /// </summary>
    [DisallowMultipleComponent]
    public class Spawner : MonoBehaviour
    {
        [Tooltip("Spawner index (GDD §12.2): 0 = west ground, 1 = north ground, 2 = air corridor.")]
        [SerializeField] private int index;

        [Tooltip("The ground route enemies spawned here walk. Leave null for the air spawner.")]
        [SerializeField] private PathRoute route;

        [Tooltip("The Core transform air units fly toward. Optional — the mover falls back to the route's last waypoint.")]
        [SerializeField] private Transform coreTarget;

        /// <summary>Spawner index this spawn point answers to (GDD §12.2).</summary>
        public int Index => index;

        /// <summary>The ground route (null for the air spawner).</summary>
        public PathRoute Route => route;

        /// <summary>The Core transform air units fly toward (may be null).</summary>
        public Transform CoreTarget => coreTarget;

        /// <summary>World-space spawn position.</summary>
        public Vector3 Position => transform.position;

        /// <summary>Set the index (used by editor tooling / scene setup).</summary>
        public void SetIndex(int value) => index = value;

        /// <summary>Assign the route this spawner uses.</summary>
        public void SetRoute(PathRoute newRoute) => route = newRoute;

        /// <summary>Assign the Core target for air units.</summary>
        public void SetCoreTarget(Transform core) => coreTarget = core;

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            switch (index)
            {
                case 0: Gizmos.color = new Color(0.2f, 1f, 0.6f, 1f); break; // west ground
                case 1: Gizmos.color = new Color(0.2f, 0.6f, 1f, 1f); break; // north ground
                default: Gizmos.color = new Color(1f, 0.6f, 0.2f, 1f); break; // air
            }
            Gizmos.DrawWireSphere(transform.position, 1f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 2f);
        }
#endif
    }
}
