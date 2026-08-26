using Corehold.Towers;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Turreted enemies: slews a yaw (and optionally pitch) pivot onto whatever
    /// this unit's <see cref="EnemyWeapon"/> is shooting at, so a tank's turret
    /// tracks the turret it is killing instead of staring rigidly forward while
    /// tracers leave it sideways. The enemy counterpart of
    /// <see cref="Corehold.Towers.TurretAim"/>.
    ///
    /// <b>PURELY COSMETIC, and that is a deliberate contract.</b> TurretAim gates
    /// tower firing on <c>IsAimed</c>, which is what makes towers feel
    /// mechanical. This does NOT gate enemy firing: an enemy's return fire is
    /// damage arriving at the player's turrets, and making it wait for a slew
    /// would quietly reduce enemy DPS-on-target — a balance change wearing a
    /// visual-polish costume. Rounds leave exactly when EnemyWeapon says they
    /// do; only the model turns.
    ///
    /// Pivots are optional and independent: a chassis with only a rotating
    /// turret ring wires yaw alone, one with an elevating gun wires both, and a
    /// unit with neither simply does not carry this component. Aim is computed
    /// in each pivot's PARENT space, so it composes correctly with a body that
    /// is itself walking, turning and leaning on terrain grades (M-b).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EnemyWeapon))]
    public class EnemyAim : MonoBehaviour
    {
        [Header("Pivots (either may be empty)")]
        [Tooltip("Transform that rotates about its local Y to face the target — the turret ring.")]
        [SerializeField] private Transform yawPivot;

        [Tooltip("Transform that rotates about its local X to elevate — the gun mantlet. May be the same object as the yaw pivot, or empty.")]
        [SerializeField] private Transform pitchPivot;

        [Header("Slew")]
        [Tooltip("Degrees per second the yaw pivot turns.")]
        [SerializeField] private float yawSpeed = 90f;

        [Tooltip("Degrees per second the pitch pivot elevates.")]
        [SerializeField] private float pitchSpeed = 60f;

        [Tooltip("Elevation limits in degrees (negative = barrel up).")]
        [SerializeField] private float minPitch = -35f;
        [SerializeField] private float maxPitch = 12f;

        [Tooltip("With no target, pivots return to their neutral pose at this fraction of the slew speed. 0 leaves them wherever they stopped.")]
        [Range(0f, 1f)] [SerializeField] private float recentreFraction = 0.5f;

        private EnemyWeapon _weapon;

        /// <summary>The yaw pivot, for tooling that wires this at build time.</summary>
        public Transform YawPivot { get => yawPivot; set => yawPivot = value; }

        /// <summary>The pitch pivot, for tooling that wires this at build time.</summary>
        public Transform PitchPivot { get => pitchPivot; set => pitchPivot = value; }

        private void Awake()
        {
            _weapon = GetComponent<EnemyWeapon>();
        }

        private void LateUpdate()
        {
            // LateUpdate so the body has already moved and faced this frame —
            // aiming off a stale body pose reads as a turret that lags a frame
            // behind its own hull.
            if (yawPivot == null && pitchPivot == null)
                return;

            Tower target = _weapon != null ? _weapon.PrimaryTarget : null;
            float dt = Time.deltaTime;

            if (target == null)
            {
                Recentre(dt);
                return;
            }

            Vector3 point = target.transform.position + Vector3.up * 0.8f;

            if (yawPivot != null)
            {
                Vector3 local = ToParentSpace(yawPivot, point - yawPivot.position);
                if (local.sqrMagnitude > 0.0001f)
                {
                    float desired = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                    yawPivot.localRotation = Quaternion.RotateTowards(
                        yawPivot.localRotation, Quaternion.Euler(0f, desired, 0f), yawSpeed * dt);
                }
            }

            if (pitchPivot != null && pitchPivot != yawPivot)
            {
                Vector3 local = ToParentSpace(pitchPivot, point - pitchPivot.position);
                float horizontal = new Vector2(local.x, local.z).magnitude;
                if (horizontal > 0.0001f || Mathf.Abs(local.y) > 0.0001f)
                {
                    // Unity pitches nose-DOWN on +X, hence the negation.
                    float desired = Mathf.Clamp(
                        -Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg, minPitch, maxPitch);
                    pitchPivot.localRotation = Quaternion.RotateTowards(
                        pitchPivot.localRotation, Quaternion.Euler(desired, 0f, 0f), pitchSpeed * dt);
                }
            }
        }

        /// <summary>Ease pivots back to neutral when nothing is being engaged.</summary>
        private void Recentre(float dt)
        {
            if (recentreFraction <= 0f)
                return;
            if (yawPivot != null)
                yawPivot.localRotation = Quaternion.RotateTowards(
                    yawPivot.localRotation, Quaternion.identity, yawSpeed * recentreFraction * dt);
            if (pitchPivot != null && pitchPivot != yawPivot)
                pitchPivot.localRotation = Quaternion.RotateTowards(
                    pitchPivot.localRotation, Quaternion.identity, pitchSpeed * recentreFraction * dt);
        }

        /// <summary>A world direction expressed in a pivot's PARENT space (identity
        /// when the pivot is a root), which is what makes the aim compose with a
        /// moving, turning, grade-leaning hull.</summary>
        private static Vector3 ToParentSpace(Transform pivot, Vector3 worldDir)
        {
            return pivot.parent != null ? pivot.parent.InverseTransformDirection(worldDir) : worldDir;
        }
    }
}
