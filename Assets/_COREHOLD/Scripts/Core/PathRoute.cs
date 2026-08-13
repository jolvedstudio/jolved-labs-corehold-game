using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Corehold.Core
{
    /// <summary>
    /// Holds an ordered list of waypoint Transforms defining a route.
    /// Provides cumulative distance queries and draws the route (with
    /// per-waypoint cumulative distance labels) in the Scene view.
    /// </summary>
    [DisallowMultipleComponent]
    public class PathRoute : MonoBehaviour
    {
        /// <summary>
        /// An explicit outgoing tangent forced onto one knot, overriding AutoSmooth
        /// (roadmap R7). Used at a merge knot so two routes sharing a tail produce
        /// the identical curve from the merge onward.
        /// </summary>
        [System.Serializable]
        public struct TangentPin
        {
            [Tooltip("Index of the waypoint whose outgoing tangent is pinned.")]
            public int knotIndex;

            [Tooltip("Outgoing tangent as a world-space offset from the knot. Every route sharing this merge MUST carry the identical value — that is what makes the shared tails match.")]
            public Vector3 tangentOut;
        }

        [Tooltip("Ordered list of waypoints. The route runs from index 0 to the last entry.")]
        [SerializeField] private Transform[] waypoints;

        [Header("Lanes (1-D car-following model)")]
        [Tooltip("Number of parallel lanes on this ground track. Enemies follow the centreline at a fixed lateral offset per lane so same-track units never share a line. 2 is the default for ground routes.")]
        [SerializeField] private int laneCount = 2;

        [Tooltip("Half-width in metres of the lane band: the outermost lanes sit at ±this. Lanes are spread evenly across [-laneHalfWidth, +laneHalfWidth].")]
        [SerializeField] private float laneHalfWidth = 0.9f;

        [Tooltip("Extra longitudinal spacing (metres) added on top of the two body radii between consecutive same-lane units. Scale up for tightly-curved routes so inner-lane world spacing stays honest.")]
        [SerializeField] private float spacingBuffer = 0.4f;

        [Header("Spline backbone (roadmap R6)")]
        [Tooltip("When ON, the route is evaluated as a smooth spline through the waypoints (AutoSmooth knots) instead of straight segments. Length and SamplePosition both change, and route geometry is balance-load-bearing — re-run the balance model after flipping this (roadmap R10).")]
        [SerializeField] private bool useSpline = false;

        [Tooltip("Arc-length table resolution: samples per knot interval. Higher = finer distance→position mapping. Rebuilt only when the waypoints actually move, so this costs nothing per frame.")]
        [SerializeField] private int samplesPerCurve = 16;

        [Tooltip("Merge-knot tangent pins (roadmap R7). Two routes that share a tail but approach it from different directions get DIFFERENT AutoSmooth tangents at the merge knot — because AutoSmooth reads a knot's neighbours — so their shared tails diverge. Pinning the same outgoing tangent at the merge knot on both routes makes the shared geometry identical. Wire with Tools → COREHOLD → Pin Merge Knots.")]
        [SerializeField] private TangentPin[] tangentPins;

        [Header("Gizmo")]
        [SerializeField] private Color lineColor = new Color(0.2f, 1f, 0.6f, 1f);
        [SerializeField] private float pointRadius = 0.25f;

        [Tooltip("Colour of the spline overlay drawn alongside the raw polyline while useSpline is on (R6 comparison view).")]
        [SerializeField] private Color splineColor = new Color(1f, 0.85f, 0.2f, 1f);

        [Tooltip("Draw the swept lane band and the tower-clearance envelope in the Scene view.")]
        [SerializeField] private bool drawLaneBand = true;

        [Tooltip("Draw the per-waypoint cumulative distance labels. Handles.Label formats a string per waypoint per frame, so switch this off when profiling gizmo allocations (R6).")]
        [SerializeField] private bool drawDistanceLabels = true;

        // Cached cumulative distance from start to each waypoint (metres).
        private float[] _cumulativeDistances;
        private float _length;

        // ----- Spline backbone (R6) -----
        //
        // The curve is baked ONCE per geometry change into a flat arc-length table
        // (positions + cumulative distance), and every runtime query reads that
        // table. Two consequences that matter:
        //
        //   • Zero GC per frame. Recompute() is called from Awake, OnValidate AND
        //     every OnDrawGizmos; a dirty-check keyed on a hash of the waypoint
        //     positions means the rebuild (and the array churn that used to happen
        //     unconditionally, even before splines) only runs when a waypoint moves.
        //
        //   • No native-collection lifetime to leak. NativeSpline is used for the
        //     allocation-free evaluation that fills the table and disposed in the
        //     same scope, rather than cached across frames where a domain reload
        //     would leak it. Tangents come from finite differences along the table,
        //     which keeps the sampling identical in shape to the polyline path.
        private Vector3[] _arcPoints;
        private float[] _arcDistances;
        private float[] _knotDistances;
        private bool _splineReady;
        private int _geometryHash;
        private bool _built;

        /// <summary>Relative gap between the chord-summed table length and the spline's own arc length that is tolerated before warning (R6).</summary>
        private const float ArcTableTolerance = 0.001f;

        /// <summary>Number of waypoints in the route.</summary>
        public int PointCount => waypoints != null ? waypoints.Length : 0;

        /// <summary>Total length of the route in metres (cached, recomputed in OnValidate). Spline length when the backbone is on (R6).</summary>
        public float Length => _length;

        /// <summary>True when this route is authored to evaluate as a spline (R6).</summary>
        public bool UseSpline => useSpline;

        /// <summary>True when the spline arc table is built and driving SamplePosition (R6).</summary>
        public bool SplineReady => _splineReady;

        /// <summary>Number of parallel lanes on this track.</summary>
        public int LaneCount => Mathf.Max(1, laneCount);

        /// <summary>Half-width in metres of the lane band (outermost lane offset magnitude).</summary>
        public float LaneHalfWidth => laneHalfWidth;

        /// <summary>Extra longitudinal buffer (metres) between consecutive same-lane units.</summary>
        public float SpacingBuffer => spacingBuffer;

        /// <summary>
        /// Signed lateral offset in metres for a lane index in [0, LaneCount).
        /// Lanes are spread evenly across [-laneHalfWidth, +laneHalfWidth]. A single
        /// lane sits on the centreline (offset 0).
        /// </summary>
        public float LaneOffset(int lane)
        {
            int n = LaneCount;
            if (n <= 1)
                return 0f;
            lane = Mathf.Clamp(lane, 0, n - 1);
            float t = lane / (float)(n - 1);        // 0..1
            return Mathf.Lerp(-laneHalfWidth, laneHalfWidth, t);
        }

        /// <summary>
        /// World-space position of a point at arc-length <paramref name="distance"/>
        /// on the given <paramref name="lane"/> (centreline offset laterally by the
        /// lane offset, on the horizontal plane).
        /// </summary>
        public Vector3 LanePosition(float distance, int lane, out Vector3 tangent)
        {
            Vector3 centre = SamplePosition(distance, out tangent);
            Vector3 right = HorizontalRight(tangent);
            return centre + right * LaneOffset(lane);
        }

        /// <summary>Horizontal "right" vector perpendicular to a travel direction.</summary>
        public static Vector3 HorizontalRight(Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return Vector3.right;
            forward.Normalize();
            return Vector3.Cross(Vector3.up, forward).normalized;
        }

        /// <summary>
        /// Conservative per-lane capacity given a representative unit radius: how many
        /// units fit along the track at minimum spacing. Used for the derived
        /// concurrency cap (computed with the largest spawnable radius for safety).
        /// </summary>
        public int LaneCapacity(float unitRadius)
        {
            float minSpacing = 2f * unitRadius + spacingBuffer;
            if (minSpacing <= 0.0001f)
                return 0;
            return Mathf.Max(1, Mathf.FloorToInt(_length / minSpacing));
        }

        /// <summary>Conservative total capacity across all lanes for the given unit radius.</summary>
        public int TotalCapacity(float unitRadius) => LaneCapacity(unitRadius) * LaneCount;

        /// <summary>World-space position of the waypoint at the given index.</summary>
        public Vector3 GetPoint(int index)
        {
            if (waypoints == null || index < 0 || index >= waypoints.Length || waypoints[index] == null)
                return Vector3.zero;
            return waypoints[index].position;
        }

        /// <summary>
        /// Sample the centreline position at a given arc-length <paramref name="distance"/>
        /// (metres from the start), and output the unit tangent (direction of travel)
        /// at that point. Used by the enemy mover's lane model, which advances a
        /// scalar distance along the route and offsets laterally, so lateral spread
        /// never fights forward progress.
        /// </summary>
        public Vector3 SamplePosition(float distance, out Vector3 tangent)
        {
            tangent = Vector3.forward;

            int count = PointCount;
            if (count == 0)
                return Vector3.zero;
            if (_cumulativeDistances == null || _cumulativeDistances.Length != count)
                Recompute();
            if (count == 1)
                return GetPoint(0);

            // Spline backbone (R6): same contract, curve-based answer.
            if (_splineReady)
                return SampleSplineByDistance(distance, out tangent);

            if (distance <= 0f)
            {
                Vector3 t0 = GetPoint(1) - GetPoint(0);
                if (t0.sqrMagnitude > 0.0001f) tangent = t0.normalized;
                return GetPoint(0);
            }
            if (distance >= _length)
            {
                Vector3 tE = GetPoint(count - 1) - GetPoint(count - 2);
                if (tE.sqrMagnitude > 0.0001f) tangent = tE.normalized;
                return GetPoint(count - 1);
            }

            for (int i = 1; i < count; i++)
            {
                if (distance <= _cumulativeDistances[i])
                {
                    float segStart = _cumulativeDistances[i - 1];
                    float segLen = _cumulativeDistances[i] - segStart;
                    float t = segLen > 0.0001f ? (distance - segStart) / segLen : 0f;
                    Vector3 a = GetPoint(i - 1);
                    Vector3 b = GetPoint(i);
                    Vector3 seg = b - a;
                    if (seg.sqrMagnitude > 0.0001f) tangent = seg.normalized;
                    return Vector3.Lerp(a, b, t);
                }
            }

            Vector3 tLast = GetPoint(count - 1) - GetPoint(count - 2);
            if (tLast.sqrMagnitude > 0.0001f) tangent = tLast.normalized;
            return GetPoint(count - 1);
        }

        /// <summary>
        /// Cumulative distance in metres from the start of the route to the given
        /// waypoint — measured along the curve when the spline backbone is on, so
        /// callers that mix DistanceAlongAt with SamplePosition stay consistent.
        /// </summary>
        public float DistanceAlongAt(int index)
        {
            if (_cumulativeDistances == null || PointCount != _cumulativeDistances.Length)
                Recompute();

            if (_cumulativeDistances == null || index < 0 || index >= _cumulativeDistances.Length)
                return 0f;

            // Knot distances are located during the bake — they are NOT at a fixed
            // stride through the table, because the sampling is uniform in arc
            // length rather than in knot index (see ComputeKnotDistances).
            if (_splineReady && _knotDistances != null && index < _knotDistances.Length)
                return _knotDistances[index];

            return _cumulativeDistances[index];
        }

        private void Awake()
        {
            Recompute();
        }

        private void OnValidate()
        {
            Recompute();
        }

        /// <summary>
        /// Rebuild the cached geometry — cumulative distances, total length and (when
        /// <see cref="useSpline"/> is on) the spline arc table. Guarded by a
        /// dirty-check on a hash of the waypoint positions plus the spline settings,
        /// because this is called from <c>Awake</c>, <c>OnValidate</c> AND every
        /// <c>OnDrawGizmos</c>: without the guard the Scene view reallocates the
        /// distance array every frame.
        /// </summary>
        private void Recompute()
        {
            int hash = ComputeGeometryHash();
            if (_built && hash == _geometryHash)
                return;

            _geometryHash = hash;
            _built = true;

            RebuildLinear();

            if (useSpline)
                RebuildSpline();
            else
                ClearSpline();
        }

        /// <summary>
        /// Allocation-free hash over the waypoint positions and the spline settings.
        /// Any change here means the cached geometry is stale.
        /// </summary>
        private int ComputeGeometryHash()
        {
            int count = PointCount;
            unchecked
            {
                int h = 17;
                h = h * 31 + count;
                h = h * 31 + (useSpline ? 1 : 0);
                h = h * 31 + samplesPerCurve;
                if (tangentPins != null)
                {
                    for (int i = 0; i < tangentPins.Length; i++)
                    {
                        h = h * 31 + tangentPins[i].knotIndex;
                        Vector3 t = tangentPins[i].tangentOut;
                        h = h * 31 + t.x.GetHashCode();
                        h = h * 31 + t.y.GetHashCode();
                        h = h * 31 + t.z.GetHashCode();
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    Transform wp = waypoints[i];
                    if (wp == null)
                    {
                        h *= 31;
                        continue;
                    }
                    Vector3 p = wp.position;
                    h = h * 31 + p.x.GetHashCode();
                    h = h * 31 + p.y.GetHashCode();
                    h = h * 31 + p.z.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>Piecewise-linear cumulative distances and total length (the pre-spline behaviour).</summary>
        private void RebuildLinear()
        {
            int count = PointCount;
            if (_cumulativeDistances == null || _cumulativeDistances.Length != count)
                _cumulativeDistances = new float[count];
            _length = 0f;

            if (count == 0)
                return;

            _cumulativeDistances[0] = 0f;
            for (int i = 1; i < count; i++)
            {
                float segment = 0f;
                if (waypoints[i] != null && waypoints[i - 1] != null)
                    segment = Vector3.Distance(waypoints[i - 1].position, waypoints[i].position);

                _length += segment;
                _cumulativeDistances[i] = _length;
            }
        }

        private void ClearSpline()
        {
            _splineReady = false;
        }

        /// <summary>
        /// Bake the AutoSmooth spline through the waypoints into the arc-length table
        /// and adopt its length (R6). Knot positions are WORLD space, matching every
        /// other read in this class, so the spline needs no transform baked in.
        /// </summary>
        private void RebuildSpline()
        {
            _splineReady = false;

            int count = PointCount;
            if (count < 2)
                return;

            var spline = new Spline();
            for (int i = 0; i < count; i++)
            {
                Transform wp = waypoints[i];
                Vector3 p = wp != null ? wp.position : Vector3.zero;
                // AutoSmooth derives each knot's tangent from its NEIGHBOURS — which
                // is exactly why the two routes' duplicated merge knots diverge until
                // R7 pins them. Tangents are passed as zero because AutoSmooth
                // recomputes them; the explicit 3-arg ctor is the documented one.
                spline.Add(new BezierKnot(new float3(p.x, p.y, p.z), float3.zero, float3.zero),
                           TangentMode.AutoSmooth);
            }
            spline.Closed = false;

            ApplyTangentPins(spline);

            int step = Mathf.Max(2, samplesPerCurve);
            int samples = (count - 1) * step;

            if (_arcPoints == null || _arcPoints.Length != samples + 1)
            {
                _arcPoints = new Vector3[samples + 1];
                _arcDistances = new float[samples + 1];
            }

            // NativeSpline gives allocation-free evaluation while the table is filled,
            // and is disposed in the same scope — nothing native outlives this call.
            float nativeLength;
            using (var native = new NativeSpline(spline, Allocator.Temp))
            {
                nativeLength = native.GetLength();
                for (int j = 0; j <= samples; j++)
                {
                    float3 p = native.EvaluatePosition(j / (float)samples);
                    _arcPoints[j] = new Vector3(p.x, p.y, p.z);
                }
            }

            _arcDistances[0] = 0f;
            float total = 0f;
            for (int j = 1; j <= samples; j++)
            {
                total += Vector3.Distance(_arcPoints[j - 1], _arcPoints[j]);
                _arcDistances[j] = total;
            }

            _length = total;
            ComputeKnotDistances(count, samples);
            _splineReady = true;

            // Cross-check the chord-summed table against the package's own arc length.
            // Chord sums always UNDER-read a curve, and Length feeds the balance model
            // (roadmap R10), so a table too coarse to measure the route honestly must
            // surface as a warning rather than a quiet bias. Raise samplesPerCurve if
            // this fires.
            if (nativeLength > 0.001f &&
                Mathf.Abs(total - nativeLength) / nativeLength > ArcTableTolerance)
            {
                Debug.LogWarning(
                    $"[PathRoute] '{name}' arc table under-reads the curve: table {total:0.###} m vs " +
                    $"spline {nativeLength:0.###} m ({(nativeLength - total) / nativeLength:P2}). " +
                    $"Raise samplesPerCurve (currently {samplesPerCurve}) — Length feeds the balance model.",
                    this);
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only (R7 tooling): the outgoing tangent AutoSmooth derives for a
        /// knot, with no pins applied. This lets the pinning tool copy one route's
        /// NATURAL tangent onto the other, instead of guessing at the package's
        /// tension constant — so the donor route's shape is preserved exactly and
        /// only the adopting route moves. Returns false when the index is invalid.
        /// </summary>
        public bool TryGetAutoSmoothTangentOut(int knotIndex, out Vector3 tangentOut)
        {
            tangentOut = Vector3.zero;

            int count = PointCount;
            if (count < 2 || knotIndex < 0 || knotIndex >= count)
                return false;

            var spline = new Spline();
            for (int i = 0; i < count; i++)
            {
                Transform wp = waypoints[i];
                Vector3 p = wp != null ? wp.position : Vector3.zero;
                spline.Add(new BezierKnot(new float3(p.x, p.y, p.z), float3.zero, float3.zero),
                           TangentMode.AutoSmooth);
            }
            spline.Closed = false;

            // BezierKnot tangents are stored in the knot's LOCAL frame — AutoSmooth
            // aligns the knot's rotation so local +Z is the travel direction and puts
            // the magnitude there. Rotate into world space before handing it out, or
            // two routes with different approach directions would appear to share a
            // tangent while pointing different ways.
            BezierKnot knot = spline[knotIndex];
            float3 world = math.mul(knot.Rotation, knot.TangentOut);
            tangentOut = new Vector3(world.x, world.y, world.z);
            return true;
        }
#endif

        /// <summary>
        /// Locate each knot's arc distance along the baked curve.
        ///
        /// The table CANNOT be indexed as knot·stride: <c>EvaluatePosition(t)</c>
        /// walks the curve at uniform ARC LENGTH, not uniform knot index, so a knot
        /// sits wherever its geometry puts it (on the live west route, knot 2 is at
        /// ~30 m — sample ~41 of 208 — not at sample 32). Each knot is therefore
        /// found by scanning forward from the previous knot's sample for the closest
        /// point, then refining onto the neighbouring segment. Forward-only scanning
        /// is what keeps this correct on a route that folds back on itself: the
        /// hairpin legs run ~10 m apart, so a nearest-point search over the whole
        /// table could otherwise match the wrong pass.
        /// </summary>
        private void ComputeKnotDistances(int count, int samples)
        {
            if (_knotDistances == null || _knotDistances.Length != count)
                _knotDistances = new float[count];

            _knotDistances[0] = 0f;
            int from = 0;

            for (int k = 1; k < count; k++)
            {
                Transform wp = waypoints[k];
                Vector3 target = wp != null ? wp.position : Vector3.zero;

                int best = from;
                float bestSq = float.MaxValue;
                for (int j = from; j <= samples; j++)
                {
                    float sq = (_arcPoints[j] - target).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = j;
                    }
                }

                _knotDistances[k] = RefineKnotDistance(best, samples, target);
                from = best;
            }

            // The final knot is the end of the curve by construction.
            _knotDistances[count - 1] = _arcDistances[samples];
        }

        /// <summary>
        /// Sub-sample refinement: project the knot onto whichever segment adjoining
        /// the nearest sample it actually falls on, so the returned distance is
        /// accurate to well under a centimetre rather than half a sample spacing.
        /// </summary>
        private float RefineKnotDistance(int best, int samples, Vector3 target)
        {
            float bestDist = _arcDistances[best];
            float bestSq = (_arcPoints[best] - target).sqrMagnitude;

            for (int n = -1; n <= 1; n += 2)
            {
                int other = best + n;
                if (other < 0 || other > samples)
                    continue;

                Vector3 a = _arcPoints[best];
                Vector3 ab = _arcPoints[other] - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-9f)
                    continue;

                float t = Mathf.Clamp01(Vector3.Dot(target - a, ab) / len2);
                Vector3 proj = a + ab * t;
                float sq = (proj - target).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    bestDist = Mathf.Lerp(_arcDistances[best], _arcDistances[other], t);
                }
            }
            return bestDist;
        }

        /// <summary>
        /// Force the authored outgoing tangents onto their knots (R7), overriding
        /// what AutoSmooth derived from the neighbours.
        ///
        /// Only the OUT tangent is pinned. The curve leaving the merge knot — the
        /// shared tail — depends on this knot's out tangent and the next knot's in
        /// tangent; every knot after the merge has identical neighbours on both
        /// routes, so their AutoSmooth tangents already match. Pinning the out
        /// tangent to the same value on both routes is therefore sufficient to make
        /// the entire shared tail identical. The IN tangent keeps its AutoSmooth
        /// value because the approach leg is NOT shared and should keep its natural
        /// shape — Broken mode is what lets the two differ.
        /// </summary>
        private void ApplyTangentPins(Spline spline)
        {
            if (tangentPins == null || tangentPins.Length == 0)
                return;

            for (int i = 0; i < tangentPins.Length; i++)
            {
                int k = tangentPins[i].knotIndex;
                if (k < 0 || k >= spline.Count)
                    continue;

                // Read first: AutoSmooth has already written its computed tangents
                // into the knot, and we want to keep the incoming one.
                BezierKnot knot = spline[k];
                Vector3 pin = tangentPins[i].tangentOut;

                // Tangents are stored in the knot's LOCAL frame (rotated by
                // knot.Rotation), and AutoSmooth gives each route a different
                // rotation here because their approach directions differ. Writing the
                // same local value into both routes would therefore point them
                // different ways in world space — which is exactly the divergence
                // this pin exists to remove. So normalise the frame: carry the
                // incoming tangent into world space and store an identity rotation,
                // leaving Position + world tangents as the only things that shape the
                // curve. Nothing here reads knot rotation (no up-vector evaluation).
                float3 worldIn = math.mul(knot.Rotation, knot.TangentIn);

                spline.SetTangentMode(k, TangentMode.Broken);
                spline.SetKnot(k, new BezierKnot(
                    knot.Position,
                    worldIn,
                    new float3(pin.x, pin.y, pin.z),
                    quaternion.identity));
            }
        }

        /// <summary>
        /// Position at an arc length along the baked curve, with the travel tangent.
        /// Binary search over the arc table, then lerp — allocation-free and O(log n),
        /// and it honours the same clamped-ends contract as the polyline path.
        /// </summary>
        private Vector3 SampleSplineByDistance(float distance, out Vector3 tangent)
        {
            int last = _arcPoints.Length - 1;

            if (distance <= 0f)
            {
                tangent = TableTangent(0, 1);
                return _arcPoints[0];
            }
            if (distance >= _length)
            {
                tangent = TableTangent(last - 1, last);
                return _arcPoints[last];
            }

            int lo = 0, hi = last;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_arcDistances[mid] <= distance) lo = mid;
                else hi = mid;
            }

            float seg = _arcDistances[hi] - _arcDistances[lo];
            float t = seg > 0.0001f ? (distance - _arcDistances[lo]) / seg : 0f;
            tangent = TableTangent(lo, hi);
            return Vector3.Lerp(_arcPoints[lo], _arcPoints[hi], t);
        }

        private Vector3 TableTangent(int a, int b)
        {
            Vector3 d = _arcPoints[b] - _arcPoints[a];
            return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            int count = PointCount;
            if (count == 0)
                return;

            // Keep cached distances fresh while editing in the Scene view. Cheap:
            // the dirty-check inside early-outs unless a waypoint actually moved.
            Recompute();

            // With the spline on, dim the raw polyline so the two read apart (R6).
            Color polyColor = _splineReady
                ? new Color(lineColor.r, lineColor.g, lineColor.b, lineColor.a * 0.35f)
                : lineColor;
            Gizmos.color = polyColor;

            for (int i = 0; i < count; i++)
            {
                Transform wp = waypoints[i];
                if (wp == null)
                    continue;

                Vector3 pos = wp.position;

                // Small sphere at each waypoint (knot).
                Gizmos.DrawSphere(pos, pointRadius);

                // Line to the next waypoint.
                if (i + 1 < count && waypoints[i + 1] != null)
                    Gizmos.DrawLine(pos, waypoints[i + 1].position);

                // Cumulative distance label (metres from start).
                if (drawDistanceLabels)
                {
                    float dist = DistanceAlongAt(i);
                    Handles.Label(pos + Vector3.up * (pointRadius + 0.35f), $"{dist:0.##} m");
                }
            }

            // Spline overlay: the curve the enemies actually walk when useSpline is
            // on, drawn over the dimmed polyline for a direct comparison (R6).
            if (_splineReady && _arcPoints != null && _arcPoints.Length > 1)
            {
                Gizmos.color = splineColor;
                for (int i = 1; i < _arcPoints.Length; i++)
                    Gizmos.DrawLine(_arcPoints[i - 1], _arcPoints[i]);
            }

            if (drawLaneBand && LaneCount > 1)
            {
                // Outer lane edges: draw each outermost lane as a polyline so the
                // authored width the crowd actually uses is visible.
                Gizmos.color = new Color(lineColor.r, lineColor.g, lineColor.b, 0.35f);
                DrawLanePolyline(0);
                DrawLanePolyline(LaneCount - 1);
            }
        }

        /// <summary>Draw one lane's polyline by sampling the centreline at each waypoint distance.</summary>
        private void DrawLanePolyline(int lane)
        {
            int count = PointCount;
            Vector3 prev = Vector3.zero;
            bool has = false;
            for (int i = 0; i < count; i++)
            {
                float d = DistanceAlongAt(i);
                Vector3 p = LanePosition(d, lane, out _);
                if (has) Gizmos.DrawLine(prev, p);
                prev = p;
                has = true;
            }
        }
#endif
    }
}
