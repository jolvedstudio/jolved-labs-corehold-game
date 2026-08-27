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

    /// <summary>[TUNE] Props settle this far into the ground (× their scale) so
    /// nothing perches on a groundline. The terrain stage lifts by +Height
    /// afterwards, so the sink survives on hills and on flat maps alike.</summary>
    private const float SinkIn = 0.10f;

    /// <summary>[TUNE] Placement attempts per cluster satellite (cheap misses).</summary>
    private const int SatelliteAttempts = 6;

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
                               List<PathRoute> routes, Vector3 corePos, Vector3 airSpawn,
                               out List<string> stillBlocked)
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

        // The SAME analytic terrain field the terrain stage will build later —
        // identical inputs (seed purpose, flat route samples, pads, core, air
        // lane) ⇒ identical field — so placement can settle props onto slopes
        // it KNOWS are coming and seek interesting relief, even though the
        // mesh does not exist yet. Null on flat maps: every terrain-aware step
        // simply skips.
        TerrainField field = null;
        if (blueprint.terrainRelief)
        {
            var polylines = new List<Vector3[]>();
            foreach (PathRoute route in routes)
            {
                int n = Mathf.Max(2, Mathf.CeilToInt(route.Length / 2f) + 1);
                var pts = new Vector3[n];
                for (int i = 0; i < n; i++)
                    pts[i] = route.SamplePosition(route.Length * i / (n - 1f), out _);
                polylines.Add(pts);
            }
            if (blueprint.airCorridor)
                polylines.Add(new[] { airSpawn, corePos });
            var padPositions = new Vector3[pads.Length];
            for (int i = 0; i < pads.Length; i++)
                padPositions[i] = pads[i].transform.position;
            field = new TerrainField((int)GenerationPipeline.Fnv1a(blueprint.randomSeed, "terrain"),
                                     polylines, padPositions, corePos);
        }

        // Satellite pool for clusters: the pack's small stuff.
        var clutterPool = theme.entries?.Where(e => e.prefab != null && e.role == EnvPack.PropRole.Clutter)
            .OrderBy(e => e.prefab.name, System.StringComparer.Ordinal).ToList();

        int tinted = 0;

        // Per-instance size: the entry's authored band, then the pack's jitter
        // damped by role — landmarks are navigation anchors and stay near
        // their recognized size, clutter is where sameness shows most. The
        // jitter draw happens even at knob 0 so turning variation off never
        // reshuffles WHERE things landed, only how uniform they look.
        float DrawScale(EnvPack.Entry entry)
        {
            float baseScale = Mathf.Lerp(
                entry.scaleRange.x > 0f ? entry.scaleRange.x : 1f,
                entry.scaleRange.y > 0f ? entry.scaleRange.y : 1f,
                rng.Range(0f, 1f));
            float j = theme.scaleJitter * RoleScaleJitter(entry.role);
            return baseScale * (1f + rng.Range(-1f, 1f) * j);
        }

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

            int placedCount = 0, satellites = 0;
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
                    float scale = DrawScale(entry);

                    Vector3 pos = inField
                        ? new Vector3(rng.Range(-halfW, halfW), 0f, rng.Range(-halfD, halfD))
                        : new Vector3(rng.Range(-halfW * 1.2f, halfW * 1.2f), 0f,
                                      blueprint.playfieldSize.y * 0.5f + rng.Range(8f, 22f));
                    float yaw = rng.Range(0f, 360f);

                    // Silhouettes keep the LITE checks (far band, outside every
                    // gate's jurisdiction); everything in-field runs the full suite.
                    var go = TryPlaceProp(entry, pos, yaw, scale,
                        $"Prop_{role}_{placedCount + 1}", requireApron: false, liteChecks: !inField);
                    if (go == null)
                        continue;

                    placedCount++;
                    used.Add(entry.prefab);

                    // Composition: a placed anchor may seed a cluster of small
                    // satellites — clumping is what reads as a real place.
                    if (inField)
                        satellites += PlaceSatellites(pos, entry.footprintRadius * scale, yaw,
                                                      go.name, requireApron: false);
                    break;
                }
            }
            // The distinct count is the variety truth: "12/12 placed" can still
            // be three prefabs on repeat, and that only shows up here.
            log.AppendLine($"  {role,-10} {placedCount}/{target} placed, +{satellites} satellite(s) " +
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

            int placedCount = 0, satellites = 0;
            var used = new HashSet<GameObject>();
            for (int i = 0; i < target; i++)
            {
                for (int attempt = 0; attempt < MaxAttemptsPerProp; attempt++)
                {
                    EnvPack.Entry entry = pool[(int)(rng.NextU() % (uint)pool.Count)];
                    float scale = DrawScale(entry);

                    var pos = new Vector3(rng.Range(fb.min.x + 3f, fb.max.x - 3f), 0f,
                                          rng.Range(fb.min.z + 3f, fb.max.z - 3f));
                    // Apron only: in-field space belongs to the in-field roles
                    // and their deliberate budgets.
                    if (Mathf.Abs(pos.x) < halfW + 2f && Mathf.Abs(pos.z) < halfD + 2f)
                        continue;

                    // TERRAIN AFFINITY: props seek the relief instead of
                    // ignoring it — candidates on hills always pass, bare flat
                    // apron only sometimes, so rocks gather at shoulders and
                    // crests the way they would in a real place. (Flat maps
                    // have no field; uniform scatter as before.)
                    if (field != null && field.Relief(pos.x, pos.z) < 0.4f &&
                        rng.Range(0f, 1f) > 0.35f)
                        continue;

                    float yaw = rng.Range(0f, 360f);
                    var go = TryPlaceProp(entry, pos, yaw, scale,
                        $"Prop_Outfield_{placedCount + 1}", requireApron: true, liteChecks: false);
                    if (go == null)
                        continue;

                    placedCount++;
                    used.Add(entry.prefab);
                    satellites += PlaceSatellites(pos, entry.footprintRadius * scale, yaw,
                                                  go.name, requireApron: true);
                    break;
                }
            }
            log.AppendLine($"  Outfield   {placedCount}/{target} placed, +{satellites} satellite(s) " +
                           $"over the {apron:0} m² apron ({used.Count} distinct); terrain re-projects " +
                           "them onto the relief");
        }

        // ------------------------------------------------------------- core
        // The one placement path every lane shares — anchors, satellites and
        // outfield alike: clearance suite, slope settle, instantiate, marker,
        // bookkeeping, visibility-budget spend. Returns null when refused.
        GameObject TryPlaceProp(EnvPack.Entry entry, Vector3 pos, float yaw, float scale,
                                string name, bool requireApron, bool liteChecks)
        {
            float radius = entry.footprintRadius * scale;
            float height = entry.height * scale;

            if (requireApron && Mathf.Abs(pos.x) < halfW + 2f && Mathf.Abs(pos.z) < halfD + 2f)
                return null;

            if (liteChecks)
            {
                // Far-band silhouettes: outside every gate's jurisdiction —
                // prop spacing only, and no visibility budget to spend.
                pendingHidden.Clear();
                if (!ClearOfProps(pos, radius))
                    return null;
            }
            else if (!ClearOf(pos, radius, height, entry.allowInFold))
            {
                return null;
            }

            // SLOPE SETTLE (terrain maps): tilt toward the coming slope, capped
            // by the theme, and sink slightly — nothing perches. The terrain
            // stage lifts by +Height afterwards, so both survive the bake.
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            if (field != null && theme.slopeTiltMaxDegrees > 0f)
            {
                const float e = 0.75f;
                float dhx = field.Height(pos.x + e, pos.z) - field.Height(pos.x - e, pos.z);
                float dhz = field.Height(pos.x, pos.z + e) - field.Height(pos.x, pos.z - e);
                var normal = new Vector3(-dhx, 2f * e, -dhz).normalized;
                rot = Quaternion.RotateTowards(
                    Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal),
                    theme.slopeTiltMaxDegrees) * rot;
            }

            // UPRIGHT JITTER: a small role-damped lean in the prop's own frame,
            // on top of the settle. The draws always happen, so zeroing the
            // knob straightens the props without reshuffling the layout.
            float lean = theme.uprightJitterDegrees * RoleLean(entry.role);
            float leanX = rng.Range(-1f, 1f) * lean;
            float leanZ = rng.Range(-1f, 1f) * lean;
            if (lean > 0f)
                rot = rot * Quaternion.Euler(leanX, 0f, leanZ);

            // Sink varies per prop (0.06–0.15 × scale): a uniform sink line
            // across a whole field reads as manufactured as uniform scale did.
            Vector3 placedPos = pos;
            placedPos.y = -rng.Range(0.06f, 0.15f) * scale;

            // TONE: one of five weathering steps, relief-biased on terrain maps
            // (crests lean sun-bleached, hollows lean damp/dark) so the tint
            // reads as one coherent place, not per-prop noise.
            int toneStep = (int)(rng.NextU() % 5u) - 2;
            if (field != null)
            {
                float relief = field.Relief(pos.x, pos.z);
                toneStep = Mathf.Clamp(toneStep + (relief > 0.7f ? 1 : relief < 0.3f ? -1 : 0), -2, 2);
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab);
            go.transform.SetParent(dressing.transform, false);
            go.transform.position = placedPos;
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * scale;
            go.name = name;

            if (theme.toneVariation > 0f && ToneVariants.Apply(go, toneStep, theme.toneVariation) > 0)
                tinted++;

            var marker = go.AddComponent<PlacedProp>();
            marker.placedFootprintRadius = radius;
            marker.placedHeight = height;
            marker.role = entry.role.ToString();

            var data = new Placed { pos = pos, radius = radius, height = height };
            placed.Add(data);
            placedObjects.Add((go, data));

            // Spend the budget only on an accepted placement — a rejected
            // candidate must not charge the map for route it never hid.
            foreach (int idx in pendingHidden)
                routeHidden[idx] = true;
            hiddenMetres += pendingHidden.Count * RouteVisibility.SampleStep;
            return go;
        }

        // A placed anchor may seed a cluster: a few smaller Clutter props
        // scattered close around it with correlated rotation. Every satellite
        // runs the FULL clearance suite, so clusters can never compromise what
        // the gates protect — they only fill the space between.
        int PlaceSatellites(Vector3 anchorPos, float anchorRadius, float anchorYaw,
                            string anchorName, bool requireApron)
        {
            if (clutterPool == null || clutterPool.Count == 0 || theme.clusterMaxSatellites <= 0)
                return 0;
            if (rng.Range(0f, 1f) >= theme.clusterChance)
                return 0;

            int want = 1 + (int)(rng.NextU() % (uint)theme.clusterMaxSatellites);
            int done = 0;
            for (int s = 0; s < want; s++)
            {
                for (int attempt = 0; attempt < SatelliteAttempts; attempt++)
                {
                    EnvPack.Entry entry = clutterPool[(int)(rng.NextU() % (uint)clutterPool.Count)];
                    float scale = DrawScale(entry) * rng.Range(0.7f, 0.95f);

                    float ang = rng.Range(0f, Mathf.PI * 2f);
                    // Satellites shrink with distance from the anchor — debris
                    // thins outward the way real scatter does, instead of the
                    // cluster ending on a hard ring.
                    float extra = rng.Range(0.4f, 3.0f);
                    scale *= Mathf.Lerp(1f, 0.85f, (extra - 0.4f) / 2.6f);
                    float dist = anchorRadius + entry.footprintRadius * scale + extra;
                    Vector3 pos = anchorPos + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                    float yaw = anchorYaw + rng.Range(-45f, 45f);

                    if (TryPlaceProp(entry, pos, yaw, scale, $"{anchorName}_Sat{done + 1}",
                                     requireApron, liteChecks: false) != null)
                    {
                        done++;
                        break;
                    }
                }
            }
            return done;
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

        log.AppendLine($"  variation: scale ±{theme.scaleJitter:P0} by role, " +
                       $"{tinted} prop(s) tone-shifted across 5 steps (strength {theme.toneVariation:0.##}" +
                       (field != null ? ", relief-biased" : "") + "), " +
                       $"lean ±{theme.uprightJitterDegrees:0.#}° by role, sink 0.06–0.15 × scale");

        return log.ToString();
    }

    /// <summary>Scale-jitter damping per role: landmarks are navigation anchors
    /// and stay near their recognized size; clutter is where sameness shows.</summary>
    private static float RoleScaleJitter(EnvPack.PropRole role)
    {
        switch (role)
        {
            case EnvPack.PropRole.Landmark: return 0.5f;
            case EnvPack.PropRole.Clutter: return 1.4f;
            case EnvPack.PropRole.Silhouette: return 1.2f;
            default: return 1f;
        }
    }

    /// <summary>Upright-jitter damping per role: a leaning rock is geology, a
    /// leaning building is a mistake.</summary>
    private static float RoleLean(EnvPack.PropRole role)
    {
        switch (role)
        {
            case EnvPack.PropRole.Landmark: return 0.25f;
            case EnvPack.PropRole.MidField: return 0.6f;
            case EnvPack.PropRole.Silhouette: return 0.8f;
            default: return 1f;
        }
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
