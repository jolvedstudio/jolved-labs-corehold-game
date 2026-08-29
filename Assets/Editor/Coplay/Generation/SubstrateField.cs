using System.Collections.Generic;
using Corehold.Data;
using UnityEngine;

/// <summary>
/// The SUBSTRATE fields behind dressing (E1) — what the ground under a given
/// square metre actually IS, so props have a REASON to be where they are.
///
/// The old placer drew candidate positions from a uniform distribution over the
/// whole field and accepted the first one that cleared geometry. Uniform scatter
/// is why generated maps read as boring: rocks and plants interleave at the same
/// constant density everywhere, nothing clusters, nothing is ever empty, and no
/// square metre looks like it has a history. Real ground is ZONED — a stony
/// shelf here with nothing growing on it, a scrub flat there with no boulders,
/// bare open pans between them, and debris where traffic passes.
///
/// So this class publishes a handful of cheap, deterministic, low-frequency
/// fields that every placement consults:
///
///   • ROCKINESS — one low-frequency band. High = exposed stone and gravel.
///   • FERTILITY — <c>1 - rockiness</c>. Deriving it from the SAME noise rather
///     than a second independent one is the whole trick: it makes the two
///     ANTI-CORRELATED, so a rocky shelf is visibly bare of plants and a scrub
///     flat is visibly free of boulders. Two independent fields would just give
///     back the mush we started with.
///   • OPENNESS — a separate mid-scale band, hard-thresholded into CLEARINGS.
///     Convincing emptiness cannot come out of a uniform sampler: at a constant
///     accept rate every region fills at the same rate. Empty ground has to be
///     decided, not left over.
///   • DISTURBANCE — distance to the play corridor. Near the routes the ground
///     is trafficked: vegetation thins, wreckage gathers. This is the one term
///     that is also gameplay-LEGIBLE — it makes the corridor read as a used road.
///   • SLOPE — gradient of the terrain height. Steep ground sheds soil and
///     exposes stone; scrub cannot hold a face.
///
/// Noise comes from <see cref="TerrainField.Fbm"/> — the same hash-based
/// generator the heightfield uses, on its own seed stream. One generator, so
/// the two fields cannot drift apart across platforms or Unity versions.
///
/// EDITOR-TIME ONLY, like the rest of generation: the result is baked into
/// placed transforms and the runtime never samples this.
/// </summary>
public class SubstrateField
{
    // ---- [TUNE] the scale of the zoning ----

    /// <summary>Metres across one rock/scrub band. Should be big enough that a
    /// zone reads as a PLACE from the fixed camera (~130 m of field on screen),
    /// not as texture.</summary>
    public const float SubstrateWavelength = 38f;

    /// <summary>Metres across one clearing. Smaller than the substrate bands so
    /// clearings punch through zones rather than lining up with them.</summary>
    public const float ClearingWavelength = 30f;

    /// <summary>Noise below this is fully clear; above <see cref="ClearingHigh"/>
    /// is fully dressable. The narrow band between them is what gives a clearing
    /// a visible EDGE — widen it and clearings dissolve back into gradient.</summary>
    public const float ClearingLow = -0.30f;
    public const float ClearingHigh = 0.05f;

    /// <summary>Metres from the corridor over which disturbance falls to zero.</summary>
    public const float DisturbanceRange = 20f;

    /// <summary>Height gradient (m per m) that counts as fully steep.</summary>
    public const float SlopeFullGradient = 0.35f;

    /// <summary>Metres across one density patch. Longer than the substrate
    /// bands and on its own stream, so patchiness does not line up with the
    /// rock/scrub zoning and add up to one big blob.</summary>
    public const float PatchWavelength = 46f;

    /// <summary>Density floor in the sparsest patch. The band 0.45→1 is what an
    /// UNCLASSIFIED pack gets instead of a uniform carpet.</summary>
    public const float NeutralFloor = 0.45f;

    private readonly int _seed;
    private readonly TerrainField _corridor;
    private readonly bool _hasRelief;
    private readonly Dictionary<GameObject, EnvPack.SubstrateAffinity> _resolved =
        new Dictionary<GameObject, EnvPack.SubstrateAffinity>();

    /// <param name="corridor">Always required — supplies corridor distance, which
    /// is meaningful on flat maps too.</param>
    /// <param name="hasRelief">False on flat maps: the slope term is then zero
    /// rather than reading heights the map will never build.</param>
    public SubstrateField(int seed, TerrainField corridor, bool hasRelief)
    {
        _seed = seed;
        _corridor = corridor;
        _hasRelief = hasRelief;
    }

    // ------------------------------------------------------------- the fields

    /// <summary>0 = soil, 1 = exposed stone. Clamped hard at the tails so a good
    /// fraction of the map is unambiguously ONE or the OTHER — a field that only
    /// ever reads 0.4-0.6 produces the same mush as no field at all.</summary>
    public float Rockiness(float x, float z)
    {
        float raw = TerrainField.Fbm(x / SubstrateWavelength, z / SubstrateWavelength,
                                     _seed * 11 + 3, 3);
        return Mathf.Clamp01(Mathf.InverseLerp(-0.45f, 0.45f, raw));
    }

    /// <summary>The anti-correlate of <see cref="Rockiness"/>: plants take the
    /// ground stone did not.</summary>
    public float Fertility(float x, float z) => 1f - Rockiness(x, z);

    /// <summary>1 = dressable, 0 = a CLEARING that stays empty. Deliberately NOT
    /// relaxable by the placer: a clearing is a composition decision, and an
    /// attempt budget that eventually fills it would erase the only empty ground
    /// on the map.</summary>
    public float Openness(float x, float z)
    {
        float c = TerrainField.Fbm(x / ClearingWavelength, z / ClearingWavelength,
                                   _seed * 17 + 9, 2);
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ClearingLow, ClearingHigh, c));
    }

    /// <summary>
    /// Plain density modulation in [0,1], on its own stream — sparse patches
    /// and thick patches with no semantic meaning at all.
    ///
    /// This is the safety net for a pack whose prefab names carry NO signal
    /// (SM_Prop_04, Mesh_017). Such a pack resolves entirely to Neutral, the
    /// rock/scrub anti-correlation has nothing to bite on, and without this the
    /// map would come back as the same uniform carpet we set out to fix. Varied
    /// density alone already reads far better than none, and it costs one extra
    /// noise sample.
    /// </summary>
    public float Patchiness(float x, float z)
    {
        float p = TerrainField.Fbm(x / PatchWavelength, z / PatchWavelength, _seed * 23 + 7, 2);
        return Mathf.Clamp01(Mathf.InverseLerp(-0.5f, 0.5f, p));
    }

    /// <summary>1 next to the routes/pads/core, 0 well away from them.</summary>
    public float Disturbance(float x, float z)
    {
        if (_corridor == null)
            return 0f;
        return 1f - Mathf.Clamp01(_corridor.DistanceToCorridor(x, z) / DisturbanceRange);
    }

    /// <summary>0 flat, 1 at <see cref="SlopeFullGradient"/> or steeper. Central
    /// differences over the analytic height — the mesh does not exist yet.</summary>
    public float Slope01(float x, float z)
    {
        if (!_hasRelief || _corridor == null)
            return 0f;
        const float e = 1.5f;
        float dhx = _corridor.Height(x + e, z) - _corridor.Height(x - e, z);
        float dhz = _corridor.Height(x, z + e) - _corridor.Height(x, z - e);
        float grad = Mathf.Sqrt(dhx * dhx + dhz * dhz) / (2f * e);
        return Mathf.Clamp01(grad / SlopeFullGradient);
    }

    // -------------------------------------------------------------- affinity

    /// <summary>How much this prop WANTS to be at (x,z), in [0,1]. A soft
    /// preference — the placer relaxes it as a slot runs out of attempts, so
    /// composition never costs fill rate.</summary>
    public float Affinity(EnvPack.Entry entry, float x, float z)
        => Affinity(Resolve(entry), x, z);

    public float Affinity(EnvPack.SubstrateAffinity affinity, float x, float z)
    {
        switch (affinity)
        {
            case EnvPack.SubstrateAffinity.Rock:
            {
                // Stone where the ground is stony, and ALWAYS on a steep face:
                // a slope sheds its soil and exposes what was under it.
                float rock = Rockiness(x, z);
                return Mathf.Clamp01(Mathf.Max(rock, Slope01(x, z)));
            }

            case EnvPack.SubstrateAffinity.Scrub:
            {
                // The anti-correlate, thinned by traffic and unable to hold a face.
                float fert = 1f - Rockiness(x, z);
                return Mathf.Clamp01(fert
                                     * (1f - 0.65f * Slope01(x, z))
                                     * (1f - 0.60f * Disturbance(x, z)));
            }

            case EnvPack.SubstrateAffinity.Debris:
                // Wreckage gathers where things happen — along the corridor.
                return Mathf.Lerp(0.15f, 1f, Disturbance(x, z));

            default:
                // No opinion about the GROUND, but still not a uniform carpet.
                return Mathf.Lerp(NeutralFloor, 1f, Patchiness(x, z));
        }
    }

    /// <summary>
    /// How much a role is allowed to stand in a CLEARING, in [0,1].
    ///
    /// Not every role should obey emptiness equally. The oldest composition in
    /// landscape painting is open ground with one strong silhouette in it, and
    /// a rule that pushed landmarks out of every clearing would forbid exactly
    /// that shot — leaving dead holes instead of framed ones. So landmarks are
    /// largely exempt, mid-field mostly obeys, and clutter obeys absolutely:
    /// scatter is what makes ground read as busy, so scatter is what has to
    /// stay out.
    /// </summary>
    public static float ClearingTolerance(EnvPack.PropRole role)
    {
        switch (role)
        {
            case EnvPack.PropRole.Landmark: return 0.6f;
            case EnvPack.PropRole.MidField: return 0.2f;
            default: return 0f;
        }
    }

    // -------------------------------------------------------------- resolving

    /// <summary>
    /// The entry's affinity, inferring one from the prefab NAME when the entry
    /// says <see cref="EnvPack.SubstrateAffinity.Auto"/> (the default, so every
    /// pack already on disk gets zoned dressing without being re-authored).
    ///
    /// Inference is deliberately CONSERVATIVE: only unambiguous tokens are
    /// matched, and anything unrecognised falls to Neutral, which places at
    /// roughly the old uniform rate. A wrong guess would put boulders in the
    /// scrub — worse than no guess. Authors override per entry when a name lies.
    /// </summary>
    public EnvPack.SubstrateAffinity Resolve(EnvPack.Entry entry)
    {
        if (entry.affinity != EnvPack.SubstrateAffinity.Auto)
            return entry.affinity;
        if (entry.prefab == null)
            return EnvPack.SubstrateAffinity.Neutral;
        if (_resolved.TryGetValue(entry.prefab, out var cached))
            return cached;

        var inferred = Infer(entry.prefab.name);
        _resolved[entry.prefab] = inferred;
        return inferred;
    }

    // Plants first (their names never mean anything else), then stone, then
    // wreckage. Generic words that read both ways — wall, tank, post, pipe,
    // panel — are left OUT on purpose: Neutral is the safe answer.
    private static readonly string[] ScrubTokens =
    {
        "tree", "bush", "shrub", "grass", "plant", "cactus", "cacti", "scrub",
        "fern", "weed", "flower", "palm", "agave", "yucca", "sage", "foliage",
        "vegetation", "stump", "hedge", "reed", "moss", "vine"
    };

    private static readonly string[] RockTokens =
    {
        "rock", "stone", "boulder", "cliff", "mesa", "crag", "scree", "gravel",
        "butte", "spire", "outcrop", "pebble", "granite", "sandstone", "geode",
        "formation", "monolith", "hoodoo"
    };

    private static readonly string[] DebrisTokens =
    {
        "wreck", "debris", "rubble", "scrap", "ruin", "crate", "barrel",
        "container", "barricade", "sandbag", "girder", "junk", "husk",
        "carcass", "chassis", "cable", "pallet", "canister"
    };

    private static EnvPack.SubstrateAffinity Infer(string prefabName)
    {
        string n = prefabName.ToLowerInvariant();
        foreach (string t in ScrubTokens)
            if (n.Contains(t)) return EnvPack.SubstrateAffinity.Scrub;
        foreach (string t in RockTokens)
            if (n.Contains(t)) return EnvPack.SubstrateAffinity.Rock;
        foreach (string t in DebrisTokens)
            if (n.Contains(t)) return EnvPack.SubstrateAffinity.Debris;
        return EnvPack.SubstrateAffinity.Neutral;
    }
}
