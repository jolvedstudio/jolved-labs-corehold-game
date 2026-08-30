using UnityEngine;
using UnityEngine.Rendering;

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
        /// <summary>
        /// What FALLS. Distinct from <see cref="groundSnow"/> / <see cref="groundWetness"/>,
        /// which are what the ground LOOKS like — the two are deliberately
        /// independent, because ground stays wet after rain stops and snow lies
        /// long after it stops falling.
        ///
        /// Appended, never reordered: these serialise as integers, so inserting
        /// a value in the middle would silently turn every authored Dust preset
        /// on disk into something else.
        /// </summary>
        public enum Precipitation
        {
            None,
            Rain,
            Dust,
            Snow
        }

        [Header("Ambient (GDD §5.5)")]
        [Tooltip("[TUNE] Override the scene's flat ambient light.")]
        public bool overrideAmbient;

        [Tooltip("[TUNE] Flat ambient colour applied when overrideAmbient is on.")]
        [ColorUsage(false, true)] public Color ambientColor = new Color(0.32f, 0.35f, 0.42f, 1f);

        [Header("Sun (directional light)")]
        [Tooltip("[TUNE] Grade the sun: colour temperature + filter tint, with relative intensity and shadow strength. The applier resolves the sun at apply time (RenderSettings.sun, else the brightest active directional light) and restores it exactly on clear. Cosmetic only — Blackout's gameplay darkness is a wave mutator and stays out of here.")]
        public bool overrideSun;

        [Tooltip("[TUNE] Colour temperature in Kelvin; the final sun colour is filter × blackbody(kelvin). ~6500 is neutral daylight; lower is warmer (golden hour ~3500), higher is colder/bluer (overcast ~7500, storm light 8000+).")]
        [Range(1500f, 20000f)] public float sunTemperatureKelvin = 6500f;

        [Tooltip("[TUNE] Filter colour multiplied over the temperature. Leave white for a pure temperature shift; use it for non-blackbody looks (a green storm cast, dust-orange haze light).")]
        public Color sunFilter = Color.white;

        [Tooltip("[TUNE] MULTIPLIER on the authored sun intensity — relative, never absolute, so a dim-authored sun (R23 night variant) keeps its identity under any preset. 1 = unchanged; overcast reads right around 0.6.")]
        [Range(0f, 2f)] public float sunIntensityMult = 1f;

        [Tooltip("[TUNE] MULTIPLIER on the authored shadow strength. Diffuse overcast light wants faint shadows (~0.4); 1 = unchanged.")]
        [Range(0f, 1f)] public float sunShadowStrengthMult = 1f;

        [Header("Distance fog")]
        [Tooltip("[TUNE] Override the fog baseline. R11 owns the baseline (it is solved from the camera and IS the null-preset look); a preset overrides it while active and the applier restores it on clear.")]
        public bool overrideFog;

        [Tooltip("[TUNE] Fog colour while this preset is active.")]
        public Color fogColor = new Color(0.10f, 0.12f, 0.16f, 1f);

        [Tooltip("[TUNE] ExponentialSquared fog density while this preset is active. R11 solves the baseline as sqrt(-ln T)/d_far; scale around that rather than guessing.")]
        public float fogDensity = 0.004f;

        [Header("Ground tint")]
        [Tooltip("[TUNE] Tint the ground and silhouette band. Applied through a shared MaterialPropertyBlock — never a per-object material instance, which would break batching and leak materials.")]
        [Tooltip("Surface response: how WET the ground looks (0 = dry). Darkens and desaturates the " +
                 "terrain rather than adding gloss — at 130-150 m wetness reads as darker ground, and a " +
                 "specular path would cost WebGL bandwidth for something nobody can resolve. Pairs " +
                 "naturally with Rain, but it is independent: ground stays wet after the rain stops.")]
        [Range(0f, 1f)] public float groundWetness;

        [Tooltip("Surface response: how much FILM lies on the ground (0 = none). Accumulates by the " +
                 "surface normal, so flat ground coats while slopes stay bare. With the default cool " +
                 "colour this is SNOW; with a warm colour it is DUST — a sandstorm preset uses this " +
                 "same lane with sand-coloured film. Props coat too, so the field does not end up " +
                 "white ground under untouched brown rocks.")]
        [Range(0f, 1f)] public float groundSnow;

        [Tooltip("Seconds for the surface film and wetness to reach their values after the preset " +
                 "applies (and for the precipitation rate to ramp in). Progressive on purpose — snow " +
                 "that pops on in one frame reads as a bug, snow that builds over ten seconds reads " +
                 "as weather. 0 = the 10 s default; edit-mode preview is always instant.")]
        [Range(0f, 60f)] public float surfaceChangeSeconds;

        [Tooltip("The colour snow lies as. Slightly blue-shifted off white reads as snow; pure white " +
                 "reads as blown-out ground.")]
        public Color snowColor = new Color(0.92f, 0.94f, 0.98f, 1f);

        [Tooltip("How hard ground enemies carve TRAILS through the snow film as they walk (0 = no " +
                 "trails). Trails darken the film back toward the ground beneath — carving, not " +
                 "deformation, because at 130-150 m darkening is all a track reads as anyway. " +
                 "Meaningful only while groundSnow is up: tracks need something to be tracks in.")]
        [Range(0f, 1f)] public float trailStrength = 0.8f;

        [Tooltip("Seconds for a carved trail to fill back in while the snow keeps falling. Short = " +
                 "a blizzard erasing the army's passage; long = the field remembers every wave.")]
        [Range(5f, 300f)] public float trailMeltSeconds = 45f;

        public bool overrideGroundTint;

        [Tooltip("[TUNE] Multiplicative tint for the tinted renderers.")]
        public Color groundTint = Color.white;

        [Header("Precipitation (camera-attached, screen-space)")]
        [Tooltip("[TUNE] Precipitation style. None keeps the particle layer switched off entirely.")]
        public Precipitation precipitation = Precipitation.None;

        [Header("Ambience (GDD §10)")]
        [Tooltip("Looping ambience for this weather. Leave EMPTY and a synthesized loop plays instead — rain hiss or wind, generated at runtime, so weather is audible with no audio assets. Assign a clip to replace it.")]
        public AudioClip ambientLoop;

        [Tooltip("Ambience loudness, scaled by the music group gain (mute silences it). 0 = this weather is silent.")]
        [Range(0f, 1f)] public float ambientVolume = 0.35f;

        [Tooltip("Optional authored prefab (e.g. CFXR) to use instead of the procedural layer. Leave null to build the layer at runtime with no asset dependency.")]
        public GameObject precipitationPrefab;

        [Tooltip("[TUNE] Particles emitted per second. The legibility bar (907×510) and the ≤3 alpha layers of overdraw are both a function of this — raise it carefully.")]
        [Range(0f, 800f)] public float precipitationRate = 220f;

        [Tooltip("[TUNE] Fall speed in metres/second.")]
        [Range(0f, 40f)] public float fallSpeed = 14f;

        [Tooltip("[TUNE] Particle WIDTH in metres — this is the literal size, no hidden multiplier. Read it in pixels, not world scale: the layer sits 12 m from the camera, so at 907×510 one pixel is roughly 0.015 m. A 0.05 m drop is ~3.4 px wide, which is already fat for rain.")]
        [Range(0.005f, 1f)] public float particleSize = 0.02f;

        [Tooltip("[TUNE] Rain only: streak length as a multiple of the particle WIDTH (Unity's stretched-billboard Length Scale). ~15-20 reads as rain; 2-3 reads as a fat dash. Ignored by Dust, which billboards.")]
        [Range(1f, 40f)] public float streakLength = 18f;

        [Tooltip("[TUNE] Particle colour and alpha. Low alpha is what keeps overdraw cheap and the field readable through the effect.")]
        public Color particleColor = new Color(0.75f, 0.82f, 0.95f, 0.35f);

        [Header("Post-processing grade")]
        [Tooltip("[TUNE] Layer a graded VolumeProfile over the scene. This is ADDITIVE — the applier drives its own higher-priority global Volume rather than replacing the scene's, so the base profile's Bloom and Tonemapping survive (swapping it wholesale would kill the HDR tracer glow). Author only the overrides weather should change.")]
        public bool overridePostProfile;

        [Tooltip("Grading profile layered over the base post FX. Declare only weather-relevant overrides — colour adjustments, white balance, vignette.")]
        public VolumeProfile postProfile;

        [Tooltip("[TUNE] Blend weight of the grade. 0 contributes nothing, 1 applies it fully.")]
        [Range(0f, 1f)] public float postWeight = 1f;

        [Header("Wind")]
        [Tooltip("[TUNE] Horizontal wind direction; normalized on use.")]
        public Vector3 windDirection = new Vector3(0.3f, 0f, -1f);

        [Tooltip("[TUNE] Wind strength in metres/second, applied as lateral drift on precipitation.")]
        [Range(0f, 20f)] public float windStrength = 2f;

        [Tooltip("GUSTING: fraction the wind strength swings by (0 = steady). A constant wind reads " +
                 "as mechanical; gusts are what make dust and snow feel alive. Costs one curve write " +
                 "per throttled tick, nothing per frame.")]
        [Range(0f, 1f)] public float gustStrength;

        [Tooltip("Seconds per gust cycle.")]
        [Range(1f, 20f)] public float gustPeriodSeconds = 7f;

        [Header("Lightning")]
        [Tooltip("Strikes per minute (0 = none). A strike is a two-pulse flash on the sun and " +
                 "ambient — enormous drama for the cost of two light writes per frame, only while a " +
                 "flash is live. Keep flashes brief; a long bright flash costs readability.")]
        [Range(0f, 20f)] public float lightningStrikesPerMinute;

        [Tooltip("How bright the flash peaks, as a multiplier on the sun's applied intensity.")]
        [Range(1.5f, 6f)] public float lightningIntensity = 3.5f;

        [Tooltip("Flash colour — cold blue-white reads as lightning.")]
        public Color lightningColor = new Color(0.85f, 0.9f, 1f, 1f);

        [Header("Composition")]
        [Tooltip("Sub-presets stacked OVER this one, in order — 'Heavy Snow' + a gust layer + a " +
                 "lightning layer composes without authoring a combined preset. Rules: flagged " +
                 "channels (ambient/sun/fog/tint/post) go to the LAST layer that sets the flag; " +
                 "surface film and wetness take the MAX across layers; precipitation, wind and " +
                 "lightning go to the last layer that uses them. One post volume, one particle " +
                 "sheet, whatever the stack says — layers add authoring freedom, never draw cost. " +
                 "Cycles are guarded; depth caps at 4.")]
        public WeatherPreset[] layers;
    }
}
