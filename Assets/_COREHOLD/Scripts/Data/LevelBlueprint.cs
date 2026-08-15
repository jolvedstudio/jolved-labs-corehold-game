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
        ///
        /// <b>This is the only pad count in the blueprint.</b> There is no separate
        /// total field: a total and a breakdown that must agree will eventually
        /// disagree, and the disagreement blocks generation over a number nothing
        /// reads. The total is <see cref="Total"/>, and <see cref="WithTotal"/>
        /// turns "I want N pads" back into a mix.
        /// </summary>
        [System.Serializable]
        public struct PadClassMix
        {
            /// <summary>
            /// Premium pads the coverage rule demands (R28's gate: three pads at
            /// 4+ covered spans). The blueprint validator, the gate and
            /// <see cref="WithTotal"/> all read it here so the floor is one number.
            /// </summary>
            public const int MinPremium = 3;

            [Tooltip("Pads covering 4+ spans. The coverage rule needs at least THREE, so a blueprint below that can never pass the gate.")]
            public int premium;

            [Tooltip("Pads covering 2-3 spans.")]
            public int standard;

            [Tooltip("Pads on the final approach + air-terminal leg.")]
            public int rear;

            [Tooltip("Siege Mortar homes — set back, sparse close coverage inside the 20 m ring / 6 m dead zone.")]
            public int overwatch;

            /// <summary>Total pads this mix asks for — the map's hardpoint count.</summary>
            public int Total => premium + standard + rear + overwatch;

            /// <summary>
            /// This mix re-spread to <paramref name="wanted"/> pads, so a designer
            /// can drive the total directly and get a valid breakdown back.
            ///
            /// The rule is stated rather than clever, because a silent
            /// redistribution that surprises you is worse than one you can predict:
            /// growth lands entirely on <b>Standard</b>, the only class with no
            /// structural precondition (Premium needs geometry that scores 4+ spans,
            /// Rear needs the final approach, Overwatch needs a ≥12 m fold). Shrink
            /// takes from Standard first, then Overwatch, then Rear, and only then
            /// from Premium — which never drops below <see cref="MinPremium"/>,
            /// since a mix under it can never pass the coverage gate. A total below
            /// that floor is clamped up to it for the same reason.
            /// </summary>
            public PadClassMix WithTotal(int wanted)
            {
                PadClassMix m = this;
                m.premium = Mathf.Max(m.premium, MinPremium);
                m.standard = Mathf.Max(m.standard, 0);
                m.rear = Mathf.Max(m.rear, 0);
                m.overwatch = Mathf.Max(m.overwatch, 0);

                int delta = Mathf.Max(wanted, MinPremium) - m.Total;
                if (delta >= 0)
                {
                    m.standard += delta;
                    return m;
                }

                int owed = -delta;
                TakeFrom(ref m.standard, 0, ref owed);
                TakeFrom(ref m.overwatch, 0, ref owed);
                TakeFrom(ref m.rear, 0, ref owed);
                TakeFrom(ref m.premium, MinPremium, ref owed);
                return m;
            }

            private static void TakeFrom(ref int slot, int floor, ref int owed)
            {
                int take = Mathf.Min(owed, slot - floor);
                if (take <= 0)
                    return;
                slot -= take;
                owed -= take;
            }
        }

        [Header("Determinism")]
        [Tooltip("The ONLY source of randomness in generation. Same seed ⇒ same everything, on every device. R37's daily seed depends on it.")]
        public int randomSeed = 1;

        [Header("Playfield")]
        [Tooltip("Design-box size in metres (shipped map: 130 × 75). NOTE: this does NOT size the ground plane — the floor is fitted to the camera frustum after framing (R11/R26), because a design-box floor is wrong by a different amount on every map.")]
        public Vector2 playfieldSize = new Vector2(130f, 75f);

        [Header("Protected structure (the Core)")]
        [Tooltip("PROJECT PREFAB placed as the thing being defended — not a scene object. " +
                 "A ScriptableObject lives in the project, so Unity refuses to let it reference " +
                 "anything that only exists in an open scene; drag the Core's PREFAB from the " +
                 "Project window, or make one from the scene object first. Leave empty to get the " +
                 "shipped platform + shield-generator stack.")]
        public GameObject protectedPrefab;

        [Tooltip("Position on the playfield, normalized from its south-west corner. Default matches the shipped Core at world (34.5, −6.5) on a 130 × 75 field.")]
        public Vector2 protectedNormalizedPos = new Vector2(0.765f, 0.413f);

        /// <summary>
        /// The SHAPE of the approach, as opposed to its parameters (R40).
        ///
        /// R27 varies fold count, placement and drop, but every corridor map is
        /// the same map: enter west and north, merge, snake east, Core on the
        /// right. Learn one defence and you have learned all of them. Topology is
        /// the axis that actually makes maps different.
        ///
        /// <b>These are the whole parameter.</b> Sector count and arc are not
        /// separate fields to tune: each name below is one MEASURED-SAFE
        /// combination, and the combinations that are not here are the ones that
        /// failed to separate when the geometry was fuzzed. Exposing the two
        /// numbers raw would mostly offer settings that cannot generate.
        /// </summary>
        public enum ApproachTopology
        {
            /// <summary>Shipped shape: two entrance legs merge into one folded run (R27).</summary>
            Corridor = 0,

            /// <summary>One entrance, no merge — the corridor run without the second leg.</summary>
            SingleLane = 1,

            /// <summary>Two approaches on opposite sides. The lightest siege.</summary>
            Pincer = 2,

            /// <summary>Three approaches, every side. The default castle siege.</summary>
            Siege = 3,

            /// <summary>Four approaches over 270° — all sides but one, which is where four still separate.</summary>
            Encirclement = 4,
        }

        [Header("Approach topology (R40)")]
        [Tooltip("The map's SHAPE. Corridor/SingleLane fold a run past the Core (the shipped shape); " +
                 "Pincer/Siege/Encirclement spiral in on a centred Core from 2, 3 or 4 bearings. Each " +
                 "siege option is a combination measured to separate on a 130×75 field at 154 m — the " +
                 "sector count and arc are not separate knobs because most combinations of them cannot " +
                 "generate.")]
        public ApproachTopology topology = ApproachTopology.Corridor;

        /// <summary>Whether this topology spirals in on a centred Core rather than folding past it.</summary>
        public bool IsSiege => topology == ApproachTopology.Pincer ||
                               topology == ApproachTopology.Siege ||
                               topology == ApproachTopology.Encirclement;

        /// <summary>Entrance legs for a corridor map. Siege topologies ignore it.</summary>
        public int GroundLegs => topology == ApproachTopology.SingleLane ? 1 : 2;

        /// <summary>Approaches around the Core, 0 for corridor topologies.</summary>
        public int SiegeSectors
        {
            get
            {
                switch (topology)
                {
                    case ApproachTopology.Pincer: return 2;
                    case ApproachTopology.Siege: return 3;
                    case ApproachTopology.Encirclement: return 4;
                    default: return 0;
                }
            }
        }

        /// <summary>
        /// Arc the approaches spread over. Its BEARING is drawn from the seed, so
        /// which side is safe varies map to map; only the width is fixed here.
        /// Encirclement is 270° rather than 360° because four approaches measured
        /// under the 4.5 m lane envelope at 360° and hold at 270°.
        /// </summary>
        public float SiegeArcDegrees
        {
            get
            {
                switch (topology)
                {
                    case ApproachTopology.Pincer: return 180f;
                    case ApproachTopology.Siege: return 360f;
                    case ApproachTopology.Encirclement: return 270f;
                    default: return 0f;
                }
            }
        }

        [Header("Routes (R27)")]
        [Tooltip("Target spline length in metres, hit within ±5% by iterating hairpin anchors. The shipped routes measure 153.7 / 154.5 m as splines, which is the geometry the balance model is baselined on.")]
        public float routeLengthTarget = 154f;

        [Tooltip("Hairpin pocket width in metres — a HARD synthesis constraint, not an aesthetic one. Below 7.5 m no pad clears the 3.75 m envelope from the pocket centre; above ~20 m the shortest-ranged turret (Arc Node, 10 m) cannot reach both legs; a Mortar pocket needs ≥12 m or both legs sit inside its 6 m dead zone. The shipped folds are 10 and 11 m, " +
                 "but the DEFAULT here is 12: the default class mix asks for an Overwatch pad, and 11 would " +
                 "warn about it on every new blueprint. The shipped map used 11.")]
        [Range(7.5f, 20f)] public float foldWidth = 12f;

        // Entrance legs are no longer a field: SingleLane vs Corridor says it, and
        // a leg count that disagreed with the topology could only be a mistake.
        // Read LevelBlueprint.GroundLegs.

        [Tooltip("Whether air units get a straight corridor to the Core. Air ignores routes entirely and is unaffected by the spline work.")]
        public bool airCorridor = true;

        [Header("Hardpoints (R28)")]
        [Tooltip("The pad set, by class — and the map's pad COUNT, which is just their sum. " +
                 "Premium must be ≥3 or the coverage rule is unsatisfiable. To drive the total " +
                 "instead, use the Level Generator window's 'Total pads' field: it re-spreads " +
                 "the mix for you rather than storing a second number to keep in sync.")]
        public PadClassMix classMix = new PadClassMix { premium = 3, standard = 2, rear = 2, overwatch = 1 };

        /// <summary>Pads this map will have. The mix is the authority; this is its sum.</summary>
        public int HardpointCount => classMix.Total;

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
