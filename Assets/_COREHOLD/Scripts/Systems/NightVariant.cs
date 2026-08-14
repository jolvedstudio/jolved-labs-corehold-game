using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Runtime toggle for the night lighting variant of the shipped layout (R23).
    /// Geometry never moves — night is a LIGHTING state:
    ///
    ///   • ambient dropped to the ticket's 0.25,
    ///   • the directional sun dimmed and cooled to moonlight,
    ///   • emissive materials boosted ×[TUNE] via MaterialPropertyBlocks
    ///     (never by mutating shared material assets),
    ///   • the authored "NightLights" lamp container enabled (≤10 non-shadowing
    ///     point lights, placed by Tools → COREHOLD → Scene Setup → Night Variant
    ///     and then hand-adjusted — the human-led part of the ticket).
    ///
    /// Everything applied is cached first and restored exactly on the way back to
    /// day, so the toggle is idempotent in both directions. Enemy renderers are
    /// excluded from the emissive pass — the Colossus enrage owns their MPBs.
    ///
    /// Toggle from the DebugConsole (`N`) or via <see cref="SetNight"/>. Note the
    /// WeatherApplier also tints ambient when a weather preset is active; night
    /// applies last-writer-wins, so toggle night AFTER weather setup, or re-toggle.
    /// </summary>
    [DisallowMultipleComponent]
    public class NightVariant : MonoBehaviour
    {
        /// <summary>Name of the lamp container the setup tool authors.</summary>
        public const string LampContainerName = "NightLights";

        [Header("Night values (R23)")]
        [Tooltip("[TUNE] Ambient intensity at night (ticket: 0.25).")]
        [SerializeField] private float nightAmbientIntensity = 0.25f;

        [Tooltip("[TUNE] Flat ambient colour at night (used when the scene's ambient is a flat colour).")]
        [SerializeField] private Color nightAmbientColor = new Color(0.10f, 0.12f, 0.18f, 1f);

        [Tooltip("[TUNE] Directional light intensity at night (moonlight).")]
        [SerializeField] private float nightSunIntensity = 0.12f;

        [Tooltip("[TUNE] Directional light colour at night (cool moonlight).")]
        [SerializeField] private Color nightSunColor = new Color(0.62f, 0.70f, 0.95f, 1f);

        [Tooltip("[TUNE] Emissive boost at night (ticket: 1.5–2).")]
        [Range(1f, 3f)] [SerializeField] private float emissiveBoost = 1.7f;

        private static NightVariant _instance;

        /// <summary>The scene's night controller, if present.</summary>
        public static NightVariant Instance => _instance;

        /// <summary>True while the night state is applied.</summary>
        public bool IsNight { get; private set; }

        // Cached day state, captured the moment night is applied.
        private float _dayAmbientIntensity;
        private Color _dayAmbientLight;
        private Light _sun;
        private float _daySunIntensity;
        private Color _daySunColor;

        // Renderers whose materials carry an enabled _EMISSION, with their base
        // emissive colour, boosted through an MPB at night and cleared by day.
        private struct EmissiveEntry
        {
            public Renderer renderer;
            public Color baseEmission;
        }

        private readonly List<EmissiveEntry> _emissives = new List<EmissiveEntry>();
        private MaterialPropertyBlock _block;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>Flip between day and night.</summary>
        public void Toggle() => SetNight(!IsNight);

        /// <summary>Apply or remove the night lighting state (R23).</summary>
        public void SetNight(bool night)
        {
            if (night == IsNight)
                return;
            IsNight = night;

            if (night)
                ApplyNight();
            else
                RestoreDay();

            var lamps = transform.Find(LampContainerName);
            if (lamps == null)
            {
                var found = GameObject.Find(LampContainerName);
                lamps = found != null ? found.transform : null;
            }
            if (lamps != null)
                lamps.gameObject.SetActive(night);

            Debug.Log($"[NightVariant] {(night ? "Night" : "Day")} applied " +
                      $"(ambient {(night ? nightAmbientIntensity : _dayAmbientIntensity):0.##}, " +
                      $"emissives ×{(night ? emissiveBoost : 1f):0.##}, lamps {(lamps != null ? (night ? "on" : "off") : "MISSING — run Scene Setup → Night Variant")}).");
        }

        private void ApplyNight()
        {
            // Ambient. Cache both the intensity and the flat colour — whichever
            // mode the scene uses, the restore path puts back exactly what was there.
            _dayAmbientIntensity = RenderSettings.ambientIntensity;
            _dayAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientIntensity = nightAmbientIntensity;
            RenderSettings.ambientLight = nightAmbientColor;

            // Sun.
            _sun = RenderSettings.sun;
            if (_sun == null)
            {
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { _sun = l; break; }
            }
            if (_sun != null)
            {
                _daySunIntensity = _sun.intensity;
                _daySunColor = _sun.color;
                _sun.intensity = nightSunIntensity;
                _sun.color = nightSunColor;
            }

            BoostEmissives();
        }

        private void RestoreDay()
        {
            RenderSettings.ambientIntensity = _dayAmbientIntensity;
            RenderSettings.ambientLight = _dayAmbientLight;

            if (_sun != null)
            {
                _sun.intensity = _daySunIntensity;
                _sun.color = _daySunColor;
            }

            if (_block == null)
                _block = new MaterialPropertyBlock();
            for (int i = 0; i < _emissives.Count; i++)
            {
                var e = _emissives[i];
                if (e.renderer == null)
                    continue;
                e.renderer.GetPropertyBlock(_block);
                _block.SetColor(EmissionColorId, e.baseEmission);
                e.renderer.SetPropertyBlock(_block);
            }
            _emissives.Clear();
        }

        /// <summary>
        /// Scan the scene for renderers whose material has emission ENABLED and
        /// boost them via MPB. Re-scanned on every night toggle so towers built
        /// since the last one are included. Enemies are skipped — their MPBs
        /// belong to the enrage system.
        /// </summary>
        private void BoostEmissives()
        {
            _emissives.Clear();
            if (_block == null)
                _block = new MaterialPropertyBlock();

            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || r is ParticleSystemRenderer)
                    continue;
                if (r.GetComponentInParent<Corehold.Enemies.Enemy>() != null)
                    continue;

                Material m = r.sharedMaterial;
                if (m == null || !m.HasProperty(EmissionColorId) || !m.IsKeywordEnabled("_EMISSION"))
                    continue;

                Color baseEmission = m.GetColor(EmissionColorId);
                _emissives.Add(new EmissiveEntry { renderer = r, baseEmission = baseEmission });

                r.GetPropertyBlock(_block);
                _block.SetColor(EmissionColorId, baseEmission * emissiveBoost);
                r.SetPropertyBlock(_block);
            }
        }
    }
}
