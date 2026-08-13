using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Everything the level generator needs to produce one map (roadmap R25).
    ///
    /// <b>This asset is the single source of generation determinism.</b> Every
    /// random draw in the pipeline derives from <see cref="randomSeed"/> — route
    /// anchors, hardpoint tie-breaks, prop placement and scale, weather choice.
    /// Same seed ⇒ identical routes, identical pads, identical dressing, identical
    /// wave table. That is a hard rule rather than a preference: R37's daily-seed
    /// challenge gives every player the same map from the same date, and it only
    /// works if generation is reproducible across devices.
    ///
    /// A run emits a **playable scene** plus a <see cref="LevelDefinition"/> wired
    /// into it — the full root set, not a geometry dump (see the P6 pipeline
    /// preamble in the roadmap for the twelve stages and their required order).
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Level Blueprint", fileName = "Blueprint_")]
    public class LevelBlueprint : ScriptableObject
    {
        /// <summary>
        /// How many pads of each class the generator must select. Classes are
        /// assigned from MEASURED coverage, never from intent (R28): a pad is
        /// Premium because it scored 4+ covered spans, not because it was labelled
        /// one. The shipped map is 3 / 2 / 2 / 1.
        /// </summary>
        [System.Serializable]
        public struct PadClassMix
        {
            [Tooltip("Pads covering 4+ spans. The coverage rule needs at least THREE, so a blueprint below that can never pass the gate.")]
            public int premium;

            [Tooltip("Pads covering 2-3 spans.")]
            public int standard;

            [Tooltip("Pads on the final approach + air-terminal leg.")]
            public int rear;

            [Tooltip("Siege Mortar homes — set back, sparse close coverage inside the 20 m ring / 6 m dead zone.")]
            public int overwatch;

            /// <summary>Total pads this mix asks for.</summary>
            public int Total => premium + standard + rear + overwatch;
        }

        [Header("Determinism")]
        [Tooltip("The ONLY source of randomness in generation. Same seed ⇒ same everything, on every device. R37's daily seed depends on it.")]
        public int randomSeed = 1;

        [Header("Playfield")]
        [Tooltip("Design-box size in metres (shipped map: 130 × 75). NOTE: this does NOT size the ground plane — the floor is fitted to the camera frustum after framing (R11/R26), because a design-box floor is wrong by a different amount on every map.")]
        public Vector2 playfieldSize = new Vector2(130f, 75f);

        [Header("Protected structure (the Core)")]
        [Tooltip("Prefab placed as the thing being defended.")]
        public GameObject protectedPrefab;

        [Tooltip("Position on the playfield, normalized from its south-west corner. Default matches the shipped Core at world (34.5, −6.5) on a 130 × 75 field.")]
        public Vector2 protectedNormalizedPos = new Vector2(0.765f, 0.413f);

        [Header("Routes (R27)")]
        [Tooltip("Target spline length in metres, hit within ±5% by iterating hairpin anchors. The shipped routes measure 153.7 / 154.5 m as splines, which is the geometry the balance model is baselined on.")]
        public float routeLengthTarget = 154f;

        [Tooltip("Hairpin pocket width in metres — a HARD synthesis constraint, not an aesthetic one. Below 7.5 m no pad clears the 3.75 m envelope from the pocket centre; above ~20 m the shortest-ranged turret (Arc Node, 10 m) cannot reach both legs; a Mortar pocket needs ≥12 m or both legs sit inside its 6 m dead zone. The shipped folds are 10 and 11 m.")]
        [Range(7.5f, 20f)] public float foldWidth = 11f;

        [Tooltip("Ground entrance legs merging into the shared tail (1-2). Two legs inherit the AutoSmooth merge divergence and REQUIRE R7's world-space tangent pin.")]
        [Range(1, 2)] public int groundSpawnLegs = 2;

        [Tooltip("Whether air units get a straight corridor to the Core. Air ignores routes entirely and is unaffected by the spline work.")]
        public bool airCorridor = true;

        [Header("Hardpoints (R28)")]
        [Tooltip("Total pads to select. Must equal the class mix total.")]
        public int hardpointCount = 8;

        [Tooltip("How those pads break down by class. Premium must be ≥3 or the coverage rule is unsatisfiable.")]
        public PadClassMix classMix = new PadClassMix { premium = 3, standard = 2, rear = 2, overwatch = 1 };

        [Header("Dressing & atmosphere")]
        [Tooltip("Themes this level may be dressed in. The seed picks one, so a single blueprint yields visually distinct maps. ONE entry pins the theme. Entries carry the footprint radius and height the clearance and occlusion tests need.")]
        public EnvPack[] envPackPool;

        [Tooltip("OVERRIDE for the chosen theme's own weather. Leave EMPTY and the theme decides, which is what keeps an ice map off desert dust. Set it only to force weather regardless of theme. Empty in both places means the null preset — the scene keeps its authored look, which R13 guarantees is pixel-identical.")]
        public WeatherPreset[] weatherPool;

        [Header("Rules")]
        [Tooltip("LevelDefinition cloned as the emitted level's starting point (R30). The generator then solves hpGrowthPerWave against the balance model and derives maxLiveEnemies from the generated route capacity.")]
        public LevelDefinition rulesTemplate;
    }
}
