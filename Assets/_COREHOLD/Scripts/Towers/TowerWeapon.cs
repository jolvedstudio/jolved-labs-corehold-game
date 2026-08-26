using System.Collections.Generic;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Drives the turret's aim and firing (GDD §7.4). Every frame it asks
    /// <see cref="TowerTargeting"/> for the current target and, if there is one,
    /// tells <see cref="TurretAim"/> to slew onto it. It only fires when
    /// <see cref="TurretAim.IsAimed"/> is true and a weapon's fire-rate timer has
    /// elapsed — that gate is what makes turrets feel mechanical.
    ///
    /// A turret can mount MORE THAN ONE weapon (twin autocannon barrels, or a cannon
    /// paired with a grenade launcher), so firing is driven by the current tier's
    /// <see cref="TowerTier.Weapons"/> ARRAY. Each mount has its OWN damage, fire
    /// rate, projectile/chain/splash behaviour, muzzle and tracer colour, and fires
    /// independently on its own cooldown at the shared target. Range, min-range and
    /// the support aura remain tier-level, so all mounts share one acquisition.
    ///
    /// Ticket 18: all tuning is read from the assigned <see cref="TowerDefinition"/>
    /// at the current <see cref="TierIndex"/>. No number is hard-coded here.
    ///
    /// Ticket 20 (Arc Node): when a mount's <see cref="TowerWeaponMount.chainTargets"/>
    /// is greater than 1 its hitscan shot becomes a chain — it hits the primary
    /// target, then jumps to the nearest not-yet-hit enemy within
    /// <see cref="ChainJumpRange"/> metres of the previous target, up to
    /// chainTargets hits total, keeping <see cref="TowerWeaponMount.chainFalloff"/>
    /// of the previous hit's damage each jump. Every jump draws its own pooled
    /// two-frame <see cref="ChainTracer"/> segment.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TowerTargeting))]
    [RequireComponent(typeof(TurretAim))]
    public class TowerWeapon : MonoBehaviour
    {
        [Header("Definition (GDD §7.3)")]
        [Tooltip("The turret this instance represents. All tuning is read from its current tier.")]
        [SerializeField] private TowerDefinition definition;

        [Tooltip("Zero-based tier index into definition.tiers (0 = tier 1, 2 = tier 3).")]
        [SerializeField] private int tierIndex;

        [Header("Muzzles")]
        [Tooltip("Muzzle points, indexed by each weapon's muzzleIndex. Element 0 is also the default single-muzzle fallback.")]
        [SerializeField] private Transform[] muzzlePoints = new Transform[0];

        [Tooltip("Legacy single muzzle (migrated into muzzlePoints[0] when that array is empty).")]
        [HideInInspector] [SerializeField] private Transform muzzlePoint;

        [Header("Arc chain (GDD §7.2)")]
        [Tooltip("Max distance in metres a chain may jump from one target to the next.")]
        [SerializeField] private float chainJumpRange = 6f;

        [Tooltip("Colour of the chain-lightning tracer segments (Arc Node). HDR-bright.")]
        [ColorUsage(true, true)]
        [SerializeField] private Color chainColor = new Color(1.2f, 2.6f, 3.5f, 1f);

        [Tooltip("Fallback tracer colour for a mount that does not author one (alpha 0). HDR-bright.")]
        [ColorUsage(true, true)]
        [SerializeField] private Color tracerColor = new Color(3.5f, 2.2f, 0.8f, 1f);

        private TowerTargeting _targeting;
        private TurretAim _aim;
        private Tower _tower;
        private TurretBarrelSpin _barrelSpin;

        // Per-mount fire cooldowns, parallel to the current tier's weapons array.
        private float[] _cooldowns = new float[0];
        private int[] _shotCounts = new int[0]; // advances per shot so multi-muzzle mounts cycle.

        // Scratch list reused every chain so we never allocate mid-wave (GDD §11).
        private readonly List<Enemy> _chainHits = new List<Enemy>(8);

        /// <summary>Max distance in metres a chain jump may span (GDD §7.2).</summary>
        public float ChainJumpRange => chainJumpRange;

        /// <summary>The definition backing this turret. All tuning comes from it.</summary>
        public TowerDefinition Definition
        {
            get => definition;
            set { definition = value; SyncTargeting(); }
        }

        /// <summary>Current zero-based tier index. Clamped to the definition's tier array.</summary>
        public int TierIndex
        {
            get => tierIndex;
            set { SetTier(value); }
        }

        /// <summary>True when a definition with a valid tier is assigned.</summary>
        public bool HasTier =>
            definition != null &&
            definition.tiers != null &&
            tierIndex >= 0 &&
            tierIndex < definition.tiers.Length;

        /// <summary>The active tier's data (GDD §7.3). Only valid when <see cref="HasTier"/>.</summary>
        public TowerTier CurrentTier => definition.tiers[tierIndex];

        /// <summary>
        /// Combined damage-per-volley actually dealt (before the damage/armour
        /// multiplier), summed across every mount and scaled by any aura damage
        /// buff and the tower's veterancy rank (R21).
        /// </summary>
        public float Damage => HasTier
            ? CurrentTier.TotalDamagePerVolley * (1f + DamageBonus) * VeterancyMultiplier
            : 0f;

        /// <summary>Combined shots-per-second across every mount, scaled by any aura fire-rate buff.</summary>
        public float FireRate => HasTier ? CurrentTier.TotalFireRate * (1f + FireRateBonus) : 0f;

        // ----- Manual control (M-a, man-the-turret) -----

        /// <summary>While manned, the weapon fires only when the trigger is held —
        /// the player's hand replaces the autonomous loop.</summary>
        public bool ManualMode { get; set; }

        /// <summary>The player's trigger state while manned.</summary>
        public bool ManualTrigger { get; set; }

        /// <summary>[TUNE] Fire-rate bonus while a player mans this turret. A
        /// player-optional buff only makes runs EASIER than certified, the same
        /// class as skilled play — deliberately unmodeled, the model stays a
        /// lower bound.</summary>
        public float MannedFireRateBonus { get; set; } = 0.25f;

        // Aura buff fractions, sourced from the owning Tower (0 when none present).
        // High ground (M-b) joins the SAME additive damage funnel — and the
        // balance model's per-pad hg term composes identically (1 + aura + hg),
        // so certified margins stay in step with what the weapon actually deals.
        // The Tower resolves its pad per read, so relocation (M-c) is honest.
        private float DamageBonus =>
            _tower != null ? _tower.CurrentModifiers.damageBonus + _tower.HighGroundBonus : 0f;
        private float FireRateBonus =>
            (_tower != null ? _tower.CurrentModifiers.fireRateBonus : 0f) +
            (ManualMode ? MannedFireRateBonus : 0f);

        // Veterancy damage multiplier from the owning Tower (R21; 1 when none present).
        private float VeterancyMultiplier => _tower != null ? _tower.VeterancyDamageMultiplier : 1f;

        private void Awake()
        {
            _targeting = GetComponent<TowerTargeting>();
            _aim = GetComponent<TurretAim>();
            _tower = GetComponent<Tower>();
            _barrelSpin = GetComponentInChildren<TurretBarrelSpin>(true);
            MigrateMuzzles();
            SyncTargeting();
            AllocateCooldowns();
        }

        private void OnValidate()
        {
            if (definition != null && definition.tiers != null)
                tierIndex = Mathf.Clamp(tierIndex, 0, Mathf.Max(0, definition.tiers.Length - 1));
            MigrateMuzzles();
        }

        /// <summary>Copy the legacy single muzzle into muzzlePoints[0] when that array is empty.</summary>
        private void MigrateMuzzles()
        {
            if ((muzzlePoints == null || muzzlePoints.Length == 0) && muzzlePoint != null)
                muzzlePoints = new[] { muzzlePoint };
        }

        private void AllocateCooldowns()
        {
            int n = HasTier ? CurrentTier.Weapons.Length : 0;
            if (_cooldowns == null || _cooldowns.Length != n)
            {
                _cooldowns = new float[n];
                _shotCounts = new int[n];
            }
        }

        /// <summary>
        /// Set the active tier (0-based) and push the tier's range down to
        /// <see cref="TowerTargeting"/> so aim/range stay consistent (GDD §7.3).
        /// </summary>
        public void SetTier(int index)
        {
            if (definition == null || definition.tiers == null || definition.tiers.Length == 0)
            {
                tierIndex = 0;
                return;
            }

            tierIndex = Mathf.Clamp(index, 0, definition.tiers.Length - 1);
            SyncTargeting();
            AllocateCooldowns();
        }

        /// <summary>Assign a definition at a given tier in one call (e.g. on build/upgrade).</summary>
        public void Configure(TowerDefinition def, int tier = 0)
        {
            definition = def;
            SetTier(tier);
        }

        private void SyncTargeting()
        {
            if (_targeting == null)
                _targeting = GetComponent<TowerTargeting>();
            if (_targeting != null)
                _targeting.ApplyTier(definition, tierIndex);
        }

        private void Update()
        {
            // Unconfigured turrets do nothing.
            if (!HasTier)
                return;

            AllocateCooldowns();

            TowerWeaponMount[] weapons = CurrentTier.Weapons;

            // A turret can fire if ANY of its mounts has a positive fire rate. Support
            // turrets (Scan Relay) have none, but they STILL idle-scan so every turret
            // on the board reads as active.
            bool canFire = false;
            for (int i = 0; i < weapons.Length; i++)
            {
                if (weapons[i].fireRate > 0f) { canFire = true; break; }
            }

            for (int i = 0; i < _cooldowns.Length; i++)
            {
                if (_cooldowns[i] > 0f)
                    _cooldowns[i] -= Time.deltaTime;
            }

            Enemy target = canFire && _targeting != null ? _targeting.CurrentTarget : null;
            if (target == null || !target.IsAlive)
            {
                // No threat (or a non-combat turret): sweep the barrel as if scanning.
                if (_aim != null)
                    _aim.Idle();
                return;
            }

            // Slew toward the target's hit point every frame.
            Vector3 aimPoint = target.HitPoint;
            _aim.AimAt(aimPoint);

            // Fire every ready mount only once aimed. Manned turrets add one more
            // condition: the player's trigger — the aim keeps tracking either way.
            if (_aim.IsAimed && !(ManualMode && !ManualTrigger))
            {
                for (int i = 0; i < weapons.Length; i++)
                {
                    if (weapons[i].fireRate > 0f && _cooldowns[i] <= 0f)
                        FireWeapon(i, weapons[i], target);
                }
            }
        }

        /// <summary>
        /// Fire a single weapon mount at the shared target. Resolves the mount's own
        /// damage (buffed), muzzle, tracer colour and firing behaviour (projectile /
        /// chain / plain hitscan), then resets that mount's cooldown from its own rate.
        /// </summary>
        private void FireWeapon(int index, TowerWeaponMount weapon, Enemy target)
        {
            // Resolve the muzzle for this shot. A single weapon with several muzzle
            // indices alternates between them (twin barrel / minigun) — purely visual,
            // no change to fire rate or damage — via the per-mount shot counter.
            int muzzleIndex = weapon.ResolveMuzzleIndex(_shotCounts[index]);
            _shotCounts[index]++;
            Vector3 origin = ResolveMuzzle(muzzleIndex, out Transform muzzle);

            // Keep the barrel spinning only while actively firing (item e).
            if (_barrelSpin != null)
                _barrelSpin.NotifyFired();

            float effectiveDamage = weapon.damage * (1f + DamageBonus) * VeterancyMultiplier;
            float effectiveFireRate = weapon.fireRate * (1f + FireRateBonus);
            Color trace = weapon.tracerColor.a > 0f ? weapon.tracerColor : tracerColor;

            // Muzzle flash for this turret's damage type, facing the target (GDD §11).
            if (VFXDirector.Instance != null && definition != null)
            {
                Vector3 forward = muzzle != null ? muzzle.forward : (target.HitPoint - origin);
                VFXDirector.Instance.PlayMuzzle(definition.damageType, origin, forward);
            }

            // Fire SFX for this weapon's family (GDD §10).
            if (AudioDirector.Instance != null && definition != null)
            {
                bool isProjectile = weapon.projectilePrefab != null;
                bool isMortar = isProjectile && weapon.splashRadius >= VFXDirector.LargeSplashThreshold;
                AudioDirector.Instance.PlayFire(definition.damageType, isProjectile, isMortar);
            }

            if (weapon.projectilePrefab != null)
            {
                // Projectile weapon (Missile / Mortar / grenade launcher): spawn a
                // pooled projectile that leads the target and applies splash on impact.
                // Build a per-shot tier carrying this mount's projectile settings and
                // buffed damage so Projectile.Spawn stays unchanged (GDD §7.2).
                TowerTier fireTier = default;
                fireTier.damage = effectiveDamage;
                fireTier.splashRadius = weapon.splashRadius;
                fireTier.projectilePrefab = weapon.projectilePrefab;
                fireTier.projectileSpeed = weapon.projectileSpeed;
                Projectile.Spawn(origin, target, fireTier, definition.damageType, _tower);
            }
            else if (weapon.chainTargets > 1)
            {
                // Arc weapon: chain-lightning hitscan (GDD §7.2).
                FireChain(origin, target, weapon, effectiveDamage);
            }
            else if (weapon.pierce)
            {
                // Railgun (roster): one bolt damages everything near the line.
                FirePierce(origin, target, effectiveDamage, trace);
            }
            else
            {
                // Plain hitscan: apply damage directly, draw tracer + impact spark.
                ApplyDamage(target, effectiveDamage);
                DrawTracer(origin, target.HitPoint, trace);
                // Counter-readable impact (R22): the spark's look encodes whether this
                // turret's damage type countered the target's armour (GDD §7.1 pillar).
                if (VFXDirector.Instance != null)
                    VFXDirector.Instance.PlayImpactEffective(target.HitPoint, Multiplier(target), target.ArmourType);
                if (AudioDirector.Instance != null)
                    AudioDirector.Instance.PlayImpact();
            }

            _cooldowns[index] = effectiveFireRate > 0f ? 1f / effectiveFireRate : float.PositiveInfinity;
        }

        /// <summary>
        /// Resolve a mount's muzzle world position (and transform). Uses the muzzle at
        /// <paramref name="muzzleIndex"/> when valid, otherwise the first muzzle, and
        /// finally this transform.
        /// </summary>
        private Vector3 ResolveMuzzle(int muzzleIndex, out Transform muzzle)
        {
            muzzle = null;
            if (muzzlePoints != null && muzzlePoints.Length > 0)
            {
                if (muzzleIndex >= 0 && muzzleIndex < muzzlePoints.Length && muzzlePoints[muzzleIndex] != null)
                    muzzle = muzzlePoints[muzzleIndex];
                else if (muzzlePoints[0] != null)
                    muzzle = muzzlePoints[0];
            }
            return muzzle != null ? muzzle.position : transform.position;
        }

        /// <summary>
        /// Resolve a chain-lightning shot (GDD §7.2) for one mount. Starting at the
        /// primary target, apply damage, draw a tracer from the previous point, then
        /// find the nearest enemy not already hit within <see cref="chainJumpRange"/>
        /// of the current target and repeat — up to the mount's chainTargets hits.
        /// Damage is multiplied by the mount's chainFalloff on every jump.
        /// </summary>
        private void FireChain(Vector3 origin, Enemy primary, TowerWeaponMount weapon, float baseDamage)
        {
            _chainHits.Clear();

            float falloff = weapon.chainFalloff > 0f ? weapon.chainFalloff : 1f;
            int maxHits = Mathf.Max(1, weapon.chainTargets);
            float rangeSqr = chainJumpRange * chainJumpRange;

            Enemy current = primary;
            Vector3 fromPoint = origin;
            float damageThisHit = baseDamage;

            for (int hop = 0; hop < maxHits && current != null; hop++)
            {
                DrawTracer(fromPoint, current.HitPoint, chainColor);
                if (VFXDirector.Instance != null)
                    VFXDirector.Instance.PlayImpactEffective(current.HitPoint, Multiplier(current), current.ArmourType);

                float dealt = ApplyDamage(current, damageThisHit);
                _chainHits.Add(current);

                Debug.Log($"[Corehold] Arc chain hop {hop + 1}/{maxHits} on {current.name}: " +
                          $"{dealt:0.##} dmg (falloff factor {Mathf.Pow(falloff, hop):0.###}).");

                fromPoint = current.HitPoint;
                current = NearestUnhit(fromPoint, rangeSqr);
                damageThisHit *= falloff;
            }
        }

        /// <summary>Half-width (m) of the Railgun pierce corridor, plus each victim's body radius.</summary>
        private const float PierceHalfWidth = 0.7f;

        /// <summary>
        /// Railgun shot (roster): full damage to EVERY live enemy whose body lies
        /// within <see cref="PierceHalfWidth"/> of the line from the muzzle through
        /// the primary target, out to the turret's effective range. One long tracer,
        /// impacts on each victim. Air/ground eligibility follows the targeting gate
        /// (a pierce cannot hit what the turret may not target).
        /// </summary>
        private void FirePierce(Vector3 origin, Enemy primary, float damage, Color trace)
        {
            float reach = _targeting != null ? Mathf.Max(4f, _targeting.Range) : 30f;
            Vector3 dir = primary.HitPoint - origin;
            if (dir.sqrMagnitude < 0.0001f)
                dir = transform.forward;
            dir.Normalize();
            Vector3 end = origin + dir * reach;

            bool airOnly = definition != null && definition.targetAirOnly;
            bool canAir = definition != null && definition.canTargetAir;

            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive)
                    continue;
                if (e.IsAir ? !canAir : airOnly)
                    continue;

                // Distance from the segment origin→end to the enemy's hit point.
                Vector3 toE = e.HitPoint - origin;
                float along = Vector3.Dot(toE, dir);
                if (along < 0f || along > reach)
                    continue;
                Vector3 closest = origin + dir * along;
                float lateral = (e.HitPoint - closest).magnitude;
                if (lateral > PierceHalfWidth + e.BodyRadius)
                    continue;

                ApplyDamage(e, damage);
                if (VFXDirector.Instance != null)
                    VFXDirector.Instance.PlayImpactEffective(e.HitPoint, Multiplier(e), e.ArmourType);
            }

            DrawTracer(origin, end, trace);
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayImpact();
        }

        /// <summary>
        /// Nearest live enemy to <paramref name="fromPoint"/> that has not yet been
        /// hit in the current chain and lies within the squared jump range. Returns
        /// null when the chain has nowhere left to go.
        /// </summary>
        private Enemy NearestUnhit(Vector3 fromPoint, float rangeSqr)
        {
            Enemy best = null;
            float bestSqr = rangeSqr;

            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive || _chainHits.Contains(e))
                    continue;

                float d = (e.HitPoint - fromPoint).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    best = e;
                }
            }

            return best;
        }

        /// <summary>
        /// Draw a hitscan tracer for the Autocannon / Arc Node (GDD §11). Prefers the
        /// <see cref="VFXDirector"/>'s pooled additive tracer when one exists, and
        /// falls back to the legacy <see cref="ChainTracer"/> pool otherwise.
        /// </summary>
        private void DrawTracer(Vector3 from, Vector3 to, Color color)
        {
            if (VFXDirector.Instance != null)
                VFXDirector.Instance.DrawTracer(from, to, color);
            else
                ChainTracer.Draw(from, to, color);
        }

        /// <summary>
        /// Apply damage to one enemy with the damage-vs-armour multiplier (GDD §7.1),
        /// reusing the shared table wired at boot. Returns the final damage dealt.
        /// A hit that flips the enemy from alive to dead credits the owning tower's
        /// veterancy (R21) — attribution lives at the damage site, so Enemy never
        /// has to know what a Tower is.
        /// </summary>
        /// <summary>
        /// The damage-vs-armour multiplier this turret's damage type deals to the
        /// given enemy (GDD §7.1). Used to pick the counter-readable impact VFX so
        /// the effect matches the damage actually applied. Returns 1 when no table
        /// is wired (effects then read as neutral).
        /// </summary>
        private float Multiplier(Enemy enemy)
        {
            if (Projectile.SharedDamageTable == null || definition == null || enemy == null)
                return 1f;
            return Projectile.SharedDamageTable.Multiplier(definition.damageType, enemy.ArmourType);
        }

        private float ApplyDamage(Enemy enemy, float baseDamage)
        {
            float mult = Projectile.SharedDamageTable != null
                ? Projectile.SharedDamageTable.Multiplier(definition.damageType, enemy.ArmourType)
                : 1f;
            float dealt = baseDamage * mult;
            bool wasAlive = enemy.IsAlive;
            enemy.TakeDamage(dealt);
            if (wasAlive && !enemy.IsAlive && _tower != null)
                _tower.RegisterKill();
            return dealt;
        }
    }
}
