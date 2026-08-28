using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Low-intensity, cooldown-gated camera shake for Core hits and Colossus
    /// footfalls only (GDD §3.3, §11.5). The camera in COREHOLD is fixed, so the
    /// shake is <b>additive</b>: it records the transform's rest pose the first time
    /// it runs and every frame writes rest + a decaying random offset, then restores
    /// the exact rest pose when the shake ends. Framing, the rotate-to-portrait prompt
    /// and any editor nudge to the camera are therefore never fought — the rest pose
    /// is re-sampled whenever the camera is idle.
    ///
    /// Design rules from the ticket / §3.3:
    ///   • <b>Low intensity.</b> A leak or footfall is a nudge, not a punch. The
    ///     default trauma is small and the positional throw is a few centimetres.
    ///   • <b>1.5 s cooldown.</b> A twelve-Scuttler breach on the same frame must not
    ///     turn into a seizure, so a shake is refused unless at least
    ///     <see cref="cooldown"/> seconds (unscaled) have passed since the last one.
    ///   • <b>Unscaled.</b> Both the cooldown and the decay run on
    ///     <see cref="Time.unscaledDeltaTime"/> so the 2× speed toggle (§9.6) neither
    ///     shortens the cooldown nor speeds up the recovery — the feel is identical
    ///     at 1× and 2×, and it still works while the game is paused (timeScale 0).
    ///
    /// Access is through the <see cref="Instance"/> singleton; gameplay calls
    /// <see cref="Shake"/> (or the named helpers) and the component silently drops
    /// the request when it is still on cooldown.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraShake : MonoBehaviour
    {
        [Header("Intensity (GDD §3.3 — low)")]
        [Tooltip("Default trauma (0..1) added per triggered shake. Kept low: a nudge, not a punch.")]
        [Range(0f, 1f)] [SerializeField] private float defaultTrauma = 0.35f;

        [Tooltip("Maximum positional throw in metres at trauma = 1. Trauma is squared before this is applied, so ordinary shakes move only a couple of centimetres.")]
        [SerializeField] private float maxPositionShake = 0.18f;

        [Tooltip("Maximum rotational throw in degrees at trauma = 1 (roll/pitch/yaw wobble).")]
        [SerializeField] private float maxRotationShake = 0.9f;

        [Tooltip("How fast trauma decays back to zero, in trauma-per-second (unscaled).")]
        [SerializeField] private float traumaDecayPerSecond = 1.6f;

        [Tooltip("Frequency of the shake noise in Hz — higher reads as a sharper rattle.")]
        [SerializeField] private float frequency = 22f;

        [Header("Cooldown (GDD §3.3)")]
        [Tooltip("Minimum UNSCALED seconds between shakes. A breach must not become a seizure. GDD §3.3: 1.5 s.")]
        [SerializeField] private float cooldown = 1.5f;

        [Header("Impact kick (R5) — directional, fast exponential settle")]
        [Tooltip("[TUNE] Kick distance (m) per hitscan/projectile impact (VFXDirector.PlayImpact).")]
        [SerializeField] private float kickImpact = 0.05f;

        [Tooltip("[TUNE] Kick distance (m) when the Core takes a hit (PlayCoreHit).")]
        [SerializeField] private float kickCoreHit = 0.10f;

        [Tooltip("[TUNE] Kick distance (m) per splash explosion (PlayExplosion).")]
        [SerializeField] private float kickExplosion = 0.14f;

        [Tooltip("[TUNE] Trauma (0..1) added per splash explosion so big blasts RUMBLE " +
                 "rather than nudge (VFX plan Tier 4). Rides the standard Shake() path, " +
                 "so the 1.5 s cooldown and the global feedback scale both apply.")]
        [Range(0f, 1f)] [SerializeField] private float explosionTrauma = 0.22f;

        [Tooltip("[TUNE] Exponential decay rate (per unscaled second) back to the framed position — high = a sharp nudge that settles fast.")]
        [SerializeField] private float kickDecayPerSecond = 9f;

        [Tooltip("[TUNE] Hard ceiling (m) on the accumulated kick offset so stacked impacts can never throw the framing.")]
        [SerializeField] private float kickMaxOffset = 0.35f;

        [Tooltip("[TUNE] Minimum unscaled seconds between accepted kicks — autocannon fire reads as texture, not a rattle.")]
        [SerializeField] private float kickMinInterval = 0.06f;

        [Header("Accessibility (R5)")]
        [Tooltip("[TUNE] Global scale on ALL camera feedback — trauma shake and kicks alike. 0 = off entirely.")]
        [Range(0f, 1f)] [SerializeField] private float effectScale = 1f;

        [Header("Micro hit-stop (R5 — optional, default off)")]
        [Tooltip("[TUNE] When on, explosions also trigger a tiny time dip through GameManager.TimeDip (interrupt-safe, R3).")]
        [SerializeField] private bool enableHitStop = false;

        [Tooltip("[TUNE] Time.timeScale during the micro hit-stop.")]
        [SerializeField] private float hitStopScale = 0.05f;

        [Tooltip("[TUNE] Unscaled seconds the micro hit-stop lasts.")]
        [SerializeField] private float hitStopSeconds = 0.05f;

        // ----- Singleton -----
        private static CameraShake _instance;

        /// <summary>The active shaker, if one exists in the scene.</summary>
        public static CameraShake Instance => _instance;

        // ----- Runtime -----
        private float _trauma;             // current shake energy (0..1)
        private float _lastShakeTime = -999f; // Time.unscaledTime of the last accepted shake
        private Vector3 _restPosition;     // local rest pose, re-sampled while idle
        private Quaternion _restRotation;
        private bool _hasRest;
        private float _seedX, _seedY, _seedZ, _seedRot;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;

            // Distinct Perlin seeds so the axes don't move in lockstep.
            _seedX = Random.value * 100f;
            _seedY = Random.value * 100f;
            _seedZ = Random.value * 100f;
            _seedRot = Random.value * 100f;

            CaptureRest();
        }

        private void OnEnable()
        {
            CaptureRest();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void CaptureRest()
        {
            _restPosition = transform.localPosition;
            _restRotation = transform.localRotation;
            _hasRest = true;
        }

        /// <summary>
        /// True while a shake is settling. External systems that reposition the
        /// camera (framing, the rotate overlay) can check this to defer.
        /// </summary>
        public bool IsShaking => _trauma > 0.0001f;

        /// <summary>Seconds (unscaled) until another shake will be accepted; 0 when ready.</summary>
        public float CooldownRemaining =>
            Mathf.Max(0f, cooldown - (Time.unscaledTime - _lastShakeTime));

        /// <summary>Add the default trauma, honouring the 1.5 s cooldown. Returns true if accepted.</summary>
        public bool Shake() => Shake(defaultTrauma);

        /// <summary>
        /// Add <paramref name="trauma"/> (0..1), honouring the 1.5 s cooldown
        /// (GDD §3.3). Returns false and does nothing while on cooldown so a swarm
        /// of leaks on the same frame is a single nudge, not a seizure.
        /// </summary>
        public bool Shake(float trauma)
        {
            // Accessibility master switch (Settings screen, persisted in SaveData).
            if (!SaveData.ShakeEnabled)
                return false;

            float now = Time.unscaledTime;
            if (now - _lastShakeTime < cooldown)
                return false;

            _lastShakeTime = now;

            // Re-sample the rest pose from wherever the camera currently sits (it is
            // at rest — we were not shaking, or the previous shake had fully settled),
            // so an editor move or framing change is respected.
            if (!IsShaking || !_hasRest)
                CaptureRest();

            _trauma = Mathf.Clamp01(Mathf.Max(_trauma, trauma));
            return true;
        }

        /// <summary>Core-hit shake (a leak reached the Core). Uses the default low trauma (GDD §3.3).</summary>
        public bool ShakeCoreHit() => Shake(defaultTrauma);

        /// <summary>
        /// Colossus footfall shake (GDD §3.3, §11.5). Slightly softer than a Core
        /// hit — a heavy step is felt, not a hit taken.
        /// </summary>
        public bool ShakeFootfall() => Shake(defaultTrauma * 0.8f);

        // ----- Impact kick (R5) — the reusable screen-kick standard -----
        //
        // A kick is a small DIRECTIONAL recoil away from an impact point with a
        // rapid exponential settle back to the framed position — distinct from
        // the Perlin trauma shake above (which stays reserved for Core hits and
        // footfalls). Additive over the same captured rest pose, so neither can
        // fight the content framing. Future tickets call these helpers; never
        // re-implement a kick elsewhere.

        private Vector3 _kickOffset;
        private float _lastKickTime = -999f;

        /// <summary>
        /// Nudge the camera away from a world-space impact point by
        /// <paramref name="strength"/> metres (clamped, rate-limited, scaled by
        /// the accessibility <see cref="effectScale"/>).
        /// </summary>
        public void Kick(Vector3 worldFrom, float strength)
        {
            if (strength <= 0f || effectScale <= 0f)
                return;
            if (!SaveData.ShakeEnabled)
                return;

            float now = Time.unscaledTime;
            if (now - _lastKickTime < kickMinInterval)
                return;
            _lastKickTime = now;

            Vector3 worldDir = transform.position - worldFrom;
            worldDir = worldDir.sqrMagnitude > 0.0001f ? worldDir.normalized : -transform.forward;
            Vector3 local = transform.InverseTransformDirection(worldDir) * strength;
            _kickOffset = Vector3.ClampMagnitude(_kickOffset + local, kickMaxOffset);
        }

        /// <summary>Small kick for a shot striking a unit (wired from VFXDirector.PlayImpact).</summary>
        public void KickImpact(Vector3 worldFrom) => Kick(worldFrom, kickImpact);

        /// <summary>Kick for a Core hit (wired from VFXDirector.PlayCoreHit).</summary>
        public void KickCoreHit(Vector3 worldFrom)
        {
            Kick(worldFrom, kickCoreHit);
            // The single strongest haptic beat the game has: the Core taking a
            // hit. Intensity rides the same accessibility scale as the shake.
            Haptics.Pulse(0.15f, 1f * Mathf.Clamp01(effectScale));
        }

        /// <summary>
        /// Larger kick for a splash explosion (wired from VFXDirector.PlayExplosion),
        /// plus the optional micro hit-stop through GameManager's interrupt-safe
        /// TimeDip (R3) when enabled.
        /// </summary>
        public void KickExplosion(Vector3 worldFrom)
        {
            Kick(worldFrom, kickExplosion);
            // A big blast RUMBLES, not just nudges (VFX plan Tier 4): a small
            // trauma add rides the existing noise shake. Shake() honours the
            // shared cooldown, so chained explosions cannot stack into nausea,
            // and the global feedbackScale/accessibility scaling still applies.
            Shake(explosionTrauma);
            // …and on WebGL the blast is FELT: a short rumble through the
            // browser haptics bridge, on the same accessibility scale.
            Haptics.Pulse(0.09f, 0.8f * Mathf.Clamp01(effectScale));
            if (enableHitStop && Corehold.Core.GameManager.Instance != null)
                Corehold.Core.GameManager.Instance.TimeDip(hitStopScale, hitStopSeconds);
        }

        private void LateUpdate()
        {
            if (!_hasRest)
            {
                CaptureRest();
                return;
            }

            bool kicking = _kickOffset.sqrMagnitude > 0.000001f;

            if (_trauma <= 0.0001f && !kicking)
            {
                // Idle: keep the rest pose current so external repositioning sticks,
                // and make sure we are sitting exactly on it.
                _restPosition = transform.localPosition;
                _restRotation = transform.localRotation;
                return;
            }

            float fx = Mathf.Clamp01(effectScale); // accessibility (R5): 0 = off

            // Trauma shake (Core hits / footfalls). Shake amount rises with the
            // square of trauma so low trauma is gentle and only a rare
            // high-trauma event throws the camera hard.
            Vector3 offset = Vector3.zero;
            Quaternion wobble = Quaternion.identity;
            if (_trauma > 0.0001f)
            {
                float shake = _trauma * _trauma;
                float t = Time.unscaledTime * frequency;

                // Perlin noise in [-1, 1] per axis, decorrelated by seed.
                float nx = Mathf.PerlinNoise(_seedX, t) * 2f - 1f;
                float ny = Mathf.PerlinNoise(_seedY, t) * 2f - 1f;
                float nz = Mathf.PerlinNoise(_seedZ, t) * 2f - 1f;
                float nr = Mathf.PerlinNoise(_seedRot, t) * 2f - 1f;

                offset = new Vector3(nx, ny, nz) * (maxPositionShake * shake);
                wobble = Quaternion.Euler(ny * maxRotationShake * shake,
                                          nx * maxRotationShake * shake,
                                          nr * (maxRotationShake * shake));

                // Decay (unscaled — the 2× toggle and pause must not change recovery).
                _trauma = Mathf.Max(0f, _trauma - traumaDecayPerSecond * Time.unscaledDeltaTime);
            }

            // Compose: rest + (trauma noise + directional kick), both accessibility
            // scaled, all additive over the captured rest pose (framing-safe).
            transform.localPosition = _restPosition + (offset + _kickOffset) * fx;
            transform.localRotation = _restRotation *
                Quaternion.Slerp(Quaternion.identity, wobble, fx);

            // Kick: rapid exponential settle back to the framed position (R5) —
            // deterministic decay, no random jitter on the way home.
            if (kicking)
            {
                _kickOffset *= Mathf.Exp(-kickDecayPerSecond * Time.unscaledDeltaTime);
                if (_kickOffset.sqrMagnitude < 0.000001f)
                    _kickOffset = Vector3.zero;
            }

            if (_trauma <= 0.0001f && _kickOffset == Vector3.zero)
            {
                // Settle back exactly onto the rest pose to avoid drift.
                _trauma = 0f;
                transform.localPosition = _restPosition;
                transform.localRotation = _restRotation;
            }
        }
    }
}
