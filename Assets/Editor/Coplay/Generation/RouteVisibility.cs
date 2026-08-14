using System.Collections.Generic;
using Corehold.Core;
using Corehold.Towers;
using UnityEngine;

/// <summary>
/// How much of the enemy route the dressing hides from the player (R28).
///
/// Pads get an absolute guarantee — no prop may hide one. Routes get a BUDGET
/// instead, and the difference is deliberate. A pad is a point: protecting it
/// costs a small exclusion zone. A route is 150 m of ground, and at the fixed
/// 38° pitch a prop hides ~1.3× its height behind it, so protecting every metre
/// would exclude a band behind every prop position on the map and leave the
/// field bare. A budget keeps the useful middle: an occasional prop clipping a
/// stretch of path is fine, a wall across the approach is not.
///
/// One implementation, two callers — <see cref="PropPlacer"/> spends the budget
/// as it places, and the pipeline's gate 2b re-measures the finished scene
/// against it. A second copy of this maths is exactly how a gate ends up
/// disagreeing with the thing it gates.
/// </summary>
public static class RouteVisibility
{
    /// <summary>Arc-length spacing of the route samples this measures on. [TUNE]</summary>
    public const float SampleStep = 0.5f;

    /// <summary>
    /// Fraction of the route allowed to be hidden from the camera by dressing.
    /// 6% of the shipped 154 m route is ~9 m — a couple of short stretches
    /// behind props, not a screen. [TUNE]
    /// </summary>
    public const float HiddenBudgetFraction = 0.06f;

    /// <summary>
    /// Distinct route sample positions. The two entrance routes share their tail,
    /// so raw per-route sampling would count those metres twice and quietly
    /// double the effective budget there; coincident samples are de-duplicated on
    /// a 0.1 m grid so a "metre of route" means one metre of ground.
    /// </summary>
    public static List<Vector3> SampleRoutes(IEnumerable<PathRoute> routes)
    {
        var samples = new List<Vector3>();
        var seen = new HashSet<long>();

        foreach (PathRoute route in routes)
        {
            if (route == null)
                continue;
            for (float d = 0f; d <= route.Length; d += SampleStep)
            {
                Vector3 p = route.SamplePosition(d, out _);
                p.y = 0f;
                long key = ((long)Mathf.RoundToInt(p.x * 10f) << 32) ^ (uint)Mathf.RoundToInt(p.z * 10f);
                if (seen.Add(key))
                    samples.Add(p);
            }
        }
        return samples;
    }

    /// <summary>Metres of route the samples represent.</summary>
    public static float TotalMetres(List<Vector3> samples) => samples.Count * SampleStep;

    /// <summary>Metres of route allowed to be hidden, for a route of this length.</summary>
    public static float BudgetMetres(List<Vector3> samples) =>
        TotalMetres(samples) * HiddenBudgetFraction;

    /// <summary>
    /// Height above the route that must be visible — enemy centre mass, the same
    /// point the turret sight line aims at, so "hidden from the player" and
    /// "hidden from the turret" mean the same height of thing.
    /// </summary>
    public const float RouteVisibleHeight = HardpointCoverageGizmo.TargetHeight;

    /// <summary>
    /// Which samples are hidden from <paramref name="cam"/> by <paramref name="occluders"/>.
    /// <paramref name="into"/> is filled with sample INDICES, so a caller tracking
    /// what is already hidden can union results without re-testing.
    /// </summary>
    public static void FindHidden(List<Vector3> samples, Camera cam,
                                  IReadOnlyList<HardpointCoverageGizmo.Occluder> occluders,
                                  List<int> into, bool[] skipAlreadyHidden = null)
    {
        into.Clear();
        if (cam == null || occluders == null || occluders.Count == 0)
            return;

        Vector3 eye = cam.transform.position;
        for (int i = 0; i < samples.Count; i++)
        {
            if (skipAlreadyHidden != null && skipAlreadyHidden[i])
                continue;
            Vector3 target = samples[i] + Vector3.up * RouteVisibleHeight;
            if (HardpointCoverageGizmo.LineBlocked(eye, target, occluders))
                into.Add(i);
        }
    }

    /// <summary>Metres of route hidden from the camera by the given occluders.</summary>
    public static float HiddenMetres(List<Vector3> samples, Camera cam,
                                     IReadOnlyList<HardpointCoverageGizmo.Occluder> occluders)
    {
        var hidden = new List<int>();
        FindHidden(samples, cam, occluders, hidden);
        return hidden.Count * SampleStep;
    }
}
