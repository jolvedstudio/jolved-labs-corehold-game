using System;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Moves an enemy along a track using a strict 1-D car-following model
    /// (GDD §9, §7.4 — transform-only, no Rigidbody, no collider). The mover holds
    /// a single monotonic scalar <see cref="Frontness"/> (how far toward the Core it
    /// has progressed) and a fixed render lane offset; its world position is a pure
    /// function of those two values, never negotiated by forces. All scheduling —
    /// leader gaps, spacing clamps, admission, wide-body handling — lives in
    /// <see cref="Corehold.Core.RouteTraffic"/>, which ticks movers front-to-back
    /// each frame and calls <see cref="PlaceAtFrontness"/>.
    ///
    /// <b>Frontness</b> unifies ground and air:
    ///   • Ground: Frontness = arc-length <c>s ∈ [0, route.Length]</c>.
    ///   • Air:    Frontness = <c>-remainingDistanceToCore ∈ [-total, 0]</c>, which
    ///             increases monotonically as the unit nears the Core. Two air units
    ///             at remaining r₁, r₂ are ≥ |r₁−r₂| apart in world space (points on
    ///             spheres about a common centre), so enforcing spacing on this
    ///             scalar guarantees no world overlap.
    /// In both cases larger Frontness = closer to the Core, arrival at
    /// <see cref="MaxFrontness"/>.
    ///
    /// Special behaviours (GDD §6.2) affect only the DESIRED speed / animation:
    ///   • Roller two-phase — drops base speed at the phase fraction, fires OnPhaseChange.
    ///   • Colossus enrage — SpeedMultiplier boost.
    ///   • Status effects (R18) — StatusSpeedMultiplier, a second slot composed with
    ///     the enrage multiplier so the two never overwrite each other.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Enemy))]
    public class EnemyMover : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Metres per second along the route. Base speed before any multiplier (GDD §6.1).")]
        [SerializeField] private float moveSpeed = 5f;

        [Tooltip("Degrees per second the unit rotates to face its direction of travel.")]
        [SerializeField] private float turnRate = 360f;

        [Tooltip("[TUNE] Arrival standoff (m): the unit reaches the Core when its front is this far from the route end (its body radius is added on top), so the crash detonates at the Core's FACE instead of the model sinking into the structure. Shortens travel by ~(standoff+radius)/speed seconds — small against full routes; the live balance gate re-certifies every level anyway.")]
        [SerializeField] private float coreArrivalStandoff = 1.1f;

        [Tooltip("Absolute floor on desired speed in m/s. Any future slow/stun effect clamps to this so a unit is never fully stopped by a status effect (keeps the car-following chain live — GDD liveness).")]
        [SerializeField] private float minDesiredSpeed = 0.4f;

        [Tooltip("[TUNE] Terrain grade response (M-b): ground units slow climbing and speed up " +
                 "descending by this factor × the route tangent's vertical component, clamped to " +
                 "±25%. SYMMETRIC about 1 by construction, so over a route that starts and ends on " +
                 "the same rolling band the linear term cancels and traverse time stays within ~1% " +
                 "of the planar balance model — the model remains a lower bound. 0 disables. Flat " +
                 "maps have flat tangents, so this is inert on every pre-terrain scene.")]
        [SerializeField] private float gradeSpeedFactor = 0.6f;

        [Tooltip("[TUNE] Cosmetic terrain lean (M-b): ground units pitch into the travel " +
                 "tangent's grade so they visibly climb and descend, clamped to ±this many " +
                 "degrees. The corridor band tops out near 8°, so the clamp only guards spline " +
                 "overshoot. Purely visual — position, speed and scheduling never read it. " +
                 "0 keeps bodies upright; flat tangents make it inert on every pre-terrain scene.")]
        [SerializeField] private float maxLeanDegrees = 10f;

        [Header("Anti-overlap")]
        [Tooltip("This unit's physical radius in metres. Drives car-following spacing and lane assignment. Set per enemy size.")]
        [SerializeField] private float bodyRadius = 0.6f;

        [Header("Route")]
        [Tooltip("The route this enemy follows. Assign at spawn or in the inspector.")]
        [SerializeField] private PathRoute route;

        [Header("Definition (GDD §6.2)")]
        [Tooltip("Optional definition. Drives air movement and the Roller two-phase change. " +
                 "Assign at spawn via Configure(), or in the inspector for editor testing.")]
        [SerializeField] private EnemyDefinition definition;

        [Tooltip("Target the air units fly toward (the Core). Falls back to the last route waypoint when unset.")]
        [SerializeField] private Transform coreTarget;

        private Enemy _enemy;
        private float _frontness;      // unified monotonic progress scalar
        private float _prevFrontness;  // last frame's frontness, for velocity
        private bool _finished;
        private Vector3 _velocity;     // current world velocity (m/s), for projectile leading

        private float _baseSpeed;      // moveSpeed captured at Configure/OnEnable, before multipliers
        private float _speedMultiplier = 1f;
        private float _statusSpeedMultiplier = 1f;
        private float _waveSpeedMultiplier = 1f;
        private float _gradeSpeedMultiplier = 1f; // terrain grade (M-b), from the travel tangent
        private bool _phaseChanged;    // Roller: has the second-phase change fired yet?

        private float _renderLaneOffset; // fixed lateral offset (m) this unit renders at

        // Colossus footfall (GDD §3.3, §11.5).
        private bool _heavyFootfalls;
        private float _footfallStride = 6f;
        private float _nextFootfallDistance;

        // Air movement (GDD §5.2).
        private bool _isAir;
        private Vector3 _airStart;
        private Vector3 _airTarget;
        private float _airTotalDistance;

        /// <summary>Raised once when the Roller reaches its phase-change fraction (GDD §6.2).</summary>
        public event Action OnPhaseChange;

        /// <summary>Effective movement speed in m/s: base × enrage × status × wave × grade multipliers.</summary>
        public float MoveSpeed => _baseSpeed * _speedMultiplier * _statusSpeedMultiplier *
                                  _waveSpeedMultiplier * _gradeSpeedMultiplier;

        /// <summary>
        /// Desired speed used by the traffic scheduler. Never below
        /// <see cref="minDesiredSpeed"/> so status effects can slow but never stop a
        /// unit (guarantees the front-of-lane unit always drains — liveness).
        /// </summary>
        public float DesiredSpeed => Mathf.Max(minDesiredSpeed, MoveSpeed);

        /// <summary>Base (unmultiplied) movement speed in m/s.</summary>
        public float BaseSpeed
        {
            get => _baseSpeed;
            set => _baseSpeed = Mathf.Max(0f, value);
        }

        /// <summary>Speed multiplier (Colossus enrage — GDD §6.2). Clamped to a positive floor.</summary>
        public float SpeedMultiplier
        {
            get => _speedMultiplier;
            set => _speedMultiplier = Mathf.Max(0.01f, value);
        }

        /// <summary>
        /// Status-effect multiplier slot (R18 stun/slow), owned by
        /// <see cref="Enemy"/>'s status list and composed with the enrage
        /// <see cref="SpeedMultiplier"/> rather than sharing its slot. 0 is legal:
        /// <see cref="DesiredSpeed"/> still floors at minDesiredSpeed, so a stunned
        /// unit crawls instead of stopping and the car-following chain stays live.
        /// </summary>
        public float StatusSpeedMultiplier
        {
            get => _statusSpeedMultiplier;
            set => _statusSpeedMultiplier = Mathf.Clamp(value, 0f, 10f);
        }

        /// <summary>
        /// Wave-mutator multiplier slot (R20 Storm), stamped by the WaveManager at
        /// spawn and reset on (re)configure. Its own slot so a Storm Wasp that gets
        /// slowed or an enraging unit never has one system overwrite another.
        /// </summary>
        public float WaveSpeedMultiplier
        {
            get => _waveSpeedMultiplier;
            set => _waveSpeedMultiplier = Mathf.Max(0.01f, value);
        }

        /// <summary>
        /// Convoy mutator (R20): when true, RouteTraffic registers this unit through
        /// its WIDE-BODY path — it occupies every lane of its track and rides the
        /// centreline, which is exactly single-file (ordered, no overtaking).
        /// Assigned via <see cref="Configure"/> so it is set BEFORE registration.
        /// </summary>
        public bool ForceSingleLane { get; private set; }

        /// <summary>True while this unit flies straight to the Core (GDD §5.2).</summary>
        public bool IsAir => _isAir;

        /// <summary>Physical radius in metres, used for car-following spacing and lane sizing.</summary>
        public float BodyRadius
        {
            get => bodyRadius;
            set => bodyRadius = Mathf.Max(0.01f, value);
        }

        /// <summary>The route this mover follows (null for air).</summary>
        public PathRoute Route => route;

        /// <summary>Unified monotonic progress scalar. Larger = closer to the Core.</summary>
        public float Frontness => _frontness;

        /// <summary>Frontness value at which the unit reaches the Core.</summary>
        public float MaxFrontness => _isAir ? 0f : (route != null ? route.Length : 0f);

        /// <summary>Frontness value at the track entrance (spawn point).</summary>
        public float MinFrontness => _isAir ? -_airTotalDistance : 0f;

        /// <summary>True once the unit has reached the Core and been finalised.</summary>
        public bool Finished => _finished;

        /// <summary>
        /// Current world-space velocity in m/s. Reflects the ACTUAL applied step
        /// (so it accounts for queue slowdown), letting projectiles lead correctly.
        /// </summary>
        public Vector3 Velocity => _velocity;

        /// <summary>Progress 0 (start) → 1 (Core). Ground: s/Length. Air: flown/total.</summary>
        public float PathFraction
        {
            get
            {
                if (_isAir)
                    return _airTotalDistance <= 0f ? 0f
                        : Mathf.Clamp01((_airTotalDistance + _frontness) / _airTotalDistance);
                float len = route != null ? route.Length : 0f;
                return len <= 0f ? 0f : Mathf.Clamp01(_frontness / len);
            }
        }

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
        }

        private void OnEnable()
        {
            ResetToStart();

            // A POOLED ground unit is enabled before Configure() hands it a route,
            // so registering here would be refused — and would warn, loudly and
            // wrongly, on every single pooled spawn. Configure registers it the
            // moment it has a route; air units have none by design and register
            // straight away.
            if (_isAir || route != null)
                RouteTraffic.Instance.Register(this);
        }

        private void OnDisable()
        {
            var rt = RouteTraffic.InstanceOrNull;
            if (rt != null)
                rt.Unregister(this);
        }

        /// <summary>
        /// Assign definition, route and Core target at spawn, then reset.
        /// <paramref name="forceSingleLane"/> (R20 Convoy) is a Configure input —
        /// not a settable property — because registration happens INSIDE this call
        /// and the lane decision must already know it.
        /// </summary>
        public void Configure(EnemyDefinition def, PathRoute newRoute, Transform core = null,
            bool forceSingleLane = false)
        {
            definition = def;
            route = newRoute;
            if (core != null)
                coreTarget = core;

            if (def != null && def.moveSpeed > 0.0001f)
                moveSpeed = def.moveSpeed;

            // Re-register with the traffic manager under the new track: remove then
            // readmit so a pooled/recycled mover can never linger in a stale lane.
            var rt = RouteTraffic.InstanceOrNull;
            if (rt != null)
                rt.Unregister(this);

            ResetToStart();
            ForceSingleLane = forceSingleLane;

            if (isActiveAndEnabled)
                RouteTraffic.Instance.Register(this);
        }

        /// <summary>Assign the route and reset progress.</summary>
        public void SetRoute(PathRoute newRoute)
        {
            var rt = RouteTraffic.InstanceOrNull;
            if (rt != null) rt.Unregister(this);
            route = newRoute;
            ResetToStart();
            if (isActiveAndEnabled) RouteTraffic.Instance.Register(this);
        }

        /// <summary>Assign the Core transform air units fly toward.</summary>
        public void SetCoreTarget(Transform core) => coreTarget = core;

        /// <summary>Set the fixed lateral offset this unit renders at (assigned by RouteTraffic).</summary>
        public void SetRenderLaneOffset(float offset) => _renderLaneOffset = offset;

        /// <summary>Snap to the start of the track and clear progress. Call when respawning.</summary>
        public void ResetToStart()
        {
            _finished = false;
            _velocity = Vector3.zero;
            _phaseChanged = false;
            _speedMultiplier = 1f;
            _statusSpeedMultiplier = 1f;
            _waveSpeedMultiplier = 1f;
            _gradeSpeedMultiplier = 1f;
            ForceSingleLane = false;
            _baseSpeed = moveSpeed;
            _renderLaneOffset = 0f;

            _isAir = definition != null && definition.isAir;

            _heavyFootfalls = !_isAir && _enemy != null && _enemy.IsColossus;
            _nextFootfallDistance = _footfallStride;

            if (_isAir)
                SetupAirRoute();

            _frontness = MinFrontness;
            _prevFrontness = _frontness;

            // Snap to the entrance immediately so registration reads a valid pose.
            PlaceOnly(_frontness);
        }

        /// <summary>Air setup: capture the straight-line start/target at flight altitude.</summary>
        private void SetupAirRoute()
        {
            float altitude = definition != null ? definition.flightAltitude : 4f;

            _airStart = transform.position;
            _airStart.y = altitude;

            Vector3 corePos;
            if (coreTarget != null)
                corePos = coreTarget.position;
            else if (route != null && route.PointCount > 0)
                corePos = route.GetPoint(route.PointCount - 1);
            else
                corePos = _airStart + transform.forward * 10f;

            _airTarget = corePos;
            _airTarget.y = altitude;
            _airTotalDistance = Vector3.Distance(_airStart, _airTarget);
        }

        /// <summary>
        /// Move the unit to the given frontness (called by RouteTraffic front-to-back).
        /// Handles facing, velocity, footfalls and phase change. Returns true if the
        /// unit reached the Core this step (RouteTraffic then removes it).
        /// </summary>
        public bool PlaceAtFrontness(float frontness, float dt)
        {
            if (_finished)
                return true;

            frontness = Mathf.Clamp(frontness, MinFrontness, MaxFrontness);

            Vector3 tangent;
            Vector3 pos = ComputePose(frontness, out tangent);

            // Terrain grade (M-b): the spline tangent's vertical component IS the
            // slope — no heightfield exists at runtime. Applies to NEXT frame's
            // desired speed (the scheduler reads DesiredSpeed before placing),
            // one frame of lag that smooth terrain never shows. Air flies level.
            if (!_isAir)
                _gradeSpeedMultiplier = gradeSpeedFactor == 0f
                    ? 1f
                    : Mathf.Clamp(1f - gradeSpeedFactor * tangent.y, 0.75f, 1.25f);

            // Actual applied speed (>= 0) for velocity/animation.
            float applied = dt > 0f ? (frontness - _prevFrontness) / dt : 0f;
            _velocity = tangent * Mathf.Max(0f, applied);

            transform.position = pos;
            FaceDirection(tangent, dt);

            _prevFrontness = frontness;
            _frontness = frontness;

            TryPhaseChange();
            TryFootfall();
            TryTrailStamp(pos);

            // Arrival at the STANDOFF, not the route end: the route runs to the
            // Core's centre, and finishing there sank the model into the
            // structure before it vanished. Works for air identically (air
            // frontness is the negative distance to the Core).
            if (_frontness >= MaxFrontness - coreArrivalStandoff - BodyRadius - 0.0001f)
            {
                ArriveAtCore();
                return true;
            }
            return false;
        }

        /// <summary>Place without side effects (used for the initial snap at spawn).</summary>
        private void PlaceOnly(float frontness)
        {
            Vector3 pos = ComputePose(frontness, out Vector3 tan);
            transform.position = pos;
            FaceDirection(tan, 1f);
        }

        /// <summary>World pose (position + travel tangent) for a frontness value.</summary>
        private Vector3 ComputePose(float frontness, out Vector3 tangent)
        {
            if (_isAir)
            {
                tangent = _airTarget - _airStart;
                if (tangent.sqrMagnitude > 0.0001f) tangent.Normalize();
                else tangent = transform.forward;

                float flown = _airTotalDistance + frontness; // frontness = -remaining
                float t = _airTotalDistance > 0.0001f ? Mathf.Clamp01(flown / _airTotalDistance) : 1f;
                Vector3 p = Vector3.Lerp(_airStart, _airTarget, t);
                Vector3 rightA = PathRoute.HorizontalRight(tangent);
                return p + rightA * _renderLaneOffset;
            }

            if (route != null)
            {
                Vector3 centre = route.SamplePosition(frontness, out tangent);
                Vector3 right = PathRoute.HorizontalRight(tangent);
                return centre + right * _renderLaneOffset;
            }

            tangent = transform.forward;
            return transform.position;
        }

        /// <summary>
        /// Stamp the weather trail map every ~1.2 m of GROUND travel. The call
        /// into <see cref="Corehold.Systems.TrailMap.Stamp"/> is statically
        /// guarded, so when no snow film is up this whole path costs one
        /// squared-distance compare per frame — the mover must never pay for
        /// weather it is not standing in. Air units leave no tracks.
        /// </summary>
        private Vector3 _lastTrailStamp;

        private void TryTrailStamp(Vector3 pos)
        {
            if (_isAir)
                return;
            float dx = pos.x - _lastTrailStamp.x, dz = pos.z - _lastTrailStamp.z;
            if (dx * dx + dz * dz < 1.44f)
                return;
            _lastTrailStamp = pos;
            Corehold.Systems.TrailMap.Stamp(pos);
        }

        private void TryFootfall()
        {
            if (!_heavyFootfalls)
                return;
            float flown = _isAir ? (_airTotalDistance + _frontness) : _frontness;
            if (flown < _nextFootfallDistance)
                return;
            _nextFootfallDistance = flown + _footfallStride;
            if (Corehold.Systems.CameraShake.Instance != null)
                Corehold.Systems.CameraShake.Instance.ShakeFootfall();
        }

        private void TryPhaseChange()
        {
            if (_phaseChanged || definition == null || !definition.hasSecondPhase)
                return;
            if (PathFraction < definition.phaseChangeAtPathFraction)
                return;

            _phaseChanged = true;
            if (definition.secondPhaseSpeed > 0.0001f)
                _baseSpeed = definition.secondPhaseSpeed;
            OnPhaseChange?.Invoke();
        }

        private void FaceDirection(Vector3 direction, float dt)
        {
            // Yaw from the FLATTENED direction, exactly as pre-terrain — the
            // turn-rate semantics and the up-vector (no roll) stay untouched.
            Vector3 flat = direction;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f)
                return;
            Quaternion targetRot = Quaternion.LookRotation(flat, Vector3.up);

            // Terrain lean (M-b, cosmetic): compose a clamped pitch toward the
            // 3-D travel tangent so walkers lean into climbs and descents.
            // Positive X rotation pitches nose-DOWN in Unity, hence the minus.
            // A flat tangent composes identity, so pre-terrain maps are inert.
            if (!_isAir && maxLeanDegrees > 0f && direction.sqrMagnitude > 0.0001f)
            {
                float pitch = Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                pitch = Mathf.Clamp(pitch, -maxLeanDegrees, maxLeanDegrees);
                targetRot *= Quaternion.Euler(-pitch, 0f, 0f);
            }

            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnRate * dt);
        }

        private void ArriveAtCore()
        {
            if (_finished)
                return;
            _finished = true;
            _frontness = MaxFrontness;
            _velocity = Vector3.zero;
            if (_enemy != null)
                _enemy.ReachCore();
        }
    }
}
