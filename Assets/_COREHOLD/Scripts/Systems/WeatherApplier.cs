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
            _baselineCaptured = true;
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

            BuildOrUpdatePrecipitation(next);
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

            if (_precipitation == null)
                _precipitation = p.precipitationPrefab != null
                    ? Instantiate(p.precipitationPrefab)
                    : new GameObject("Precipitation");

            _precipitation.transform.SetParent(cam.transform, false);
            // Sit the volume in front of the camera so it fills the view without
            // spraying particles behind it.
            _precipitation.transform.localPosition = new Vector3(0f, 0f, 12f);
            _precipitation.transform.localRotation = Quaternion.identity;
            SetPrecipitationActive(true);

            if (p.precipitationPrefab != null)
                return; // an authored prefab configures itself

            ConfigureProceduralParticles(_precipitation, p, cam);
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
            main.startSize = p.particleSize * (rain ? 1f : 3f);
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
                if (_precipitationMaterial.HasProperty("_Surface")) _precipitationMaterial.SetFloat("_Surface", 1f);
                if (_precipitationMaterial.HasProperty("_ZWrite")) _precipitationMaterial.SetFloat("_ZWrite", 0f);
            }
            renderer.sharedMaterial = _precipitationMaterial;
            renderer.renderMode = rain
                ? ParticleSystemRenderMode.Stretch
                : ParticleSystemRenderMode.Billboard;
            if (rain)
            {
                renderer.velocityScale = 0.12f;
                renderer.lengthScale = 2.5f;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.alignment = ParticleSystemRenderSpace.View;

            ps.Clear();
            ps.Play();
        }
    }
}
