using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The Cryo Node's chill field (roster expansion): a support tower that
    /// periodically applies the R18 SLOW to every enemy inside its radius —
    /// the status system's first tower client. No damage, no targeting; the
    /// radius rides the tier's auraRadius (the Floodlight trick), so the
    /// support classification comes free and tiers are data-only.
    ///
    /// Ticks on a coarse timer rather than per frame; re-applying inside the
    /// field REFRESHES the slow (R18 refresh-not-stack), so a unit is slowed
    /// continuously while inside and recovers ~a second after leaving.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Tower))]
    public class CryoField : MonoBehaviour
    {
        [Tooltip("[TUNE] Seconds between field pulses.")]
        [SerializeField] private float tickSeconds = 0.5f;

        [Tooltip("[TUNE] Slow duration per pulse — outlasts the tick so coverage is continuous.")]
        [SerializeField] private float slowSeconds = 1.1f;

        [Tooltip("[TUNE] Slow strength per tier index (0.35 = 65% speed at tier 1).")]
        [SerializeField] private float[] slowStrengthPerTier = { 0.35f, 0.45f, 0.55f };

        private Tower _tower;
        private float _nextTick;

        private float Radius =>
            _tower != null && _tower.HasTier ? _tower.CurrentTier.auraRadius : 0f;

        private float Strength
        {
            get
            {
                if (_tower == null || slowStrengthPerTier == null || slowStrengthPerTier.Length == 0)
                    return 0.35f;
                int i = Mathf.Clamp(_tower.TierIndex, 0, slowStrengthPerTier.Length - 1);
                return slowStrengthPerTier[i];
            }
        }

        private void Awake()
        {
            _tower = GetComponent<Tower>();
        }

        private void Update()
        {
            if (Time.time < _nextTick)
                return;
            _nextTick = Time.time + tickSeconds;

            float r = Radius;
            if (r <= 0f)
                return;
            float rSqr = r * r;
            Vector3 origin = transform.position;
            float strength = Strength;

            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive)
                    continue;
                Vector3 d = e.transform.position - origin;
                d.y = 0f; // ground circle; a Wasp over the node is chilled too
                if (d.sqrMagnitude > rSqr)
                    continue;
                e.ApplySlow(slowSeconds, strength);
            }
        }

        private void OnDrawGizmosSelected()
        {
            var t = _tower != null ? _tower : GetComponent<Tower>();
            float r = t != null && t.HasTier ? t.CurrentTier.auraRadius : 0f;
            if (r <= 0f)
                return;
            Gizmos.color = new Color(0.5f, 0.85f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, r);
        }
    }
}
