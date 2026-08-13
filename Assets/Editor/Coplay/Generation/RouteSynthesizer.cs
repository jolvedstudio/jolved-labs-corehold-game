using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Seeded route synthesis (R27): from a <see cref="LevelBlueprint"/>, produce
/// the ground-route waypoint lists as a <see cref="LevelLayout"/>.
///
/// The topology is the shipped map's, generalized — it is the shape the whole
/// game was validated on, so synthesis varies its PARAMETERS rather than
/// inventing new shapes: entrance leg(s) → merge at ~20% of route length → a
/// top run with 2–3 hairpin FOLDS dropping off it → a tail turning south to
/// the Core. With the shipped blueprint's numbers and the right draws this
/// degenerates to almost exactly the shipped route, which is the strongest
/// evidence the generalization is faithful.
///
/// Determinism: every draw comes from a counter-free xorshift stream seeded by
/// FNV-1a(seed, "routes"), drawn in a FIXED order before any fitting begins.
/// The length fit then adjusts ONE parameter (the fold drop) by deterministic
/// secant steps, so the same seed produces the same knots on every machine —
/// R37's daily seed depends on that.
///
/// Fold width is a HARD constraint, not a preference (roadmap): the pocket
/// between a fold's parallel legs is where hardpoints live, so every fold is
/// exactly <c>blueprint.foldWidth</c> wide — the fit never touches it.
///
/// Length is measured on the SPLINE (AutoSmooth, unpinned), the same curve
/// construction PathRoute bakes. The merge pin moves the measured length by
/// well under a metre (R7 measured the live map insensitive across
/// 153.89–154.52 m), which the ±5% band absorbs ~15× over.
/// </summary>
public static class RouteSynthesizer
{
    /// <summary>Interior knots keep this many metres from the field edge (R27).</summary>
    private const float FieldMargin = 4f;

    /// <summary>Entrance legs meet the snake at this fraction of the route target.</summary>
    private const float MergeFraction = 0.20f;

    /// <summary>The tail corner must clear the Core by at least this along x.</summary>
    private const float CoreStandoff = 8f;

    private const int MaxFitIterations = 10;

    /// <summary>Deterministic xorshift32. NOT System.Random — its algorithm is not
    /// contractually stable across runtimes, and draws must agree on every device.</summary>
    private struct Rng
    {
        private uint _state;
        public Rng(uint seed) { _state = seed == 0 ? 2463534242u : seed; }

        public uint NextU()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        public float Range(float min, float max) => min + (NextU() / 4294967296f) * (max - min);
        public int Range(int min, int maxExclusive) => min + (int)(NextU() % (uint)(maxExclusive - min));
    }

    /// <summary>
    /// Synthesize the layout, or return null with the reason in
    /// <paramref name="report"/> — an impossible blueprint (field too small for
    /// its fold width, unreachable length target) must say WHY, because the fix
    /// is a blueprint edit, not a reseed.
    /// </summary>
    public static LevelLayout Synthesize(LevelBlueprint b, out string report)
    {
        var log = new StringBuilder();
        float W = b.playfieldSize.x, D = b.playfieldSize.y;
        float L = b.routeLengthTarget;
        float F = Mathf.Clamp(b.foldWidth, 7.5f, 20f);
        Vector3 core = LevelLayout.FromNormalized(b.protectedNormalizedPos, b.playfieldSize);

        // ---- all draws up front, fixed order — determinism survives refits ----
        var rng = new Rng(GenerationPipeline.Fnv1a(b.randomSeed, "routes"));
        int foldCount = rng.Range(2, 4);                       // 2 or 3 pockets
        float zTopDraw = rng.Range(0.35f, 0.75f);              // fraction of the usable band
        float leadIn = rng.Range(8f, 14f);                     // merge → first fold
        float gapFrac0 = rng.Range(0.9f, 1.2f);                // inter-fold gaps, in fold widths
        float gapFrac1 = rng.Range(0.9f, 1.2f);
        float gapFracEnd = rng.Range(0.9f, 1.2f);              // last fold → tail corner
        float jitter0 = rng.Range(-1f, 1f);                    // per-fold drop variation
        float jitter1 = rng.Range(-1f, 1f);
        float jitter2 = rng.Range(-1f, 1f);
        float tailDraw = rng.Range(10f, 14f);                  // tail corner height over core z
        float northAngle = rng.Range(35f, 60f);                // north leg bearing, deg east of +Z

        float legLen = Mathf.Clamp(MergeFraction * L, 20f, 40f);
        float[] jitters = { jitter0, jitter1, jitter2 };
        float[] gapFracs = { gapFrac0, gapFrac1 };

        // Top run height, drawn as a fraction of the usable band so the same
        // draw scales with the field instead of hugging one edge. Two-leg maps
        // reserve a deeper north corridor (14.5 m vs 8): the north approach
        // must climb 4.5 m clear of the top run before it exits the merge
        // zone, and fuzzing showed a high top run leaves it no room to — the
        // approach hugs the run in a sustained sub-clearance band.
        float zTopMin = core.z + 14f;
        float zTopMax = D * 0.5f - (b.groundSpawnLegs >= 2 ? 14.5f : 8f);
        if (zTopMax <= zTopMin)
        {
            report = $"field depth {D:0.#} m leaves no top-run band above the Core (z {core.z:0.#}) — " +
                     "deepen the field or move protectedNormalizedPos south";
            return null;
        }
        float zTop = Mathf.Lerp(zTopMin, zTopMax, zTopDraw);

        Vector3 spawnW = new Vector3(-W * 0.5f + 5f, 0f, zTop);
        Vector3 merge = new Vector3(spawnW.x + legLen, 0f, zTop);

        // ---- fit the fold run to the field ------------------------------------
        // First drop fold COUNT while even the minimum layout (lead-in 8, gaps
        // at 0.9 folds) overruns; then, if the DRAWN layout still overruns,
        // compress lead-in and gaps deterministically toward those minimums.
        // Fuzzing showed refusing instead of compressing rejects half of all
        // seeds on the shipped field — the draws just need squeezing, not
        // discarding. Refusal is reserved for genuinely impossible blueprints.
        float avail = core.x - CoreStandoff - merge.x;
        while (true)
        {
            float needMin = 8f + foldCount * F + (foldCount - 1) * 0.9f * F + 0.9f * F;
            if (needMin <= avail)
                break;
            foldCount--;
            if (foldCount < 2)
            {
                report = $"field width {W:0.#} m cannot fit 2 folds of {F:0.#} m plus the entrance leg " +
                         $"and Core standoff — widen the field or narrow foldWidth";
                return null;
            }
            log.AppendLine($"[fit] fold count reduced to {foldCount} — {F:0.#} m folds did not fit the field");
        }
        {
            float drawn = leadIn + foldCount * F + SumGaps(F, gapFracs, foldCount - 1) + F * gapFracEnd;
            float needMin = 8f + foldCount * F + (foldCount - 1) * 0.9f * F + 0.9f * F;
            if (drawn > avail)
            {
                float t = (avail - needMin) / (drawn - needMin);
                leadIn = 8f + (leadIn - 8f) * t;
                for (int i = 0; i < gapFracs.Length; i++)
                    gapFracs[i] = 0.9f + (gapFracs[i] - 0.9f) * t;
                gapFracEnd = 0.9f + (gapFracEnd - 0.9f) * t;
                log.AppendLine($"[fit] drawn run compressed by t={t:0.###} to fit the field");
            }
        }

        // ---- deterministic secant on the fold drop to hit the length target ---
        float dropMin = 7f;
        float dropMax = Mathf.Min(15f, zTop - (-D * 0.5f + FieldMargin) - 1f);
        if (dropMax <= dropMin)
        {
            report = $"top run at z {zTop:0.#} leaves no room for a ≥{dropMin:0.#} m fold drop above " +
                     "the south margin — deepen the field";
            return null;
        }

        float drop = Mathf.Clamp(10f, dropMin, dropMax);
        float prevDrop = 0f, prevErr = 0f;
        Vector3[] westPts = null;
        float measured = 0f;
        bool have = false;

        for (int it = 0; it < MaxFitIterations; it++)
        {
            westPts = BuildWest(spawnW, merge, leadIn, foldCount, F, gapFracs, gapFracEnd,
                                zTop, drop, jitters, dropMin, dropMax, tailDraw, core);
            measured = MeasureSplineLength(westPts);
            float err = measured - L;
            log.AppendLine($"[fit] iter {it}: drop {drop:0.##} m → spline {measured:0.##} m (err {err:+0.##;-0.##})");

            if (Mathf.Abs(err) <= L * 0.02f)   // fit well inside the ±5% gate band
                break;

            float next;
            if (!have)
            {
                // Each metre of drop adds ~2 m per fold — the analytic slope
                // seeds the secant so iteration 2 is already close.
                next = drop - err / (2f * foldCount);
                have = true;
            }
            else
            {
                float dErr = err - prevErr;
                next = Mathf.Abs(dErr) < 0.001f ? drop : drop - err * (drop - prevDrop) / dErr;
            }
            prevDrop = drop; prevErr = err;
            drop = Mathf.Clamp(next, dropMin, dropMax);
        }

        if (Mathf.Abs(measured - L) > L * 0.05f)
        {
            report = $"could not reach {L:0.#} m ±5% — best {measured:0.##} m with fold drop " +
                     $"{drop:0.##} m (bounds {dropMin:0.#}–{dropMax:0.#}). The target is out of range " +
                     "for this field/foldWidth; adjust routeLengthTarget or the field.";
            return null;
        }

        // ---- north leg: straight line from the merge, clamped inside the edge --
        var layout = new LevelLayout
        {
            corePos = core,
            airSpawn = new Vector3(0f, 4f, D * 0.5f - 0.5f),
            pads = null,                               // R28 selects
        };

        if (b.groundSpawnLegs >= 2)
        {
            // The north leg is BENT, not straight: the inner half climbs away
            // from the top run at a fixed steep bearing (35° east of +Z, a
            // 0.82 m climb per metre — 4.5 m clear before it exits the 8 m
            // merge zone), and the seeded bearing lives in the outer half,
            // swung further east only as far as the field edge forces. A
            // straight leg at the edge-clamped bearing hugs the top run in a
            // sustained sub-clearance band — fuzzing failed 124/300 seeds on
            // exactly that before the bend.
            float half = legLen * 0.5f;
            float innerRad = 35f * Mathf.Deg2Rad;
            Vector3 midN = merge + new Vector3(Mathf.Sin(innerRad), 0f, Mathf.Cos(innerRad)) * half;

            float maxCos = (D * 0.5f - 1.5f - midN.z) / half;
            float outerRad = northAngle * Mathf.Deg2Rad;
            if (Mathf.Cos(outerRad) > maxCos)
                outerRad = Mathf.Acos(Mathf.Clamp(maxCos, -1f, 1f));

            Vector3 spawnN = midN + new Vector3(Mathf.Sin(outerRad), 0f, Mathf.Cos(outerRad)) * half;
            spawnN.x = Mathf.Min(spawnN.x, W * 0.5f - 6f);

            int mergeIdx = IndexOfMerge(westPts, merge);
            var northPts = new List<Vector3> { spawnN, midN };
            for (int i = mergeIdx; i < westPts.Length; i++)   // shared tail: merge knot onward
                northPts.Add(westPts[i]);

            layout.groundRoutes = new[] { westPts, northPts.ToArray() };
            layout.routeNames = new[] { "Route_West", "Route_North" };
        }
        else
        {
            layout.groundRoutes = new[] { westPts };
            layout.routeNames = new[] { "Route_West" };
        }

        log.AppendLine($"[ok] {foldCount} folds of {F:0.#} m, top run z {zTop:0.##}, drop {drop:0.##} m, " +
                       $"west spline {measured:0.##} m (target {L:0.#} ±5%)");
        report = log.ToString();
        return layout;
    }

    // ------------------------------------------------------------------ helpers

    private static float SumGaps(float F, float[] gapFracs, int count)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
            sum += F * gapFracs[i % gapFracs.Length];
        return sum;
    }

    /// <summary>
    /// The full west waypoint list: spawn → mid → merge → folds along the top
    /// run → tail corner → Core. The same list past the merge IS the shared
    /// tail the north route reuses, which is what makes the two routes'
    /// duplicated knots identical by construction (R7's precondition).
    /// </summary>
    private static Vector3[] BuildWest(Vector3 spawnW, Vector3 merge, float leadIn, int foldCount,
                                       float F, float[] gapFracs, float gapFracEnd, float zTop,
                                       float drop, float[] jitters, float dropMin, float dropMax,
                                       float tailDraw, Vector3 core)
    {
        var pts = new List<Vector3>
        {
            spawnW,
            new Vector3(spawnW.x + 15f, 0f, zTop),
            merge,
        };

        float x = merge.x + leadIn;
        for (int i = 0; i < foldCount; i++)
        {
            float di = Mathf.Clamp(drop + jitters[i % jitters.Length], dropMin, dropMax);
            float zBottom = zTop - di;
            pts.Add(new Vector3(x, 0f, zTop));
            pts.Add(new Vector3(x, 0f, zBottom));
            pts.Add(new Vector3(x + F, 0f, zBottom));
            pts.Add(new Vector3(x + F, 0f, zTop));
            x += F + (i < foldCount - 1 ? F * gapFracs[i % gapFracs.Length] : F * gapFracEnd);
        }

        pts.Add(new Vector3(x, 0f, zTop));
        pts.Add(new Vector3(x, 0f, core.z + tailDraw));
        pts.Add(core);
        return pts.ToArray();
    }

    private static int IndexOfMerge(Vector3[] westPts, Vector3 merge)
    {
        for (int i = 0; i < westPts.Length; i++)
            if (Vector3.Distance(westPts[i], merge) <= 0.01f)
                return i;
        return 2;   // by construction the merge is index 2
    }

    /// <summary>
    /// Spline length of an AutoSmooth curve through the points — the identical
    /// construction <c>PathRoute.RebuildSpline</c> bakes, measured with the
    /// package's own <c>GetLength</c> so the fit and the built route agree.
    /// </summary>
    internal static float MeasureSplineLength(Vector3[] pts)
    {
        var spline = new Spline();
        foreach (Vector3 p in pts)
            spline.Add(new BezierKnot(new float3(p.x, p.y, p.z), float3.zero, float3.zero),
                       TangentMode.AutoSmooth);
        spline.Closed = false;

        using (var native = new NativeSpline(spline, Allocator.Temp))
            return native.GetLength();
    }
}
