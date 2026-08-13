using System.Collections.Generic;
using System.Text;
using Corehold.Core;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Merge-knot tangent pinning and the shared-tail divergence check (roadmap R7).
///
/// The west and north routes carry duplicated, identical-position knots for the
/// shared snake tail. AutoSmooth derives every knot's tangent from its
/// NEIGHBOURS, and the two routes approach the merge from different directions,
/// so the merge knot alone gets a different tangent on each route — and the
/// first shared curve diverges. Every knot AFTER the merge has identical
/// neighbours on both routes, so its tangents already agree; pinning the merge
/// knot's outgoing tangent to the same value on both routes is therefore
/// sufficient to make the whole shared tail identical.
///
/// Two menu items, both report-only unless you run the pinning one:
///   • Tools/COREHOLD/Pin Merge Knots      — writes identical pins to both routes.
///   • Tools/COREHOLD/Check Route Divergence — samples both curves every 0.5 m
///     over the shared tail and reports the maximum separation (gate: ≤ 0.05 m).
/// </summary>
public static class MergeKnotPinning
{
    /// <summary>Sampling step along the shared tail, in metres (R7 ticket).</summary>
    private const float SampleStep = 0.5f;

    /// <summary>Maximum tolerated separation between the two curves (R7 done-when).</summary>
    private const float DivergenceGate = 0.05f;

    /// <summary>Two knots count as the same position within this many metres.</summary>
    private const float SamePointEpsilon = 0.01f;

    [MenuItem("Tools/COREHOLD/Pin Merge Knots")]
    public static void PinMergeKnots()
    {
        if (!ResolveRoutes(out PathRoute primary, out PathRoute secondary, out string error))
        {
            Debug.LogWarning("[R7] " + error);
            return;
        }

        var log = new StringBuilder();
        log.AppendLine("=== R7 merge-knot pinning ===");

        if (!FindSharedTail(primary, secondary, out int primaryMerge, out int secondaryMerge, out int sharedCount))
        {
            Debug.LogWarning($"[R7] '{primary.name}' and '{secondary.name}' share no tail knots — nothing to pin.");
            return;
        }

        log.AppendLine($"Shared tail: {sharedCount} knots, starting at " +
                       $"{primary.name}[{primaryMerge}] / {secondary.name}[{secondaryMerge}] " +
                       $"= {primary.GetPoint(primaryMerge)}");

        if (primaryMerge <= 0 || primaryMerge + 1 >= primary.PointCount)
        {
            Debug.LogWarning("[R7] The merge knot is at a route end — no neighbours to derive a tangent from.");
            return;
        }

        // Derive the pin from the PRIMARY route's own neighbours, using the standard
        // Catmull-Rom convention (next − previous)/3. Two consequences worth knowing:
        // the primary route's shape barely moves (this is close to what AutoSmooth
        // already gives it), and the secondary route adopts the primary's entry into
        // the shared tail. Any residual shift in route Length is captured by R9's
        // Length-delta filing and re-baselined by R10.
        Vector3 prev = primary.GetPoint(primaryMerge - 1);
        Vector3 next = primary.GetPoint(primaryMerge + 1);
        Vector3 pin = (next - prev) / 3f;

        log.AppendLine($"Pin (from {primary.name}, (next−prev)/3): {pin}");

        WritePin(primary, primaryMerge, pin, log);
        WritePin(secondary, secondaryMerge, pin, log);

        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(primary.gameObject.scene);

        log.AppendLine();
        log.AppendLine(Measure(primary, secondary, primaryMerge, secondaryMerge));
        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/COREHOLD/Check Route Divergence")]
    public static void CheckDivergence()
    {
        if (!ResolveRoutes(out PathRoute primary, out PathRoute secondary, out string error))
        {
            Debug.LogWarning("[R7] " + error);
            return;
        }
        if (!FindSharedTail(primary, secondary, out int primaryMerge, out int secondaryMerge, out _))
        {
            Debug.LogWarning($"[R7] '{primary.name}' and '{secondary.name}' share no tail knots.");
            return;
        }

        string report = Measure(primary, secondary, primaryMerge, secondaryMerge);
        if (report.Contains("FAIL"))
            Debug.LogWarning(report);
        else
            Debug.Log(report);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Sample both curves every <see cref="SampleStep"/> metres from their own merge
    /// knot to the end and report the worst separation. Distances are measured per
    /// route because the approach legs differ in length, so the shared tail starts at
    /// a different arc distance on each.
    /// </summary>
    private static string Measure(PathRoute a, PathRoute b, int aMergeIndex, int bMergeIndex)
    {
        float aStart = a.DistanceAlongAt(aMergeIndex);
        float bStart = b.DistanceAlongAt(bMergeIndex);
        float tail = Mathf.Min(a.Length - aStart, b.Length - bStart);

        float worst = 0f;
        float worstAt = 0f;
        int samples = 0;
        for (float d = 0f; d <= tail; d += SampleStep)
        {
            Vector3 pa = a.SamplePosition(aStart + d, out _);
            Vector3 pb = b.SamplePosition(bStart + d, out _);
            float sep = Vector3.Distance(pa, pb);
            if (sep > worst)
            {
                worst = sep;
                worstAt = d;
            }
            samples++;
        }

        string mode = (a.UseSpline || b.UseSpline)
            ? $"spline (ready: {a.name}={a.SplineReady}, {b.name}={b.SplineReady})"
            : "piecewise-linear (useSpline is OFF on both — turn it on to measure the curves)";

        var sb = new StringBuilder();
        sb.AppendLine("=== R7 shared-tail divergence ===");
        sb.AppendLine($"Mode        : {mode}");
        sb.AppendLine($"Shared tail : {tail:0.00} m from {a.name}[{aMergeIndex}] / {b.name}[{bMergeIndex}] " +
                      $"({samples} samples @ {SampleStep} m)");
        sb.AppendLine($"Route length: {a.name} {a.Length:0.###} m, {b.name} {b.Length:0.###} m");
        sb.AppendLine($"Max divergence: {worst:0.####} m at {worstAt:0.0} m along the tail " +
                      $"(gate ≤ {DivergenceGate} m) -> {(worst <= DivergenceGate ? "PASS" : "**FAIL**")}");
        return sb.ToString();
    }

    /// <summary>
    /// Walk both routes backwards from their final knot while the positions match.
    /// Returns the first shared index on each route.
    /// </summary>
    private static bool FindSharedTail(PathRoute a, PathRoute b,
                                       out int aMergeIndex, out int bMergeIndex, out int sharedCount)
    {
        int ai = a.PointCount - 1;
        int bi = b.PointCount - 1;
        sharedCount = 0;

        while (ai >= 0 && bi >= 0 &&
               Vector3.Distance(a.GetPoint(ai), b.GetPoint(bi)) <= SamePointEpsilon)
        {
            ai--;
            bi--;
            sharedCount++;
        }

        aMergeIndex = ai + 1;
        bMergeIndex = bi + 1;
        return sharedCount > 0;
    }

    private static void WritePin(PathRoute route, int knotIndex, Vector3 tangentOut, StringBuilder log)
    {
        var so = new SerializedObject(route);
        SerializedProperty pins = so.FindProperty("tangentPins");
        if (pins == null)
        {
            log.AppendLine($"[warn] '{route.name}' has no tangentPins field — is the R7 code compiled?");
            return;
        }

        // Replace an existing pin for this knot, else append one.
        int slot = -1;
        for (int i = 0; i < pins.arraySize; i++)
        {
            if (pins.GetArrayElementAtIndex(i).FindPropertyRelative("knotIndex").intValue == knotIndex)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            slot = pins.arraySize;
            pins.arraySize++;
        }

        SerializedProperty entry = pins.GetArrayElementAtIndex(slot);
        entry.FindPropertyRelative("knotIndex").intValue = knotIndex;
        entry.FindPropertyRelative("tangentOut").vector3Value = tangentOut;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(route);

        log.AppendLine($"[ok] {route.name}: pinned knot {knotIndex} out-tangent to {tangentOut}");
    }

    /// <summary>
    /// Resolve the two ground routes: by name when the shipped names are present,
    /// otherwise the first two routes with more than one knot.
    /// </summary>
    private static bool ResolveRoutes(out PathRoute primary, out PathRoute secondary, out string error)
    {
        primary = secondary = null;
        error = null;

        var routes = new List<PathRoute>(
            Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None));
        routes.RemoveAll(r => r == null || r.PointCount < 2);

        foreach (var r in routes)
        {
            if (r.name == "Route_West") primary = r;
            else if (r.name == "Route_North") secondary = r;
        }

        if (primary == null || secondary == null)
        {
            if (routes.Count < 2)
            {
                error = $"need two ground routes in the scene, found {routes.Count}.";
                return false;
            }
            primary = routes[0];
            secondary = routes[1];
        }
        return true;
    }
}
