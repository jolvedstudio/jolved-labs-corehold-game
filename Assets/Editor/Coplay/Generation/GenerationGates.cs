using System.Collections.Generic;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The generation gates (R29). A gate returns null when it passes and an
/// ACTIONABLE failure string when it does not — naming the offending knots and
/// pads, because "gate failed" with no address costs the human a debugging
/// session the report already paid for.
///
/// Gates measure the REAL scene objects — the built PathRoutes and the actual
/// HardpointCoverageGizmo components — never a parallel reimplementation. The
/// gizmo is the same validator the shipped map was authored against (R8), so a
/// generated pad and a hand-placed one are judged by identical code.
/// </summary>
public static class GenerationGates
{
    // Clearance constants (roadmap P2/P6). laneHalfWidth + maxBodyRadius per
    // side: two lane bands may approach until their outermost bodies touch.
    private const float LaneHalfWidth = 0.9f;
    private const float MaxBodyRadius = 1.35f;
    private const float MinRouteSeparation = 2f * (LaneHalfWidth + MaxBodyRadius);  // 4.5 m

    /// <summary>Field-edge margin for interior knots (R27's synthesis margin).</summary>
    private const float FieldMargin = 4f;

    /// <summary>Curve sampling step for separation checks.</summary>
    private const float SampleStep = 1f;

    /// <summary>
    /// Same-route pairs closer than this along the arc are neighbours, not a
    /// self-intersection risk — the window must exceed the widest hairpin turn.
    /// </summary>
    private const float SelfArcWindow = 8f;

    /// <summary>
    /// Cross-route pairs within this distance of the merge point are excluded:
    /// two legs converging on one knot approach each other BY DESIGN, and the
    /// shared tail after it is identical geometry on both routes.
    /// </summary>
    private const float MergeExclusion = 8f;

    // ------------------------------------------------------------ gate 1

    /// <summary>
    /// GATE 1 — route clearance. Checks, on the curves as walked:
    ///   • spline Length within ±5% of the blueprint target,
    ///   • interior knots inside the field minus the 4 m margin (endpoints are
    ///     spawn/core and may sit on the edge),
    ///   • no self-approach closer than 4.5 m outside the hairpin window,
    ///   • no cross-route approach closer than 4.5 m outside the merge zone.
    /// Returns null on pass (with a one-line summary), else the failure text.
    /// </summary>
    public static string CheckClearance(List<PathRoute> routes, LevelBlueprint blueprint, out string summary)
    {
        summary = null;
        var problems = new StringBuilder();

        // -- length band -------------------------------------------------------
        float target = blueprint.routeLengthTarget;
        foreach (PathRoute route in routes)
        {
            float deviation = Mathf.Abs(route.Length - target) / target;
            if (deviation > 0.05f)
                problems.AppendLine($"  • {route.name} measures {route.Length:0.##} m — " +
                                    $"{deviation:P1} off the {target:0.#} m target (band ±5%)");
        }

        // -- interior knots inside the margin ---------------------------------
        float haltW = blueprint.playfieldSize.x * 0.5f - FieldMargin;
        float haltD = blueprint.playfieldSize.y * 0.5f - FieldMargin;
        foreach (PathRoute route in routes)
        {
            for (int i = 1; i < route.PointCount - 1; i++)
            {
                Vector3 p = route.GetPoint(i);
                if (Mathf.Abs(p.x) > haltW || Mathf.Abs(p.z) > haltD)
                    problems.AppendLine($"  • {route.name} knot {i} at ({p.x:0.#}, {p.z:0.#}) breaches the " +
                                        $"{FieldMargin:0} m field margin (|x|≤{haltW:0.#}, |z|≤{haltD:0.#})");
            }
        }

        // -- separation, measured on the sampled curves ------------------------
        var sampled = new List<(PathRoute route, List<Vector3> pts, List<float> dist)>();
        foreach (PathRoute route in routes)
        {
            var pts = new List<Vector3>();
            var dist = new List<float>();
            for (float d = 0f; d <= route.Length; d += SampleStep)
            {
                pts.Add(route.SamplePosition(d, out _));
                dist.Add(d);
            }
            sampled.Add((route, pts, dist));
        }

        // A merge is a DESIGNED pinch: one route's approach folds onto the
        // shared tail, and the lane band legitimately self-overlaps around the
        // join — the SHIPPED map pinches to ~2.5 m there. Both separation
        // checks therefore exempt the merge zone; everything outside it is a
        // real violation.
        bool hasMerge = sampled.Count == 2;
        Vector3 mergePoint = hasMerge ? FindMergePoint(routes[0], routes[1]) : Vector3.zero;

        foreach (var (route, pts, dist) in sampled)
        {
            float worst = float.MaxValue;
            float worstAt = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (hasMerge && HorizontalDistance(pts[i], mergePoint) < MergeExclusion)
                    continue;
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (dist[j] - dist[i] < SelfArcWindow)
                        continue;
                    if (hasMerge && HorizontalDistance(pts[j], mergePoint) < MergeExclusion)
                        continue;
                    float d = HorizontalDistance(pts[i], pts[j]);
                    if (d < worst) { worst = d; worstAt = dist[i]; }
                }
            }
            if (worst < MinRouteSeparation)
                problems.AppendLine($"  • {route.name} approaches itself at {worst:0.##} m " +
                                    $"(≥{MinRouteSeparation:0.##} m required) near arc {worstAt:0.#} m — " +
                                    "two lane bands would overlap");
        }

        if (hasMerge)
        {
            // Samples past the merge lie on the SHARED tail — identical
            // geometry on both routes, but sampled at different arc phases, so
            // a coincidence epsilon misses them by up to a full step. Exclude
            // shared-tail pairs by ARC POSITION instead: a pair only counts
            // when at least one sample is on an un-shared approach leg.
            float mergeArcA = ArcDistanceAtPoint(routes[0], mergePoint);
            float mergeArcB = ArcDistanceAtPoint(routes[1], mergePoint);

            float worst = float.MaxValue;
            Vector3 worstAtA = Vector3.zero;
            for (int i = 0; i < sampled[0].pts.Count; i++)
            {
                Vector3 a = sampled[0].pts[i];
                if (HorizontalDistance(a, mergePoint) < MergeExclusion)
                    continue;
                for (int j = 0; j < sampled[1].pts.Count; j++)
                {
                    Vector3 b = sampled[1].pts[j];
                    if (HorizontalDistance(b, mergePoint) < MergeExclusion)
                        continue;
                    if (sampled[0].dist[i] >= mergeArcA - 0.5f &&
                        sampled[1].dist[j] >= mergeArcB - 0.5f)
                        continue;                       // both on the shared tail
                    float d = HorizontalDistance(a, b);
                    if (d < worst) { worst = d; worstAtA = a; }
                }
            }
            if (worst < MinRouteSeparation)
                problems.AppendLine($"  • routes approach each other at {worst:0.##} m " +
                                    $"(≥{MinRouteSeparation:0.##} m) near ({worstAtA.x:0.#}, {worstAtA.z:0.#}), " +
                                    "outside the merge zone — entrance lanes would overlap");
        }

        if (problems.Length > 0)
            return "clearance violations:\n" + problems.ToString().TrimEnd();

        summary = $"lengths in ±5% of {target:0.#} m; margins held; " +
                  $"no approach under {MinRouteSeparation:0.##} m";
        return null;
    }

    /// <summary>
    /// The R29 clearance-adjustment loop: when gate 1 fails on a SYNTHESIZED
    /// route (parity geometry is never touched — it must mirror the shipped
    /// map), knots breaching the field margin are clamped inside it, each move
    /// LOGGED, for at most three passes with a full re-check between passes.
    ///
    /// Only margin breaches are adjusted. Separation violations stay
    /// fail-and-reseed: nudging interleaved legs apart moves the fold geometry
    /// that R28's pockets and the length fit both depend on — exactly the
    /// repair-one-break-another loop the roadmap forbids (it is how the shipped
    /// map got a 3-span Premium pad). A margin clamp, by contrast, moves a knot
    /// by centimetres at the field edge, far from any pocket.
    ///
    /// The pipeline runs this BEFORE pads, coverage and the model, so the
    /// roadmap's "adjustment must be followed by a full re-run of coverage +
    /// model" holds by stage order rather than by bookkeeping.
    /// </summary>
    public static string AdjustAndRecheck(List<PathRoute> routes, LevelBlueprint blueprint,
                                          out string summary, out List<string> adjustments)
    {
        adjustments = new List<string>();
        string failure = CheckClearance(routes, blueprint, out summary);
        if (failure == null)
            return null;

        float haltW = blueprint.playfieldSize.x * 0.5f - FieldMargin;
        float haltD = blueprint.playfieldSize.y * 0.5f - FieldMargin;

        for (int pass = 1; pass <= 3 && failure != null; pass++)
        {
            bool moved = false;
            foreach (PathRoute route in routes)
            {
                for (int i = 1; i < route.PointCount - 1; i++)
                {
                    Vector3 p = route.GetPoint(i);
                    float cx = Mathf.Clamp(p.x, -haltW, haltW);
                    float cz = Mathf.Clamp(p.z, -haltD, haltD);
                    if (Mathf.Approximately(cx, p.x) && Mathf.Approximately(cz, p.z))
                        continue;

                    // Waypoints are child Transforms; move the transform, then
                    // force the rebake so the re-check reads the new curve.
                    var so = new SerializedObject(route);
                    var wp = so.FindProperty("waypoints").GetArrayElementAtIndex(i)
                               .objectReferenceValue as Transform;
                    if (wp == null)
                        continue;
                    Vector3 to = new Vector3(cx, p.y, cz);
                    wp.position = to;
                    moved = true;
                    adjustments.Add($"pass {pass}: {route.name} knot {i} " +
                                    $"({p.x:0.##}, {p.z:0.##}) → ({to.x:0.##}, {to.z:0.##}) [margin clamp]");
                }
                route.RecomputeNow();
            }

            if (!moved)
                break;                       // remaining violations are not margin breaches
            failure = CheckClearance(routes, blueprint, out summary);
        }

        return failure;
    }

    /// <summary>Arc distance along a route of the knot at (or nearest) a world point.</summary>
    private static float ArcDistanceAtPoint(PathRoute route, Vector3 point)
    {
        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < route.PointCount; i++)
        {
            float sq = (route.GetPoint(i) - point).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return route.DistanceAlongAt(best);
    }

    /// <summary>First knot (walking back from the core) where the two routes coincide.</summary>
    private static Vector3 FindMergePoint(PathRoute a, PathRoute b)
    {
        int ai = a.PointCount - 1;
        int bi = b.PointCount - 1;
        Vector3 merge = a.GetPoint(ai);
        while (ai >= 0 && bi >= 0 &&
               Vector3.Distance(a.GetPoint(ai), b.GetPoint(bi)) <= 0.01f)
        {
            merge = a.GetPoint(ai);
            ai--;
            bi--;
        }
        return merge;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------ gate 2

    /// <summary>
    /// GATE 2 — coverage, judged by the ACTUAL HardpointCoverageGizmo components
    /// in the scene (the R8 validator, measuring the walked curve): every pad
    /// ≥2 covered spans, every Premium ≥4, at least 3 Premium pads, and the
    /// class census matching the blueprint's mix. Returns null on pass.
    /// </summary>
    public static string CheckCoverage(LevelBlueprint blueprint, out string summary)
    {
        summary = null;
        var gizmos = Object.FindObjectsByType<HardpointCoverageGizmo>(FindObjectsSortMode.None);
        if (gizmos.Length == 0)
            return "no hardpoints in the scene — the pad stage emitted nothing";

        var problems = new StringBuilder();
        var census = new Dictionary<HardpointCoverageGizmo.PadClass, int>();
        int premiumAtFour = 0;

        foreach (var gz in gizmos)
        {
            census.TryGetValue(gz.padClass, out int c);
            census[gz.padClass] = c + 1;

            int covered = gz.CountCoveredSegments();
            bool premium = gz.padClass == HardpointCoverageGizmo.PadClass.Premium;
            if (premium && covered >= 4)
                premiumAtFour++;

            int need = premium ? 4 : 2;
            if (covered < need)
                problems.AppendLine($"  • {gz.name} ({gz.padClass}, {gz.intendedTurret}) covers " +
                                    $"{covered} span(s) — needs ≥{need}");
        }

        if (premiumAtFour < 3)
            problems.AppendLine($"  • only {premiumAtFour} Premium pad(s) at ≥4 spans — the rule needs 3");

        var mix = blueprint.classMix;
        CheckCensus(census, HardpointCoverageGizmo.PadClass.Premium, mix.premium, problems);
        CheckCensus(census, HardpointCoverageGizmo.PadClass.Standard, mix.standard, problems);
        CheckCensus(census, HardpointCoverageGizmo.PadClass.Rear, mix.rear, problems);
        CheckCensus(census, HardpointCoverageGizmo.PadClass.Overwatch, mix.overwatch, problems);

        if (problems.Length > 0)
            return "coverage violations:\n" + problems.ToString().TrimEnd();

        summary = $"{gizmos.Length} pads all ≥2 spans, {premiumAtFour} Premium at ≥4, mix matches blueprint";
        return null;
    }

    private static void CheckCensus(Dictionary<HardpointCoverageGizmo.PadClass, int> census,
                                    HardpointCoverageGizmo.PadClass cls, int expected, StringBuilder problems)
    {
        census.TryGetValue(cls, out int actual);
        if (actual != expected)
            problems.AppendLine($"  • {actual} {cls} pad(s) placed, blueprint asks for {expected}");
    }
}
