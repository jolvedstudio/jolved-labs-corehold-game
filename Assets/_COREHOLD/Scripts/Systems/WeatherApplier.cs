using System;
using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Corehold.Systems
{
    /// <summary>
    /// Applies a <see cref="WeatherPreset"/> to the scene at map load (roadmap R13).
    ///
    /// Three properties this component is built around:
    ///
    ///   • <b>The baseline is captured before anything is touched</b>, and every
    ///     channel is restored on <see cref="Clear"/>. The fog baseline is whatever
    ///     the scene LOADED with — originally R11's camera-solved fog, and since
    ///     M-d whatever LookStage baked from the theme (EnvPack fog/skybox); this
    ///     applier deliberately captures the loaded state rather than assuming an
    ///     owner, so a preset borrows the baseline and gives it back. Without
    ///     that, applying and clearing a preset would silently leave the scene on
    ///     the preset's fog and the "null preset is pixel-identical" requirement
    ///     would quietly fail.
    ///
    ///   • <b>No per-object material instances.</b> Ground tinting goes through one
    ///     shared <see cref="MaterialPropertyBlock"/>; touching
    ///     <c>renderer.material</c> would instance the material per object, break
    ///     batching and leak it.
    ///
    ///   • <b>No per-frame cost.</b> There is no Update here. Everything is set once
    ///     on apply; the precipitation layer is a single camera-parented particle
    ///     system that runs itself and is reused rather than rebuilt.
    ///
    /// Weather is deliberately a LEVEL property, not a mid-run effect (see R13):
    /// gameplay-affecting conditions are wave mutators (R20), and shifting
    /// legibility mid-wave is unfair in a game this read-dependent. <see cref="Apply"/>
    /// is public so a future state-boundary transition (Briefing → Build) or R23's
    /// night variant can drive it, but nothing calls it during a wave.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeatherApplier : MonoBehaviour
    {
        [Header("Preset")]
        [Tooltip("Weather for this level. Leave EMPTY for the null preset — the scene keeps its authored look exactly.")]
        [SerializeField] private WeatherPreset preset;

        [Header("Tint targets")]
        [Tooltip("Renderers a preset's ground tint applies to. Left empty, the Floor and the R11 silhouette band are resolved at apply time.")]
        [SerializeField] private Renderer[] tintTargets;

        // ----- Captured baseline (the null-preset look) -----
        private bool _baselineCaptured;
        private UnityEngine.Rendering.AmbientMode _baseAmbientMode;
        private Color _baseAmbient;
        private bool _baseFogEnabled;
        private FogMode _baseFogMode;
        private Color _baseFogColor;
        private float _baseFogDensity;

        // ----- Sun (resolved once; every field the override writes is captured) --
        private Light _sun;
        private bool _sunResolved;
        private Color _baseSunColor;
        private float _baseSunIntensity;
        private float _baseSunShadowStrength;
        private bool _baseSunUseTemperature;

        private readonly List<Renderer> _resolvedTargets = new List<Renderer>();
        // Each target's own material colour, captured at resolve time. The tint is
        // MULTIPLICATIVE over this (as the preset tooltip promises): writing the
        // preset tint verbatim — or literal white on restore — through the property
        // block would OVERWRITE any authored non-white material colour, silently
        // breaking the "null preset is pixel-identical" guarantee.
        private readonly List<Color> _baseTints = new List<Color>();
        private MaterialPropertyBlock _block;
        private GameObject _precipitation;
        private Material _precipitationMaterial;

        // Which source the live layer was built from (null = the procedural one).
        // Tracked so switching presets rebuilds instead of reusing the wrong object.
        private GameObject _precipitationSource;
        private bool _precipitationBuilt;

        /// <summary>Alpha layers a preset may spend (R14). An authored prefab exceeding this is warned about, not silently accepted.</summary>
        private const int MaxAlphaLayers = 3;

        // The applier drives its OWN global Volume rather than editing the scene's.
        // URP blends volumes by priority and a higher-priority profile overrides only
        // the properties it declares, so the base profile's Bloom and Tonemapping
        // survive — replacing the scene profile outright would kill the HDR tracer
        // glow that VFXDirector's bolts depend on. Weight 0 means no contribution at
        // all, which is what keeps the null preset pixel-identical.
        private UnityEngine.Rendering.Volume _gradeVolume;

        /// <summary>Priority headroom placed above the scene's existing volumes.</summary>
        private const int GradePriorityOffset = 10;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>The preset currently applied, or null when the scene is on its baseline.</summary>
        public WeatherPreset Active { get; private set; }

        // ------------------------------------------------ mutator → weather links

        /// <summary>A weather LAYER stacked over the base preset while a wave with
        /// the given mutator is in flight — a Storm wave that looks like a storm.</summary>
        [Serializable]
        public class MutatorWeatherLink
        {
            public WaveMutator mutator;
            public WeatherPreset layer;
        }

        [Tooltip("Weather layers stacked over the active preset while a wave with the linked mutator " +
                 "runs, removed when the field clears. Composition rules are the preset's layer rules. " +
                 "Presentation only — nothing gameplay reads weather.")]
        public MutatorWeatherLink[] mutatorLinks;

        private WaveMutator _activeMutators;

        // ------------------------------------------------------ composed runtime
        // All of this idles at ZERO cost: Update early-outs on one time compare,
        // and the throttled tick early-outs again when every current equals its
        // target and no gust or lightning is configured.

        private WeatherPreset _merged;         // the flattened stack, as one carrier
        private ParticleSystem _sheetPs;       // procedural sheet, cached at build
        private Vector3 _sheetBaseVel;         // camera-local wind+fall it was built with
        private float _resolvedRate;

        private float _surfaceSeconds = 10f;
        private float _targetSnow, _targetWet;
        private float _trailStrength = 0.8f;
        private float _trailMeltSeconds = 45f;
        private float _puddleDepth;
        private float _wetShine = 1f;
        private Color _targetFilm = Color.white;
        private float _curSnow, _curWet;
        private float _envelope = 1f;          // 0→1 ramp shared by rate + surfaces
        private float _nextTick;
        private const float TickSeconds = 0.15f;

        private float _gustStrength;
        private float _gustPeriod = 7f;
        private Vector3 _windDirection = Vector3.forward;
        private float _windStrength;
        private float _propSway = 1f;

        private float _strikesPerMinute;
        private float _lightningIntensity = 3.5f;
        private Color _lightningColor = Color.white;
        private float _nextStrikeAt = float.PositiveInfinity;
        private float _flashStartedAt = -1f;
        private float _stackSunIntensity;      // restore point after a flash
        private Color _stackSunColor;

        private void Start()
        {
            CaptureBaseline();
            Apply(preset);

            // The wave manager now HOLDS the mutator look across the wave
            // boundary (a storm should not switch off the instant the last
            // frame dies), which means the end of the RUN is the one moment
            // left with no next wave to clear it. Subscribed here rather than
            // in OnEnable because GameManager.Instance is not guaranteed to
            // exist that early.
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnStateChanged += OnGameStateChanged;
                _stateSubscribed = true;
            }
        }

        private bool _stateSubscribed;

        private void OnGameStateChanged(GameState state)
        {
            if (state == GameState.Victory || state == GameState.Defeat)
                OnMutatorsChanged(WaveMutator.None);
        }

        private void OnEnable()
        {
            WaveManager.ActiveMutatorsChanged += OnMutatorsChanged;
            WaveManager.ActiveMutatorAssetsChanged += OnMutatorAssetsChanged;
        }

        private void OnDisable()
        {
            WaveManager.ActiveMutatorsChanged -= OnMutatorsChanged;
            WaveManager.ActiveMutatorAssetsChanged -= OnMutatorAssetsChanged;
            if (_stateSubscribed && GameManager.Instance != null)
            {
                GameManager.Instance.OnStateChanged -= OnGameStateChanged;
                _stateSubscribed = false;
            }
        }

        private void OnMutatorsChanged(WaveMutator now)
        {
            if (_activeMutators == now)
                return;
            _activeMutators = now;
            if (Application.isPlaying && _baselineCaptured)
                ApplyStack();
        }

        /// <summary>
        /// Weather layers carried by the wave's AUTHORED mutators (R33).
        ///
        /// This is what makes a new mutator arrive complete: the asset names
        /// its own layer, so a designer adding one gets its look without
        /// opening a single scene. <see cref="mutatorLinks"/> stays for the
        /// four legacy flags, whose layers are wired per scene.
        /// </summary>
        private readonly List<WeatherPreset> _mutatorAssetLayers = new List<WeatherPreset>(4);

        private void OnMutatorAssetsChanged(IReadOnlyList<WaveMutatorDefinition> now)
        {
            _mutatorAssetLayers.Clear();
            if (now != null)
                foreach (WaveMutatorDefinition d in now)
                    if (d != null && d.weatherLayer != null &&
                        !_mutatorAssetLayers.Contains(d.weatherLayer))
                        _mutatorAssetLayers.Add(d.weatherLayer);

            if (Application.isPlaying && _baselineCaptured)
                ApplyStack();
        }

        private void OnDestroy()
        {
            // Leave the editor's scene state as we found it.
            if (_baselineCaptured)
                RestoreBaseline();
            // Props hold SWAPPED materials while weather is up; a scene change
            // must put the dressing back on its own before the variants die
            // with the domain, or the next scene inherits pink renderers.
            PropSnow.Restore();

            // Shader globals outlive the scene. A menu loaded after a rainstorm
            // must not keep the storm's water table and shine — the same reason
            // TrailMap zeroes its strength on the way out.
            Shader.SetGlobalVector(WaterId, Vector4.zero);
            if (Application.isPlaying && AudioDirector.Instance != null)
                AudioDirector.Instance.StopWeatherLoop();
            if (_precipitationMaterial != null)
                Destroy(_precipitationMaterial);
        }

        /// <summary>
        /// Snapshot every channel a preset can touch. Called once, before the first
        /// apply, so the baseline is the scene's authored look rather than whatever
        /// a previous preset left behind.
        /// </summary>
        private void CaptureBaseline()
        {
            if (_baselineCaptured)
                return;

            _baseAmbientMode = RenderSettings.ambientMode;
            _baseAmbient = RenderSettings.ambientLight;
            _baseFogEnabled = RenderSettings.fog;
            _baseFogMode = RenderSettings.fogMode;
            _baseFogColor = RenderSettings.fogColor;
            _baseFogDensity = RenderSettings.fogDensity;

            ResolveSun();
            EnsureGradeVolume();
            _baselineCaptured = true;
        }

        /// <summary>
        /// Find the scene's sun and snapshot every field the sun override writes.
        /// RenderSettings.sun (the Lighting window's explicit Sun Source) wins;
        /// otherwise the brightest active directional light — the same light the
        /// player is actually lit by.
        /// </summary>
        private void ResolveSun()
        {
            if (_sunResolved)
                return;
            _sunResolved = true;

            _sun = RenderSettings.sun;
            if (_sun == null || _sun.type != LightType.Directional || !_sun.isActiveAndEnabled)
            {
                _sun = null;
                float best = float.MinValue;
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (l == null || l.type != LightType.Directional || !l.isActiveAndEnabled)
                        continue;
                    if (l.intensity > best)
                    {
                        _sun = l;
                        best = l.intensity;
                    }
                }
            }
            if (_sun == null)
                return;

            _baseSunColor = _sun.color;
            _baseSunIntensity = _sun.intensity;
            _baseSunShadowStrength = _sun.shadowStrength;
            _baseSunUseTemperature = _sun.useColorTemperature;
        }

        /// <summary>
        /// Create the applier's own global Volume, sitting above every existing
        /// volume in priority so its overrides win, but starting at weight 0 so it
        /// contributes nothing until a preset asks for a grade.
        /// </summary>
        private void EnsureGradeVolume()
        {
            if (_gradeVolume != null)
                return;

            int highest = int.MinValue;
            foreach (var v in FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
            {
                if (v != null && v.priority > highest)
                    highest = (int)v.priority;
            }
            if (highest == int.MinValue)
                highest = 0;

            _gradeVolume = gameObject.AddComponent<UnityEngine.Rendering.Volume>();
            _gradeVolume.isGlobal = true;
            _gradeVolume.priority = highest + GradePriorityOffset;
            _gradeVolume.weight = 0f;
            _gradeVolume.sharedProfile = null;
        }

        private void RestoreBaseline()
        {
            RenderSettings.ambientMode = _baseAmbientMode;
            RenderSettings.ambientLight = _baseAmbient;
            RenderSettings.fog = _baseFogEnabled;
            RenderSettings.fogMode = _baseFogMode;
            RenderSettings.fogColor = _baseFogColor;
            RenderSettings.fogDensity = _baseFogDensity;

            if (_sun != null)
            {
                _sun.useColorTemperature = _baseSunUseTemperature;
                _sun.color = _baseSunColor;
                _sun.intensity = _baseSunIntensity;
                _sun.shadowStrength = _baseSunShadowStrength;
            }

            TintTargets(Color.white);

            // Stand the grade down entirely — weight 0 contributes nothing, so the
            // scene falls back to its own volumes exactly.
            if (_gradeVolume != null)
            {
                _gradeVolume.weight = 0f;
                _gradeVolume.sharedProfile = null;
            }
        }

        /// <summary>
        /// Apply a preset. Passing null clears back to the captured baseline, which
        /// is what makes the null preset pixel-identical to the authored scene.
        /// </summary>
        public void Apply(WeatherPreset next)
        {
            Active = next;
            ApplyStack();
        }

        /// <summary>
        /// Apply the FULL stack: the active preset, its declared layers
        /// (depth-first, cycle-guarded), and any mutator-linked layers for the
        /// wave in flight — flattened, merged by the composition rules, and
        /// applied exactly once. One grade volume, one particle sheet, one set
        /// of channel writes, however tall the stack: layers add authoring
        /// freedom, never draw cost.
        /// </summary>
        private void ApplyStack()
        {
            CaptureBaseline();

            // Always start from the baseline so stacks never stack on stacks.
            RestoreBaseline();

            // A restart also ends any in-flight flash and gust authority; the
            // merged stack below re-establishes both from scratch.
            _flashStartedAt = -1f;
            _nextStrikeAt = float.PositiveInfinity;
            _gustStrength = 0f;
            // Wind authority is re-established from the merged stack below, so
            // it has to be surrendered here as well: left standing, a cleared
            // preset's wind would keep the dressing swaying (and keep it
            // swapped onto the weather shader) with no weather to justify it.
            _windStrength = 0f;
            _propSway = 0f;
            _puddleDepth = 0f;
            _sheetPs = null;

            var stack = new List<WeatherPreset>(8);
            Flatten(Active, stack, 0);
            if (mutatorLinks != null && _activeMutators != WaveMutator.None)
                foreach (MutatorWeatherLink link in mutatorLinks)
                    if (link != null && link.layer != null && (_activeMutators & link.mutator) != 0)
                        Flatten(link.layer, stack, 0);

            // Authored mutators bring their own layer, stacked AFTER the scene's
            // links so an asset's look wins a tie with a flag's — the asset is
            // the newer, more specific statement of intent. Guarded against a
            // layer arriving twice when an asset is bound to a linked flag.
            foreach (WeatherPreset layer in _mutatorAssetLayers)
                if (layer != null && !stack.Contains(layer))
                    Flatten(layer, stack, 0);

            if (stack.Count == 0)
            {
                SetPrecipitationActive(false);
                // Dry out rather than snap when playing: clearing a snowstorm
                // melts, it does not blink. Edit mode snaps for honest preview.
                _targetSnow = 0f;
                _targetWet = 0f;
                if (!Application.isPlaying)
                {
                    _curSnow = 0f;
                    _curWet = 0f;
                    PushSurface();
                }
                if (Application.isPlaying && AudioDirector.Instance != null)
                    AudioDirector.Instance.StopWeatherLoop();
                return;
            }

            DestroyEitherMode(_merged);
            _merged = Merge(stack);
            WeatherPreset next = _merged;

            if (next.overrideAmbient)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = next.ambientColor;
            }

            if (next.overrideSun && _sun != null)
            {
                // Compose filter × blackbody ourselves rather than leaning on
                // Light.useColorTemperature: that flag's contribution depends on
                // project graphics settings, and a sun ALREADY authored in
                // temperature mode would double-compose its own Kelvin with the
                // preset's. Forcing the flag off while the override is active
                // makes the written colour the whole story; restore puts flag
                // and colour back exactly. Intensity and shadow strength are
                // MULTIPLIERS over the captured baseline — same doctrine as the
                // ground tint — so dim-authored suns keep their identity.
                _sun.useColorTemperature = false;
                _sun.color = next.sunFilter *
                             Mathf.CorrelatedColorTemperatureToRGB(next.sunTemperatureKelvin);
                _sun.intensity = _baseSunIntensity * next.sunIntensityMult;
                _sun.shadowStrength = _baseSunShadowStrength * next.sunShadowStrengthMult;
            }

            if (next.overrideFog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = next.fogColor;
                RenderSettings.fogDensity = next.fogDensity;
            }

            if (next.overrideGroundTint)
                TintTargets(next.groundTint);

            // Surface response goes to TARGETS, not straight to the ground: the
            // throttled tick walks the currents there over surfaceChangeSeconds,
            // because snow that pops on in one frame reads as a bug while snow
            // that builds over ten seconds reads as weather. Edit mode snaps —
            // a preview that ramps is a preview that lies about the end state.
            _targetWet = next.groundWetness;
            _targetSnow = next.groundSnow;
            _targetFilm = next.snowColor;
            _trailStrength = next.trailStrength;
            _trailMeltSeconds = next.trailMeltSeconds;
            _puddleDepth = next.puddleDepth;
            _wetShine = next.wetShine;
            _surfaceSeconds = next.surfaceChangeSeconds <= 0f ? 10f : next.surfaceChangeSeconds;
            _envelope = 0f;
            if (!Application.isPlaying)
            {
                _curWet = _targetWet;
                _curSnow = _targetSnow;
                _envelope = 1f;
                PushSurface();
            }

            // sharedProfile, not profile: assigning the asset directly avoids
            // instantiating a runtime copy per apply (the same reason ground tinting
            // goes through a property block rather than renderer.material).
            if (next.overridePostProfile && next.postProfile != null && _gradeVolume != null)
            {
                _gradeVolume.sharedProfile = next.postProfile;
                _gradeVolume.weight = Mathf.Clamp01(next.postWeight);
            }

            BuildOrUpdatePrecipitation(next);

            // Ambience: the preset's authored loop, or a synthesized one when no
            // clip is assigned — weather that can be heard with zero audio assets.
            if (Application.isPlaying && AudioDirector.Instance != null)
            {
                if (next.precipitation == WeatherPreset.Precipitation.None || next.ambientVolume <= 0f)
                    AudioDirector.Instance.StopWeatherLoop();
                else
                    AudioDirector.Instance.PlayWeatherLoop(
                        next.ambientLoop,
                        next.precipitation == WeatherPreset.Precipitation.Rain,
                        next.ambientVolume);
            }

            // The flash restore point: whatever sun the STACK just applied is
            // what a lightning strike must come back to — override or baseline.
            if (_sun != null)
            {
                _stackSunIntensity = _sun.intensity;
                _stackSunColor = _sun.color;
            }
            _gustStrength = next.gustStrength;
            _gustPeriod = Mathf.Max(1f, next.gustPeriodSeconds);
            _windDirection = next.windDirection;
            _windStrength = next.windStrength;
            _propSway = next.propSway;
            _strikesPerMinute = next.lightningStrikesPerMinute;
            _lightningIntensity = next.lightningIntensity;
            _lightningColor = next.lightningColor;
            if (Application.isPlaying && _strikesPerMinute > 0f)
                _nextStrikeAt = Time.time + (60f / _strikesPerMinute) * UnityEngine.Random.Range(0.3f, 1f);
        }

        /// <summary>Depth-first flatten: the preset, then its layers in order —
        /// so a layer OVERRIDES what it modifies (last wins). Cycle-guarded and
        /// depth-capped; a self-referencing preset degrades to itself.</summary>
        private static void Flatten(WeatherPreset p, List<WeatherPreset> into, int depth)
        {
            if (p == null || depth > 4 || into.Contains(p))
                return;
            into.Add(p);
            if (p.layers == null)
                return;
            foreach (WeatherPreset layer in p.layers)
                Flatten(layer, into, depth + 1);
        }

        /// <summary>
        /// Merge a flattened stack into one carrier. The rules, per channel:
        /// flagged channels (ambient/sun/fog/tint/post) go to the LAST layer
        /// that sets the flag; surface film and wetness take the MAX (heavy
        /// snow plus anything stays heavy snow); precipitation, wind, gust,
        /// lightning and audio go to the last layer that uses them.
        /// </summary>
        private static WeatherPreset Merge(List<WeatherPreset> stack)
        {
            var m = ScriptableObject.CreateInstance<WeatherPreset>();
            m.name = "Weather(MergedStack)";
            m.hideFlags = HideFlags.HideAndDontSave;
            m.windStrength = 0f;
            m.gustStrength = 0f;
            m.lightningStrikesPerMinute = 0f;
            m.surfaceChangeSeconds = 0f;
            m.groundSnow = 0f;
            m.groundWetness = 0f;
            m.puddleDepth = 0f;

            foreach (WeatherPreset l in stack)
            {
                if (l.overrideAmbient) { m.overrideAmbient = true; m.ambientColor = l.ambientColor; }
                if (l.overrideSun)
                {
                    m.overrideSun = true;
                    m.sunTemperatureKelvin = l.sunTemperatureKelvin;
                    m.sunFilter = l.sunFilter;
                    m.sunIntensityMult = l.sunIntensityMult;
                    m.sunShadowStrengthMult = l.sunShadowStrengthMult;
                }
                if (l.overrideFog)
                {
                    m.overrideFog = true;
                    m.fogColor = l.fogColor;
                    m.fogDensity = l.fogDensity;
                }
                if (l.overrideGroundTint) { m.overrideGroundTint = true; m.groundTint = l.groundTint; }
                if (l.overridePostProfile && l.postProfile != null)
                {
                    m.overridePostProfile = true;
                    m.postProfile = l.postProfile;
                    m.postWeight = l.postWeight;
                }

                // Trail knobs travel WITH the film that wins: found dead in
                // review — the merged carrier kept its field defaults, so the
                // per-preset knobs did nothing through the only path the
                // runtime applies.
                if (l.groundSnow > m.groundSnow)
                {
                    m.groundSnow = l.groundSnow;
                    m.snowColor = l.snowColor;
                    m.trailStrength = l.trailStrength;
                    m.trailMeltSeconds = l.trailMeltSeconds;
                }
                if (l.groundWetness > m.groundWetness)
                {
                    // Wetness is ACCUMULATIVE (max wins — a second layer cannot
                    // dry the ground), and the water that stands in it belongs
                    // to whichever layer brought the water. Carried together so
                    // a light drizzle layered under a downpour cannot leave the
                    // downpour's soak with the drizzle's puddles.
                    m.groundWetness = l.groundWetness;
                    m.puddleDepth = l.puddleDepth;
                    m.wetShine = l.wetShine;
                }
                if (l.surfaceChangeSeconds > 0f) m.surfaceChangeSeconds = l.surfaceChangeSeconds;

                if (l.precipitation != WeatherPreset.Precipitation.None)
                {
                    m.precipitation = l.precipitation;
                    m.precipitationPrefab = l.precipitationPrefab;
                    m.precipitationRate = l.precipitationRate;
                    m.fallSpeed = l.fallSpeed;
                    m.particleSize = l.particleSize;
                    m.particleSizeJitter = l.particleSizeJitter;
                    m.streakLength = l.streakLength;
                    m.particleColor = l.particleColor;
                    m.ambientLoop = l.ambientLoop;
                    m.ambientVolume = l.ambientVolume;
                }

                if (l.windStrength > 0f)
                {
                    m.windDirection = l.windDirection;
                    m.windStrength = l.windStrength;
                    // Sway travels WITH the wind that carries it: a layer that
                    // brings its own wind brings its own answer for how hard
                    // that wind bends things. Left behind, a calm base preset's
                    // sway would silently govern a gale layered over it.
                    m.propSway = l.propSway;
                }
                if (l.gustStrength > 0f)
                {
                    m.gustStrength = l.gustStrength;
                    m.gustPeriodSeconds = l.gustPeriodSeconds;
                }
                if (l.lightningStrikesPerMinute > 0f)
                {
                    m.lightningStrikesPerMinute = l.lightningStrikesPerMinute;
                    m.lightningIntensity = l.lightningIntensity;
                    m.lightningColor = l.lightningColor;
                }
            }
            return m;
        }

        // -------------------------------------------------------- the live loop

        private void Update()
        {
            float now = Time.time;

            // Per-frame work exists ONLY while a flash is live (a 0.12 s pulse
            // needs frame resolution); everything else rides the throttled tick.
            if (_flashStartedAt >= 0f)
                FlashEnvelope(now);

            if (now < _nextTick)
                return;
            _nextTick = now + TickSeconds;
            Tick(now);
        }

        private void Tick(float now)
        {
            // ---- progressive surfaces + precipitation ramp -------------------
            bool moving = _envelope < 1f ||
                          !Mathf.Approximately(_curSnow, _targetSnow) ||
                          !Mathf.Approximately(_curWet, _targetWet);
            if (moving)
            {
                float step = TickSeconds / Mathf.Max(0.5f, _surfaceSeconds);
                _envelope = Mathf.Min(1f, _envelope + step);
                _curSnow = Mathf.MoveTowards(_curSnow, _targetSnow, step);
                _curWet = Mathf.MoveTowards(_curWet, _targetWet, step);
                PushSurface();

                if (_sheetPs != null)
                {
                    var emission = _sheetPs.emission;
                    emission.rateOverTime = _resolvedRate * Mathf.SmoothStep(0f, 1f, _envelope);
                }
            }

            // ---- gusts -------------------------------------------------------
            // Two incommensurate sines so the rhythm never quite repeats. The
            // envelope multiplies the HORIZONTAL drift only: gusts push sideways,
            // they do not make snow fall faster.
            if (_gustStrength > 0f)
            {
                float g = 0.6f * Mathf.Sin(now * (2f * Mathf.PI) / _gustPeriod) +
                          0.4f * Mathf.Sin(now * (2f * Mathf.PI) * 2.7f / _gustPeriod + 1.7f);
                float factor = 1f + _gustStrength * g;
                if (_sheetPs != null)
                {
                    var vel = _sheetPs.velocityOverLifetime;
                    vel.x = new ParticleSystem.MinMaxCurve(_sheetBaseVel.x * factor);
                    vel.z = new ParticleSystem.MinMaxCurve(_sheetBaseVel.z * factor);
                }

                // The SAME gust drives the vegetation, so a gust that pushes
                // the snow sideways bends the trees on the same beat. Falling
                // motes and standing props reacting to different winds is the
                // detail that gives the whole effect away. Not gated on the
                // particle sheet: a windy clear day has no sheet and still has
                // trees. One global write per throttled tick.
                if (_propSway > 0f && _windStrength > 0f)
                    PropSnow.PushWind(_windDirection,
                        PropSnow.SwayAmplitude(_windStrength * factor * _envelope, _propSway));
            }

            // ---- lightning scheduling ---------------------------------------
            if (_strikesPerMinute > 0f && _flashStartedAt < 0f && now >= _nextStrikeAt)
            {
                _flashStartedAt = now;
                _nextStrikeAt = now + (60f / _strikesPerMinute) * UnityEngine.Random.Range(0.45f, 1.6f);
            }
        }

        /// <summary>Push the CURRENT surface values to terrain and props.</summary>
        private void PushSurface()
        {
            ApplySurfaceResponse(_curWet, _curSnow, _targetFilm);
            PushWater(_curWet);
            ApplyRoadwayCover(_curSnow, _curWet);
            PropSnow.Apply(_curSnow, _curWet, _targetFilm,
                           _windDirection, _windStrength * _envelope, _propSway);
            // Trails follow the RAMPED film, so they fade in with the snowfall
            // and stop mattering as it melts — and the map self-clears when the
            // film is gone, so the next storm starts unmarked.
            TrailMap.Push(gameObject, _curSnow, _trailStrength, _trailMeltSeconds);
        }

        /// <summary>The strike: a sharp main pulse and a dimmer echo, ~0.4 s
        /// total, written straight onto the sun and restored exactly to what the
        /// stack applied. Brief on purpose — a long bright flash costs the
        /// readability doctrine more than it buys drama.</summary>
        private void FlashEnvelope(float now)
        {
            if (_sun == null)
            {
                _flashStartedAt = -1f;
                return;
            }

            float t = now - _flashStartedAt;
            float e;
            if (t < 0.10f) e = 1f - t / 0.10f;                       // main pulse
            else if (t < 0.22f) e = 0f;
            else if (t < 0.34f) e = 0.4f * (1f - (t - 0.22f) / 0.12f); // echo
            else
            {
                _sun.intensity = _stackSunIntensity;
                _sun.color = _stackSunColor;
                _flashStartedAt = -1f;
                return;
            }

            _sun.intensity = _stackSunIntensity * Mathf.Lerp(1f, _lightningIntensity, e);
            _sun.color = Color.Lerp(_stackSunColor, _lightningColor, e * 0.8f);
        }

        /// <summary>Clear back to the authored look.</summary>
        public void Clear() => Apply(null);

        /// <summary>
        /// Re-run the current preset (DebugConsole `W`). Weather normally applies
        /// once at map load, so edits to a preset asset during play were invisible
        /// until a restart — which made tuning feel like the fields did nothing.
        /// Idempotent: the overrides converge, so pressing it twice changes nothing.
        /// </summary>
        public void Reapply() => Apply(Active != null ? Active : preset);

        // ------------------------------------------------------------ tinting

        private void TintTargets(Color tint)
        {
            ResolveTargets();
            if (_block == null)
                _block = new MaterialPropertyBlock();

            for (int i = 0; i < _resolvedTargets.Count; i++)
            {
                Renderer r = _resolvedTargets[i];
                if (r == null)
                    continue;
                // Multiplicative over the material's OWN colour — a white tint
                // (the restore path) therefore reproduces the authored look
                // exactly, whatever colour the material was authored with. The
                // existing block is read first so the generator's ground-tiling
                // properties survive on the same renderer.
                Color composed = _baseTints[i] * tint;
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, composed);
                _block.SetColor(ColorId, composed);
                r.SetPropertyBlock(_block);
            }
        }

        private static readonly int WaterId = Shader.PropertyToID("_CoreholdWater");
        private static readonly int SkyColorId = Shader.PropertyToID("_CoreholdSkyColor");

        /// <summary>Shoreline softness in metres. Wide enough that the water
        /// line is a wet margin rather than a cut edge, narrow enough that a
        /// shallow pool still has a shape.</summary>
        private const float ShorelineFeather = 0.45f;

        /// <summary>The terrain's own vertical extent, measured once from the
        /// resolved ground renderers. The water table climbs through THIS, so a
        /// map with 12 m of relief floods differently from a map with 2 m —
        /// which is right, and needs nothing authored per map.</summary>
        private float _groundMinY, _groundRangeY = 1f;
        private bool _groundMeasured;

        private void MeasureGround()
        {
            if (_groundMeasured)
                return;
            _groundMeasured = true;

            bool any = false;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 0; i < _resolvedTargets.Count; i++)
            {
                Renderer r = _resolvedTargets[i];
                if (r == null)
                    continue;
                Bounds b = r.bounds;
                min = Mathf.Min(min, b.min.y);
                max = Mathf.Max(max, b.max.y);
                any = true;
            }
            if (!any)
                return;

            _groundMinY = min;
            // A FLAT map measures ~0 range, which would make the water table a
            // switch rather than a rise. Flooring it keeps pools patchy there
            // (the shoreline wobble does the work) instead of tiling the whole
            // floor with water the instant it rains.
            _groundRangeY = Mathf.Max(max - min, 1.2f);
        }

        /// <summary>Raise the water table and hand the shaders the sky that wet
        /// ground has to mirror.</summary>
        private void PushWater(float wet)
        {
            ResolveTargets();
            MeasureGround();

            // Ripples belong to RAIN. Snow and dust settle on water without
            // stirring it, and a shimmering puddle under a dust storm is an
            // instant tell that this is a shader and not weather.
            float ripple = _merged != null &&
                           _merged.precipitation == WeatherPreset.Precipitation.Rain &&
                           _resolvedRate > 0f ? 1f : 0f;

            float level = _groundMinY + _groundRangeY * _puddleDepth * Mathf.Clamp01(wet);
            Shader.SetGlobalVector(WaterId,
                new Vector4(level, ShorelineFeather, ripple, _wetShine));

            // The scene's own atmosphere is the honest answer for what a puddle
            // reflects: fog colour while fog is on (it IS the sky's colour at
            // distance), ambient otherwise. A fixed blue would fight every
            // preset that grades the light.
            Color sky = RenderSettings.fog ? RenderSettings.fogColor : RenderSettings.ambientLight;
            Shader.SetGlobalColor(SkyColorId, sky);
        }

        private static readonly int SnowAmountId = Shader.PropertyToID("_SnowAmount");
        private static readonly int SnowColorId = Shader.PropertyToID("_SnowColor");
        private static readonly int WetAmountId = Shader.PropertyToID("_WetAmount");

        /// <summary>
        /// Push the surface response — wet and snow — onto the ground renderers
        /// through the SAME property block the tint uses, so a renderer keeps
        /// its generator-written tiling and its weather tint at once.
        ///
        /// Only the terrain shader reads these properties; on any other ground
        /// material they are inert, which is the desired failure: a theme whose
        /// ground is a plain URP Lit plane loses the effect rather than the
        /// scene. Always written (including zeros) so CLEARING weather restores
        /// dry ground rather than leaving the last preset's snow lying there.
        /// </summary>
        private void ApplySurfaceResponse(float wet, float snow, Color snowColor)
        {
            ResolveTargets();
            if (_block == null)
                _block = new MaterialPropertyBlock();

            for (int i = 0; i < _resolvedTargets.Count; i++)
            {
                Renderer r = _resolvedTargets[i];
                if (r == null)
                    continue;
                r.GetPropertyBlock(_block);
                _block.SetFloat(WetAmountId, Mathf.Clamp01(wet));
                _block.SetFloat(SnowAmountId, Mathf.Clamp01(snow));
                _block.SetColor(SnowColorId, snowColor);
                r.SetPropertyBlock(_block);
            }
        }

        private void ResolveTargets()
        {
            if (_resolvedTargets.Count > 0)
                return;

            if (tintTargets != null && tintTargets.Length > 0)
            {
                _resolvedTargets.AddRange(tintTargets);
            }
            else
            {
                var floor = GameObject.Find("Floor");
                if (floor != null)
                {
                    var r = floor.GetComponent<Renderer>();
                    if (r != null) _resolvedTargets.Add(r);
                }

                // Terrain maps (M-b): the relief mesh IS the visible ground over
                // the design box — tinting only the flat floor under it painted
                // the apron and left a seam at the relief's edge.
                var relief = GameObject.Find("TerrainRelief");
                if (relief != null)
                    _resolvedTargets.AddRange(relief.GetComponentsInChildren<Renderer>(true));

                // Shipped map: the R11 silhouette band object.
                var band = GameObject.Find("SilhouetteBand");
                if (band != null)
                    _resolvedTargets.AddRange(band.GetComponentsInChildren<Renderer>(true));

                // Generated maps: silhouettes are placed props under Dressing,
                // marked with their EnvPack role — the band object never exists.
                foreach (var prop in FindObjectsByType<PlacedProp>(FindObjectsSortMode.None))
                    if (prop != null && prop.role == "Silhouette")
                        _resolvedTargets.AddRange(prop.GetComponentsInChildren<Renderer>(true));
            }

            ResolveRoadways();

            // Capture each target's authored material colour once — the value the
            // multiplicative tint composes over and the restore returns to.
            _baseTints.Clear();
            for (int i = 0; i < _resolvedTargets.Count; i++)
            {
                Renderer r = _resolvedTargets[i];
                Color baseColor = Color.white;
                Material m = r != null ? r.sharedMaterial : null;
                if (m != null)
                {
                    if (m.HasProperty(BaseColorId)) baseColor = m.GetColor(BaseColorId);
                    else if (m.HasProperty(ColorId)) baseColor = m.GetColor(ColorId);
                }
                _baseTints.Add(baseColor);
            }
        }

        // ---------------------------------------------------------- roadways

        /// <summary>
        /// The generated worn-path ribbons (LookStage): dark unlit transparent
        /// bands floating 5 cm over the ground along every route.
        ///
        /// They matter to weather for a reason that is easy to miss and ruins
        /// the effect: enemies walk EXACTLY on them, so the trails carved into
        /// the terrain's snow film are drawn underneath a ribbon that covers
        /// them and does not itself respond to weather. Snow settling over a
        /// path should bury the path — that is what makes tracks the only thing
        /// left to read — so the ribbon's alpha falls as the film rises.
        ///
        /// Not folded into the tint targets: a roadway must NOT take the ground
        /// tint or the snow colour (it would stop being a road), and it is the
        /// alpha, not the colour, that has to move.
        /// </summary>
        private readonly List<Renderer> _roadways = new List<Renderer>();
        private readonly List<Color> _roadwayBase = new List<Color>();

        /// <summary>How much of the ribbon a full film covers. Not 1: the lane
        /// is gameplay-critical reading, and a path that vanishes entirely
        /// costs the player more than the snow buys.</summary>
        private const float RoadwaySnowCover = 0.75f;

        private void ResolveRoadways()
        {
            _roadways.Clear();
            _roadwayBase.Clear();
            var root = GameObject.Find("Roadways");
            if (root == null)
                return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterial == null)
                    continue;
                Color c = r.sharedMaterial.HasProperty(BaseColorId)
                    ? r.sharedMaterial.GetColor(BaseColorId)
                    : r.sharedMaterial.HasProperty(ColorId)
                        ? r.sharedMaterial.GetColor(ColorId)
                        : Color.white;
                _roadways.Add(r);
                _roadwayBase.Add(c);
            }
        }

        /// <summary>How much more the worn path reads when it is soaked. A dirt
        /// track is the FIRST thing to darken in rain — it is compacted, so the
        /// water sits on it rather than draining through.</summary>
        private const float RoadwayWetGain = 0.45f;

        /// <summary>Sink the worn-path ribbons under the accumulating film, and
        /// deepen them under rain.</summary>
        private void ApplyRoadwayCover(float snow, float wet)
        {
            if (_roadways.Count == 0)
                return;
            if (_block == null)
                _block = new MaterialPropertyBlock();

            // Snow BURIES the path; rain DEEPENS it. Both at once is a thaw,
            // and the snow wins — which is correct, since what is on top is
            // what you see.
            float keep = (1f + RoadwayWetGain * Mathf.Clamp01(wet)) *
                         (1f - RoadwaySnowCover * Mathf.Clamp01(snow));
            for (int i = 0; i < _roadways.Count; i++)
            {
                Renderer r = _roadways[i];
                if (r == null)
                    continue;
                Color c = _roadwayBase[i];
                c.a = Mathf.Clamp01(c.a * keep);
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, c);
                _block.SetColor(ColorId, c);
                r.SetPropertyBlock(_block);
            }
        }

        // ------------------------------------------------------ precipitation

        private void SetPrecipitationActive(bool active)
        {
            if (_precipitation != null && _precipitation.activeSelf != active)
                _precipitation.SetActive(active);
        }

        /// <summary>
        /// Build (once) and configure the camera-attached precipitation layer.
        /// Parented to the camera with LOCAL simulation space, which is what makes
        /// it screen-space: the volume travels with the view instead of being a
        /// world-sized system the camera looks into.
        /// </summary>
        private void BuildOrUpdatePrecipitation(WeatherPreset p)
        {
            if (p.precipitation == WeatherPreset.Precipitation.None)
            {
                SetPrecipitationActive(false);
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
                return;

            // Rebuild when the SOURCE changes — procedural ⇄ prefab, or a different
            // prefab. Apply() is public, so R23's night variant or a generated map
            // picking from a pool can swap presets at any time, and reusing the
            // previous layer's object would leave an authored prefab unspawned or a
            // procedural system stale.
            if (_precipitationBuilt && _precipitationSource != p.precipitationPrefab && _precipitation != null)
            {
                DestroyEitherMode(_precipitation);
                _precipitation = null;
            }

            if (_precipitation == null)
            {
                _precipitation = p.precipitationPrefab != null
                    ? Instantiate(p.precipitationPrefab)
                    : new GameObject("Precipitation");
                _precipitationSource = p.precipitationPrefab;
                _precipitationBuilt = true;
                _prefabBaseScale = _precipitation.transform.localScale;
            }

            SetPrecipitationActive(true);

            if (p.precipitationPrefab != null)
            {
                // An authored prefab is a WORLD effect and is placed as one. The
                // camera-parenting below is right only for the procedural sheet:
                // parented at (0,0,12) with identity local rotation, a prefab's
                // emitter sits at a point mid-view — pitched 38° with the camera —
                // and rains a tilted column onto the middle of the screen. World
                // placement over the camera's ground footprint is what "falls from
                // the top" actually means for an authored volume.
                PlaceAuthoredPrefab(cam, p);

                // It still has to live inside R14's overdraw budget, and a kit
                // effect built from half a dozen stacked systems will blow it
                // silently. Count once, at apply, and say so.
                int layers = _precipitation.GetComponentsInChildren<ParticleSystem>(true).Length;
                if (layers > MaxAlphaLayers)
                {
                    Debug.LogWarning(
                        $"[Weather] '{p.name}' uses an authored prefab with {layers} particle systems — " +
                        $"R14 budgets {MaxAlphaLayers} alpha layers. Disable the extra sub-systems or " +
                        "author a lighter prefab; overdraw is what costs the 907×510 legibility bar.", this);
                }
                return;
            }

            // Procedural sheet: camera-attached and screen-spanning by design.
            _precipitation.transform.SetParent(cam.transform, false);
            _precipitation.transform.localPosition = new Vector3(0f, 0f, 12f);
            _precipitation.transform.localRotation = Quaternion.identity;
            ConfigureProceduralParticles(_precipitation, p, cam);
        }

        private Vector3 _prefabBaseScale = Vector3.one;

        /// <summary>
        /// Place an authored precipitation prefab as a WORLD-UPRIGHT screen layer
        /// at the same 12 m the procedural sheet uses.
        ///
        /// The previous approach — spanning the camera's whole GROUND footprint —
        /// asked a rain kit to cover 100×60 m from 40 m up, and kit prefabs are
        /// authored for ~10 m of fall: drops died mid-air, which on screen read as
        /// rain that starts and vanishes in the middle of the view.
        ///
        /// At 12 m the window is small — but "the prefab's own lifetimes cross it"
        /// proved FALSE on a pitched camera: the traverse from above the top edge
        /// to below the bottom needs lift + screenHeight/up.y + margin (≈17 m at
        /// 38°), and kit lifetimes stop a little short, which reads as rain that
        /// never reaches the bottom of the screen. So placement now calls
        /// <see cref="ApplyAuthoredOverrides"/>, which EXTENDS each system's
        /// lifetime until its fall covers the traverse — and, same trip, makes the
        /// preset's `[TUNE]` numbers real for authored prefabs (they previously
        /// applied only to the procedural sheet, so editing them did nothing).
        ///
        /// World-upright (yaw only), because inheriting the camera's 38° pitch is
        /// what made the prefab rain sideways; parented AFTER posing, with the
        /// world pose kept, so it follows the (fixed) camera without adopting its
        /// rotation.
        /// </summary>
        private void PlaceAuthoredPrefab(Camera cam, WeatherPreset p)
        {
            const float layerDistance = 12f;

            _precipitation.transform.SetParent(null);
            _precipitation.transform.localScale = _prefabBaseScale;   // measure at authored scale

            float halfH = layerDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfW = halfH * Mathf.Max(cam.aspect, 20f / 9f);

            Vector3 centre = cam.transform.position + cam.transform.forward * layerDistance;
            // Emitter above the view's top edge at this distance (cam.up.y is the
            // world-vertical share of screen-up on a pitched camera), plus margin.
            float lift = halfH * Mathf.Max(0.2f, cam.transform.up.y) + 1.5f;

            _precipitation.transform.rotation = Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f);
            _precipitation.transform.position = centre + Vector3.up * lift;

            Vector2 have = MeasureEmitterHalfExtent(_precipitation);
            float scale = 1f;
            if (have.x > 0.25f)
                scale = Mathf.Clamp(halfW / have.x, 1f, 12f);
            _precipitation.transform.localScale = _prefabBaseScale * scale;

            _precipitation.transform.SetParent(cam.transform, true);

            ApplyAuthoredOverrides(p, cam, halfH, lift);

            // Pre-simulate so the column is falling through the whole window from
            // frame one instead of raining only at the top for the first seconds.
            foreach (var ps in _precipitation.GetComponentsInChildren<ParticleSystem>(true))
            {
                ps.Clear(false);
                ps.Simulate(3f, false, true);
                ps.Play(false);
            }
        }

        /// <summary>
        /// Make the preset's `[TUNE]` numbers authoritative for an AUTHORED prefab
        /// (they always were for the procedural sheet):
        ///
        ///   • REACH — each system's lifetime is extended until its fall
        ///     (startSpeed + velocity-over-lifetime + gravity) covers the full
        ///     screen traverse, so drops exit below the bottom edge instead of
        ///     dying mid-view. Falling past the screen is free; stopping short is
        ///     what reads as broken.
        ///   • SIZE — `particleSize` CAPS each system's start size (never
        ///     enlarges), which is what tames kit dust motes that render fat at
        ///     the 12 m layer.
        ///   • RATE — every system's emission is rescaled proportionally so the
        ///     TOTAL matches `precipitationRate`.
        ///
        /// All three are idempotent, so re-applying (DebugConsole `W`) is safe.
        /// </summary>
        private void ApplyAuthoredOverrides(WeatherPreset p, Camera cam, float halfH, float lift)
        {
            var systems = _precipitation.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0)
                return;

            float upShare = Mathf.Max(0.35f, cam.transform.up.y);
            float fallNeeded = lift + (2f * halfH) / upShare + 2f;

            float totalRate = 0f;
            for (int i = 0; i < systems.Length; i++)
            {
                var em = systems[i].emission;
                if (em.enabled)
                    totalRate += em.rateOverTime.constantMax;
            }
            float rateScale = totalRate > 0.01f && p.precipitationRate > 0f
                ? Mathf.Clamp(p.precipitationRate / totalRate, 0.05f, 10f)
                : 1f;

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                var main = ps.main;

                // Reach: solve fall(t) = v0·t + ½g·t² ≥ fallNeeded for t.
                float t0 = Mathf.Max(0.05f, main.startLifetime.constantMax);
                float v0 = Mathf.Abs(main.startSpeed.constantMax);
                var vol = ps.velocityOverLifetime;
                if (vol.enabled)
                    v0 += Mathf.Abs(vol.y.constantMax);
                float g = 9.81f * Mathf.Max(0f, main.gravityModifier.constantMax);

                float drop = v0 * t0 + 0.5f * g * t0 * t0;
                if (drop + 0.01f < fallNeeded && (v0 > 0.01f || g > 0.01f))
                {
                    float t1 = g > 0.01f
                        ? (-v0 + Mathf.Sqrt(v0 * v0 + 2f * g * fallNeeded)) / g
                        : fallNeeded / v0;
                    main.startLifetime = Mathf.Clamp(t1, t0, t0 * 8f);
                }

                // Size cap (dust motes): never enlarge an authored look. The cap
                // carries the preset's spread with it, so an authored system
                // brought down to size gains the depth cue rather than becoming
                // a field of identical dots.
                if (p.particleSize > 0.004f && main.startSize.constantMax > p.particleSize)
                    main.startSize = SizeCurve(p);

                // Rate: proportional reshape toward the preset total.
                var emission = ps.emission;
                if (emission.enabled && !Mathf.Approximately(rateScale, 1f))
                    emission.rateOverTime = emission.rateOverTime.constantMax * rateScale;

                main.maxParticles = Mathf.Max(main.maxParticles,
                    Mathf.CeilToInt(emission.rateOverTime.constantMax * main.startLifetime.constantMax) + 32);
            }
        }

        /// <summary>Half-extent (x, z) of the widest emitter shape in the prefab, at its current scale.</summary>
        private static Vector2 MeasureEmitterHalfExtent(GameObject root)
        {
            var best = Vector2.zero;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var shape = ps.shape;
                if (!shape.enabled)
                    continue;
                Vector3 ls = ps.transform.lossyScale;
                Vector2 half;
                switch (shape.shapeType)
                {
                    case ParticleSystemShapeType.Box:
                    case ParticleSystemShapeType.BoxShell:
                    case ParticleSystemShapeType.BoxEdge:
                        half = new Vector2(Mathf.Abs(shape.scale.x * ls.x) * 0.5f,
                                           Mathf.Abs(shape.scale.z * ls.z) * 0.5f);
                        break;
                    default:
                        float r = shape.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
                        half = new Vector2(r, r);
                        break;
                }
                if (half.x * half.y > best.x * best.y)
                    best = half;
            }
            return best;
        }

        /// <summary>Destroy that works whether the applier is driven in play mode or from an editor tool.
        /// (Named to avoid hiding UnityEngine.Object's deprecated static DestroyObject — CS0108.)</summary>
        private static void DestroyEitherMode(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>
        /// The preset's size as a RANGE rather than a constant — the sheet's
        /// only depth cue.
        ///
        /// The spread is applied around the authored size, not above it, so
        /// raising the jitter never raises the average: the mean stays exactly
        /// `particleSize` and the R14 legibility budget (alpha layers, overdraw)
        /// is unchanged. Jitter 0 collapses back to the constant the preset
        /// asked for, which is what keeps every already-authored asset looking
        /// the way it looked.
        /// </summary>
        private static ParticleSystem.MinMaxCurve SizeCurve(WeatherPreset p)
        {
            float j = Mathf.Clamp(p.particleSizeJitter, 0f, 0.9f);
            if (j <= 0.001f)
                return new ParticleSystem.MinMaxCurve(p.particleSize);
            return new ParticleSystem.MinMaxCurve(p.particleSize * (1f - j),
                                                  p.particleSize * (1f + j));
        }

        private void ConfigureProceduralParticles(GameObject host, WeatherPreset p, Camera cam)
        {
            var ps = host.GetComponent<ParticleSystem>();
            if (ps == null)
                ps = host.AddComponent<ParticleSystem>();

            bool rain = p.precipitation == WeatherPreset.Precipitation.Rain;

            // Emission volume sized to cover the view at the layer's distance.
            float halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float height = 2f * 12f * Mathf.Tan(halfV);
            float width = height * Mathf.Max(cam.aspect, 20f / 9f);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local; // camera-attached
            main.startSpeed = 0f;
            // The preset's size is literal — no hidden multiplier. Apparent size is
            // driven by the layer distance (12 m), where one pixel at 907×510 is
            // roughly 0.015 m, so these numbers are far smaller than world-scale
            // intuition suggests.
            //
            // SPREAD is the depth cue. The sheet is a flat plane, so nothing in
            // it is genuinely nearer or further; identical dots therefore read
            // as a texture laid over the screen rather than as weather the
            // camera is inside. A size range fakes the parallax the flat sheet
            // cannot have — big motes read as close, small as far — and costs
            // one MinMaxCurve at build time, no per-frame work and no extra draw.
            main.startSize = SizeCurve(p);
            main.startColor = p.particleColor;
            main.gravityModifier = 0f;

            // MOTION FIRST — it decides where the emitter belongs. Fall + wind,
            // resolved into the camera's frame (the sheet is camera-local).
            Vector3 wind = p.windDirection.sqrMagnitude > 0.0001f
                ? p.windDirection.normalized * p.windStrength
                : Vector3.zero;
            Vector3 worldVel = Vector3.down * p.fallSpeed + wind;
            Vector3 localVel = cam.transform.InverseTransformDirection(worldVel);

            // Time in frame on each screen axis. The spawn box reaches 0.3·height
            // DEEPER than the 12 m layer, where the same screen height spans more
            // metres — hence the stretched span. Both speeds are the REAL ones
            // (fall projected onto screen-up, plus wind), not fallSpeed alone.
            float span = height * ((12f + height * 0.3f) / 12f);
            float downSpeed = Mathf.Max(0f, -localVel.y);
            float sideSpeed = Mathf.Abs(localVel.x);
            float tDown = downSpeed > 0.05f ? span / downSpeed : float.PositiveInfinity;
            float tSide = sideSpeed > 0.05f ? width / sideSpeed : float.PositiveInfinity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;

            if (tDown <= 0.6f * tSide)
            {
                // FALLS through the frame (rain): a thin slab along the top edge,
                // raining down. Streaks read best entering from above, and the
                // crosswind is far too slow to blow them out on the way.
                shape.scale = new Vector3(width, 0.1f, height * 0.6f);
                shape.position = new Vector3(0f, height * 0.5f, 0f);
                main.startLifetime = tDown + 2f / Mathf.Max(0.1f, downSpeed);

                // The sheet is ONE reused system across presets, so the drift
                // branch's fade has to be cleared here or a dust→rain change
                // would leave rain fading in and out mid-fall.
                var fade = ps.colorOverLifetime;
                fade.enabled = false;
            }
            else
            {
                // DRIFTS across the frame (dust on a crosswind). The top slab is
                // WRONG here, and was the bug: dust's wind (5 m/s, mostly sideways)
                // beats its fall (1.6 m/s), so motes spawned along the top edge blew
                // out of the SIDE before they could cross the view — the sheet only
                // ever painted the upper strip, at every camera pitch. Fill the
                // whole visible volume instead: motes exist wherever the player
                // looks, whatever direction the wind takes them.
                shape.scale = new Vector3(width * 1.1f, height * 1.15f, height * 0.6f);
                shape.position = Vector3.zero;
                main.startLifetime = Mathf.Clamp(Mathf.Min(tDown, tSide) * 0.9f, 0.5f, 20f);

                // Volume spawning means motes appear INSIDE the frame, so fade them
                // in and out instead of popping.
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[]
                    {
                        new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f),
                        new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f)
                    });
                col.color = new ParticleSystem.MinMaxGradient(grad);
            }

            // Looping + prewarm so the layer is already full on the first frame
            // (a volume that fills in over its lifetime reads as weather fading in).
            main.prewarm = true;
            main.maxParticles = Mathf.CeilToInt(p.precipitationRate * main.startLifetime.constant) + 32;

            var emission = ps.emission;
            emission.enabled = true;
            // In play the rate starts at the envelope (0 on a fresh apply) and
            // the throttled tick ramps it in with the surfaces — precipitation
            // that snaps to full blast reads as a switch, not as weather.
            emission.rateOverTime = Application.isPlaying
                ? p.precipitationRate * Mathf.SmoothStep(0f, 1f, _envelope)
                : p.precipitationRate;
            _resolvedRate = p.precipitationRate;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.x = new ParticleSystem.MinMaxCurve(localVel.x);
            vel.y = new ParticleSystem.MinMaxCurve(localVel.y);
            vel.z = new ParticleSystem.MinMaxCurve(localVel.z);

            // One shared unlit transparent material: one draw call for the layer,
            // and a single alpha pass so overdraw stays inside R14's budget.
            var renderer = host.GetComponent<ParticleSystemRenderer>();
            if (_precipitationMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                _precipitationMaterial = new Material(shader) { name = "Weather_Precipitation (shared)" };

                // The FULL transparent surface state, not just the _Surface float.
                // URP materials get their blend state and queue from the shader GUI
                // at edit time; a material configured from code has to set the
                // keyword, the blend factors and the queue itself, or it stays on
                // the shader's defaults — effectively opaque-queue. That is what
                // made rain and dust vanish over the pads and paths: every ground
                // overlay there (pad auras, blob shadows, pips — queue 3000+) drew
                // AFTER the sheet and painted over it. Queue 3080 puts the sheet
                // above those and below the health bars (5000), which matches the
                // physical layout — the sheet hangs 12 m from the camera, nearer
                // than anything it was losing to.
                if (_precipitationMaterial.HasProperty("_Surface")) _precipitationMaterial.SetFloat("_Surface", 1f);
                if (_precipitationMaterial.HasProperty("_Blend")) _precipitationMaterial.SetFloat("_Blend", 0f);
                if (_precipitationMaterial.HasProperty("_SrcBlend"))
                    _precipitationMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (_precipitationMaterial.HasProperty("_DstBlend"))
                    _precipitationMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (_precipitationMaterial.HasProperty("_ZWrite")) _precipitationMaterial.SetFloat("_ZWrite", 0f);
                _precipitationMaterial.SetOverrideTag("RenderType", "Transparent");
                _precipitationMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _precipitationMaterial.DisableKeyword("_ALPHATEST_ON");
                _precipitationMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 80;

                // A particle material with NO base map draws a flat opaque
                // QUAD — which is why dust rendered as a field of hard grey
                // squares over the whole map instead of soft motes. Generated
                // rather than authored, so it needs no asset and cannot go
                // missing in a build: a 32×32 radial alpha falloff.
                Texture2D sprite = BuildMoteSprite();
                if (_precipitationMaterial.HasProperty("_BaseMap"))
                    _precipitationMaterial.SetTexture("_BaseMap", sprite);
                if (_precipitationMaterial.HasProperty("_MainTex"))
                    _precipitationMaterial.SetTexture("_MainTex", sprite);
            }
            renderer.sharedMaterial = _precipitationMaterial;
            renderer.renderMode = rain
                ? ParticleSystemRenderMode.Stretch
                : ParticleSystemRenderMode.Billboard;
            if (rain)
            {
                // Stretched-billboard length is width × lengthScale. velocityScale
                // would add length proportional to particle SPEED — but all motion
                // here lives in velocityOverLifetime while startSpeed stays 0, so it
                // contributes nothing and relying on it just produced a stubby dash.
                // Drive the streak from lengthScale alone, which is predictable.
                renderer.velocityScale = 0f;
                renderer.lengthScale = Mathf.Max(1f, p.streakLength);
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.alignment = ParticleSystemRenderSpace.View;

            // Cache for the live loop: gusts modulate THESE base velocities and
            // the ramp raises THIS system's rate, without a GetComponent per tick.
            _sheetPs = ps;
            _sheetBaseVel = localVel;

            ps.Clear();
            ps.Play();
        }

        /// <summary>
        /// The soft round mote every precipitation particle wears: a 32×32
        /// radial alpha falloff, generated once and shared.
        ///
        /// Generated rather than authored for the same reason the terrain
        /// detail noise is — an asset can be missing, mis-imported or stripped
        /// from a build, and this must never be any of those. Without it a
        /// URP particle material has no base map and draws a flat opaque QUAD,
        /// which is exactly how dust came out as a field of hard grey squares
        /// across the whole map.
        ///
        /// Alpha is squared so the edge fades faster than linearly — a linear
        /// falloff still reads as a disc with a visible rim at this size.
        /// </summary>
        private static Texture2D _moteSprite;

        private static Texture2D BuildMoteSprite()
        {
            if (_moteSprite != null)
                return _moteSprite;

            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "Weather_Mote (generated)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[size * size];
            const float centre = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / centre, dy = (y - centre) / centre;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            _moteSprite = tex;
            return tex;
        }
    }
}
