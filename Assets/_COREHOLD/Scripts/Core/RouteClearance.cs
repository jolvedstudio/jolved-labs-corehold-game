using System.Collections.Generic;
using System.Text;
using Corehold.Towers;
using UnityEngine;

namespace Corehold.Core
{
    /// <summary>
    /// Author-time validation for the 1-D lane navigation (GDD redesign §Gap:
    /// tower keep-out removed). Because enemies no longer reactively steer around
    /// turrets, a route's swept lane band must not intersect any hardpoint pad — a
    /// clipping route would put a unit inside a turret with no recovery. This checks
    /// exactly that and reports precise conflicts so a designer fixes the WAYPOINTS
    /// (route geometry is balance-load-bearing: length → travel time, coverage,
    /// capacity), never a silent auto-nudge.
    /// </summary>
    public static class RouteClearance
    {
        public struct Conflict
        {
            public PathRoute Route;
            public TowerHardpoint Pad;
            public float DeficitMetres;   // how much clearance is missing
            public Vector3 NearestOnRoute;
            public Vector3 SuggestedNudge; // minimal lateral push to clear (report only)
        }

        /// <summary>
        /// Check every route against every hardpoint pad. A conflict is raised when
        /// the pad centre is closer to the route centreline than
        /// (laneHalfWidth + maxBodyRadius + padRadius). Sampled along arc-length.
        /// </summary>
        public static List<Conflict> Check(
            IEnumerable<PathRoute> routes,
            IEnumerable<TowerHardpoint> pads,
            float maxBodyRadius,
            float padRadius,
            float sampleStep = 0.5f)
        {
            var conflicts = new List<Conflict>();
            if (routes == null || pads == null)
                return conflicts;

            var padList = new List<TowerHardpoint>(pads);

            foreach (var route in routes)
            {
                if (route == null || route.PointCount < 2)
                    continue;

                float halfWidth = route.LaneHalfWidth;
                float clearance = halfWidth + maxBodyRadius + padRadius;
                float len = route.Length;
                int steps = Mathf.Max(2, Mathf.CeilToInt(len / Mathf.Max(0.1f, sampleStep)));

                foreach (var pad in padList)
                {
                    if (pad == null)
                        continue;

                    Vector3 padPos = pad.transform.position;
                    float best = float.PositiveInfinity;
                    Vector3 bestPoint = Vector3.zero;

                    for (int i = 0; i <= steps; i++)
                    {
                        float d = len * (i / (float)steps);
                        Vector3 c = route.SamplePosition(d, out _);
                        Vector3 delta = padPos - c;
                        delta.y = 0f;
                        float dist = delta.magnitude;
                        if (dist < best)
                        {
                            best = dist;
                            bestPoint = c;
                        }
                    }

                    if (best < clearance)
                    {
                        Vector3 away = padPos - bestPoint;
                        away.y = 0f;
                        if (away.sqrMagnitude < 0.0001f)
                            away = Vector3.right;
                        away.Normalize();
                        float deficit = clearance - best;
                        conflicts.Add(new Conflict
                        {
                            Route = route,
                            Pad = pad,
                            DeficitMetres = deficit,
                            NearestOnRoute = bestPoint,
                            // Minimal move of the route point AWAY from the pad to clear.
                            SuggestedNudge = -away * deficit
                        });
                    }
                }
            }

            return conflicts;
        }

        /// <summary>Format a conflict list into a precise, actionable report string.</summary>
        public static string Report(List<Conflict> conflicts)
        {
            if (conflicts == null || conflicts.Count == 0)
                return "Route clearance OK: no route lane-band intersects any hardpoint pad.";

            var sb = new StringBuilder();
            sb.AppendLine($"Route clearance FAILED: {conflicts.Count} conflict(s). Fix route waypoints (do not auto-nudge — geometry is balance-load-bearing):");
            foreach (var c in conflicts)
            {
                string routeName = c.Route != null ? c.Route.name : "<null route>";
                string padName = c.Pad != null ? c.Pad.name : "<null pad>";
                sb.AppendLine(
                    $"  • Route '{routeName}' vs pad '{padName}': clearance deficit {c.DeficitMetres:0.00} m " +
                    $"at ~{c.NearestOnRoute}. Suggested minimal route-point nudge: {c.SuggestedNudge} " +
                    $"(move that route segment ~{c.DeficitMetres:0.00} m away from the pad).");
            }
            return sb.ToString();
        }
    }
}
