using System;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Data source of truth for the <see cref="VFXDirector"/> wiring (GDD §11).
    ///
    /// Historically the director's effect slots were written from a hard-coded map
    /// inside the editor setup tool, so the ONLY way to change which Cartoon FX
    /// prefab played for an effect was to edit code and re-run the tool on every
    /// scene. This asset makes that wiring DATA: when it exists, both the testbed's
    /// "Apply" button (which writes it from a tuned scene director) and the Level
    /// Generator's VFX setup (which reads it) use it as the single source — so a
    /// change made once in the testbed flows into every future generated level.
    ///
    /// One asset lives at <c>Assets/_COREHOLD/Data/VFXDirectorConfig.asset</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/VFX Director Config", fileName = "VFXDirectorConfig")]
    public class VFXDirectorConfig : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("Which logical effect this prefab fulfils (GDD §11).")]
            public VFXDirector.Effect id;

            [Tooltip("Any Shuriken-based effect prefab (Cartoon FX, Epic Toon FX, etc.) played for this effect. Must contain at least one ParticleSystem.")]
            public GameObject prefab;

            [Tooltip("How many copies to prewarm so the first play never allocates.")]
            public int prewarm;
        }

        [Tooltip("One entry per logical effect the VFXDirector plays, in slot order.")]
        public Entry[] effects = Array.Empty<Entry>();

        [Header("Hitscan tracer (Autocannon + Arc Node)")]
        [Tooltip("ADDITIVE material for the tracer's white-hot core line. When null the VFXDirector builds one at runtime from the Corehold/VFXTracer shader.")]
        public Material tracerCoreMaterial;

        [Tooltip("ALPHA-BLEND material for the tracer's coloured halo line (hue-preserving). When null the VFXDirector builds one at runtime from the Corehold/VFXTracer shader.")]
        public Material tracerHaloMaterial;

        [Min(1f)]
        [Tooltip("How much wider the coloured halo line is than the core line.")]
        public float tracerHaloWidthScale = 3f;

        [Min(0f)]
        [Tooltip("HDR brightness of the white-hot core (Bloom glow). Keep moderate so the halo hue survives tonemapping.")]
        public float tracerCoreGlow = 1.6f;

        [Tooltip("Copies of the tracer prewarmed into its pool (shared by both factions).")]
        public int tracerPrewarm = 8;

        [Header("Friendly tracer (tower fire)")]
        [Tooltip("Friendly (tower) tracer line width in metres.")]
        public float friendlyTracerWidth = 0.08f;

        [Min(0f)]
        [Tooltip("Brightness multiplier applied to the friendly tracer's HDR colour. Keep moderate so the hue survives ACES tonemapping.")]
        public float friendlyTracerGlow = 1f;

        [ColorUsage(true, true)]
        [Tooltip("Friendly (tower) tracer colour — cool blue faction identity (halo hue).")]
        public Color friendlyTracerColor = new Color(0.15f, 0.55f, 1.8f, 1f);

        [Header("Hostile tracer (enemy fire)")]
        [Tooltip("Hostile (enemy) tracer line width in metres.")]
        public float hostileTracerWidth = 0.08f;

        [Min(0f)]
        [Tooltip("Brightness multiplier applied to the hostile tracer's HDR colour. Keep moderate so the hue survives ACES tonemapping.")]
        public float hostileTracerGlow = 1f;

        [ColorUsage(true, true)]
        [Tooltip("Hostile (enemy) tracer colour — hot red faction identity.")]
        public Color hostileTracerColor = new Color(3.0f, 0.05f, 0.03f, 1f);
    }
}
