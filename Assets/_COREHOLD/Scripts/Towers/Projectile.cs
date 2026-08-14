using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// A leading, travelling projectile for the Missile Battery and Siege Mortar
    /// (GDD §7.2, §7.4). Everything about it is designed around the anti-air
    /// requirement: a 22 m/s missile against a 9 m/s Wasp must actually connect,
    /// which it will not if it flies at the target's <em>current</em> position.
    /// So on launch it solves a first-order intercept against the target's
    /// current velocity and flies toward the predicted meeting point.
    ///
    /// Travel shape depends on <see cref="arcApex"/>: a missile (apex 0) flies
    /// straight at the intercept point; a mortar shell (apex 6 m) interpolates
    /// along a parabola that rises to <see cref="arcApex"/> metres above the
    /// straight line and comes back down, so it visibly lobs.
    ///
    /// On impact it applies damage to every live enemy within
    /// <see cref="TowerTier.splashRadius"/>, falling off linearly from full at the
    /// centre to 40% at the edge, with the damage-table multiplier
    /// (<see cref="SharedDamageTable"/>) applied per target from the launching
    /// turret's damage type against each enemy's armour type.
    ///
    /// If the target dies mid-flight the projectile does not retarget and does
    /// not despawn silently — it continues to the last known intercept position
    /// and detonates there, so splash weapons still do useful work (GDD §7.2).
    ///
    /// Pooled through <see cref="CoreholdPool{T}"/> — nothing here calls
    /// Instantiate or Destroy during a wave (GDD §11).
    /// </summary>
    [DisallowMultipleComponent]
    public class Projectile : MonoBehaviour
    {
        [Header("Travel")]
        [Tooltip("Apex height in metres above the straight line from muzzle to impact. " +
                 "0 = straight (missile). 6 = arcing shell (mortar). GDD §7.2.")]
        [SerializeField] private float arcApex = 0f;

        [Tooltip("Explosion VFX spawned on detonation (optional). Wired in a later ticket.")]
        [SerializeField] private GameObject impactVfx;

        // ----- Shared services (assigned at boot) -----

        /// <summary>
        /// The damage-vs-armour table applied per target (GDD §7.1). Assigned once
        /// at game boot. When null every multiplier is 1.0, so projectiles still
        /// deal their raw tier damage.
        /// </summary>
        public static DamageTable SharedDamageTable { get; set; }

        // ----- Pooling -----

        // One pool per distinct projectile prefab (Missile and Mortar differ), so
        // nothing calls Instantiate during a wave (GDD §11). Pools are created
        // lazily on first use and parented under a shared root in the scene.
        private static readonly System.Collections.Generic.Dictionary<Projectile, CoreholdPool<Projectile>> _pools =
            new System.Collections.Generic.Dictionary<Projectile, CoreholdPool<Projectile>>();

        private static Transform _poolRoot;

        [Tooltip("Instances prewarmed into the pool the first time this prefab is spawned.")]
        [SerializeField] private int poolPrewarm = 8;

        // Set to the pool that owns this instance so it can return itself on detonation.
        private CoreholdPool<Projectile> _owningPool;

        /// <summary>
        /// Optionally prewarm the pool for a given projectile prefab at boot. Not
        /// required — <see cref="Spawn"/> creates the pool lazily on first fire —
        /// but calling it during a build phase avoids the first-shot allocation.
        /// </summary>
        public static void Prewarm(Projectile prefab)
        {
            if (prefab != null)
                GetPool(prefab);
        }

        private static Transform PoolRoot
        {
            get
            {
                if (_poolRoot == null)
                {
                    var go = new GameObject("Pool_Projectiles");
                    _poolRoot = go.transform;
                }
                return _poolRoot;
            }
        }

        private static CoreholdPool<Projectile> GetPool(Projectile prefab)
        {
            if (_pools.TryGetValue(prefab, out CoreholdPool<Projectile> pool))
                return pool;

            var parentGo = new GameObject($"Pool_{prefab.name}");
            parentGo.transform.SetParent(PoolRoot, false);
            pool = new CoreholdPool<Projectile>(prefab, parentGo.transform, prefab.poolPrewarm);
            _pools.Add(prefab, pool);
            return pool;
        }

        /// <summary>
        /// Clear all projectile pools. Call when tearing a level down so stale
        /// pooled instances do not survive a scene reload.
        /// </summary>
        public static void ClearPools()
        {
            _pools.Clear();
            _poolRoot = null;
        }

        /// <summary>
        /// Spawn a projectile from the pool for <paramref name="tier"/>'s
        /// projectile prefab and launch it. This is the entry point turrets use —
        /// never Instantiate. Returns null if the tier has no projectile prefab.
        /// </summary>
        public static Projectile Spawn(Vector3 origin, Enemy target, TowerTier tier, DamageType type,
            Tower owner = null)
        {
            var prefab = tier.projectilePrefab != null ? tier.projectilePrefab.GetComponent<Projectile>() : null;
            if (prefab == null)
            {
                Debug.LogWarning("[Corehold] Tier has no Projectile prefab; cannot spawn projectile.");
                return null;
            }

            CoreholdPool<Projectile> pool = GetPool(prefab);
            Projectile p = pool.Get();
            p._owningPool = pool;
            p.Launch(origin, target, tier, type, owner);
            return p;
        }

        // ----- Runtime state -----

        private Enemy _target;
        private TowerTier _tier;
        private DamageType _damageType;
        private Tower _owner;   // veterancy kill credit (R21); may be null

        private Vector3 _startPos;      // launch muzzle position
        private Vector3 _interceptPos;  // solved intercept (updated while target lives)
        private float _speed;           // straight-line speed (m/s)
        private float _totalDistance;   // straight-line distance start -> intercept
        private float _travelled;       // straight-line distance covered so far
        private bool _inFlight;

        /// <summary>True while this projectile is travelling toward its detonation point.</summary>
        public bool InFlight => _inFlight;

        /// <summary>
        /// Launch toward a leading intercept of <paramref name="target"/> (GDD §7.2).
        /// Signature is fixed by the ticket: origin, target, tier, damage type — the
        /// optional <paramref name="owner"/> (R21 kill credit) is additive.
        /// </summary>
        public void Launch(Vector3 origin, Enemy target, TowerTier tier, DamageType type,
            Tower owner = null)
        {
            _tier = tier;
            _damageType = type;
            _target = target;
            _owner = owner;

            _speed = Mathf.Max(0.01f, tier.projectileSpeed);
            _startPos = origin;
            transform.position = origin;

            _interceptPos = SolveIntercept(origin, target, _speed);
            _totalDistance = Vector3.Distance(origin, _interceptPos);
            _travelled = 0f;
            _inFlight = true;

            // Face the intercept immediately so a straight missile points correctly.
            Vector3 flat = _interceptPos - origin;
            if (flat.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
        }

        /// <summary>
        /// First-order intercept: find the point where a projectile of the given
        /// speed, fired from <paramref name="origin"/> now, meets the target given
        /// the target's current position and velocity (GDD §7.2). Solves the
        /// quadratic |P + Vt| = speed·t for the earliest positive t. Falls back to
        /// the target's current position when no real solution exists (e.g. the
        /// target outruns the projectile).
        /// </summary>
        private static Vector3 SolveIntercept(Vector3 origin, Enemy target, float speed)
        {
            if (target == null)
                return origin;

            Vector3 targetPos = target.HitPoint;
            Vector3 targetVel = target.Mover != null ? target.Mover.Velocity : Vector3.zero;
            return SolveIntercept(origin, targetPos, targetVel, speed);
        }

        /// <summary>
        /// Pure first-order intercept solver (GDD §7.2), exposed for testing and
        /// reuse. Given a shooter at <paramref name="origin"/> firing at
        /// <paramref name="speed"/>, and a target now at <paramref name="targetPos"/>
        /// moving at <paramref name="targetVel"/>, returns the world point where
        /// the projectile meets the target — or the target's current position when
        /// no positive-time solution exists.
        /// </summary>
        public static Vector3 SolveIntercept(Vector3 origin, Vector3 targetPos, Vector3 targetVel, float speed)
        {
            Vector3 toTarget = targetPos - origin;

            // Quadratic in t: (v·v - s²) t² + 2(P·V) t + (P·P) = 0
            float a = Vector3.Dot(targetVel, targetVel) - speed * speed;
            float b = 2f * Vector3.Dot(toTarget, targetVel);
            float c = Vector3.Dot(toTarget, toTarget);

            float t = -1f;

            if (Mathf.Abs(a) < 0.0001f)
            {
                // Target and projectile speeds are effectively equal: linear solve.
                if (Mathf.Abs(b) > 0.0001f)
                    t = -c / b;
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sqrt = Mathf.Sqrt(disc);
                    float t1 = (-b - sqrt) / (2f * a);
                    float t2 = (-b + sqrt) / (2f * a);

                    // Earliest strictly-positive root.
                    if (t1 > 0f && t2 > 0f) t = Mathf.Min(t1, t2);
                    else if (t1 > 0f) t = t1;
                    else if (t2 > 0f) t = t2;
                }
            }

            // No valid intercept (target un-catchable) → aim at its current point.
            if (t <= 0f)
                return targetPos;

            return targetPos + targetVel * t;
        }

        private void Update()
        {
            if (!_inFlight)
                return;

            // The intercept is solved ONCE at launch and the projectile flies
            // straight to that fixed point (GDD §7.2: "Missiles fly straight at the
            // intercept point"). For a constant-velocity target this is an exact
            // hit. We do NOT retarget: if the target dies mid-flight we simply keep
            // flying to the same last-known intercept and detonate there — splash
            // still does useful work and it reads as correct (GDD §7.2).
            if (_target != null && (!_target.IsAlive || !_target.isActiveAndEnabled))
                _target = null; // orphaned: continue to the fixed intercept, do not retarget.

            _travelled += _speed * Time.deltaTime;

            if (_totalDistance <= 0.0001f || _travelled >= _totalDistance)
            {
                transform.position = _interceptPos;
                Detonate();
                return;
            }

            float frac = _travelled / _totalDistance;
            Vector3 pos = ArcPositionAt(_startPos, _interceptPos, arcApex, frac);

            // Orient along the travel direction (matters for the arcing shell).
            Vector3 dir = pos - transform.position;
            if (dir.sqrMagnitude > 0.000001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

            transform.position = pos;
        }

        /// <summary>
        /// Position along the flight path at normalised progress <paramref name="frac"/>
        /// (0 at launch, 1 at impact). A straight missile (apex 0) is a plain lerp;
        /// an arcing shell adds a parabolic vertical offset that peaks at
        /// <paramref name="arcApex"/> metres mid-flight (GDD §7.2). Exposed for testing.
        /// </summary>
        public static Vector3 ArcPositionAt(Vector3 start, Vector3 end, float arcApex, float frac)
        {
            Vector3 pos = Vector3.Lerp(start, end, frac);
            if (arcApex > 0f)
                pos.y += 4f * arcApex * frac * (1f - frac); // 0 at ends, arcApex at midpoint
            return pos;
        }

        /// <summary>
        /// Linear splash falloff: 1.0 at the centre down to 0.4 at the edge of the
        /// radius, clamped (GDD §7.2/§7.3). Exposed for testing.
        /// </summary>
        public static float SplashFalloff(float distance, float radius)
        {
            if (radius <= 0f)
                return distance <= 0f ? 1f : 0f;
            return Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(distance / radius));
        }

        /// <summary>
        /// Apply damage at the detonation point. Splash weapons hit everything in
        /// <see cref="TowerTier.splashRadius"/> with linear falloff to 40% at the
        /// edge; single-target projectiles apply full damage to the nearest enemy
        /// within a small radius. The damage-table multiplier is applied per
        /// target. Then the projectile returns to the pool.
        /// </summary>
        private void Detonate()
        {
            _inFlight = false;
            Vector3 center = transform.position;

            // Detonation VFX (GDD §11): splash weapons play one of two explosion
            // sizes chosen by their splash radius; a single-target projectile just
            // sparks. Routed through the pooled VFXDirector — no Instantiate.
            if (VFXDirector.Instance != null)
            {
                if (_tier.splashRadius > 0f)
                    VFXDirector.Instance.PlayExplosion(center, _tier.splashRadius);
                else
                    VFXDirector.Instance.PlayImpact(center);
            }

            // Detonation SFX (GDD §10): splash weapons play the explosion sound, a
            // single-target projectile just impacts. Collapse + voice-steal in the
            // director keep a wave of missiles from clipping.
            if (AudioDirector.Instance != null)
            {
                if (_tier.splashRadius > 0f)
                    AudioDirector.Instance.PlayExplosion();
                else
                    AudioDirector.Instance.PlayImpact();
            }

            float radius = _tier.splashRadius;

            if (radius > 0f)
            {
                // Splash: linear falloff from 1.0 at centre to 0.4 at the edge.
                float radiusSqr = radius * radius;
                var live = Enemy.Live;
                for (int i = 0; i < live.Count; i++)
                {
                    Enemy e = live[i];
                    if (e == null || !e.IsAlive)
                        continue;

                    float distSqr = (e.HitPoint - center).sqrMagnitude;
                    if (distSqr > radiusSqr)
                        continue;

                    float dist = Mathf.Sqrt(distSqr);
                    float falloff = SplashFalloff(dist, radius);
                    ApplyDamage(e, _tier.damage * falloff);
                }
            }
            else
            {
                // Single-target: hit the nearest enemy essentially at the point.
                Enemy nearest = NearestEnemy(center, 1.0f);
                if (nearest != null)
                    ApplyDamage(nearest, _tier.damage);
            }

            ReturnToPool();
        }

        /// <summary>
        /// Apply tier damage to one enemy with the damage-table multiplier (GDD §7.1).
        /// A hit that flips the enemy from alive to dead credits the owning tower's
        /// veterancy (R21) — every splash kill counts, not just the primary target.
        /// </summary>
        private void ApplyDamage(Enemy e, float baseDamage)
        {
            float mult = SharedDamageTable != null
                ? SharedDamageTable.Multiplier(_damageType, e.ArmourType)
                : 1f;
            bool wasAlive = e.IsAlive;
            e.TakeDamage(baseDamage * mult);
            if (wasAlive && !e.IsAlive && _owner != null)
                _owner.RegisterKill();
        }

        private static Enemy NearestEnemy(Vector3 point, float maxRadius)
        {
            float bestSqr = maxRadius * maxRadius;
            Enemy best = null;
            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive)
                    continue;
                float d = (e.HitPoint - point).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    best = e;
                }
            }
            return best;
        }

        private void ReturnToPool()
        {
            _target = null;
            _owner = null;

            if (_owningPool != null)
                _owningPool.Release(this);
            else
                gameObject.SetActive(false); // no pool configured; still no Destroy.
        }
    }
}
