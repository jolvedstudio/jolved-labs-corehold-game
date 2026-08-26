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

            [Tooltip("A Cartoon FX Remaster prefab played for this effect.")]
            public GameObject prefab;

            [Tooltip("How many copies to prewarm so the first play never allocates.")]
            public int prewarm;
        }

        [Tooltip("One entry per logical effect the VFXDirector plays, in slot order.")]
        public Entry[] effects = Array.Empty<Entry>();

        [Header("Hitscan tracer (Autocannon + Arc Node)")]
        [Tooltip("Tracer line width in metres.")]
        public float tracerWidth = 0.15f;

        [Tooltip("Copies of the tracer prewarmed into its pool.")]
        public int tracerPrewarm = 8;

        [ColorUsage(true, true)]
        [Tooltip("Default tracer colour (additive HDR).")]
        public Color defaultTracerColor = new Color(0f, 207.88327f, 705.2075f, 1f);
    }
}
