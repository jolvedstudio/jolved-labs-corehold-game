using Corehold.Data;
using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>How a turret chooses among the enemies inside its range (GDD §7.4).</summary>
    public enum TargetingPriority
    {
        /// <summary>Furthest along the route (highest PathFraction). The default players expect.</summary>
        First,

        /// <summary>Nearest to the RangeOrigin by 3D distance.</summary>
        Closest,

        /// <summary>Highest current health.</summary>
        Strongest
    }

    /// <summary>
    /// Registry-based target acquisition for a turret (GDD §7.4). There are no
    /// colliders on enemies: this iterates <see cref="Enemy.Live"/> and compares
    /// squared 3D distances from <see cref="rangeOrigin"/> to each enemy's
    /// <see cref="Enemy.HitPoint"/> against range².
    ///
    /// Ticket 18: <see cref="Range"/>, <see cref="MinRange"/> and the air-target
    /// gate are read from the assigned <see cref="TowerDefinition"/> at the current
    /// tier via <see cref="ApplyTier"/> (called by <see cref="TowerWeapon"/>). No
    /// tuning number is hard-coded here.
    ///
    /// Re-acquire runs on a 0.2 s timer (not per frame), with the first tick
    /// staggered by instance id so turrets do not all tick on the same frame.
    /// The current target is held until it dies, leaves range, or a tick finds a
    /// strictly better candidate under the active <see cref="priority"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerTargeting : MonoBehaviour
    {
        [Header("Range origin")]
        [Tooltip("All range checks measure 3D distance from here (GDD §7.2). Falls back to this transform if unset.")]
        [SerializeField] private Transform rangeOrigin;

        [Header("Priority")]
        [Tooltip("How to pick among enemies in range. Persists per-turret.")]
        [SerializeField] private TargetingPriority priority = TargetingPriority.First;

        private const float ReacquireInterval = 0.2f;

        // Tuning pushed in from the TowerDefinition current tier (GDD §7.3).
        // These are runtime state, not serialized literals.
        private float _range;
        private float _minRange;
        private bool _canTargetAir;

        private Enemy _current;
        private EnemyMover _currentMover;
        private float _nextTick;

        /// <summary>The enemy this turret is currently tracking, or null.</summary>
        public Enemy CurrentTarget => _current;

        /// <summary>Effective range in metres, sourced from the current definition tier.</summary>
        public float Range => _range;

        /// <summary>Minimum range in metres (dead zone). Sourced from the current tier.</summary>
        public float MinRange => _minRange;

        /// <summary>Whether this turret may target air units. Sourced from the definition.</summary>
        public bool CanTargetAir => _canTargetAir;

        /// <summary>How this turret prioritises targets. Persists per-turret.</summary>
        public TargetingPriority Priority { get => priority; set => priority = value; }

        /// <summary>World position all range checks measure from.</summary>
        public Vector3 RangeOriginPosition => rangeOrigin != null ? rangeOrigin.position : transform.position;

        /// <summary>
        /// Push tuning from a definition tier (GDD §7.3): range, minimum range and
        /// the air-target gate. Called by <see cref="TowerWeapon"/> on build/upgrade.
        /// </summary>
        public void ApplyTier(TowerDefinition definition, int tierIndex)
        {
            if (definition == null || definition.tiers == null ||
                tierIndex < 0 || tierIndex >= definition.tiers.Length)
            {
                _range = 0f;
                _minRange = 0f;
                _canTargetAir = false;
                if (_current != null)
                    ClearTarget();
                return;
            }

            TowerTier tier = definition.tiers[tierIndex];
            _range = tier.range;
            _minRange = tier.minRange;
            _canTargetAir = definition.canTargetAir;
        }

        /// <summary>
        /// Override the acquisition range with the owning <see cref="Tower"/>'s
        /// effective (aura-buffed) range (GDD §7.3). Called by <see cref="Tower"/>
        /// on build, upgrade and whenever a support aura refresh changes the buff —
        /// the discrete moments the effective range moves, never per frame. The
        /// minimum range and air gate come from <see cref="ApplyTier"/> and are left
        /// untouched here (auras buff maximum range only).
        /// </summary>
        public void SetEffectiveRange(float effectiveRange)
        {
            _range = Mathf.Max(0f, effectiveRange);
        }

        private void OnEnable()
        {
            // Stagger the first tick so turrets do not all re-acquire on the same frame.
            _nextTick = Time.time + (GetInstanceID() % 10) * 0.02f;
        }

        private void Update()
        {
            // Drop a target that has died or been disabled between ticks.
            if (_current != null && (!_current.IsAlive || !_current.isActiveAndEnabled))
                ClearTarget();

            if (Time.time < _nextTick)
                return;
            _nextTick = Time.time + ReacquireInterval;

            Reacquire();
        }

        private void Reacquire()
        {
            // A turret with no effective range (e.g. a support relay, or unconfigured)
            // never acquires a combat target.
            if (_range <= 0f)
            {
                if (_current != null)
                    ClearTarget();
                return;
            }

            float rangeSqr = _range * _range;
            float minRangeSqr = _minRange * _minRange;
            Vector3 origin = RangeOriginPosition;

            // If we still have a valid target in range, only replace it with a
            // STRICTLY better candidate. Otherwise pick the best in range.
            bool currentInRange =
                _current != null &&
                _current.IsAlive &&
                InRange((_current.HitPoint - origin).sqrMagnitude, rangeSqr, minRangeSqr,
                        AcquisitionScale(_current)) &&
                CanTarget(_current);

            if (!currentInRange)
                ClearTarget();

            Enemy best = currentInRange ? _current : null;
            float bestScore = currentInRange ? Score(_current) : float.NegativeInfinity;

            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive)
                    continue;

                if (!CanTarget(e))
                    continue;

                float distSqr = (e.HitPoint - origin).sqrMagnitude;
                if (!InRange(distSqr, rangeSqr, minRangeSqr, AcquisitionScale(e)))
                    continue;

                float score = Score(e, distSqr);

                // Strictly better only — ties keep the current target.
                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            if (best != _current)
                SetTarget(best);
        }

        /// <summary>
        /// Inside the annulus [minRange, range] (all squared, min may be 0).
        /// <paramref name="distanceScale"/> (R20 Blackout) inflates the distance for
        /// the MAX-range comparison only — an unlit unit is seen at half range, but
        /// the min-range dead zone stays physical (darkness must never let a Mortar
        /// fire inside its own dead zone).
        /// </summary>
        private static bool InRange(float distSqr, float rangeSqr, float minRangeSqr,
            float distanceScale = 1f)
        {
            float scaledSqr = distSqr * distanceScale * distanceScale;
            return scaledSqr <= rangeSqr && distSqr >= minRangeSqr;
        }

        /// <summary>
        /// The distance scale to use for this enemy's max-range comparison: its
        /// Blackout stamp (R20) unless a Floodlight lights it (R24) — light
        /// restores full range. Cheap by construction: unstamped units (scale 1)
        /// never query the floodlight registry at all.
        /// </summary>
        private static float AcquisitionScale(Enemy e)
        {
            float s = e.AcquisitionDistanceScale;
            if (s <= 1f)
                return s;
            return Floodlight.IsLit(e.HitPoint) ? 1f : s;
        }

        /// <summary>Air-target gate (GDD §7.2). Ground-only turrets skip air enemies.</summary>
        private bool CanTarget(Enemy e)
        {
            if (_canTargetAir)
                return true;
            return !e.IsAir;
        }

        /// <summary>
        /// Higher score = more desirable. distSqr is optional (recomputed if not
        /// supplied) so we avoid measuring twice inside the loop.
        /// </summary>
        private float Score(Enemy e, float distSqr = -1f)
        {
            switch (priority)
            {
                case TargetingPriority.Closest:
                    if (distSqr < 0f)
                        distSqr = (e.HitPoint - RangeOriginPosition).sqrMagnitude;
                    return -distSqr; // nearer = higher score

                case TargetingPriority.Strongest:
                    return e.CurrentHealth;

                case TargetingPriority.First:
                default:
                    // Furthest along the route = highest PathFraction.
                    return e.Mover != null ? e.Mover.PathFraction : 0f;
            }
        }

        private void SetTarget(Enemy e)
        {
            _current = e;
            _currentMover = e != null ? e.Mover : null;
        }

        private void ClearTarget()
        {
            _current = null;
            _currentMover = null;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireSphere(RangeOriginPosition, _range);
            if (_minRange > 0f)
            {
                Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.35f);
                Gizmos.DrawWireSphere(RangeOriginPosition, _minRange);
            }
        }
    }
}
