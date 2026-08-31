using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// One upgrade tier of a turret (GDD §12.2, §7.3). Not an asset — a serializable
    /// struct embedded in the <see cref="TowerDefinition.tiers"/> array (three per turret).
    /// A tier upgrade swaps the weapon child mesh and leaves the base untouched.
    ///
    /// A turret can mount MORE THAN ONE weapon (twin autocannon barrels, or a cannon
    /// paired with a grenade launcher), so per-shot combat data is authored as the
    /// <see cref="weapons"/> ARRAY of <see cref="TowerWeaponMount"/>. Range, min-range
    /// and the support aura stay tier-level (shared targeting/economy); every per-shot
    /// combat value (damage, fire rate, projectile, chain, splash, muzzle, tracer)
    /// lives on each mount.
    ///
    /// The legacy single-weapon fields (<see cref="damage"/>, <see cref="fireRate"/>,
    /// <see cref="chainTargets"/>, ...) are retained so definitions authored before the
    /// array existed still deserialize; <see cref="Weapons"/> falls back to synthesising
    /// a single mount from them when the array is empty.
    /// </summary>
    [System.Serializable]
    public struct TowerTier
    {
        [Header("Economy")]
        [Tooltip("Salvage cost to reach this tier (not cumulative).")]
        public int cost;

        [Header("Weapons")]
        [Tooltip("Every weapon this tier mounts. Each entry fires independently at its own rate, damage, projectile and muzzle. Turrets may have several (twin barrels, cannon + launcher). Leave empty to use the legacy single-weapon fields below.")]
        public TowerWeaponMount[] weapons;

        [Header("Targeting (shared across all weapons)")]
        [Tooltip("Maximum 3D range in metres.")]
        public float range;

        [Tooltip("Minimum range in metres (Siege Mortar has a 6 m dead zone). 0 = none.")]
        public float minRange;

        [Header("Legacy single-weapon combat (migrated into weapons[0] when array empty)")]
        [Tooltip("Damage per shot (before the damage/armour multiplier).")]
        public float damage;

        [Tooltip("Shots per second.")]
        public float fireRate;

        [Tooltip("Splash radius in metres. 0 = single target. Damage falls off linearly to 40% at the edge.")]
        public float splashRadius;

        [Tooltip("Number of targets a chain hits. 0/1 = no chaining.")]
        public int chainTargets;

        [Tooltip("Damage multiplier retained per chain jump (e.g. 0.70 = 70% of previous target).")]
        public float chainFalloff;

        [Tooltip("Projectile prefab. Null = hitscan.")]
        public GameObject projectilePrefab;

        [Tooltip("Projectile travel speed in m/s (used for leading intercept).")]
        public float projectileSpeed;

        [Header("Visuals & Audio")]
        [Tooltip("Index of the weapon child mesh to enable for this tier.")]
        public int weaponVisualIndex;

        [Tooltip("Muzzle flash VFX spawned on fire.")]
        public GameObject muzzleVfx;

        [Tooltip("Sound played on fire.")]
        public AudioClip fireSfx;

        [Header("Shield (damage-absorbing barrier, GDD §7 — authored per tier)")]
        [Tooltip("Shield hit points. A damage-absorbing buffer that soaks hits BEFORE the turret's health; 0 = this turret has no shield. Overflow damage from a hit that empties the shield carries through to health.")]
        public float shieldHitPoints;

        [Tooltip("Shield points regenerated per second once regen kicks in. 0 = the shield never recharges (one-time buffer).")]
        public float shieldRegenPerSec;

        [Tooltip("Seconds of NOT being hit before the shield starts regenerating. Prevents a shield from healing mid-focus-fire.")]
        public float shieldRegenDelay;

        [Header("Scan Relay aura")]
        [Tooltip("Aura radius in metres (support turret only).")]
        public float auraRadius;

        [Tooltip("Fire rate bonus granted to turrets in the aura (e.g. 0.15 = +15%).")]
        public float auraFireRateBonus;

        [Tooltip("Range bonus granted to turrets in the aura (e.g. 0.10 = +10%).")]
        public float auraRangeBonus;

        [Tooltip("Damage bonus granted to turrets in the aura (e.g. 0.10 = +10%).")]
        public float auraDamageBonus;

        /// <summary>
        /// The weapons this tier mounts. Returns the authored <see cref="weapons"/>
        /// array when populated; otherwise synthesises a single mount from the legacy
        /// combat fields so pre-array definitions keep firing. Never returns null.
        /// </summary>
        public TowerWeaponMount[] Weapons
        {
            get
            {
                if (weapons != null && weapons.Length > 0)
                    return weapons;

                // Legacy fallback: one mount from the flat fields. A zero fire rate
                // means a non-combat tier (e.g. Scan Relay) — still return the mount
                // so callers can inspect it, TowerWeapon skips zero-rate mounts.
                return new[]
                {
                    new TowerWeaponMount
                    {
                        damage = damage,
                        fireRate = fireRate,
                        muzzleIndex = -1,
                        chainTargets = chainTargets,
                        chainFalloff = chainFalloff,
                        projectilePrefab = projectilePrefab,
                        projectileSpeed = projectileSpeed,
                        splashRadius = splashRadius,
                        tracerColor = new Color(3.5f, 2.2f, 0.8f, 1f)
                    }
                };
            }
        }

        /// <summary>
        /// Total shots-per-second across every mounted weapon — used as the tier's
        /// representative fire rate for support-aura buffing and UI DPS estimates.
        /// Falls back to the legacy <see cref="fireRate"/> when no array is authored.
        /// </summary>
        public float TotalFireRate
        {
            get
            {
                if (weapons == null || weapons.Length == 0)
                    return fireRate;
                float sum = 0f;
                for (int i = 0; i < weapons.Length; i++)
                    sum += Mathf.Max(0f, weapons[i].fireRate);
                return sum;
            }
        }

        /// <summary>
        /// Total damage-per-shot across every mounted weapon — used for UI DPS
        /// estimates. Falls back to the legacy <see cref="damage"/> when no array is
        /// authored.
        /// </summary>
        public float TotalDamagePerVolley
        {
            get
            {
                if (weapons == null || weapons.Length == 0)
                    return damage;
                float sum = 0f;
                for (int i = 0; i < weapons.Length; i++)
                    sum += Mathf.Max(0f, weapons[i].damage);
                return sum;
            }
        }

        /// <summary>
        /// True damage-per-second across every mounted weapon — the sum of each
        /// mount's (damage × fire rate), NOT the product of the summed totals (which
        /// would overstate a multi-weapon turret). Falls back to the legacy
        /// <see cref="damage"/> × <see cref="fireRate"/> when no array is authored.
        /// </summary>
        public float TotalDps
        {
            get
            {
                if (weapons == null || weapons.Length == 0)
                    return damage * fireRate;
                float sum = 0f;
                for (int i = 0; i < weapons.Length; i++)
                    sum += Mathf.Max(0f, weapons[i].damage) * Mathf.Max(0f, weapons[i].fireRate);
                return sum;
            }
        }
    }
}
