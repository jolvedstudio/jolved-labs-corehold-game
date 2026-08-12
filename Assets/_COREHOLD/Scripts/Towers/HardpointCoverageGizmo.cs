using System.Collections.Generic;
using Corehold.Core;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Corehold.Towers
{
    /// <summary>
    /// Editor-only coverage validator for a hardpoint (GDD §5.2 / §5.3, Ticket 30).
    ///
    /// Draws the <b>intended turret's tier-1 range ring</b> for this pad and counts
    /// how many distinct route segments it covers. The coverage rule is load-bearing:
    /// every hardpoint must cover at least <b>two</b> route segments at its intended
    /// turret's tier-1 range; at least <b>three</b> premium pads must cover <b>four
    /// or more</b>. Per-turret rings are used deliberately — a single 12 m sphere
    /// over-validates the Arc Node and under-validates the Mortar.
    ///
    /// A segment is "covered" when the ring intersects it in the horizontal (XZ)
    /// plane — the closest point on the segment to the pad is within range. This is
    /// the same 2D test a designer eyeballs; range checks in play are full 3D
    /// (GDD §7.2), but for reading a ground route on the map the horizontal test is
    /// the correct authoring tool.
    /// </summary>
    [DisallowMultipleComponent]
    public class HardpointCoverageGizmo : MonoBehaviour
    {
        public enum TurretKind
        {
            Autocannon,   // 12 m tier-1
            Missile,      // 13 m tier-1
            ArcNode,      // 10 m tier-1
            Mortar        // 20 m tier-1
        }

        public enum PadClass
        {
            Premium,      // must cover 4+ segments
            Standard,     // 2-3 segments
            Rear,         // final approach + air terminal leg
            Overwatch     // Siege Mortar home, sparse close coverage
        }

        [Tooltip("The turret this pad is designed to host. Drives the tier-1 range ring used for the coverage check.")]
        public TurretKind intendedTurret = TurretKind.Autocannon;

        [Tooltip("Design intent for this pad (GDD §5.3).")]
        public PadClass padClass = PadClass.Standard;

        [Tooltip("Ground routes this pad is checked against. All routes share the same snake, so listing both entrance routes double-counts the shared segments; " +
                 "assign the single canonical route (or both — the gizmo de-duplicates identical world segments).")]
        public PathRoute[] routes;

        [Tooltip("Mortar has a 6 m dead zone (GDD §7.3). Segments entirely inside this radius do not count for the Mortar.")]
        public float mortarMinRange = 6f;

        /// <summary>Tier-1 range in metres for a turret kind (GDD §7.3).</summary>
        public static float RangeFor(TurretKind kind)
        {
            switch (kind)
            {
                case TurretKind.Autocannon: return 12f;
                case TurretKind.Missile: return 13f;
                case TurretKind.ArcNode: return 10f;
                case TurretKind.Mortar: return 20f;
                default: return 12f;
            }
        }

        /// <summary>
        /// Counts distinct covered route segments in the horizontal plane. Shared
        /// segments (same endpoints in world space) are counted once.
        /// </summary>
        public int CountCoveredSegments()
        {
            if (routes == null)
                return 0;

            float range = RangeFor(intendedTurret);
            bool isMortar = intendedTurret == TurretKind.Mortar;
            Vector3 padXZ = transform.position;
            padXZ.y = 0f;

            var seen = new HashSet<string>();
            int covered = 0;

            foreach (var route in routes)
            {
                if (route == null) continue;
                int count = route.PointCount;
                for (int i = 0; i + 1 < count; i++)
                {
                    Vector3 a = route.GetPoint(i);
                    Vector3 b = route.GetPoint(i + 1);
                    a.y = 0f; b.y = 0f;

                    // De-duplicate shared snake segments across routes.
                    string key = SegKey(a, b);
                    if (!seen.Add(key)) continue;

                    float d = DistancePointToSegment(padXZ, a, b);
                    if (d > range) continue;

                    if (isMortar)
                    {
                        // Dead-zone rule: segment must have at least one point beyond min range.
                        float da = Vector3.Distance(padXZ, a);
                        float db = Vector3.Distance(padXZ, b);
                        if (da < mortarMinRange && db < mortarMinRange) continue;
                    }

                    covered++;
                }
            }
            return covered;
        }

        private static string SegKey(Vector3 a, Vector3 b)
        {
            // Round to 0.1 m and order endpoints so A-B == B-A.
            Vector3 lo = a, hi = b;
            if (b.x < a.x || (Mathf.Approximately(a.x, b.x) && b.z < a.z)) { lo = b; hi = a; }
            return $"{lo.x:0.0},{lo.z:0.0}|{hi.x:0.0},{hi.z:0.0}";
        }

        private static float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector3.Distance(p, a);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            Vector3 proj = a + t * ab;
            return Vector3.Distance(p, proj);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            float range = RangeFor(intendedTurret);
            int covered = CountCoveredSegments();

            Color ring = ColorFor(intendedTurret);
            bool meetsMin = padClass == PadClass.Premium ? covered >= 4 : covered >= 2;
            if (!meetsMin) ring = Color.red;

            // Range ring (horizontal circle).
            DrawCircleXZ(transform.position, range, ring);

            if (intendedTurret == TurretKind.Mortar && mortarMinRange > 0f)
                DrawCircleXZ(transform.position, mortarMinRange, new Color(1f, 0.4f, 0f, 0.6f));

            Handles.color = ring;
            Handles.Label(transform.position + Vector3.up * 2.2f,
                $"{name}\n{intendedTurret} r{range:0}m · {padClass}\ncovers {covered} seg");
        }

        private static Color ColorFor(TurretKind kind)
        {
            switch (kind)
            {
                case TurretKind.Autocannon: return new Color(1f, 0.85f, 0.2f, 1f);
                case TurretKind.Missile: return new Color(1f, 0.4f, 0.3f, 1f);
                case TurretKind.ArcNode: return new Color(0.4f, 0.8f, 1f, 1f);
                case TurretKind.Mortar: return new Color(0.7f, 0.4f, 1f, 1f);
                default: return Color.white;
            }
        }

        private static void DrawCircleXZ(Vector3 center, float radius, Color color)
        {
            Handles.color = color;
            Handles.DrawWireDisc(center, Vector3.up, radius);
        }
#endif
    }
}
