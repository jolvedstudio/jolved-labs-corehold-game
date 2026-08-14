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
    ///     channel is restored on <see cref="Clear"/>. R11 owns the fog baseline —
    ///     it is solved from the camera and IS the null-preset look — so a preset
    ///     borrows it and gives it back. Without that, applying and clearing a
    ///     preset would silently leave the scene on the preset's fog and the
    ///     "null preset is pixel-identical" requirement would quietly fail.
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

        private readonly List<Renderer> _resolvedTargets = new List<Renderer>();
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

            EnsureGradeVolume();
            _baselineCaptured = true;
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
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, tint);
                _block.SetColor(ColorId, tint);
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
                return;
            }

            var floor = GameObject.Find("Floor");
            if (floor != null)
            {
                var r = floor.GetComponent<Renderer>();
                if (r != null) _resolvedTargets.Add(r);
            }

            var band = GameObject.Find("SilhouetteBand");
            if (band != null)
                _resolvedTargets.AddRange(band.GetComponentsInChildren<Renderer>(true));
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
                PlaceAuthoredPrefab(cam);

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
        /// rain that starts and vanishes in the middle of the view. At 12 m the
        /// visible window is only ~8 m tall, so an authored fall crosses ALL of it:
        /// drops enter above the top edge and are still falling when they leave the
        /// bottom — full-screen by construction, using the prefab's own lifetimes.
        ///
        /// World-upright (yaw only), because inheriting the camera's 38° pitch is
        /// what made the prefab rain sideways; parented AFTER posing, with the
        /// world pose kept, so it follows the (fixed) camera without adopting its
        /// rotation.
        /// </summary>
        private void PlaceAuthoredPrefab(Camera cam)
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

            // Pre-simulate so the column is falling through the whole window from
            // frame one instead of raining only at the top for the first seconds.
            foreach (var ps in _precipitation.GetComponentsInChildren<ParticleSystem>(true))
            {
                ps.Clear(false);
                ps.Simulate(3f, false, true);
                ps.Play(false);
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
            main.startLifetime = height / Mathf.Max(0.1f, p.fallSpeed);
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
