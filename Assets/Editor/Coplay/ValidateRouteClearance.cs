using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Corehold.Core;
using Corehold.Towers;

/// <summary>
/// Editor validation for the 1-D lane navigation: confirms no route's swept lane
/// band intersects any hardpoint pad (see RouteClearance). Stop-and-surface — it
/// reports precise conflicts and a suggested minimal nudge; it never edits geometry.
/// </summary>
public static class ValidateRouteClearance
{
    public static string Execute()
    {
        var routes = new List<PathRoute>(Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None));
        var pads = new List<TowerHardpoint>(Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None));

        // Largest spawnable body radius + pad tap radius as the clearance envelope.
        const float maxBodyRadius = 1.35f; // Breaker
        const float padRadius = 1.5f;      // TowerHardpoint tap radius default

        var conflicts = RouteClearance.Check(routes, pads, maxBodyRadius, padRadius);
        string report = RouteClearance.Report(conflicts);

        if (conflicts.Count > 0)
            Debug.LogWarning(report);
        else
            Debug.Log(report);

        return $"Routes: {routes.Count}, Pads: {pads.Count}\n{report}";
    }
}
