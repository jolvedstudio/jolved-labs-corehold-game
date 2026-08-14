using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Ticket 30/31 — Refinery Delta blockout builder.
/// Run: Tools/COREHOLD/Level/Build Refinery Delta.
///
/// Rebuilds the "RefineryLevel" container, route waypoints, hardpoints and set
/// dressing, and removes the old placeholder Core / Route_* objects. Coordinate
/// frame: X in [-65,65] (west = -X), Z in [-37.5,37.5] (north = +Z, camera sits
/// at -Z). Core is lower-right = (+X, -Z).
/// </summary>
public static class RefineryDeltaBlockout
{
    // ---- Playfield ----
    const float FieldW = 130f;
    const float FieldD = 75f;

    static readonly Vector3 CorePos = new Vector3(34.5f, 0f, -6.5f);
    static readonly Vector3 Merge = new Vector3(-30f, 0f, 18f);

    // West entrance leg (30 m). Straight east into the merge.
    static readonly Vector3[] WestLeg =
    {
        new Vector3(-60f, 0f, 18f),
        new Vector3(-45f, 0f, 18f),
        Merge,
    };

    // North entrance leg (30 m). Angled down-left into the merge.
    static readonly Vector3[] NorthLeg =
    {
        new Vector3(-6f, 0f, 36f),
        new Vector3(-18f, 0f, 27f),
        Merge,
    };

    // Shared 120 m snake, merge -> Core. Four hairpins with tight parallel legs
    // (~10-11 m apart) folded around fixed refinery structures.
    static readonly Vector3[] Snake =
    {
        // Merge (-30,18) prepended by the builder; do not repeat here.
        new Vector3(-19f, 0f, 18f),   // S1  east off the merge (top run)
        new Vector3(-19f, 0f, 9f),    // S2  hairpin 1 south (west of Tank_A)
        new Vector3(-9f,  0f, 9f),    // S3  east step under Tank_A
        new Vector3(-9f,  0f, 18f),   // S4  hairpin 2 north (east of Tank_A)
        new Vector3(2f,   0f, 18f),   // S5  east along the top run
        new Vector3(2f,   0f, 8f),    // S6  hairpin 3 south (west of Silo)
        new Vector3(13f,  0f, 8f),    // S7  east step under the Silo
        new Vector3(13f,  0f, 18f),   // S8  hairpin 4 north (east of Silo)
        new Vector3(23f,  0f, 18f),   // S9  east on the top run
        new Vector3(23f,  0f, 6f),    // S10 south toward the core wall
        CorePos,                      // arrive at the Core (34.5,-6.5)
    };

    // ---- Shipped layout, exposed for the generation pipeline (R26) ----
    //
    // The generator's parity path rebuilds THIS geometry through the full
    // pipeline. Route points and the core position are the blockout's own data;
    // the parity PAD set intentionally lives in ShippedLayout instead, because
    // the scene's pads were hand-moved after this builder ran (the clearance
    // pass), so the scene — not the constants below — is ground truth for them.
    internal static Vector3 ShippedCorePos => CorePos;
    internal static Vector3[] ShippedWestRoute => WestLeg.Concat(Snake).ToArray();
    internal static Vector3[] ShippedNorthRoute => NorthLeg.Concat(Snake).ToArray();
    internal static readonly Vector3 ShippedAirSpawn = new Vector3(0f, 4f, 37f);

    [MenuItem("Tools/COREHOLD/Level/Build Refinery Delta", false, 1)]
    public static void Build()
    {
        var log = new StringBuilder();

        // ---- Remove old placeholder objects from the starter scene ----
        RemovePlaceholders(log);

        // ---- Container ----
        var root = SceneLookup.Find("RefineryLevel");
        if (root != null) Object.DestroyImmediate(root);
        root = new GameObject("RefineryLevel");
        Undo.RegisterCreatedObjectUndo(root, "Build Refinery Delta");

        // ---- Report route lengths (verify 30/30/120/150) ----
        log.AppendLine("=== Route metrics ===");
        var westFull = WestLeg.Concat(Snake).ToArray();
        var northFull = NorthLeg.Concat(Snake).ToArray();
        log.AppendLine($"West leg   : {PolyLen(WestLeg):0.00} m");
        log.AppendLine($"North leg  : {PolyLen(NorthLeg):0.00} m");
        float snakeLen = PolyLen(new[] { Merge }.Concat(Snake).ToArray());
        log.AppendLine($"Shared snake (from merge): {snakeLen:0.00} m");
        log.AppendLine($"West route TOTAL : {PolyLen(westFull):0.00} m");
        log.AppendLine($"North route TOTAL: {PolyLen(northFull):0.00} m");

        // ---- Floor ----
        BuildFloor(root.transform);

        // ---- Structures (hairpin anchors) ----
        BuildStructures(root.transform);

        // ---- Routes ----
        var routesRoot = new GameObject("Routes");
        routesRoot.transform.SetParent(root.transform, false);
        var westRoute = BuildRoute(routesRoot.transform, "Route_West", westFull, new Color(0.2f, 1f, 0.6f, 1f));
        var northRoute = BuildRoute(routesRoot.transform, "Route_North", northFull, new Color(0.2f, 0.6f, 1f, 1f));

        // ---- Core ----
        var core = BuildCore(root.transform, CorePos, null);

        // ---- Spawners wiring ----
        WireSpawners(westRoute, northRoute, core, WestLeg[0], NorthLeg[0], log);

        // ---- Hardpoints ----
        BuildHardpoints(root.transform, westRoute, log);

        // ---- Set dressing: wreck + radar ----
        BuildWreckAndRadar(root.transform);

        MarkDirty();
        Debug.Log(log.ToString());
    }

    static void RemovePlaceholders(StringBuilder log)
    {
        foreach (var n in new[] { "Core", "Route_West", "Route_North" })
        {
            var go = SceneLookup.Find(n);
            // Only remove the top-level placeholder (not the ones under RefineryLevel).
            if (go != null && go.transform.parent == null)
            {
                Object.DestroyImmediate(go);
                log.AppendLine($"[cleanup] removed placeholder '{n}'");
            }
        }
    }

    static void MarkDirty()
    {
        var scene = SceneManager.GetActiveScene();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
    }

    // ---------------------------------------------------------------- helpers

    static float PolyLen(Vector3[] pts)
    {
        float l = 0f;
        for (int i = 1; i < pts.Length; i++) l += Vector3.Distance(pts[i - 1], pts[i]);
        return l;
    }

    /// <summary>
    /// Pass as <c>scale</c> to leave the prefab's OWN authored scale alone.
    /// Forcing 1.0 is not the same thing: it overwrites whatever size the prefab
    /// author chose, which is how the Core's shield generator ended up out of
    /// proportion against a platform that was correctly scaled to 0.3.
    /// </summary>
    internal const float KeepPrefabScale = -1f;

    internal static GameObject Place(string assetPath, Transform parent, Vector3 pos, Vector3 euler, float scale, string name = null)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[Refinery] Missing prefab: {assetPath}");
            return null;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.eulerAngles = euler;
        if (scale > 0f)
            go.transform.localScale = Vector3.one * scale;   // KeepPrefabScale => untouched
        if (!string.IsNullOrEmpty(name)) go.name = name;
        return go;
    }

    static void BuildFloor(Transform parent)
    {
        var floor = SceneQuery.FindGround();
        if (floor == null)
        {
            floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
        }
        if (floor.GetComponent<Corehold.Systems.LevelGround>() == null)
            floor.AddComponent<Corehold.Systems.LevelGround>().source = "primitive plane";
        floor.transform.position = Vector3.zero;

        // The ground must cover what the CAMERA sees, not the design box (R11).
        // Sizing it from FieldW/FieldD leaves void everywhere the frustum reaches
        // past 130×75 — which at the shipped framing is most of the upper screen —
        // and it is what silently reverted the hand-widened floor on every rebuild.
        // Fall back to the design box only when there is no camera to fit to.
        //
        // NOTE (R26): this runs before CameraFramingSetup, so on a freshly framed
        // map the authoritative pass is Tools → COREHOLD → Fit Ground + Fog, run
        // AFTER framing. Generation must order it that way.
        floor.transform.localScale = GroundAndSkirt.FloorScaleForCamera(
            Object.FindFirstObjectByType<Camera>(),
            new Vector3(FieldW / 10f, 1f, FieldD / 10f));
    }

    const string CreepyRoot = "Assets/Vendor/Creepy_Cat/3D Scifi Kit Vol 4/Prefabs/";

    internal static void BuildStructures(Transform parent)
    {
        var s = new GameObject("Structures");
        s.transform.SetParent(parent, false);
        var t = s.transform;

        // Tank_A inside hairpin 1/2 fold (legs x=-19 and x=-9).
        Place(CreepyRoot + "Props/Container & Crate/P_Tank_Cistern_01.prefab", t,
              new Vector3(-14f, 0f, 4f), Vector3.zero, 0.8f, "Refinery_Tank_A");

        // Storage silo inside hairpin 3/4 fold (legs x=2..13..23).
        Place(CreepyRoot + "Props/Machine/P_Storage_Liquid_Station_01.prefab", t,
              new Vector3(8f, 0f, 3f), Vector3.zero, 0.28f, "Refinery_StorageSilo");

        // Pumping station landmark back-left.
        Place(CreepyRoot + "Props/Machine/P_Pumping_Station_01.prefab", t,
              new Vector3(-48f, 0f, -6f), Vector3.zero, 0.30f, "Refinery_PumpingStation");

        // Secondary tank on the east skyline.
        Place(CreepyRoot + "Props/Container & Crate/P_Tank_Cistern_01_B.prefab", t,
              new Vector3(30f, 0f, 26f), Vector3.zero, 0.9f, "Refinery_Tank_B");

        // Connective containers / pipe run / props.
        Place(CreepyRoot + "Props/Container & Crate/P_Container_01.prefab", t,
              new Vector3(-32f, 0f, -18f), new Vector3(0f, 90f, 0f), 1f, "Container_A");
        Place(CreepyRoot + "Props/Container & Crate/P_Container_01_C.prefab", t,
              new Vector3(-28f, 0f, -18f), new Vector3(0f, 90f, 0f), 1f, "Container_B");
        Place(CreepyRoot + "Props/Pipes & Cables/P_Pipe_Big_Line_01.prefab", t,
              new Vector3(-4f, 0f, 34f), Vector3.zero, 1.5f, "Pipe_A");
        Place(CreepyRoot + "Props/Machine/P_Solar_Power_01.prefab", t,
              new Vector3(-56f, 0f, -26f), new Vector3(0f, 40f, 0f), 1f, "Solar_A");
        Place(CreepyRoot + "Props/Machine/P_Wind_Turbine_01.prefab", t,
              new Vector3(56f, 0f, 22f), new Vector3(0f, -30f, 0f), 1f, "Turbine_A");
    }

    internal static PathRoute BuildRoute(Transform parent, string name, Vector3[] pts, Color color)
    {
        var routeGo = new GameObject(name);
        routeGo.transform.SetParent(parent, false);
        var route = routeGo.AddComponent<PathRoute>();

        var wps = new List<Transform>();
        for (int i = 0; i < pts.Length; i++)
        {
            var wp = new GameObject($"WP_{i}");
            wp.transform.SetParent(routeGo.transform, false);
            wp.transform.position = pts[i];
            wps.Add(wp.transform);
        }

        var so = new SerializedObject(route);
        var arr = so.FindProperty("waypoints");
        arr.arraySize = wps.Count;
        for (int i = 0; i < wps.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = wps[i];
        so.FindProperty("lineColor").colorValue = color;
        so.ApplyModifiedPropertiesWithoutUndo();

        return route;
    }

    const string FiradzoRoot = "Assets/Vendor/TD_Sci-Fi_Turrets_Pack_V2/Prefabs/";

    /// <summary>
    /// Build the protected structure at <paramref name="corePos"/>. With a
    /// <paramref name="protectedPrefab"/> (from a blueprint, R26) that prefab IS
    /// the structure; otherwise the shipped platform + shield-generator stack is
    /// placed. Either way a "Core_Target" child marks the aim point.
    /// </summary>
    internal static Transform BuildCore(Transform parent, Vector3 corePos, GameObject protectedPrefab)
    {
        var coreRoot = new GameObject("Core_Blockout");
        coreRoot.transform.SetParent(parent, false);
        coreRoot.transform.position = corePos;

        float targetHeight;
        if (protectedPrefab != null)
        {
            var structure = (GameObject)PrefabUtility.InstantiatePrefab(protectedPrefab);
            structure.transform.SetParent(coreRoot.transform, false);
            structure.transform.position = corePos;
            structure.name = "Core_Structure";
            targetHeight = 3f;
        }
        else
        {
            var platform = Place(CreepyRoot + "Bases & Hangars/P_Plateform_Big_01.prefab", coreRoot.transform,
                  corePos, Vector3.zero, 0.3f, "Core_Platform");
            float platTop = platform != null ? 5.92f * 0.3f : 0f;

            // Keep the generator at its authored size — the shipped scene records
            // no scale override for it, so 0.3 on the platform is deliberate and
            // 1.0 here was just the default being written over the prefab.
            Place(FiradzoRoot + "Shield_generator_2/Shield_generator_2_1.prefab", coreRoot.transform,
                  corePos + new Vector3(0f, platTop, 0f), Vector3.zero, KeepPrefabScale, "Core_ShieldGenerator");
            targetHeight = platTop + 3f;
        }

        var target = new GameObject("Core_Target");
        target.transform.SetParent(coreRoot.transform, false);
        target.transform.position = corePos + new Vector3(0f, targetHeight, 0f);
        return target.transform;
    }

    static void WireSpawners(PathRoute west, PathRoute north, Transform core,
                             Vector3 westStart, Vector3 northStart, StringBuilder log)
    {
        WireOne("Spawner_West", 0, west, core, westStart, log);
        WireOne("Spawner_North", 1, north, core, northStart, log);
        WireOne("Spawner_Air", 2, null, core, new Vector3(0f, 4f, 37f), log);
    }

    /// <summary>
    /// Wire (or create, when <paramref name="createUnder"/> is given — the
    /// generation pipeline builds fresh scenes with no spawners to find) one
    /// spawner by name: index, route, core target, position.
    /// </summary>
    internal static void WireOne(string name, int index, PathRoute route, Transform core, Vector3 pos,
                                StringBuilder log, Transform createUnder = null)
    {
        var go = SceneLookup.Find(name);
        if (go == null && createUnder != null)
        {
            go = new GameObject(name);
            go.transform.SetParent(createUnder, false);
            go.AddComponent<Spawner>();
            log.AppendLine($"[ok] created spawner '{name}'");
        }
        if (go == null)
        {
            log.AppendLine($"[warn] Spawner '{name}' not found; skipped.");
            return;
        }
        var sp = go.GetComponent<Spawner>();
        if (sp == null) { log.AppendLine($"[warn] '{name}' has no Spawner."); return; }
        sp.SetIndex(index);
        if (route != null) sp.SetRoute(route);
        if (core != null) sp.SetCoreTarget(core);
        go.transform.position = pos;
        EditorUtility.SetDirty(sp);
        log.AppendLine($"[ok] wired {name} (index {index}) at {pos}");
    }

    internal struct HP
    {
        public string name; public Vector3 pos;
        public Corehold.Towers.HardpointCoverageGizmo.TurretKind kind;
        public Corehold.Towers.HardpointCoverageGizmo.PadClass cls;
    }

    internal static HP MakeHP(string n, Vector3 p,
                     Corehold.Towers.HardpointCoverageGizmo.TurretKind k,
                     Corehold.Towers.HardpointCoverageGizmo.PadClass c)
        => new HP { name = n, pos = p, kind = k, cls = c };

    /// <summary>
    /// Build a pad set under <paramref name="parent"/>: pad object, floor prefab,
    /// TowerHardpoint, coverage gizmo wired to the route. Returns true when the
    /// coverage rule held (every pad ≥2 spans, ≥3 Premium at ≥4). The generation
    /// pipeline supplies its own pad list; the menu build uses the blockout's.
    /// </summary>
    /// <summary>Single-route overload — the shipped/corridor convention (shared tail).</summary>
    internal static bool BuildHardpoints(Transform parent, PathRoute route, IList<HP> hps, StringBuilder log)
        => BuildHardpoints(parent, new[] { route }, hps, log);

    internal static bool BuildHardpoints(Transform parent, PathRoute[] routes, IList<HP> hps, StringBuilder log)
    {
        var hpRoot = new GameObject("Hardpoints");
        hpRoot.transform.SetParent(parent, false);

        log.AppendLine("=== Hardpoint coverage (per-turret tier-1 rings) ===");
        int premiumFour = 0;
        bool allPass = true;
        foreach (var h in hps)
        {
            var go = new GameObject(h.name);
            go.transform.SetParent(hpRoot.transform, false);
            go.transform.position = h.pos;

            Place(CreepyRoot + "Building/Floors/P_Floor_Cache_01.prefab", go.transform,
                  h.pos, Vector3.zero, 0.5f, "Pad");

            go.AddComponent<Corehold.Towers.TowerHardpoint>();
            var gz = go.AddComponent<Corehold.Towers.HardpointCoverageGizmo>();
            gz.intendedTurret = h.kind;
            gz.padClass = h.cls;
            // The same route set the selector SCORED against, so gate 2 measures
            // what selection promised. One route when a shared tail carries every
            // shared span (the shipped convention); all of them when the
            // approaches are disjoint — a siege pad covering three approaches
            // credited with one third of its coverage is how siege maps came out
            // "hosting 1 Premium pad".
            gz.routes = routes;

            int covered = gz.CountCoveredSegments();
            bool isPrem = h.cls == Corehold.Towers.HardpointCoverageGizmo.PadClass.Premium;
            bool pass = isPrem ? covered >= 4 : covered >= 2;
            if (isPrem && covered >= 4) premiumFour++;
            if (!pass) allPass = false;
            log.AppendLine($"{h.name,-14} {h.kind,-11} r{Corehold.Towers.HardpointCoverageGizmo.RangeFor(h.kind):0}m {h.cls,-9} -> {covered} segs {(pass ? "PASS" : "**FAIL**")}");
        }
        log.AppendLine($"Premium pads covering 4+: {premiumFour} (need >=3)");
        bool satisfied = allPass && premiumFour >= 3;
        log.AppendLine($"COVERAGE RULE: {(satisfied ? "SATISFIED" : "**NOT MET**")}");
        return satisfied;
    }

    static void BuildHardpoints(Transform parent, PathRoute route, StringBuilder log)
    {
        var Autocannon = Corehold.Towers.HardpointCoverageGizmo.TurretKind.Autocannon;
        var Missile = Corehold.Towers.HardpointCoverageGizmo.TurretKind.Missile;
        var ArcNode = Corehold.Towers.HardpointCoverageGizmo.TurretKind.ArcNode;
        var Mortar = Corehold.Towers.HardpointCoverageGizmo.TurretKind.Mortar;
        var Premium = Corehold.Towers.HardpointCoverageGizmo.PadClass.Premium;
        var Standard = Corehold.Towers.HardpointCoverageGizmo.PadClass.Standard;
        var Rear = Corehold.Towers.HardpointCoverageGizmo.PadClass.Rear;
        var Overwatch = Corehold.Towers.HardpointCoverageGizmo.PadClass.Overwatch;

        var hps = new List<HP>
        {
            // 3 premium (4+ segments) — nested in the fold corners between parallel legs.
            MakeHP("HP_Premium_1", new Vector3(-7f,  0f, 13f), Missile,    Premium),
            MakeHP("HP_Premium_2", new Vector3(4f,   0f, 13f), Autocannon, Premium),
            MakeHP("HP_Premium_3", new Vector3(15f,  0f, 13f), ArcNode,    Premium),

            // 2 standard (2-3 segments).
            MakeHP("HP_Standard_1", new Vector3(-24f, 0f, 13f), Autocannon, Standard),
            MakeHP("HP_Standard_2", new Vector3(-13f, 0f, 3f),  Missile,    Standard),

            // 2 rear near the Core (final approach + air corridor terminal leg).
            MakeHP("HP_Rear_1", new Vector3(29f, 0f, 2f),   Autocannon, Rear),
            MakeHP("HP_Rear_2", new Vector3(24f, 0f, -2f),  ArcNode,    Rear),

            // 1 overwatch — set back, Siege Mortar home (20 m ring, 6 m dead zone).
            MakeHP("HP_Overwatch", new Vector3(24f, 0f, -8f), Mortar, Overwatch),
        };

        BuildHardpoints(parent, route, hps, log);
    }

    internal static void BuildWreckAndRadar(Transform parent)
    {
        var d = new GameObject("Narrative");
        d.transform.SetParent(parent, false);

        // Toppled onto its back against the left edge, half-buried (sunk 0.4 m).
        var wreck = Place("Assets/Vendor/Destructible_Humanoid_Robot/Prefabs/HumanBot_Unskinned.prefab",
              d.transform, new Vector3(-60f, -0.4f, -6f), new Vector3(-90f, 55f, 0f), 1.5f, "TitanWreck");
        if (wreck == null)
            Place("Assets/Vendor/Destructible_Humanoid_Robot/Prefabs/HumanBot_Skinned_A.prefab",
                  d.transform, new Vector3(-60f, -0.4f, -6f), new Vector3(-90f, 55f, 0f), 1.5f, "TitanWreck");

        Place(FiradzoRoot + "Radar/Radar_1.prefab", d.transform,
              new Vector3(-48f, 0f, 34f), new Vector3(0f, 20f, 0f), 2.2f, "Skyline_Radar");
    }
}
