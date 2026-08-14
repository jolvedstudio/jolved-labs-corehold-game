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
        return b.IsSiege ? SynthesizeSiege(b, out report) : SynthesizeCorridor(b, out report);
    }

    private static LevelLayout SynthesizeCorridor(LevelBlueprint b, out string report)
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
        float zTopMax = D * 0.5f - (b.GroundLegs >= 2 ? 14.5f : 8f);
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
            sharedTail = b.GroundLegs >= 2,            // two legs merge; one does not
        };

        if (b.GroundLegs >= 2)
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

    // ------------------------------------------------------- siege topology (R40)

    /// <summary>Approaches stop spiralling here and turn in to the Core.</summary>
    private const float SiegeInnerRadius = 8f;

    /// <summary>Straight radial lead-in from the field edge, when the bearing has room for one.</summary>
    private const float SiegeMaxLeadIn = 14f;

    /// <summary>Knots per full turn of spiral — enough that the AutoSmooth curve is a spiral, not a polygon.</summary>
    private const int SiegeKnotsPerTurn = 12;

    /// <summary>
    /// Siege synthesis: N approaches spiral inward onto a centred Core.
    ///
    /// The shape is chosen for a geometric reason, not an aesthetic one. A
    /// straight radial approach cannot be long enough — the ring fits inside
    /// min(W, D)/2 minus the field margin, about 33 m on the shipped field, and
    /// a route has to measure ~154 m to keep the balance model on its baseline.
    /// Folding a 33 m run does not close a 120 m gap: each fold costs its width
    /// along the run, and only one fits. Wrapping does close it — a turn and a
    /// half at an average radius of 20 m is over 150 m of path in the same box.
    ///
    /// It also solves separation for free. Every approach is the SAME spiral
    /// rotated by 2π/N, so at any radius r the gap between two of them is
    /// r·2π/N — 9.4 m for four approaches at the inner radius, comfortably over
    /// the 4.5 m envelope. They only crowd where they converge on the Core, and
    /// that is the one place gate 1 exempts, exactly as it exempts a merge.
    /// </summary>
    private static LevelLayout SynthesizeSiege(LevelBlueprint b, out string report)
    {
        var log = new StringBuilder();
        float W = b.playfieldSize.x, D = b.playfieldSize.y;
        float L = b.routeLengthTarget;
        int n = Mathf.Clamp(b.SiegeSectors, 2, 5);
        Vector3 core = LevelLayout.FromNormalized(b.protectedNormalizedPos, b.playfieldSize);

        // Ring radius is bounded by the NEAREST field edge, so an off-centre Core
        // shrinks it — which is why a siege blueprint wants its Core centred.
        float toEdge = Mathf.Min(
            Mathf.Min(W * 0.5f - core.x, W * 0.5f + core.x),
            Mathf.Min(D * 0.5f - core.z, D * 0.5f + core.z));
        float ringMax = toEdge - FieldMargin;
        if (ringMax < SiegeInnerRadius + 6f)
        {
            report = $"Core at ({core.x:0.#}, {core.z:0.#}) leaves only {ringMax:0.#} m to the nearest field " +
                     $"edge — a siege ring needs at least {SiegeInnerRadius + 6f:0.#} m. Centre " +
                     "protectedNormalizedPos (0.5, 0.5) or enlarge playfieldSize.";
            return null;
        }

        // ---- draws up front, fixed order ------------------------------------
        var rng = new Rng(GenerationPipeline.Fnv1a(b.randomSeed, "siege"));
        float arcCentre = rng.Range(0f, 360f);                    // which way the safe side faces
        bool clockwise = (rng.NextU() & 1u) == 0u;
        float ringDraw = rng.Range(0.94f, 1f);                    // how much of the available ring to use
        float bearingJitter = rng.Range(-6f, 6f);                 // whole-ring twist, keeps sectors congruent

        float ring = ringMax * ringDraw;
        float arc = Mathf.Clamp(b.SiegeArcDegrees, 120f, 360f);

        // ---- fit the sweep to the length target ------------------------------
        // ONE knob, same secant as the corridor fit. Sweep is monotone in length,
        // so this converges in a couple of iterations.
        float sweepMin = 90f * Mathf.Deg2Rad;
        float sweepMax = 4f * Mathf.PI;                            // two full turns
        float sweep = 2f * Mathf.PI;
        float prevSweep = 0f, prevErr = 0f;
        bool have = false;
        float measured = 0f;
        Vector3[] probe = null;

        for (int it = 0; it < MaxFitIterations; it++)
        {
            probe = BuildSpiral(core, ring, SiegeInnerRadius, arcCentre + bearingJitter, sweep, clockwise,
                                W, D);
            measured = MeasureSplineLength(probe);
            float err = measured - L;
            log.AppendLine($"[fit] iter {it}: sweep {sweep * Mathf.Rad2Deg:0.#}° → spline {measured:0.##} m " +
                           $"(err {err:+0.##;-0.##})");
            if (Mathf.Abs(err) <= L * 0.02f)
                break;

            float next;
            if (!have)
            {
                // Each radian of sweep adds roughly the mean radius in metres.
                float meanR = 0.5f * (ring + SiegeInnerRadius);
                next = sweep - err / Mathf.Max(1f, meanR);
                have = true;
            }
            else
            {
                float dErr = err - prevErr;
                next = Mathf.Abs(dErr) < 0.001f ? sweep : sweep - err * (sweep - prevSweep) / dErr;
            }
            prevSweep = sweep; prevErr = err;
            sweep = Mathf.Clamp(next, sweepMin, sweepMax);
        }

        if (Mathf.Abs(measured - L) > L * 0.05f)
        {
            report = $"siege: could not reach {L:0.#} m ±5% — best {measured:0.##} m at sweep " +
                     $"{sweep * Mathf.Rad2Deg:0.#}° on a {ring:0.#} m ring. Shorten routeLengthTarget or " +
                     "enlarge the field: the ring is bounded by the nearest field edge.";
            return null;
        }

        // ---- one spiral per sector, each the same curve rotated ---------------
        var routes = new Vector3[n][];
        var names = new string[n];
        float step = arc >= 359.9f ? 360f / n : (n > 1 ? arc / (n - 1) : 0f);
        float first = arcCentre + bearingJitter - (arc >= 359.9f ? 0f : arc * 0.5f);

        for (int i = 0; i < n; i++)
        {
            float bearing = first + step * i;
            routes[i] = BuildSpiral(core, ring, SiegeInnerRadius, bearing, sweep, clockwise, W, D);
            names[i] = $"Route_{Compass(bearing)}";
        }
        DisambiguateNames(names);

        // ---- refuse rather than emit a map gate 1 will reject -----------------
        // The intuition that congruent spirals stay r·2π/N apart is WRONG, and
        // pre-C# fuzzing is what said so: a spiral sweeping past 360° crosses the
        // entry spokes of its neighbours at a smaller radius, and the real
        // minimum is set by the radial pitch, not the angular one. Measured on
        // the shipped field at a 154 m target: 2 or 3 approaches pass at any arc,
        // 4 pass only when the arc is tightened to ~270°, and 5 never fit.
        //
        // Those numbers are field- and length-specific, so they are NOT hard-coded
        // — the synthesizer measures what it actually built. Gate 1 would catch
        // it anyway, but a gate failure says "reseed" and reseeding cannot help:
        // nothing here varies with the seed. The blueprint is what has to change.
        float sep = MinPairSeparation(routes, core, SiegeInnerRadius + 2f);
        if (sep < MinSeparation)
        {
            report = $"siege: {n} approaches over {arc:0}° hold only {sep:0.##} m apart " +
                     $"(≥{MinSeparation:0.##} m required outside the Core convergence zone). Reseeding will " +
                     "not help — nothing here varies with the seed. Choose a topology with fewer " +
                     "approaches, shorten routeLengthTarget so each approach wraps less (more wrap means " +
                     "less radial pitch between approaches, which is what separation depends on), or " +
                     "enlarge the field.";
            return null;
        }

        var layout = new LevelLayout
        {
            corePos = core,
            groundRoutes = routes,
            routeNames = names,
            airSpawn = new Vector3(core.x, 4f, core.z + Mathf.Min(D * 0.5f - 0.5f - core.z, ring + 6f)),
            pads = null,
            sharedTail = false,                        // approaches converge, they do not merge
        };

        log.AppendLine($"[ok] {n} approach(es) on a {ring:0.#} m ring, sweep {sweep * Mathf.Rad2Deg:0.#}° " +
                       $"{(clockwise ? "CW" : "CCW")}, arc {arc:0}° centred {arcCentre:0}°, " +
                       $"spline {measured:0.##} m (target {L:0.#} ±5%)");
        report = log.ToString();
        return layout;
    }

    /// <summary>
    /// One inward spiral: an optional straight radial lead-in from near the field
    /// edge, then knots sweeping in to <paramref name="innerRadius"/>, then the
    /// Core. Radius falls linearly with swept angle, which keeps the turn pitch
    /// even — a constant-rate spiral bunches its knots near the middle, and
    /// AutoSmooth turns bunched knots into a wobble.
    /// </summary>
    private static Vector3[] BuildSpiral(Vector3 core, float ring, float innerRadius, float bearingDeg,
                                         float sweep, bool clockwise, float W, float D)
    {
        var pts = new List<Vector3>();
        float dir = clockwise ? -1f : 1f;
        float startRad = bearingDeg * Mathf.Deg2Rad;

        // Lead-in: walk OUT along the entry bearing toward the field edge so the
        // spawner sits where a player expects one, not floating mid-field. Bearings
        // facing a near edge simply get a shorter one.
        Vector3 outward = new Vector3(Mathf.Sin(startRad), 0f, Mathf.Cos(startRad));
        float room = DistanceToBox(core + outward * ring, outward, W, D) - FieldMargin;
        float leadIn = Mathf.Clamp(room, 0f, SiegeMaxLeadIn);
        if (leadIn > 1f)
            pts.Add(core + outward * (ring + leadIn));

        int knots = Mathf.Max(4, Mathf.CeilToInt(sweep / (2f * Mathf.PI) * SiegeKnotsPerTurn));
        for (int k = 0; k <= knots; k++)
        {
            float t = k / (float)knots;
            float angle = startRad + dir * sweep * t;
            float r = Mathf.Lerp(ring, innerRadius, t);
            pts.Add(core + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * r);
        }

        pts.Add(core);
        return pts.ToArray();
    }

    /// <summary>Lane envelope, matching gate 1's MinRouteSeparation.</summary>
    private const float MinSeparation = 2f * (0.9f + 1.35f);

    /// <summary>
    /// Closest approach between any two routes, ignoring everything within
    /// <paramref name="exclusion"/> of the Core where they converge by design.
    /// Sampled on the knot polyline rather than the spline: it runs inside the
    /// fit loop's caller, and the polyline sits within centimetres of the
    /// AutoSmooth curve at this knot density.
    /// </summary>
    private static float MinPairSeparation(Vector3[][] routes, Vector3 core, float exclusion)
    {
        var sampled = new List<List<Vector3>>();
        foreach (Vector3[] pts in routes)
        {
            var s = new List<Vector3>();
            for (int i = 0; i < pts.Length - 1; i++)
            {
                float seg = Vector3.Distance(pts[i], pts[i + 1]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(seg / 0.5f));
                for (int k = 0; k < steps; k++)
                {
                    Vector3 p = Vector3.Lerp(pts[i], pts[i + 1], k / (float)steps);
                    if (Vector3.Distance(p, core) >= exclusion)
                        s.Add(p);
                }
            }
            sampled.Add(s);
        }

        float worst = float.MaxValue;
        for (int a = 0; a < sampled.Count; a++)
            for (int b = a + 1; b < sampled.Count; b++)
                foreach (Vector3 p in sampled[a])
                    foreach (Vector3 q in sampled[b])
                        worst = Mathf.Min(worst, Vector3.Distance(p, q));
        return worst == float.MaxValue ? float.MaxValue : worst;
    }

    /// <summary>Distance from a point to the field box along a direction (both in XZ).</summary>
    private static float DistanceToBox(Vector3 from, Vector3 dir, float W, float D)
    {
        float best = float.MaxValue;
        if (Mathf.Abs(dir.x) > 0.0001f)
        {
            float edge = dir.x > 0f ? W * 0.5f : -W * 0.5f;
            best = Mathf.Min(best, (edge - from.x) / dir.x);
        }
        if (Mathf.Abs(dir.z) > 0.0001f)
        {
            float edge = dir.z > 0f ? D * 0.5f : -D * 0.5f;
            best = Mathf.Min(best, (edge - from.z) / dir.z);
        }
        return Mathf.Max(0f, best);
    }

    /// <summary>Compass label for a bearing in degrees east of north.</summary>
    private static string Compass(float bearingDeg)
    {
        string[] points = { "North", "NorthEast", "East", "SouthEast", "South", "SouthWest", "West", "NorthWest" };
        float wrapped = Mathf.Repeat(bearingDeg, 360f);
        return points[Mathf.RoundToInt(wrapped / 45f) % 8];
    }

    /// <summary>
    /// Two sectors can round to the same compass point; scene object names and the
    /// gate reports that quote them have to stay distinguishable.
    /// </summary>
    private static void DisambiguateNames(string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            int dup = 0;
            for (int j = 0; j < i; j++)
                if (names[j] == names[i] || names[j].StartsWith(names[i] + "_", System.StringComparison.Ordinal))
                    dup++;
            if (dup > 0)
                names[i] = $"{names[i]}_{dup + 1}";
        }
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
