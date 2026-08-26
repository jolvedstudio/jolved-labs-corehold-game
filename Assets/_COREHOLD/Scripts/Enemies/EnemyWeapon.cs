using UnityEngine;
using Corehold.Data;
using Corehold.Towers;

namespace Corehold.Enemies
{
    /// <summary>
    /// Lets an enemy shoot back at nearby turrets. An enemy can carry MORE THAN ONE
    /// weapon (e.g. twin barrels, or a cannon plus a grenade launcher), so combat
    /// data is authored as an ARRAY of <see cref="EnemyWeaponMount"/> — one entry
    /// per firing point. Each mount independently finds the nearest live
    /// <see cref="Tower"/> within its own range that has a <see cref="TowerHealth"/>,
    /// and fires at it on its own fire-rate cadence for its own damage, from its own
    /// muzzle, drawing its own tracer colour. Uses the <see cref="Tower.Live"/>
    /// registry — no physics queries.
    ///
    /// Legacy single-weapon prefabs (authored before the array existed) are migrated
    /// on load: the old range/fireRate/damage/muzzle/tracerColor fields become the
    /// first entry of the array if the array is empty.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Enemy))]
    public class EnemyWeapon : MonoBehaviour
    {
        [Header("Weapons (GDD §6.x)")]
        [Tooltip("Every weapon this enemy carries. Each entry fires independently at its own range, rate, damage and muzzle. Enemies may have several (twin barrels, cannon + launcher).")]
        [SerializeField] private EnemyWeaponMount[] weapons = new EnemyWeaponMount[0];

        // ----- Legacy single-weapon fields (migrated into weapons[0] on load) -----
        // Kept only so prefabs authored before the array existed still deserialize
        // and migrate cleanly. Hidden from the inspector; not used at runtime once
        // migration has copied them into the array.
        [HideInInspector] [SerializeField] private float range = 12f;
        [HideInInspector] [SerializeField] private float fireRate = 0.8f;
        [HideInInspector] [SerializeField] private float damage = 10f;
        [HideInInspector] [SerializeField] private Transform muzzle;
        [HideInInspector] [SerializeField] private Color tracerColor = new Color(4f, 1.2f, 0.3f, 1f);

        private Enemy _enemy;

        // Per-mount runtime state, parallel to the weapons array.
        private Tower[] _targets;
        private float[] _cooldowns;
        private float[] _nextReacquire;
        private int[] _shotCounts; // advances per shot so multi-muzzle mounts cycle.

        private const float ReacquireInterval = 0.3f;

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
            MigrateLegacy();
            AllocateRuntimeState();
        }

        private void OnValidate()
        {
            MigrateLegacy();
        }

        private void OnEnable()
        {
            AllocateRuntimeState();
            for (int i = 0; i < _targets.Length; i++)
            {
                _targets[i] = null;
                _cooldowns[i] = 0f;
                _nextReacquire[i] = 0f;
                _shotCounts[i] = 0;
            }
        }

        /// <summary>
        /// Copy the legacy single-weapon fields into the array's first entry when the
        /// array is empty, so prefabs authored before the array existed keep working.
        /// </summary>
        private void MigrateLegacy()
        {
            if (weapons != null && weapons.Length > 0)
                return;

            weapons = new[]
            {
                new EnemyWeaponMount
                {
                    range = range,
                    fireRate = fireRate,
                    damage = damage,
                    muzzle = muzzle,
                    tracerColor = tracerColor
                }
            };
        }

        private void AllocateRuntimeState()
        {
            int n = weapons != null ? weapons.Length : 0;
            if (_targets == null || _targets.Length != n)
            {
                _targets = new Tower[n];
                _cooldowns = new float[n];
                _nextReacquire = new float[n];
                _shotCounts = new int[n];
            }
        }

        private void Update()
        {
            if (_enemy == null || !_enemy.IsAlive || weapons == null || weapons.Length == 0)
                return;

            AllocateRuntimeState();

            for (int i = 0; i < weapons.Length; i++)
                TickWeapon(i);
        }

        private void TickWeapon(int i)
        {
            EnemyWeaponMount w = weapons[i];
            if (w.fireRate <= 0f || w.damage <= 0f)
                return;

            if (_cooldowns[i] > 0f)
                _cooldowns[i] -= Time.deltaTime;

            // Drop a target that died or was deactivated between ticks.
            if (_targets[i] != null && !_targets[i].isActiveAndEnabled)
                _targets[i] = null;

            if (Time.time >= _nextReacquire[i])
            {
                _nextReacquire[i] = Time.time + ReacquireInterval;
                _targets[i] = FindNearestTower(w.range);
            }

            Tower target = _targets[i];
            if (target == null)
                return;

            // Resolve the muzzle for the NEXT shot so a single weapon with several
            // muzzles alternates between them (twin barrel / minigun) — purely visual,
            // no change to fire rate or damage.
            Transform muzzleXf = w.ResolveMuzzle(_shotCounts[i]);
            Vector3 origin = muzzleXf != null ? muzzleXf.position : transform.position + Vector3.up;
            // Aim at the tower's GEOMETRIC CENTRE (centre mass), not its root at the
            // feet — so the tracer runs level from the barrel into the body instead of
            // angling down at the ground.
            Vector3 targetPoint = Corehold.Systems.GeometricCenter.Of(target.gameObject);

            if ((targetPoint - origin).sqrMagnitude > w.range * w.range)
                return;

            if (_cooldowns[i] <= 0f)
                Fire(i, w, target, origin, targetPoint);
        }

        private Tower FindNearestTower(float weaponRange)
        {
            Tower best = null;
            float bestSqr = weaponRange * weaponRange;
            Vector3 pos = transform.position;

            var towers = Tower.Live;
            for (int i = 0; i < towers.Count; i++)
            {
                var tw = towers[i];
                if (tw == null || !tw.isActiveAndEnabled)
                    continue;
                if (tw.GetComponent<TowerHealth>() == null)
                    continue;

                float d = (tw.transform.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    best = tw;
                }
            }
            return best;
        }

        private void Fire(int i, EnemyWeaponMount w, Tower target, Vector3 origin, Vector3 targetPoint)
        {
            _cooldowns[i] = 1f / w.fireRate;
            _shotCounts[i]++; // advance so the next shot uses the next muzzle in the cycle.

            var health = target.GetComponent<TowerHealth>();
            if (health != null)
                health.TakeDamage(w.damage);

            if (Corehold.Systems.VFXDirector.Instance != null)
            {
                Corehold.Systems.VFXDirector.Instance.DrawTracer(origin, targetPoint, w.tracerColor);
                Corehold.Systems.VFXDirector.Instance.PlayImpact(targetPoint);
            }

            // Fire SFX (GDD §10). Plays the clip authored on this enemy's definition;
            // no-ops silently when the enemy has no definition or no fire clip.
            if (Corehold.Systems.AudioDirector.Instance != null && _enemy != null)
                Corehold.Systems.AudioDirector.Instance.PlayEnemyFire(_enemy.Definition);
        }

        /// <summary>
        /// Configure combat stats at spawn (from an EnemyDefinition, optional).
        /// Applies uniformly to every mounted weapon; a positive value overrides the
        /// authored value on each mount, a non-positive value leaves it unchanged.
        /// </summary>
        public void Configure(float newRange, float newFireRate, float newDamage)
        {
            MigrateLegacy();
            if (weapons == null)
                return;

            for (int i = 0; i < weapons.Length; i++)
            {
                if (newRange > 0f) weapons[i].range = newRange;
                if (newFireRate > 0f) weapons[i].fireRate = newFireRate;
                if (newDamage > 0f) weapons[i].damage = newDamage;
            }
        }

        /// <summary>Number of weapons this enemy carries.</summary>
        public int WeaponCount => weapons != null ? weapons.Length : 0;

        /// <summary>
        /// The primary muzzle transform (first weapon, first barrel), or null if none
        /// is authored. Used by <see cref="EnemyAim"/> to anchor its pivot search on
        /// the actual firing point.
        /// </summary>
        public Transform PrimaryMuzzle
        {
            get
            {
                MigrateLegacy();
                if (weapons != null && weapons.Length > 0)
                {
                    var m = weapons[0].ResolveMuzzle(0);
                    if (m != null) return m;
                }
                return muzzle;
            }
        }
    }
}
