using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Slews a turret's yaw and pitch pivots toward a world point (GDD §7.4).
    ///
    /// Yaw rotates <see cref="yawPivot"/> around its local Y axis; pitch rotates
    /// <see cref="pitchPivot"/> around its local X axis. Each axis is moved at
    /// most its speed (degrees per second) toward the solution, using
    /// shortest-arc interpolation so it always takes the shorter way round. The
    /// yaw angle is normalised to (-180, 180] every frame so it can never unwind
    /// past 360°, and pitch is clamped to a mechanically sane -10°..+60°.
    ///
    /// <see cref="IsAimed"/> is true only when both axes are within
    /// <see cref="aimTolerance"/> degrees of their target — <c>TowerWeapon</c>
    /// gates firing on this.
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretAim : MonoBehaviour
    {
        [Header("Pivots")]
        [Tooltip("Rotates around local Y to face the target horizontally.")]
        [SerializeField] private Transform yawPivot;

        [Tooltip("Rotates around local X to elevate/depress toward the target.")]
        [SerializeField] private Transform pitchPivot;

        [Header("Slew speeds (deg/sec)")]
        [Tooltip("Maximum yaw slew speed in degrees per second.")]
        [SerializeField] private float yawSpeed = 180f;

        [Tooltip("Maximum pitch slew speed in degrees per second.")]
        [SerializeField] private float pitchSpeed = 120f;

        [Header("Aim gate")]
        [Tooltip("Both axes must be within this many degrees of the solution for IsAimed to be true.")]
        [SerializeField] private float aimTolerance = 6f;

        [Header("Idle scan (idle = continuous scan)")]
        [Tooltip("When no target is present the yaw pivot slowly sweeps as if scanning the environment.")]
        [SerializeField] private bool idleScan = true;

        [Tooltip("Idle scan yaw speed in degrees per second (slow, so it reads as scanning, not slewing).")]
        [SerializeField] private float idleScanSpeed = 22f;

        [Tooltip("How far to either side of the rest yaw the idle sweep travels, in degrees.")]
        [SerializeField] private float idleScanArc = 70f;

        // Pitch clamp (degrees), local X. Negative = barrel up in Unity's convention.
        private const float MinPitch = -10f;
        private const float MaxPitch = 60f;

        // Current local angles maintained by this component (degrees).
        private float _yaw;
        private float _pitch;

        // The most recent per-axis error to the solution (degrees), set in AimAt.
        private float _yawError = 180f;
        private float _pitchError = 180f;

        /// <summary>True when both yaw and pitch are within aimTolerance of the target.</summary>
        public bool IsAimed =>
            Mathf.Abs(_yawError) <= aimTolerance &&
            Mathf.Abs(_pitchError) <= aimTolerance;

        // Frame on which AimAt was last called and whether it produced real motion,
        // so external systems (the audio rotation loop, GDD §10) can ask whether the
        // turret is actively slewing right now.
        private int _lastAimFrame = -1;
        private bool _movedLastAim;

        /// <summary>
        /// True when the turret is actively slewing this frame (GDD §10): AimAt was
        /// called on the current frame and at least one axis is still turning toward
        /// its solution (i.e. not yet aimed). Used to gate the rotation-loop SFX so
        /// it only plays while a turret is genuinely moving.
        /// </summary>
        public bool IsSlewing => _lastAimFrame == Time.frameCount && _movedLastAim;

        /// <summary>The yaw pivot this aim controller drives.</summary>
        public Transform YawPivot { get => yawPivot; set => yawPivot = value; }

        /// <summary>The pitch pivot this aim controller drives.</summary>
        public Transform PitchPivot { get => pitchPivot; set => pitchPivot = value; }

        // Rest yaw captured at Awake; the idle sweep oscillates around this.
        private float _restYaw;
        private float _scanPhase;

        private void Awake()
        {
            if (yawPivot != null)
                _yaw = NormaliseAngle(yawPivot.localEulerAngles.y);
            if (pitchPivot != null)
                _pitch = NormaliseAngle(pitchPivot.localEulerAngles.x);
            _restYaw = _yaw;
            // Randomise the starting phase so a row of turrets does not scan in unison.
            _scanPhase = (GetInstanceID() % 628) * 0.01f;
        }

        /// <summary>
        /// Called every frame by <c>TowerWeapon</c> when the turret has NO target.
        /// Slowly sweeps the yaw pivot back and forth around its rest angle and eases
        /// the barrel to level, so an idle turret reads as scanning the environment
        /// rather than frozen. No-op when idleScan is disabled.
        /// </summary>
        public void Idle()
        {
            if (!idleScan || yawPivot == null)
            {
                _lastAimFrame = Time.frameCount;
                _movedLastAim = false;
                return;
            }

            _scanPhase += idleScanSpeed * Mathf.Deg2Rad * Time.deltaTime;
            float desiredYaw = _restYaw + Mathf.Sin(_scanPhase) * idleScanArc;

            float yawError = Mathf.DeltaAngle(_yaw, desiredYaw);
            float yawStep = idleScanSpeed * Time.deltaTime;
            _yaw = NormaliseAngle(_yaw + Mathf.Clamp(yawError, -yawStep, yawStep));

            Vector3 ey = yawPivot.localEulerAngles;
            ey.y = _yaw;
            yawPivot.localEulerAngles = ey;

            // Ease pitch back toward level so an idle barrel is not left elevated.
            if (pitchPivot != null)
            {
                float pitchStep = pitchSpeed * 0.5f * Time.deltaTime;
                _pitch = Mathf.MoveTowards(_pitch, 0f, pitchStep);
                Vector3 ep = pitchPivot.localEulerAngles;
                ep.x = _pitch;
                pitchPivot.localEulerAngles = ep;
            }

            // Idle scanning is deliberate motion but NOT slewing-to-target: report
            // not-aimed and not-slewing so firing stays gated and the slew SFX is
            // silent while merely scanning.
            _yawError = 180f;
            _pitchError = 180f;
            _lastAimFrame = Time.frameCount;
            _movedLastAim = false;
        }

        /// <summary>
        /// Slew both pivots one frame toward <paramref name="worldPoint"/>. Call
        /// every frame from the owning <c>TowerWeapon</c> with the current target.
        /// </summary>
        public void AimAt(Vector3 worldPoint)
        {
            SlewYaw(worldPoint);
            SlewPitch(worldPoint);

            // Record slewing state for external systems (rotation-loop SFX, GDD §10).
            // "Slewing" means AimAt ran this frame and at least one axis is still
            // outside the aim tolerance, i.e. genuinely turning toward the target.
            _lastAimFrame = Time.frameCount;
            _movedLastAim = !IsAimed;
        }

        private void SlewYaw(Vector3 worldPoint)
        {
            if (yawPivot == null)
            {
                _yawError = 0f;
                return;
            }

            // Direction to target in the yaw pivot's PARENT local space, so yaw
            // is measured relative to how the turret is mounted.
            Transform parent = yawPivot.parent;
            Vector3 local = parent != null
                ? parent.InverseTransformPoint(worldPoint) - yawPivot.localPosition
                : worldPoint - yawPivot.position;

            // Desired yaw about local Y: angle of the XZ direction.
            float desired = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;

            // Shortest-arc error, then move at most yawSpeed * dt toward it.
            float error = Mathf.DeltaAngle(_yaw, desired);
            float step = yawSpeed * Time.deltaTime;
            _yaw += Mathf.Clamp(error, -step, step);
            _yaw = NormaliseAngle(_yaw); // never unwind past 360°

            _yawError = Mathf.DeltaAngle(_yaw, desired);

            Vector3 e = yawPivot.localEulerAngles;
            e.y = _yaw;
            yawPivot.localEulerAngles = e;
        }

        private void SlewPitch(Vector3 worldPoint)
        {
            if (pitchPivot == null)
            {
                _pitchError = 0f;
                return;
            }

            // Direction to target in the pitch pivot's PARENT local space (which
            // is the yaw-aligned frame), so pitch is independent of current yaw.
            Transform parent = pitchPivot.parent;
            Vector3 local = parent != null
                ? parent.InverseTransformPoint(worldPoint) - pitchPivot.localPosition
                : worldPoint - pitchPivot.position;

            // Horizontal distance in the pitch frame and the height difference.
            float horizontal = new Vector2(local.x, local.z).magnitude;
            // Positive elevation (target above) => negative local X in Unity.
            float desired = -Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg;
            desired = Mathf.Clamp(desired, MinPitch, MaxPitch);

            float error = Mathf.DeltaAngle(_pitch, desired);
            float step = pitchSpeed * Time.deltaTime;
            _pitch += Mathf.Clamp(error, -step, step);
            _pitch = Mathf.Clamp(NormaliseAngle(_pitch), MinPitch, MaxPitch);

            _pitchError = Mathf.DeltaAngle(_pitch, desired);

            Vector3 e = pitchPivot.localEulerAngles;
            e.x = _pitch;
            pitchPivot.localEulerAngles = e;
        }

        /// <summary>Normalise an angle to the range (-180, 180].</summary>
        private static float NormaliseAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
                angle -= 360f;
            else if (angle <= -180f)
                angle += 360f;
            return angle;
        }
    }
}
