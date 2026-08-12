using UnityEngine;
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
        [Tooltip("Ordered list of waypoints. The route runs from index 0 to the last entry.")]
        [SerializeField] private Transform[] waypoints;

        [Header("Lanes (1-D car-following model)")]
        [Tooltip("Number of parallel lanes on this ground track. Enemies follow the centreline at a fixed lateral offset per lane so same-track units never share a line. 2 is the default for ground routes.")]
        [SerializeField] private int laneCount = 2;

        [Tooltip("Half-width in metres of the lane band: the outermost lanes sit at ±this. Lanes are spread evenly across [-laneHalfWidth, +laneHalfWidth].")]
        [SerializeField] private float laneHalfWidth = 0.9f;

        [Tooltip("Extra longitudinal spacing (metres) added on top of the two body radii between consecutive same-lane units. Scale up for tightly-curved routes so inner-lane world spacing stays honest.")]
        [SerializeField] private float spacingBuffer = 0.4f;

        [Header("Gizmo")]
        [SerializeField] private Color lineColor = new Color(0.2f, 1f, 0.6f, 1f);
        [SerializeField] private float pointRadius = 0.25f;

        [Tooltip("Draw the swept lane band and the tower-clearance envelope in the Scene view.")]
        [SerializeField] private bool drawLaneBand = true;

        // Cached cumulative distance from start to each waypoint (metres).
        private float[] _cumulativeDistances;
        private float _length;

        /// <summary>Number of waypoints in the route.</summary>
        public int PointCount => waypoints != null ? waypoints.Length : 0;

        /// <summary>Total length of the route in metres (cached, recomputed in OnValidate).</summary>
        public float Length => _length;

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

        /// <summary>Cumulative distance in metres from the start of the route to the given waypoint.</summary>
        public float DistanceAlongAt(int index)
        {
            if (_cumulativeDistances == null || PointCount != _cumulativeDistances.Length)
                Recompute();

            if (_cumulativeDistances == null || index < 0 || index >= _cumulativeDistances.Length)
                return 0f;

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

        /// <summary>Recomputes the cached cumulative distances and total length.</summary>
        private void Recompute()
        {
            int count = PointCount;
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

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            int count = PointCount;
            if (count == 0)
                return;

            // Keep cached distances fresh while editing in the Scene view.
            Recompute();

            Gizmos.color = lineColor;

            for (int i = 0; i < count; i++)
            {
                Transform wp = waypoints[i];
                if (wp == null)
                    continue;

                Vector3 pos = wp.position;

                // Small sphere at each waypoint.
                Gizmos.DrawSphere(pos, pointRadius);

                // Line to the next waypoint.
                if (i + 1 < count && waypoints[i + 1] != null)
                    Gizmos.DrawLine(pos, waypoints[i + 1].position);

                // Cumulative distance label (metres from start).
                float dist = DistanceAlongAt(i);
                Handles.Label(pos + Vector3.up * (pointRadius + 0.35f), $"{dist:0.##} m");
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
