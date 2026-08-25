using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Themed prop placement (R28): dress a generated level from the drawn theme's
/// <see cref="EnvPack"/>, then prove the dressing didn't blind a pad.
///
/// Every test uses PLACED dimensions — the pack's scale-1 metadata multiplied
/// by the applied scale — and every placed prop is stamped with a
/// <see cref="PlacedProp"/> carrying them, so the scene stays verifiable after
/// the generator is gone.
///
/// Placement is a deterministic rejection sampler: candidate positions come
/// from a seeded xorshift stream (FNV-1a(seed, "dressing")), tested against
/// route clearance, pad keep-outs, the core, the field margin and previously
/// placed props. Same seed ⇒ same dressing, prop for prop.
///
/// After placing, the OCCLUSION RE-RUN (roadmap stage 9): every pad is
/// recounted through the gizmo's sight-line-aware walk. If a pad fell below
/// its class requirement, the prop blocking the most spans is REMOVED and the
/// recount repeats. Removing dressing is legitimate self-repair — dressing is
/// decoration; the geometry gates (R29) stay reject-and-reseed.
/// </summary>
public static class PropPlacer
{
    private const float LaneHalfWidth = 0.9f;
    private const float MaxBodyRadius = 1.35f;

    /// <summary>Extra breathing room between a prop's edge and anything it must clear.</summary>
    private const float Margin = 0.5f;

    /// <summary>Pads own their pocket: props keep this far off a pad, plus their radius.</summary>
    private const float PadKeepOut = 6f;
    private const float PadKeepOutInFold = 3f;

    private const int MaxAttemptsPerProp = 24;
    private const int MaxOcclusionRemovals = 10;

    // Placement counts per role: a floor plus an area-scaled component, so a
    // bigger field gets proportionally more dressing. [TUNE]
    private const float FieldAreaReference = 130f * 75f;

    private struct Rng
    {
        private uint _state;
        public Rng(uint seed) { _state = seed == 0 ? 2463534242u : seed; }
        public uint NextU()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
        public float Range(float min, float max) => min + (NextU() / 4294967296f) * (max - min);
    }

    private class Placed
    {
        public Vector3 pos;
        public float radius;
        public float height;
    }

    /// <summary>
    /// Dress the level. Returns a summary; occlusion repair happens inside, and
    /// <paramref name="stillBlocked"/> reports any pad the repair could not
    /// save — which fails gate 2b and discards the seed.
    /// </summary>
    public static string Dress(LevelBlueprint blueprint, EnvPack theme, Transform levelContainer,
                               List<PathRoute> routes, Vector3 corePos, out List<string> stillBlocked)
    {
        var log = new StringBuilder();
        stillBlocked = new List<string>();

        var rng = new Rng(GenerationPipeline.Fnv1a(blueprint.randomSeed, "dressing"));
        float halfW = blueprint.playfieldSize.x * 0.5f - 4f;
        float halfD = blueprint.playfieldSize.y * 0.5f - 4f;
        float areaScale = (blueprint.playfieldSize.x * blueprint.playfieldSize.y) / FieldAreaReference;

        // Route curve samples once, for clearance tests.
        var routeSamples = new List<Vector3>();
        foreach (PathRoute route in routes)
            for (float d = 0f; d <= route.Length; d += 0.5f)
            {
                Vector3 p = route.SamplePosition(d, out _);
                p.y = 0f;
                routeSamples.Add(p);
            }

        var pads = SceneQuery.InActiveScene<HardpointCoverageGizmo>()
            .OrderBy(g => g.name, System.StringComparer.Ordinal).ToArray();

        // The camera is FIXED (38° pitch), so whether a prop hides a pad from the
        // player is decided at placement time and never changes. One reusable
        // single-element list keeps the per-attempt test allocation-free.
        Camera cam = SceneQuery.FirstInActiveScene<Camera>();
        var probe = new List<HardpointCoverageGizmo.Occluder> { default };

        // ROUTE OCCLUSION BUDGET (R28). Pads may never be hidden; the route may
        // be, up to a budget — see RouteVisibility for why the two differ. Spent
        // incrementally: each candidate is charged only for route it hides that
        // something else is not hiding already.
        List<Vector3> routeVisSamples = RouteVisibility.SampleRoutes(routes);
        var routeHidden = new bool[routeVisSamples.Count];
        float hiddenBudget = RouteVisibility.BudgetMetres(routeVisSamples);
        float hiddenMetres = 0f;
        var pendingHidden = new List<int>();

        var dressing = new GameObject("Dressing");
        dressing.transform.SetParent(levelContainer, false);

        var placed = new List<Placed>();
        var placedObjects = new List<(GameObject go, Placed data)>();

        // ---- in-field roles, biggest first so landmarks claim space ----------
        // Base counts are the [TUNE] floor; the THEME scales them — density is
        // an art-direction property (a forest is dense, a salt flat is not),
        // so the knob lives on the EnvPack, next to the props it multiplies.
        PlaceRole(EnvPack.PropRole.Landmark,
            Mathf.RoundToInt((2f * areaScale + 1f) * theme.landmarkDensity), true);
        PlaceRole(EnvPack.PropRole.MidField,
            Mathf.RoundToInt((7f * areaScale + 1f) * theme.midFieldDensity), true);
        PlaceRole(EnvPack.PropRole.Clutter,
            Mathf.RoundToInt((14f * areaScale + 3f) * theme.clutterDensity), true);

        // ---- silhouettes: the far band beyond the field's north edge ---------
        PlaceRole(EnvPack.PropRole.Silhouette,
            Mathf.RoundToInt((7f * areaScale + 1f) * theme.silhouetteDensity), false);

        // ---- outfield: the VISIBLE APRON beyond the design box ---------------
        // The floor is frustum-fit (R11), far larger than the 130×75 design box
        // — and nothing ever dressed it, which is most of why maps read empty.
        // Scatter non-silhouette props across it (they pass the same clearance,
        // camera-sight and route-visibility checks; the terrain stage then
        // re-projects them onto the relief, so they climb the hills for free).
        PlaceOutfield();

        void PlaceRole(EnvPack.PropRole role, int target, bool inField)
        {
            var entries = theme.entries?.Where(e => e.prefab != null && e.role == role)
                .OrderBy(e => e.prefab.name, System.StringComparer.Ordinal).ToList();
            if (entries == null || entries.Count == 0)
            {
                log.AppendLine($"  {role,-10} 0 placed — the theme pack has no entries in this role");
                return;
            }

            int placedCount = 0;
            var used = new HashSet<GameObject>();
            for (int i = 0; i < target; i++)
            {
                // The ENTRY redraws on every attempt, not once per slot: when a
                // big kit prop cannot fit anywhere, its slot used to burn all
                // its attempts on that one prefab and place NOTHING — a
                // 50-entry pack came out as the same 2-3 props. A fresh draw
                // per attempt lets a smaller prop take the slot instead, which
                // raises both fill rate and variety. Still fully seeded.
                for (int attempt = 0; attempt < MaxAttemptsPerProp; attempt++)
                {
                    EnvPack.Entry entry = entries[(int)(rng.NextU() % (uint)entries.Count)];
                    float scale = Mathf.Lerp(
                        entry.scaleRange.x > 0f ? entry.scaleRange.x : 1f,
                        entry.scaleRange.y > 0f ? entry.scaleRange.y : 1f,
                        rng.Range(0f, 1f));
                    float radius = entry.footprintRadius * scale;    // PLACED dimensions —
                    float height = entry.height * scale;             // never the raw fields

                    Vector3 pos = inField
                        ? new Vector3(rng.Range(-halfW, halfW), 0f, rng.Range(-halfD, halfD))
                        : new Vector3(rng.Range(-halfW * 1.2f, halfW * 1.2f), 0f,
                                      blueprint.playfieldSize.y * 0.5f + rng.Range(8f, 22f));

                    if (inField && !ClearOf(pos, radius, height, entry.allowInFold))
                        continue;
                    if (!inField && !ClearOfProps(pos, radius))
                        continue;

                    var go = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab);
                    go.transform.SetParent(dressing.transform, false);
                    go.transform.position = pos;
                    go.transform.rotation = Quaternion.Euler(0f, rng.Range(0f, 360f), 0f);
                    go.transform.localScale = Vector3.one * scale;
                    go.name = $"Prop_{role}_{placedCount + 1}";

                    var marker = go.AddComponent<PlacedProp>();
                    marker.placedFootprintRadius = radius;
                    marker.placedHeight = height;
                    marker.role = role.ToString();

                    var data = new Placed { pos = pos, radius = radius, height = height };
                    placed.Add(data);
                    placedObjects.Add((go, data));

                    // Spend the budget only on an accepted placement — a rejected
                    // candidate must not charge the map for route it never hid.
                    foreach (int idx in pendingHidden)
                        routeHidden[idx] = true;
                    hiddenMetres += pendingHidden.Count * RouteVisibility.SampleStep;

                    placedCount++;
                    used.Add(entry.prefab);
                    break;
                }
            }
            // The distinct count is the variety truth: "12/12 placed" can still
            // be three prefabs on repeat, and that only shows up here.
            log.AppendLine($"  {role,-10} {placedCount}/{target} placed " +
                           $"({used.Count} distinct of {entries.Count} in the pack's pool)");
        }

        void PlaceOutfield()
        {
            if (theme.outfieldDensity <= 0f)
                return;

            var pool = theme.entries?.Where(e => e.prefab != null &&
                    (e.role == EnvPack.PropRole.Landmark ||
                     e.role == EnvPack.PropRole.MidField ||
                     e.role == EnvPack.PropRole.Clutter))
                .OrderBy(e => e.prefab.name, System.StringComparer.Ordinal).ToList();
            if (pool == null || pool.Count == 0)
            {
                log.AppendLine("  Outfield    0 placed — no non-silhouette entries in the pack");
                return;
            }

            GameObject floor = SceneQuery.FindGround();
            var floorRenderers = floor != null ? floor.GetComponentsInChildren<Renderer>() : null;
            if (floorRenderers == null || floorRenderers.Length == 0)
            {
                log.AppendLine("  Outfield    0 placed — no floor to measure the apron from");
                return;
            }
            Bounds fb = floorRenderers[0].bounds;
            for (int i = 1; i < floorRenderers.Length; i++)
                fb.Encapsulate(floorRenderers[i].bounds);

            float apron = Mathf.Max(0f,
                fb.size.x * fb.size.z - blueprint.playfieldSize.x * blueprint.playfieldSize.y);
            int target = Mathf.Clamp(Mathf.RoundToInt(apron / 180f * theme.outfieldDensity), 0, 80);

            int placedCount = 0;
            var used = new HashSet<GameObject>();
            for (int i = 0; i < target; i++)
            {
                for (int attempt = 0; attempt < MaxAttemptsPerProp; attempt++)
                {
                    EnvPack.Entry entry = pool[(int)(rng.NextU() % (uint)pool.Count)];
                    float scale = Mathf.Lerp(
                        entry.scaleRange.x > 0f ? entry.scaleRange.x : 1f,
                        entry.scaleRange.y > 0f ? entry.scaleRange.y : 1f,
                        rng.Range(0f, 1f));
                    float radius = entry.footprintRadius * scale;
                    float height = entry.height * scale;

                    var pos = new Vector3(rng.Range(fb.min.x + 3f, fb.max.x - 3f), 0f,
                                          rng.Range(fb.min.z + 3f, fb.max.z - 3f));
                    // Apron only: in-field space belongs to the in-field roles
                    // and their deliberate budgets.
                    if (Mathf.Abs(pos.x) < halfW + 2f && Mathf.Abs(pos.z) < halfD + 2f)
                        continue;
                    // Full clearance suite anyway — cheap out here, and it keeps
                    // the camera's view of pads and routes protected by the same
                    // budget no matter where a prop stands.
                    if (!ClearOf(pos, radius, height, false))
                        continue;

                    var go = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab);
                    go.transform.SetParent(dressing.transform, false);
                    go.transform.position = pos;
                    go.transform.rotation = Quaternion.Euler(0f, rng.Range(0f, 360f), 0f);
                    go.transform.localScale = Vector3.one * scale;
                    go.name = $"Prop_Outfield_{placedCount + 1}";

                    var marker = go.AddComponent<PlacedProp>();
                    marker.placedFootprintRadius = radius;
                    marker.placedHeight = height;
                    marker.role = "Outfield";

                    var data = new Placed { pos = pos, radius = radius, height = height };
                    placed.Add(data);
                    placedObjects.Add((go, data));
                    foreach (int idx in pendingHidden)
                        routeHidden[idx] = true;
                    hiddenMetres += pendingHidden.Count * RouteVisibility.SampleStep;

                    placedCount++;
                    used.Add(entry.prefab);
                    break;
                }
            }
            log.AppendLine($"  Outfield   {placedCount}/{target} placed over the {apron:0} m² apron " +
                           $"({used.Count} distinct); terrain re-projects them onto the relief");
        }

        bool ClearOf(Vector3 pos, float radius, float height, bool allowInFold)
        {
            // Route clearance: lane band + widest body + this prop's own edge.
            float needRoute = LaneHalfWidth + MaxBodyRadius + radius + Margin;
            foreach (Vector3 s in routeSamples)
                if (Flat(pos - s).magnitude < needRoute)
                    return false;

            // Pads own their pockets (this is what allowInFold actually relaxes).
            float needPad = (allowInFold ? PadKeepOutInFold : PadKeepOut) + radius;
            foreach (var pad in pads)
                if (Flat(pos - pad.transform.position).magnitude < needPad)
                    return false;

            if (Flat(pos - corePos).magnitude < 10f + radius)
                return false;

            // CAMERA SIGHT LINE. Gate 2b protects the turret's view of the ROUTE;
            // this protects the player's view of the PAD, which is a different
            // line entirely. At 38° pitch a 12 m landmark hides ~15 m of ground
            // behind it, so the 6 m keep-out alone cannot prevent a pad being
            // covered on screen while every other check passes.
            if (cam != null)
            {
                probe[0] = new HardpointCoverageGizmo.Occluder
                { position = pos, radius = radius, height = height };

                foreach (var pad in pads)
                {
                    Vector3 padPoint = pad.transform.position +
                                       Vector3.up * HardpointCoverageGizmo.PadVisibleHeight;
                    if (HardpointCoverageGizmo.LineBlocked(cam.transform.position, padPoint, probe))
                        return false;
                }
            }

            // Route budget: how much NEWLY hidden route would this prop cost?
            pendingHidden.Clear();
            if (cam != null)
            {
                probe[0] = new HardpointCoverageGizmo.Occluder
                { position = pos, radius = radius, height = height };
                RouteVisibility.FindHidden(routeVisSamples, cam, probe, pendingHidden, routeHidden);

                float cost = pendingHidden.Count * RouteVisibility.SampleStep;
                if (hiddenMetres + cost > hiddenBudget)
                    return false;
            }

            return ClearOfProps(pos, radius);
        }

        bool ClearOfProps(Vector3 pos, float radius)
        {
            foreach (Placed other in placed)
                if (Flat(pos - other.pos).magnitude < radius + other.radius + 1f)
                    return false;
            return true;
        }

        // ---- occlusion re-run + self-repair (roadmap stage 9) ----------------
        int removed = 0;
        for (int pass = 0; pass <= MaxOcclusionRemovals; pass++)
        {
            var occluders = placedObjects
                .Select(p => new HardpointCoverageGizmo.Occluder
                { position = p.data.pos, radius = p.data.radius, height = p.data.height })
                .ToList();

            var shortfalls = new List<(HardpointCoverageGizmo pad, int have, int need)>();
            foreach (var pad in pads)
            {
                int need = pad.padClass == HardpointCoverageGizmo.PadClass.Premium ? 4 : 2;
                int have = pad.CountCoveredSpansOnCurve(occluders);
                if (have < need)
                    shortfalls.Add((pad, have, need));
            }

            if (shortfalls.Count == 0)
                break;

            if (pass == MaxOcclusionRemovals || placedObjects.Count == 0)
            {
                foreach (var (pad, have, need) in shortfalls)
                    stillBlocked.Add($"{pad.name} at {have}/{need} spans with sight lines applied");
                break;
            }

            // Remove the prop whose absence recovers the most spans for the
            // worst-hit pad — deterministic: first-worst pad, best single prop.
            var victim = shortfalls[0].pad;
            (GameObject go, Placed data) bestProp = default;
            int bestRecovered = -1;
            foreach (var candidate in placedObjects)
            {
                var without = placedObjects.Where(p => p.go != candidate.go)
                    .Select(p => new HardpointCoverageGizmo.Occluder
                    { position = p.data.pos, radius = p.data.radius, height = p.data.height })
                    .ToList();
                int recovered = victim.CountCoveredSpansOnCurve(without);
                if (recovered > bestRecovered)
                {
                    bestRecovered = recovered;
                    bestProp = candidate;
                }
            }

            log.AppendLine($"  [occlusion] removed '{bestProp.go.name}' — it blocked {victim.name} " +
                           $"(recovers to {bestRecovered} spans)");
            placed.Remove(bestProp.data);
            placedObjects.Remove(bestProp);
            Object.DestroyImmediate(bestProp.go);
            removed++;
        }

        log.AppendLine($"  route occlusion: {hiddenMetres:0.#} m of " +
                       $"{RouteVisibility.TotalMetres(routeVisSamples):0} m hidden " +
                       $"(budget {hiddenBudget:0.#} m = {RouteVisibility.HiddenBudgetFraction:P0})");
        log.AppendLine(removed > 0
            ? $"  occlusion re-run: {removed} prop(s) removed to keep sight lines; " +
              (stillBlocked.Count == 0 ? "all pads recovered" : $"{stillBlocked.Count} pad(s) STILL short")
            : "  occlusion re-run: no pad lost a span to dressing");

        return log.ToString();
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
