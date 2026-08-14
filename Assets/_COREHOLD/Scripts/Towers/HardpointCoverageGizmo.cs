using System.Collections.Generic;
using Corehold.Core;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Corehold.Towers
{
    /// <summary>
    /// Editor-only coverage validator for a hardpoint (GDD §5.2 / §5.3, Ticket 30,
    /// curve rewrite roadmap R8).
    ///
    /// Draws the <b>intended turret's tier-1 range ring</b> for this pad and counts
    /// how many distinct route spans it covers. The coverage rule is load-bearing:
    /// every hardpoint must cover at least <b>two</b> route spans at its intended
    /// turret's tier-1 range; at least <b>three</b> premium pads must cover <b>four
    /// or more</b>. Per-turret rings are used deliberately — a single 12 m sphere
    /// over-validates the Arc Node and under-validates the Mortar.
    ///
    /// <b>A span is a knot interval measured on the route as the enemies actually
    /// walk it</b> (R8): the interval between two consecutive waypoints is sampled
    /// along its arc length through <see cref="PathRoute.SamplePosition"/>, so a
    /// spline route is judged on its curve rather than on the chords between its
    /// knots. With <c>useSpline</c> off the same code measures the polyline and
    /// reproduces the original straight-segment answer, which is what makes the
    /// before/after table in R9's gate a like-for-like comparison.
    ///
    /// Coverage is a horizontal (XZ) test — the same one a designer eyeballs. Range
    /// checks in play are full 3D (GDD §7.2), but for reading a ground route on the
    /// map the horizontal test is the correct authoring tool.
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
            Premium,      // must cover 4+ spans
            Standard,     // 2-3 spans
            Rear,         // final approach + air terminal leg
            Overwatch     // Siege Mortar home, sparse close coverage
        }

        [Tooltip("The turret this pad is designed to host. Drives the tier-1 range ring used for the coverage check.")]
        public TurretKind intendedTurret = TurretKind.Autocannon;

        [Tooltip("Design intent for this pad (GDD §5.3).")]
        public PadClass padClass = PadClass.Standard;

        [Tooltip("Ground routes this pad is checked against. All routes share the same snake, so listing both entrance routes double-counts the shared spans; " +
                 "assign the single canonical route (or both — the gizmo de-duplicates identical world spans).")]
        public PathRoute[] routes;

        [Tooltip("Mortar has a 6 m dead zone (GDD §7.3). Spans lying entirely inside this radius do not count for the Mortar.")]
        public float mortarMinRange = 6f;

        [Tooltip("Arc-length step (m) used to walk each knot interval against the range ring (R8). Finer is more faithful; it only recomputes when this pad or its routes change, so the cost is not per-frame.")]
        public float curveSampleStep = 0.25f;

        // Coverage is recomputed only when the pad, its settings or its routes
        // change. The old implementation rebuilt a HashSet of formatted strings for
        // every pad on every OnDrawGizmos frame; curve sampling would have made that
        // far worse, so the result is cached behind a cheap hash.
        private int _coverageHash;
        private int _coveredCache;
        private bool _hasCache;

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
        /// Number of distinct covered spans, measured on the route as walked (R8).
        /// Cached; the cache invalidates when the pad or its routes change.
        /// </summary>
        public int CountCoveredSegments()
        {
            int hash = ComputeCoverageHash();
            if (_hasCache && hash == _coverageHash)
                return _coveredCache;

            _coverageHash = hash;
            _hasCache = true;
            _coveredCache = CountCoveredSpansOnCurve();
            return _coveredCache;
        }

        /// <summary>
        /// One sight-line occluder for the occlusion-aware count (roadmap R28): a
        /// vertical cylinder at a placed prop's position, using its PLACED footprint
        /// radius and height (stored metadata × applied scale — reading the raw
        /// asset fields under-measures every prop the placer scaled up).
        /// </summary>
        public struct Occluder
        {
            public Vector3 position;
            public float radius;
            public float height;
        }

        /// <summary>Height above the pad the turret muzzle fires from, for sight-line tests (R28). [TUNE]</summary>
        public const float MuzzleHeight = 1.5f;

        /// <summary>Height above the route a shot must reach — enemy centre mass. [TUNE]</summary>
        public const float TargetHeight = 1.0f;

        /// <summary>
        /// Count covered knot-interval spans by walking each interval along its arc
        /// length. A span counts when any part of it falls inside the range ring —
        /// tested per sub-segment between samples, so a span that only clips the
        /// ring between two samples is still caught.
        /// </summary>
        public int CountCoveredSpansOnCurve() => CountCoveredSpansOnCurve(null);

        /// <summary>
        /// The same walk, with SIGHT LINES (R28): a sub-segment only counts when it
        /// is inside the ring AND the muzzle→target line is not blocked by an
        /// occluder cylinder. This lives HERE, on the same walk the plain count
        /// uses, precisely so there is no second implementation to drift — the
        /// distance test and the occlusion test disagree only by the occluders.
        /// Passing null (or empty) reproduces the plain count exactly.
        /// </summary>
        public int CountCoveredSpansOnCurve(IReadOnlyList<Occluder> occluders)
        {
            if (routes == null)
                return 0;

            float range = RangeFor(intendedTurret);
            float minRange = intendedTurret == TurretKind.Mortar ? mortarMinRange : 0f;
            Vector3 padXZ = Flat(transform.position);

            var seen = new HashSet<string>();
            int covered = 0;

            foreach (var route in routes)
            {
                if (route == null)
                    continue;

                int count = route.PointCount;
                for (int i = 0; i + 1 < count; i++)
                {
                    // De-dup on the KNOT endpoints (0.1 m rounding), unchanged from
                    // the original: the two entrance routes carry identical knots for
                    // the shared snake, so its spans must be counted once.
                    string key = SegKey(Flat(route.GetPoint(i)), Flat(route.GetPoint(i + 1)));
                    if (!seen.Add(key))
                        continue;

                    if (SpanCovered(route, i, padXZ, range, minRange, occluders))
                        covered++;
                }
            }
            return covered;
        }

        /// <summary>
        /// True when the muzzle→target sight line passes through an occluder: the
        /// 3D segment from the pad at <see cref="MuzzleHeight"/> to the sample at
        /// <see cref="TargetHeight"/>, tested against each cylinder — the XZ ray
        /// must cross the circle AND sit at or below the cylinder's height where
        /// it crosses. The Mortar is EXEMPT: it fires an arcing shell over
        /// obstacles by design (GDD §7.3), which is much of why Overwatch pads
        /// tolerate set-back positions.
        /// </summary>
        private bool SightBlocked(Vector3 padXZ, Vector3 targetXZ, IReadOnlyList<Occluder> occluders)
        {
            // The Mortar lobs its shell over obstacles by design (GDD §7.3),
            // which is much of why Overwatch pads tolerate set-back positions.
            if (intendedTurret == TurretKind.Mortar)
                return false;

            return LineBlocked(padXZ + Vector3.up * MuzzleHeight,
                               targetXZ + Vector3.up * TargetHeight, occluders);
        }

        /// <summary>
        /// Does the 3D segment <paramref name="from"/> → <paramref name="to"/> pass
        /// through any occluder cylinder? The XZ ray must cross the circle AND the
        /// segment must sit at or below the cylinder's height where it crosses.
        ///
        /// Public and general because two different sight lines need it and must
        /// not drift apart: the TURRET's line to a covered route span (what gate 2b
        /// judges) and the CAMERA's line to a pad (whether the player can see the
        /// pad at all). Occluder heights are measured from the ground plane, so
        /// this assumes props stand on y = 0 — which the placer guarantees.
        /// </summary>
        public static bool LineBlocked(Vector3 from, Vector3 to, IReadOnlyList<Occluder> occluders)
        {
            if (occluders == null || occluders.Count == 0)
                return false;

            Vector3 a = Flat(from);
            Vector3 ab = Flat(to) - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f)
                return false;

            for (int i = 0; i < occluders.Count; i++)
            {
                Occluder oc = occluders[i];
                Vector3 c = Flat(oc.position);

                float t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / len2);
                Vector3 closest = a + ab * t;
                if ((closest - c).sqrMagnitude > oc.radius * oc.radius)
                    continue;

                float y = Mathf.Lerp(from.y, to.y, t);
                if (y <= oc.height)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Height above a pad that must stay visible to the camera. The PadMarker
        /// disc tops out around 0.17 m, so testing 0.2 m is the strict reading:
        /// clear this and the whole marker is in view.
        /// </summary>
        public const float PadVisibleHeight = 0.2f;

        /// <summary>
        /// The original straight-chord count, kept for the R9 before/after table:
        /// it measures the same pad against the chords between knots rather than the
        /// walked route, which is what the pre-spline map was validated on.
        /// </summary>
        public int CountCoveredSegmentsLinear()
        {
            if (routes == null)
                return 0;

            float range = RangeFor(intendedTurret);
            bool isMortar = intendedTurret == TurretKind.Mortar;
            Vector3 padXZ = Flat(transform.position);

            var seen = new HashSet<string>();
            int covered = 0;

            foreach (var route in routes)
            {
                if (route == null) continue;
                int count = route.PointCount;
                for (int i = 0; i + 1 < count; i++)
                {
                    Vector3 a = Flat(route.GetPoint(i));
                    Vector3 b = Flat(route.GetPoint(i + 1));

                    string key = SegKey(a, b);
                    if (!seen.Add(key)) continue;

                    if (DistancePointToSegment(padXZ, a, b) > range) continue;

                    if (isMortar)
                    {
                        float da = Vector3.Distance(padXZ, a);
                        float db = Vector3.Distance(padXZ, b);
                        if (da < mortarMinRange && db < mortarMinRange) continue;
                    }

                    covered++;
                }
            }
            return covered;
        }

        /// <summary>
        /// Walk one knot interval along the route and test each sub-segment against
        /// the ring. The Mortar dead-zone rule is the original one applied at finer
        /// granularity: a piece lying entirely inside the dead zone does not count.
        /// With occluders, an in-ring sub-segment must ALSO have a clear sight line
        /// to its midpoint (R28) — a span survives if ANY of its pieces is both in
        /// range and visible.
        /// </summary>
        private bool SpanCovered(PathRoute route, int knotIndex, Vector3 padXZ, float range, float minRange,
                                 IReadOnlyList<Occluder> occluders = null)
        {
            float d0 = route.DistanceAlongAt(knotIndex);
            float d1 = route.DistanceAlongAt(knotIndex + 1);
            if (d1 <= d0)
                return false;

            float step = Mathf.Max(0.05f, curveSampleStep);
            int steps = Mathf.Max(1, Mathf.CeilToInt((d1 - d0) / step));

            Vector3 prev = Flat(route.SamplePosition(d0, out _));
            for (int s = 1; s <= steps; s++)
            {
                float d = Mathf.Lerp(d0, d1, s / (float)steps);
                Vector3 cur = Flat(route.SamplePosition(d, out _));

                if (DistancePointToSegment(padXZ, prev, cur) <= range)
                {
                    float da = Vector3.Distance(padXZ, prev);
                    float db = Vector3.Distance(padXZ, cur);
                    if (Mathf.Max(da, db) >= minRange &&
                        !SightBlocked(padXZ, (prev + cur) * 0.5f, occluders))
                        return true;
                }
                prev = cur;
            }
            return false;
        }

        private int ComputeCoverageHash()
        {
            unchecked
            {
                int h = 17;
                Vector3 p = transform.position;
                h = h * 31 + p.x.GetHashCode();
                h = h * 31 + p.y.GetHashCode();
                h = h * 31 + p.z.GetHashCode();
                h = h * 31 + (int)intendedTurret;
                h = h * 31 + mortarMinRange.GetHashCode();
                h = h * 31 + curveSampleStep.GetHashCode();

                if (routes != null)
                {
                    h = h * 31 + routes.Length;
                    foreach (var r in routes)
                    {
                        if (r == null) { h *= 31; continue; }
                        h = h * 31 + r.PointCount;
                        h = h * 31 + r.Length.GetHashCode();   // moves when any knot moves
                        h = h * 31 + (r.SplineReady ? 1 : 0);  // and when the mode flips
                    }
                }
                return h;
            }
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
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
                $"{name}\n{intendedTurret} r{range:0}m · {padClass}\ncovers {covered} span");
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
