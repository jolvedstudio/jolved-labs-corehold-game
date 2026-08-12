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

        private void LateUpdate()
        {
            if (!_hasRest)
            {
                CaptureRest();
                return;
            }

            if (_trauma <= 0.0001f)
            {
                // Idle: keep the rest pose current so external repositioning sticks,
                // and make sure we are sitting exactly on it.
                _restPosition = transform.localPosition;
                _restRotation = transform.localRotation;
                return;
            }

            // Shake amount rises with the square of trauma so low trauma is gentle
            // and only a rare high-trauma event throws the camera hard.
            float shake = _trauma * _trauma;
            float t = Time.unscaledTime * frequency;

            // Perlin noise in [-1, 1] per axis, decorrelated by seed.
            float nx = Mathf.PerlinNoise(_seedX, t) * 2f - 1f;
            float ny = Mathf.PerlinNoise(_seedY, t) * 2f - 1f;
            float nz = Mathf.PerlinNoise(_seedZ, t) * 2f - 1f;
            float nr = Mathf.PerlinNoise(_seedRot, t) * 2f - 1f;

            Vector3 offset = new Vector3(nx, ny, nz) * (maxPositionShake * shake);
            float roll = nr * (maxRotationShake * shake);

            transform.localPosition = _restPosition + offset;
            transform.localRotation = _restRotation * Quaternion.Euler(ny * maxRotationShake * shake, nx * maxRotationShake * shake, roll);

            // Decay (unscaled — the 2× toggle and pause must not change recovery).
            _trauma = Mathf.Max(0f, _trauma - traumaDecayPerSecond * Time.unscaledDeltaTime);

            if (_trauma <= 0.0001f)
            {
                // Settle back exactly onto the rest pose to avoid drift.
                _trauma = 0f;
                transform.localPosition = _restPosition;
                transform.localRotation = _restRotation;
            }
        }
    }
}
