using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// A level's atmospheric look (roadmap R13). Weather is **cosmetic only** — it
    /// is applied at map load and never changes mid-run, because anything that
    /// actually changes play (air speed, acquisition range) is a wave mutator
    /// (R20) carried as a first-class balance-model term (R22). Keeping that line
    /// is what stops weather becoming a hidden difficulty variable the model
    /// cannot see.
    ///
    /// Every channel is opt-in via its own `override` flag, so a preset changes
    /// only what it declares and a **null preset changes nothing at all** — which
    /// is what makes "no preset is pixel-identical to today's look" true by
    /// construction rather than by careful authoring.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Weather Preset", fileName = "Weather_")]
    public class WeatherPreset : ScriptableObject
    {
        /// <summary>Precipitation style. Drives the camera-attached particle layer.</summary>
        public enum Precipitation
        {
            None,
            Rain,
            Dust
        }

        [Header("Ambient (GDD §5.5)")]
        [Tooltip("[TUNE] Override the scene's flat ambient light.")]
        public bool overrideAmbient;

        [Tooltip("[TUNE] Flat ambient colour applied when overrideAmbient is on.")]
        [ColorUsage(false, true)] public Color ambientColor = new Color(0.32f, 0.35f, 0.42f, 1f);

        [Header("Distance fog")]
        [Tooltip("[TUNE] Override the fog baseline. R11 owns the baseline (it is solved from the camera and IS the null-preset look); a preset overrides it while active and the applier restores it on clear.")]
        public bool overrideFog;

        [Tooltip("[TUNE] Fog colour while this preset is active.")]
        public Color fogColor = new Color(0.10f, 0.12f, 0.16f, 1f);

        [Tooltip("[TUNE] ExponentialSquared fog density while this preset is active. R11 solves the baseline as sqrt(-ln T)/d_far; scale around that rather than guessing.")]
        public float fogDensity = 0.004f;

        [Header("Ground tint")]
        [Tooltip("[TUNE] Tint the ground and silhouette band. Applied through a shared MaterialPropertyBlock — never a per-object material instance, which would break batching and leak materials.")]
        public bool overrideGroundTint;

        [Tooltip("[TUNE] Multiplicative tint for the tinted renderers.")]
        public Color groundTint = Color.white;

        [Header("Precipitation (camera-attached, screen-space)")]
        [Tooltip("[TUNE] Precipitation style. None keeps the particle layer switched off entirely.")]
        public Precipitation precipitation = Precipitation.None;

        [Tooltip("Optional authored prefab (e.g. CFXR) to use instead of the procedural layer. Leave null to build the layer at runtime with no asset dependency.")]
        public GameObject precipitationPrefab;

        [Tooltip("[TUNE] Particles emitted per second. The legibility bar (907×510) and the ≤3 alpha layers of overdraw are both a function of this — raise it carefully.")]
        [Range(0f, 800f)] public float precipitationRate = 220f;

        [Tooltip("[TUNE] Fall speed in metres/second.")]
        [Range(0f, 40f)] public float fallSpeed = 14f;

        [Tooltip("[TUNE] Particle size in metres.")]
        [Range(0.01f, 2f)] public float particleSize = 0.06f;

        [Tooltip("[TUNE] Particle colour and alpha. Low alpha is what keeps overdraw cheap and the field readable through the effect.")]
        public Color particleColor = new Color(0.75f, 0.82f, 0.95f, 0.35f);

        [Header("Wind")]
        [Tooltip("[TUNE] Horizontal wind direction; normalized on use.")]
        public Vector3 windDirection = new Vector3(0.3f, 0f, -1f);

        [Tooltip("[TUNE] Wind strength in metres/second, applied as lateral drift on precipitation.")]
        [Range(0f, 20f)] public float windStrength = 2f;
    }
}
