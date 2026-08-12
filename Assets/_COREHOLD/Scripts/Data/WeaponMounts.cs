using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// One firing point on an enemy (GDD §6.x). Enemies and turrets can carry more
    /// than one weapon (e.g. a cannon plus a grenade launcher), so weapon data is
    /// authored as an ARRAY of these mounts rather than a single set of fields. Each
    /// mount fires independently on its own cadence at its own target-in-range, with
    /// its own tracer colour.
    ///
    /// A SINGLE weapon can also have MORE THAN ONE muzzle it alternates between (a
    /// minigun, or a twin barrel that fires left-then-right for flavour but is one
    /// gun). That is modelled by <see cref="muzzles"/>: when it holds more than one
    /// transform the mount round-robins through them per shot WITHOUT changing its
    /// fire rate or damage. Leave it empty and set <see cref="muzzle"/> for the
    /// ordinary single-muzzle case. Contrast this with authoring two array entries,
    /// which is two independent weapons (double the shots).
    /// </summary>
    [System.Serializable]
    public struct EnemyWeaponMount
    {
        [Tooltip("Maximum range in metres at which this weapon can hit a turret.")]
        public float range;

        [Tooltip("Shots per second for this weapon.")]
        public float fireRate;

        [Tooltip("Damage per shot dealt to a turret by this weapon.")]
        public float damage;

        [Tooltip("Primary/fallback muzzle. Used when the Muzzles list is empty. Falls back to the enemy transform + 1m up if unset.")]
        public Transform muzzle;

        [Tooltip("Optional list of muzzles this ONE weapon alternates between per shot (twin barrel / minigun). More than one entry cycles round-robin with NO change to fire rate or damage. Empty = use Muzzle.")]
        public Transform[] muzzles;

        [Tooltip("Colour of this weapon's tracer bolt (hostile orange, HDR-bright).")]
        [ColorUsage(true, true)]
        public Color tracerColor;

        /// <summary>
        /// Resolve the muzzle transform to fire from for shot number
        /// <paramref name="shotIndex"/>. When <see cref="muzzles"/> holds more than
        /// one entry the mount round-robins through them; otherwise the single
        /// <see cref="muzzle"/> (or the first entry of the list) is used. May be null,
        /// in which case the caller falls back to the enemy transform.
        /// </summary>
        public Transform ResolveMuzzle(int shotIndex)
        {
            if (muzzles != null && muzzles.Length > 0)
            {
                int i = ((shotIndex % muzzles.Length) + muzzles.Length) % muzzles.Length;
                if (muzzles[i] != null)
                    return muzzles[i];
            }
            return muzzle;
        }
    }

    /// <summary>
    /// One firing point on a turret tier (GDD §7.2, §7.4). A single turret tier can
    /// mount several of these — a cannon paired with a grenade launcher — so weapon
    /// data is authored as an ARRAY on the <see cref="TowerTier"/>. Each mount fires
    /// independently on its own cadence and damage. Range/min-range and the support
    /// aura remain tier-level (shared targeting), but every per-shot combat value
    /// lives here.
    ///
    /// A SINGLE weapon can also alternate between MORE THAN ONE muzzle (twin barrel /
    /// minigun): set <see cref="muzzleIndices"/> to the muzzle-point indices it cycles
    /// through per shot — this is purely visual and does NOT change fire rate or DPS.
    /// Leave it empty and use <see cref="muzzleIndex"/> for the ordinary single-muzzle
    /// case. Contrast this with authoring two array entries, which is two independent
    /// weapons (double the shots).
    /// </summary>
    [System.Serializable]
    public struct TowerWeaponMount
    {
        [Tooltip("Damage per shot for this weapon (before the damage/armour multiplier).")]
        public float damage;

        [Tooltip("Shots per second for this weapon.")]
        public float fireRate;

        [Tooltip("Primary/fallback index into TowerWeapon.muzzlePoints. Used when Muzzle Indices is empty. -1 = fallback muzzle.")]
        public int muzzleIndex;

        [Tooltip("Optional muzzle-point indices this ONE weapon alternates between per shot (twin barrel / minigun). More than one entry cycles round-robin with NO change to fire rate or damage. Empty = use Muzzle Index.")]
        public int[] muzzleIndices;

        [Header("Arc chain (Arc Node)")]
        [Tooltip("Number of targets a chain hits. 0/1 = no chaining.")]
        public int chainTargets;

        [Tooltip("Damage multiplier retained per chain jump (e.g. 0.70 = 70% of previous target).")]
        public float chainFalloff;

        [Header("Projectile")]
        [Tooltip("Projectile prefab. Null = hitscan.")]
        public GameObject projectilePrefab;

        [Tooltip("Projectile travel speed in m/s (used for leading intercept).")]
        public float projectileSpeed;

        [Tooltip("Splash radius in metres. 0 = single target. Damage falls off linearly to 40% at the edge.")]
        public float splashRadius;

        [Header("Visuals")]
        [Tooltip("Colour of this weapon's hitscan tracer bolt. HDR-bright.")]
        [ColorUsage(true, true)]
        public Color tracerColor;

        /// <summary>
        /// Resolve the muzzle-point INDEX to fire from for shot number
        /// <paramref name="shotIndex"/>. When <see cref="muzzleIndices"/> holds more
        /// than one entry the mount round-robins through them; otherwise the single
        /// <see cref="muzzleIndex"/> (or the first entry of the list) is returned.
        /// </summary>
        public int ResolveMuzzleIndex(int shotIndex)
        {
            if (muzzleIndices != null && muzzleIndices.Length > 0)
            {
                int i = ((shotIndex % muzzleIndices.Length) + muzzleIndices.Length) % muzzleIndices.Length;
                return muzzleIndices[i];
            }
            return muzzleIndex;
        }
    }
}
