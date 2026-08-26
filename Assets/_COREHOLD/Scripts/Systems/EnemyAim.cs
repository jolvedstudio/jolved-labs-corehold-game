using Corehold.Enemies;
using Corehold.Towers;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Slews an enemy's TURRET (yaw ring + gun pitch pivot) toward the tower it is
    /// firing at — the enemy counterpart of the towers' <see cref="TurretAim"/>, and
    /// modelled on it deliberately so both sides aim identically. It rotates PIVOTS on
    /// the rig, never the whole enemy body: the hull stays put while the turret turns
    /// and the barrel elevates/depresses to the target's centre mass.
    ///
    /// The pivots are resolved automatically from the muzzle's ancestor chain, which
    /// on the COREHOLD kit rigs is
    ///   … / Mount_Top (turret ring)  / Cockpit_* (gun body) / … / Barrel_end
    /// so the yaw pivot is the highest "Mount_Top"/turret-ring ancestor and the pitch
    /// pivot is the "Cockpit_*" gun body the muzzle hangs from. Explicit assignments
    /// in the inspector always win; anything that cannot be resolved is simply not
    /// driven (the other axis still works).
    ///
    /// In the live game the <see cref="EnemyMover"/> faces the hull along the path and
    /// this turret aim sits on top of it; in the testbed the mover is stripped, so the
    /// turret is the only thing that moves — exactly like a stationary tower.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Enemy))]
    public class EnemyAim : MonoBehaviour
    {
        [Header("Pivots (auto-resolved from the muzzle if left empty)")]
        [Tooltip("Rotates around local Y to face the target horizontally (the turret ring). " +
                 "If none is found (a humanoid mech that holds its gun rather than mounting a " +
                 "turret), the enemy BODY is yawed instead — like a person turning to aim.")]
        [SerializeField] private Transform yawPivot;

        [Tooltip("Rotates around local X to elevate/depress toward the target (the gun body). " +
                 "Left null when the rig has no separate elevating part.")]
        [SerializeField] private Transform pitchPivot;

        [Tooltip("True when no turret ring was found and the body is yawed as the fallback. " +
                 "Serialized so it is visible on the prefab (humanoid mechs show this ticked).")]
        [SerializeField] private bool yawsBody;

        [Header("Slew speeds (deg/sec)")]
        [SerializeField] private float yawSpeed = 200f;
        [SerializeField] private float pitchSpeed = 140f;

        [Header("Pitch clamp (deg, local X)")]
        [Tooltip("Negative = barrel up in Unity's convention.")]
        [SerializeField] private float minPitch = -60f;
        [SerializeField] private float maxPitch = 30f;

        [Header("Targeting")]
        [Tooltip("How often (seconds) to re-pick the nearest tower.")]
        [SerializeField] private float reacquireInterval = 0.3f;

        private Enemy _enemy;
        private EnemyWeapon _weapon;
        private Tower _target;
        private float _nextReacquire;

        private float _yaw;
        private float _pitch;
        private bool _resolved;

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
            _weapon = GetComponent<EnemyWeapon>();
            ResolvePivots();
        }

        private void OnEnable()
        {
            _target = null;
            _nextReacquire = 0f;
            if (yawPivot != null) _yaw = NormaliseAngle(yawPivot.localEulerAngles.y);
            if (pitchPivot != null) _pitch = NormaliseAngle(pitchPivot.localEulerAngles.x);
        }

        /// <summary>
        /// Editor-only: force a fresh pivot resolution and serialize the result onto
        /// this component, so the enemy PREFAB carries wired pivots (visible in the
        /// inspector) exactly like a tower's TurretAim — rather than resolving blindly
        /// at runtime. Returns true if at least one pivot was found.
        /// </summary>
        public bool BakePivots()
        {
            _resolved = false;
            yawPivot = null;
            pitchPivot = null;
            yawsBody = false;
            ResolvePivots();
            // Always "succeeds" now: a rig with no turret ring falls back to body yaw.
            return true;
        }

        /// <summary>
        /// Find the yaw ring and pitch (gun) pivots from the muzzle's ancestor chain.
        /// Respects any explicit assignments. Idempotent.
        /// </summary>
        private void ResolvePivots()
        {
            if (_resolved)
                return;
            _resolved = true;

            if (_weapon == null)
                _weapon = GetComponent<EnemyWeapon>();

            Transform muzzle = _weapon != null ? _weapon.PrimaryMuzzle : null;
            if (muzzle == null)
                return;

            // Walk up from the muzzle collecting the chain to this transform.
            // yaw  = highest ancestor whose name marks a turret ring ("Mount_Top",
            //        "YawRing", "Turret"), i.e. the part that spins horizontally.
            // pitch = the gun-body ancestor ("Cockpit"/"Gun"/"Barrel_Pivot"/
            //        "Barrel_Base") that the muzzle hangs off, i.e. the part that
            //        elevates. Pitch must be a DESCENDANT of yaw so the two compose.
            Transform bestYaw = null;
            Transform bestPitch = null;

            for (Transform t = muzzle; t != null && t != transform; t = t.parent)
            {
                string n = t.name;
                if (bestPitch == null && IsPitchName(n))
                    bestPitch = t;
                if (IsYawName(n))
                    bestYaw = t; // keep climbing so the HIGHEST ring wins
            }

            if (yawPivot == null) yawPivot = bestYaw;
            if (pitchPivot == null) pitchPivot = bestPitch;

            // If pitch resolved to the same node as yaw (or above it), drop pitch so
            // we never rotate the ring on two axes.
            if (pitchPivot != null && yawPivot != null &&
                (pitchPivot == yawPivot || !IsDescendantOf(pitchPivot, yawPivot)))
            {
                pitchPivot = null;
            }

            // Humanoid mechs hold the gun in a hand — there is no turret ring to spin.
            // Fall back to yawing the whole BODY (this transform), like a person turning
            // to aim. Pitch stays on a pivot only if one was genuinely found; we never
            // pitch the body (that was the original wrong-looking behaviour).
            if (yawPivot == null)
            {
                yawPivot = transform;
                yawsBody = true;
            }
        }

        private static bool IsYawName(string n) =>
            n.IndexOf("Mount_Top", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("YawRing", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Turret", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsPitchName(string n) =>
            n.IndexOf("Barrel_Pivot", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Barrel_Base", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Cockpit", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Gun", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsDescendantOf(Transform node, Transform ancestor)
        {
            for (Transform t = node.parent; t != null; t = t.parent)
                if (t == ancestor) return true;
            return false;
        }

        private void Update()
        {
            if (_enemy == null || !_enemy.IsAlive)
                return;

            // When we fall back to yawing the BODY (humanoid mechs), the EnemyMover
            // owns hull facing during a live wave and would fight us. Defer to it:
            // only body-yaw here when there is no active mover steering the hull
            // (the testbed strips the mover, so this still turns them there). Turret
            // pivots are independent of the hull, so they always run.
            bool moverDrivesBody = yawsBody && _enemy.Mover != null && _enemy.Mover.enabled;

            if (Time.time >= _nextReacquire)
            {
                _nextReacquire = Time.time + reacquireInterval;
                _target = FindNearestTower();
            }
            if (_target == null || !_target.isActiveAndEnabled)
                return;

            Vector3 aimPoint = GeometricCenter.Of(_target.gameObject);
            if (!moverDrivesBody)
                SlewYaw(aimPoint);
            SlewPitch(aimPoint);
        }

        private void SlewYaw(Vector3 worldPoint)
        {
            if (yawPivot == null)
                return;

            Transform parent = yawPivot.parent;
            Vector3 local = parent != null
                ? parent.InverseTransformPoint(worldPoint) - yawPivot.localPosition
                : worldPoint - yawPivot.position;

            float desired = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float error = Mathf.DeltaAngle(_yaw, desired);
            float step = yawSpeed * Time.deltaTime;
            _yaw = NormaliseAngle(_yaw + Mathf.Clamp(error, -step, step));

            Vector3 e = yawPivot.localEulerAngles;
            e.y = _yaw;
            yawPivot.localEulerAngles = e;
        }

        private void SlewPitch(Vector3 worldPoint)
        {
            if (pitchPivot == null)
                return;

            Transform parent = pitchPivot.parent;
            Vector3 local = parent != null
                ? parent.InverseTransformPoint(worldPoint) - pitchPivot.localPosition
                : worldPoint - pitchPivot.position;

            float horizontal = new Vector2(local.x, local.z).magnitude;
            float desired = -Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg;
            desired = Mathf.Clamp(desired, minPitch, maxPitch);

            float error = Mathf.DeltaAngle(_pitch, desired);
            float step = pitchSpeed * Time.deltaTime;
            _pitch = Mathf.Clamp(NormaliseAngle(_pitch + Mathf.Clamp(error, -step, step)), minPitch, maxPitch);

            Vector3 e = pitchPivot.localEulerAngles;
            e.x = _pitch;
            pitchPivot.localEulerAngles = e;
        }

        private Tower FindNearestTower()
        {
            Tower best = null;
            float bestSqr = float.MaxValue;
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
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = tw;
                }
            }
            return best;
        }

        private static float NormaliseAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle <= -180f) angle += 360f;
            return angle;
        }
    }
}
