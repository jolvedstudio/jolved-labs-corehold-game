using System.Collections.Generic;
using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Core
{
    /// <summary>
    /// The single owner of enemy movement scheduling — the 1-D car-following
    /// traffic manager (GDD navigation redesign). It is the ONLY thing that decides
    /// where along a track each enemy sits; individual movers never negotiate with
    /// neighbours.
    ///
    /// Model (why it cannot deadlock or overlap):
    ///   • Each track (a ground <see cref="PathRoute"/>, or the shared air corridor)
    ///     has a fixed number of lanes. Each (track, lane) keeps an ORDERED list of
    ///     movers, index 0 = closest to the Core (front). Because progress is
    ///     monotonic and overtaking / lane-changes are forbidden, list order equals
    ///     progress order for the life of the list — no sorting is ever needed.
    ///   • Each frame, movers are ticked FRONT-TO-BACK per lane. A mover advances by
    ///     its desired speed, then is hard-clamped so its progress never exceeds
    ///     <c>leader.Frontness − minSpacing</c>. The clamp is the unconditional
    ///     overlap guarantee (frame-rate / order independent — clamping against a
    ///     leader's pre-update progress is merely conservative since progress only
    ///     ever increases).
    ///   • Each lane's front unit has no leader ⇒ always advances ⇒ always reaches
    ///     the Core (a sink). The follow-chains are acyclic and terminate at the
    ///     sink, so every unit provably drains in bounded time — a wave can always
    ///     complete, so it can never block later waves.
    ///
    /// Wide bodies (radius too large to clear an adjacent lane) occupy EVERY lane of
    /// their track, so every lane queues behind them instead of clipping through.
    ///
    /// Air units share one corridor track ordered by remaining-distance-to-Core:
    /// two air units at remaining r₁, r₂ are ≥ |r₁−r₂| apart in world space, so
    /// spacing on that scalar guarantees no world overlap.
    /// </summary>
    [DisallowMultipleComponent]
    public class RouteTraffic : MonoBehaviour
    {
        // ---- Tuning ----
        [Tooltip("Longitudinal slow-down band (metres): a follower eases from full speed to a stop across this distance before the hard min-spacing clamp. Purely for smoothness; the clamp is the safety.")]
        [SerializeField] private float slowBand = 1.5f;

        [Tooltip("Body radius (m) at/above which a unit is treated as WIDE and occupies every lane of its track. Also the assumed largest-neighbour radius when classifying wide bodies.")]
        [SerializeField] private float wideBodyRadius = 0.9f;

        [Tooltip("Extra longitudinal spacing (m) between air units, on top of the two body radii.")]
        [SerializeField] private float airSpacingBuffer = 0.7f;

        // ---- Singleton ----
        private static RouteTraffic _instance;

        /// <summary>Scene singleton; auto-created on first access so movers can always register.</summary>
        public static RouteTraffic Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<RouteTraffic>();
                    if (_instance == null)
                    {
                        var go = new GameObject("RouteTraffic");
                        _instance = go.AddComponent<RouteTraffic>();
                    }
                }
                return _instance;
            }
        }

        /// <summary>Non-creating accessor for teardown paths (OnDisable).</summary>
        public static RouteTraffic InstanceOrNull => _instance;

        // ---- Track model ----
        private class Track
        {
            public PathRoute route;          // null for the air corridor
            public bool isAir;
            public int laneCount;
            public List<List<EnemyMover>> lanes; // lanes[k] ordered front(0)->back
            public int roundRobin;
        }

        private class Entry
        {
            public Track track;
            public readonly List<int> lanes = new List<int>(); // lane indices this mover occupies
            public bool wide;
            public int tickStamp;
        }

        private readonly List<Track> _tracks = new List<Track>();
        private readonly Dictionary<PathRoute, Track> _groundTracks = new Dictionary<PathRoute, Track>();
        private Track _airTrack;
        private readonly Dictionary<EnemyMover, Entry> _entries = new Dictionary<EnemyMover, Entry>();
        private readonly List<EnemyMover> _finishedScratch = new List<EnemyMover>();
        private int _tickStamp;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        // ------------------------------------------------------------------
        // Registration
        // ------------------------------------------------------------------

        /// <summary>Add a mover to its track, assigning lane(s) and its render offset.</summary>
        public void Register(EnemyMover mover)
        {
            if (mover == null)
                return;
            if (_entries.ContainsKey(mover))
                Unregister(mover);

            Track track = ResolveTrack(mover);
            if (track == null)
                return;

            float radius = mover.BodyRadius;
            // Convoy units (R20) go through the wide-body path deliberately: every
            // lane occupied, centreline render — which IS single-file ordering.
            bool wide = !track.isAir && track.laneCount > 1 &&
                        (mover.ForceSingleLane || IsWide(track, radius));

            var entry = new Entry { track = track, wide = wide };

            if (wide)
            {
                for (int k = 0; k < track.laneCount; k++)
                    entry.lanes.Add(k);
                mover.SetRenderLaneOffset(0f); // wide bodies ride the centreline
            }
            else
            {
                int lane = track.isAir ? 0 : ChooseLane(track);
                entry.lanes.Add(lane);
                mover.SetRenderLaneOffset(track.isAir ? 0f : track.route.LaneOffset(lane));
            }

            foreach (int lane in entry.lanes)
                InsertSorted(track.lanes[lane], mover);

            _entries[mover] = entry;
        }

        /// <summary>Remove a mover from every lane list it occupies.</summary>
        public void Unregister(EnemyMover mover)
        {
            if (mover == null)
                return;
            if (!_entries.TryGetValue(mover, out Entry entry))
                return;

            foreach (int lane in entry.lanes)
            {
                if (lane >= 0 && lane < entry.track.lanes.Count)
                    entry.track.lanes[lane].Remove(mover);
            }
            _entries.Remove(mover);
        }

        private Track ResolveTrack(EnemyMover mover)
        {
            if (mover.IsAir)
            {
                if (_airTrack == null)
                {
                    _airTrack = new Track
                    {
                        route = null,
                        isAir = true,
                        laneCount = 1,
                        lanes = new List<List<EnemyMover>> { new List<EnemyMover>() }
                    };
                    _tracks.Add(_airTrack);
                }
                return _airTrack;
            }

            PathRoute route = mover.Route;
            if (route == null)
                return null;

            if (!_groundTracks.TryGetValue(route, out Track track))
            {
                int lc = Mathf.Max(1, route.LaneCount);
                track = new Track
                {
                    route = route,
                    isAir = false,
                    laneCount = lc,
                    lanes = new List<List<EnemyMover>>(lc)
                };
                for (int k = 0; k < lc; k++)
                    track.lanes.Add(new List<EnemyMover>());
                _groundTracks[route] = track;
                _tracks.Add(track);
            }
            return track;
        }

        private bool IsWide(Track track, float radius)
        {
            if (track.laneCount <= 1)
                return false;
            float laneSpacing = 2f * track.route.LaneHalfWidth / (track.laneCount - 1);
            return radius > (laneSpacing - wideBodyRadius) + 0.0001f;
        }

        /// <summary>Lane whose rearmost unit is furthest along (most entrance room); ties round-robin.</summary>
        private int ChooseLane(Track track)
        {
            float best = float.NegativeInfinity;
            var ties = new List<int>();
            for (int k = 0; k < track.laneCount; k++)
            {
                var list = track.lanes[k];
                float tailFront = list.Count == 0 ? float.PositiveInfinity : list[list.Count - 1].Frontness;
                if (tailFront > best + 0.001f)
                {
                    best = tailFront;
                    ties.Clear();
                    ties.Add(k);
                }
                else if (tailFront >= best - 0.001f)
                {
                    ties.Add(k);
                }
            }
            if (ties.Count == 0)
                return 0;
            int pick = ties[track.roundRobin % ties.Count];
            track.roundRobin++;
            return pick;
        }

        /// <summary>Insert keeping the list ordered by Frontness descending (index 0 = front).</summary>
        private static void InsertSorted(List<EnemyMover> list, EnemyMover mover)
        {
            int i = 0;
            while (i < list.Count && list[i].Frontness > mover.Frontness)
                i++;
            list.Insert(i, mover);
        }

        // ------------------------------------------------------------------
        // Admission (used by WaveManager before spawning)
        // ------------------------------------------------------------------

        /// <summary>
        /// True if a unit of the given radius can enter the track now (its entrance
        /// is clear by min-spacing). Ground: pass the route. Air: pass null route and
        /// the unit's entrance frontness (= −distanceToCore). Wide bodies require
        /// every lane clear.
        /// </summary>
        public bool CanAdmit(PathRoute route, bool isAir, float radius, float entranceFrontness)
        {
            Track track = isAir ? _airTrack : (route != null && _groundTracks.TryGetValue(route, out var t) ? t : null);

            // No track yet ⇒ nothing there ⇒ always room. (First unit of the wave.)
            if (track == null)
                return true;

            float buffer = isAir ? airSpacingBuffer : (route != null ? route.SpacingBuffer : 0.4f);

            if (!isAir && track.laneCount > 1 && IsWide(track, radius))
            {
                for (int k = 0; k < track.laneCount; k++)
                    if (!LaneHasEntranceRoom(track.lanes[k], radius, entranceFrontness, buffer))
                        return false;
                return true;
            }

            if (isAir || track.laneCount == 1)
                return LaneHasEntranceRoom(track.lanes[0], radius, entranceFrontness, buffer);

            // Normal ground unit: room in ANY lane is enough.
            for (int k = 0; k < track.laneCount; k++)
                if (LaneHasEntranceRoom(track.lanes[k], radius, entranceFrontness, buffer))
                    return true;
            return false;
        }

        private bool LaneHasEntranceRoom(List<EnemyMover> list, float radius, float entranceFrontness, float buffer)
        {
            if (list.Count == 0)
                return true;
            var tail = list[list.Count - 1];
            float minSpacing = radius + tail.BodyRadius + buffer;
            return (tail.Frontness - entranceFrontness) >= minSpacing;
        }

        /// <summary>
        /// Conservative total concurrent capacity across all ground tracks + air,
        /// computed with the largest spawnable radius. Used as the derived hard
        /// ceiling that replaces the old magic 14-cap.
        /// </summary>
        public int DerivedCapacity(float largestRadius)
        {
            int total = 0;
            foreach (var kv in _groundTracks)
                total += kv.Value.route.TotalCapacity(largestRadius);
            if (_airTrack != null && _airTrack.route == null)
            {
                // Air corridor length is not a route; estimate generously.
                total += 8;
            }
            return Mathf.Max(1, total);
        }

        // ------------------------------------------------------------------
        // Ticking
        // ------------------------------------------------------------------

        /// <summary>Advance all movers one step. Public for the edit-mode determinism test.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f)
                return;

            _tickStamp++;
            _finishedScratch.Clear();

            for (int ti = 0; ti < _tracks.Count; ti++)
            {
                Track track = _tracks[ti];
                float buffer = track.isAir ? airSpacingBuffer
                    : (track.route != null ? track.route.SpacingBuffer : 0.4f);

                for (int lane = 0; lane < track.laneCount; lane++)
                {
                    var list = track.lanes[lane];
                    for (int i = 0; i < list.Count; i++)
                    {
                        var m = list[i];
                        if (m == null)
                            continue;
                        if (!_entries.TryGetValue(m, out Entry e))
                            continue;
                        if (e.tickStamp == _tickStamp) // wide body already ticked in another lane
                            continue;
                        e.tickStamp = _tickStamp;

                        AdvanceMover(track, e, m, buffer, dt);
                    }
                }
            }

            // Remove finished movers from all lanes after the full pass.
            for (int i = 0; i < _finishedScratch.Count; i++)
                Unregister(_finishedScratch[i]);
        }

        private void AdvanceMover(Track track, Entry entry, EnemyMover m, float buffer, float dt)
        {
            // Binding cap = the closest leader across all lanes this mover occupies.
            float cap = float.PositiveInfinity;
            bool hasLeader = false;

            for (int li = 0; li < entry.lanes.Count; li++)
            {
                var list = track.lanes[entry.lanes[li]];
                int idx = list.IndexOf(m);
                if (idx > 0)
                {
                    var leader = list[idx - 1];
                    float minSpacing = m.BodyRadius + leader.BodyRadius + buffer;
                    float laneCap = leader.Frontness - minSpacing;
                    if (laneCap < cap)
                        cap = laneCap;
                    hasLeader = true;
                }
            }

            float scale = 1f;
            if (hasLeader && slowBand > 0.0001f)
                scale = Mathf.Clamp01((cap - m.Frontness) / slowBand);

            float newFront = m.Frontness + m.DesiredSpeed * dt * scale;

            if (hasLeader)
                newFront = Mathf.Min(newFront, cap);       // hard positional clamp (overlap guarantee)
            if (newFront < m.Frontness)
                newFront = m.Frontness;                    // never reverse

            bool finished = m.PlaceAtFrontness(newFront, dt);
            if (finished)
                _finishedScratch.Add(m);
        }

        // ------------------------------------------------------------------
        // Test / debug helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Assert the two structural invariants: every mover sits in exactly the
        /// lanes its entry records, and each lane list is ordered by Frontness
        /// descending. Returns null if OK, else a description of the first violation.
        /// </summary>
        public string CheckInvariants()
        {
            foreach (var kv in _entries)
            {
                var m = kv.Key;
                var e = kv.Value;
                foreach (int lane in e.lanes)
                {
                    if (lane < 0 || lane >= e.track.lanes.Count)
                        return $"{m.name}: bad lane index {lane}";
                    if (!e.track.lanes[lane].Contains(m))
                        return $"{m.name}: entry claims lane {lane} but list does not contain it";
                }
            }
            for (int ti = 0; ti < _tracks.Count; ti++)
            {
                var track = _tracks[ti];
                for (int lane = 0; lane < track.laneCount; lane++)
                {
                    var list = track.lanes[lane];
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i - 1].Frontness < list[i].Frontness - 0.0001f)
                            return $"track {ti} lane {lane}: order broken at {i} ({list[i - 1].Frontness:0.###} < {list[i].Frontness:0.###})";
                    }
                }
            }
            return null;
        }

        /// <summary>Live mover count known to the traffic manager.</summary>
        public int TrackedCount => _entries.Count;

        /// <summary>
        /// The solver's true spacing guarantee: for every pair of CONSECUTIVE units
        /// in the same lane, their arc-length gap must be ≥ minSpacing. Returns the
        /// worst (gap − minSpacing) seen; ≥ 0 means the guarantee holds. (World
        /// distance between arbitrary pairs is a function of level geometry — folded
        /// routes, crossing routes — not of the solver, so it is not asserted here.)
        /// </summary>
        public float WorstConsecutiveSlack()
        {
            float worst = float.PositiveInfinity;
            for (int ti = 0; ti < _tracks.Count; ti++)
            {
                var track = _tracks[ti];
                float buffer = track.isAir ? airSpacingBuffer
                    : (track.route != null ? track.route.SpacingBuffer : 0.4f);
                for (int lane = 0; lane < track.laneCount; lane++)
                {
                    var list = track.lanes[lane];
                    for (int i = 1; i < list.Count; i++)
                    {
                        var lead = list[i - 1];
                        var follow = list[i];
                        float minSpacing = lead.BodyRadius + follow.BodyRadius + buffer;
                        float gap = lead.Frontness - follow.Frontness;
                        float slack = gap - minSpacing;
                        if (slack < worst) worst = slack;
                    }
                }
            }
            return worst == float.PositiveInfinity ? 0f : worst;
        }
    }
}
