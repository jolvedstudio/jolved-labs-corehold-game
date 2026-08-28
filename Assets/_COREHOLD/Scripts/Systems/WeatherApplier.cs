using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;

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

        private void Start()
        {
            CaptureBaseline();
            Apply(preset);
        }

        private void OnDestroy()
        {
            // Leave the editor's scene state as we found it.
            if (_baselineCaptured)
                RestoreBaseline();
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
            CaptureBaseline();

            // Always start from the baseline so presets never stack.
            RestoreBaseline();
            Active = next;

            if (next == null)
            {
                SetPrecipitationActive(false);
                if (Application.isPlaying && AudioDirector.Instance != null)
                    AudioDirector.Instance.StopWeatherLoop();
                return;
            }

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

                // Size cap (dust motes): never enlarge an authored look.
                if (p.particleSize > 0.004f && main.startSize.constantMax > p.particleSize)
                    main.startSize = p.particleSize;

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
            main.startSize = p.particleSize;
            main.startColor = p.particleColor;
            // Lifetime must cover the TRAVERSE, not the screen height: the camera
            // is pitched, so world-down crosses screen-up at fallSpeed·up.y (≈0.79
            // at 38°), and the spawn box spreads particles up to 0.3·height DEEPER
            // than the 12 m layer, where the same screen height spans more metres.
            // `height / fallSpeed` ignored both, and rain died ~70% down the view.
            float upShare = Mathf.Max(0.35f, cam.transform.up.y);
            float span = height * ((12f + height * 0.3f) / 12f);
            main.startLifetime = (span / upShare + 2f) / Mathf.Max(0.1f, p.fallSpeed);
            main.maxParticles = Mathf.CeilToInt(p.precipitationRate * main.startLifetime.constant) + 32;
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = p.precipitationRate;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(width, 0.1f, height * 0.6f);
            shape.position = new Vector3(0f, height * 0.5f, 0f);

            // Fall + wind drift, expressed in the camera's local frame.
            Vector3 wind = p.windDirection.sqrMagnitude > 0.0001f
                ? p.windDirection.normalized * p.windStrength
                : Vector3.zero;
            Vector3 worldVel = Vector3.down * p.fallSpeed + wind;
            Vector3 localVel = cam.transform.InverseTransformDirection(worldVel);

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

            ps.Clear();
            ps.Play();
        }
    }
}
