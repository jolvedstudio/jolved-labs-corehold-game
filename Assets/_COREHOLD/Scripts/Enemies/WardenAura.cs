using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// The Warden's protection bubble (roster expansion): the first enemy-side
    /// support unit. Every OTHER enemy inside the radius takes reduced damage;
    /// the Warden itself is never protected (by itself or by another Warden's
    /// carrier bonus applying to it twice — kill the Warden first is the whole
    /// counterplay). Registry + static query, mirroring the tower-side auras;
    /// NON-STACKING: overlapping bubbles grant the strongest single reduction.
    ///
    /// Add this component to the Warden prefab root next to Enemy/EnemyMover —
    /// the definition (Enemy_Warden) carries the stats; this carries the bubble.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Enemy))]
    public class WardenAura : MonoBehaviour
    {
        /// <summary>Registry of live wardens.</summary>
        public static readonly List<WardenAura> Wardens = new List<WardenAura>();

        [Tooltip("[TUNE] Bubble radius in metres.")]
        [SerializeField] private float radius = 8f;

        [Tooltip("[TUNE] Damage reduction for protected allies (0.25 = they take 75%).")]
        [Range(0f, 0.9f)] [SerializeField] private float damageReduction = 0.25f;

        private Enemy _enemy;

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
        }

        private void OnEnable()
        {
            if (!Wardens.Contains(this))
                Wardens.Add(this);
        }

        private void OnDisable()
        {
            Wardens.Remove(this);
        }

        /// <summary>
        /// Damage multiplier for a hit on <paramref name="victim"/>: 1 when no
        /// live warden's bubble covers it, else 1 − the strongest reduction.
        /// The victim being a warden itself is never protected.
        /// </summary>
        public static float DamageMultiplierFor(Enemy victim)
        {
            if (Wardens.Count == 0 || victim == null)
                return 1f;
            if (victim.GetComponent<WardenAura>() != null)
                return 1f;

            float best = 0f;
            Vector3 pos = victim.transform.position;
            for (int i = 0; i < Wardens.Count; i++)
            {
                WardenAura w = Wardens[i];
                if (w == null || w._enemy == null || !w._enemy.IsAlive)
                    continue;
                Vector3 d = w.transform.position - pos;
                d.y = 0f;
                if (d.sqrMagnitude > w.radius * w.radius)
                    continue;
                if (w.damageReduction > best)
                    best = w.damageReduction;
            }
            return 1f - best;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 1f, 0.6f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
