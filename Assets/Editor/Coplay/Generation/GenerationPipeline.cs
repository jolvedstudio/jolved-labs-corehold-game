using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using CoreholdEditor;                     // BuildRealUI, CameraFramingSetup, PlayableBootstrapSetup
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The generation pipeline as CODE — the ordered stage list both the Level
/// Generator window and the headless menu item drive (R26). The stage order is
/// the P6 preamble's twelve-stage order, and it is LOAD-BEARING: floor after
/// camera, coverage re-run after dressing, model solve after geometry is final.
///
/// Layout comes from one seam (<see cref="LevelLayout"/>): the parity path
/// returns the shipped map, R27's synthesizer returns a seeded one, and every
/// stage after that seam treats the two identically — same gates, same
/// grouping, same emission. Failure anywhere DISCARDS the run: no scene saved,
/// created assets deleted (R29's "nothing emitted").
/// </summary>
public static class GenerationPipeline
{
    // ------------------------------------------------------------------ results

    public struct StageResult
    {
        public bool ok;
        public bool skipped;
        public string message;

        public static StageResult Ok(string msg) => new StageResult { ok = true, message = msg };
        public static StageResult Skip(string msg) => new StageResult { ok = true, skipped = true, message = msg };
        public static StageResult Fail(string msg) => new StageResult { ok = false, message = msg };
    }

    public class Context
    {
        public LevelBlueprint blueprint;
        public EnvPack theme;              // drawn in the theme stage; null = undressed
        public WeatherPreset weather;      // drawn preset; null = authored look (R13 null preset)
        public LevelLayout layout;
        public Transform levelContainer;
        public Transform coreTarget;
        public List<Corehold.Core.PathRoute> routes = new List<Corehold.Core.PathRoute>();
        public string scenePath;
        public string levelAssetPath;      // set once the asset EXISTS (cleanup deletes it on failure)
        public bool sceneCreated;
        public bool sceneSaved;
        public List<string> dressingStillBlocked;   // pads the occlusion self-repair could not save
        public BalanceModelRunner.Result model;     // the emission stage's model run; gate 3 judges it
    }

    public struct Stage
    {
        public string title;
        public string ticket;
        public Func<Context, StageResult> run;
    }

    // ------------------------------------------------------------------- stages

    /// <summary>Ordered stage list. Rendering and execution both read this.</summary>
    public static readonly Stage[] Stages =
    {
        new Stage { title = "Validate blueprint",        ticket = "R25/R29", run = StValidate },
        new Stage { title = "Draw theme & weather",      ticket = "P6",      run = StDraw },
        new Stage { title = "New scene + containers",    ticket = "R26",     run = StNewScene },
        new Stage { title = "Scene skeleton",            ticket = "R26",     run = StSkeleton },
        new Stage { title = "Protected structure",       ticket = "R26",     run = StProtected },
        new Stage { title = "Routes + spawners",         ticket = "R26/R27", run = StRoutes },
        new Stage { title = "GATE 1 — clearance",        ticket = "R29",     run = StGate1 },
        new Stage { title = "Hardpoints",                ticket = "R26/R28", run = StPads },
        new Stage { title = "GATE 2 — coverage",         ticket = "R28/R29", run = StGate2 },
        new Stage { title = "Camera framing",            ticket = "R26",     run = StCamera },
        new Stage { title = "Floor fit + theme ground",  ticket = "R11/R26", run = StGround },
        new Stage { title = "Dressing",                  ticket = "R26/R28", run = StDressing },
        new Stage { title = "GATE 2b — occlusion re-run", ticket = "R28",    run = StOcclusion },
        new Stage { title = "Weather",                   ticket = "R13",     run = StWeather },
        new Stage { title = "Group & verify hierarchy",  ticket = "R26",     run = StHierarchy },
        new Stage { title = "Emit LevelDefinition",      ticket = "R30",     run = StEmitLevel },
        new Stage { title = "GATE 3 — model margins",    ticket = "R29/R30", run = StModelGate },
        new Stage { title = "Save scene",                ticket = "R29",     run = StSave },
    };

    /// <summary>
    /// Run every stage in order. A failure stops the run AND discards it (R29:
    /// a blueprint that fails any stage emits no scene) — the half-built scene
    /// is closed unsaved and any created LevelDefinition asset is deleted, so
    /// the only artifacts a failed run leaves are its report lines.
    /// </summary>
    public static List<(Stage stage, StageResult result)> RunAll(
        LevelBlueprint blueprint, Action<Stage, StageResult> onStage = null)
    {
        var ctx = new Context { blueprint = blueprint };
        var results = new List<(Stage, StageResult)>();

        foreach (Stage stage in Stages)
        {
            StageResult r;
            try { r = stage.run(ctx); }
            catch (Exception ex) { r = StageResult.Fail($"{ex.GetType().Name}: {ex.Message}"); }

            results.Add((stage, r));
            onStage?.Invoke(stage, r);
            if (!r.ok)
            {
                var discard = Discard(ctx);
                results.Add((new Stage { title = "Discard", ticket = "R29", run = null }, discard));
                onStage?.Invoke(results[results.Count - 1].Item1, discard);
                break;
            }
        }
        return results;
    }

    /// <summary>Failure cleanup: nothing emitted means NOTHING — not a half-scene, not a stray asset.</summary>
    private static StageResult Discard(Context ctx)
    {
        var notes = new List<string>();

        if (!string.IsNullOrEmpty(ctx.levelAssetPath) &&
            AssetDatabase.LoadAssetAtPath<LevelDefinition>(ctx.levelAssetPath) != null)
        {
            AssetDatabase.DeleteAsset(ctx.levelAssetPath);
            notes.Add($"deleted {ctx.levelAssetPath}");
        }

        if (ctx.sceneCreated && !ctx.sceneSaved)
        {
            // Replacing the unsaved scene with an empty one is the discard —
            // there is no file to delete because none was ever written.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            notes.Add("half-built scene closed unsaved");
        }

        notes.Add("re-seed rather than repair (R29) — try Seed +1");
        return StageResult.Ok(string.Join("; ", notes));
    }

    // ---------------------------------------------------------- deterministic draw

    /// <summary>
    /// FNV-1a over the seed plus a purpose string. Plain integer math, so it is
    /// identical on every device — System.Random's algorithm is not contractually
    /// stable, and R37 (daily seed) needs draws to agree across platforms.
    /// </summary>
    internal static uint Fnv1a(int seed, string purpose)
    {
        unchecked
        {
            uint h = 2166136261;
            for (int i = 0; i < 4; i++) { h ^= (byte)(seed >> (i * 8)); h *= 16777619; }
            foreach (char c in purpose) { h ^= (byte)c; h *= 16777619; }
            return h;
        }
    }

    /// <summary>
    /// The theme this blueprint's seed draws. SORTED by themeName before indexing —
    /// pool order is an inspector array a human can reorder, and reordering must
    /// never change what a seed produces (P6 determinism).
    /// </summary>
    public static EnvPack DrawTheme(LevelBlueprint b)
    {
        if (b == null || b.envPackPool == null)
            return null;
        var pool = b.envPackPool.Where(p => p != null)
            .OrderBy(p => string.IsNullOrEmpty(p.themeName) ? p.name : p.themeName,
                     StringComparer.Ordinal)
            .ToList();
        if (pool.Count == 0)
            return null;
        return pool[(int)(Fnv1a(b.randomSeed, "theme") % (uint)pool.Count)];
    }

    /// <summary>
    /// The weather the seed draws: the blueprint's pool is an OVERRIDE, otherwise
    /// the theme's own pool decides (that is what keeps an ice map off desert
    /// dust). Null = the R13 null preset — the authored look.
    /// </summary>
    public static WeatherPreset DrawWeather(LevelBlueprint b, EnvPack theme)
    {
        WeatherPreset[] source =
            (b != null && b.weatherPool != null && b.weatherPool.Length > 0)
                ? b.weatherPool
                : theme != null ? theme.weatherPool : null;

        if (source == null)
            return null;
        var pool = source.Where(w => w != null)
            .OrderBy(w => w.name, StringComparer.Ordinal).ToList();
        if (pool.Count == 0)
            return null;
        return pool[(int)(Fnv1a(b.randomSeed, "weather") % (uint)pool.Count)];
    }

    // ----------------------------------------------------------- implementations

    private static StageResult StValidate(Context ctx)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        GenerateLevel.ValidateBlueprint(ctx.blueprint, errors, warnings);
        if (errors.Count > 0)
            return StageResult.Fail(string.Join("\n", errors));
        return StageResult.Ok(warnings.Count == 0
            ? "blueprint valid"
            : $"blueprint valid, {warnings.Count} warning(s):\n{string.Join("\n", warnings)}");
    }

    private static StageResult StDraw(Context ctx)
    {
        ctx.theme = DrawTheme(ctx.blueprint);
        ctx.weather = DrawWeather(ctx.blueprint, ctx.theme);

        if (ctx.theme == null)
            return StageResult.Skip("envPackPool empty — generating undressed, authored look");

        string themeLabel = string.IsNullOrEmpty(ctx.theme.themeName) ? ctx.theme.name : ctx.theme.themeName;
        return StageResult.Ok($"theme '{themeLabel}', weather " +
                              (ctx.weather != null ? $"'{ctx.weather.name}'" : "null preset (authored look)"));
    }

    private static StageResult StNewScene(Context ctx)
    {
        // Never build over someone's open work — this is a team tool.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return StageResult.Fail("cancelled — the open scene has unsaved changes");

        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        ctx.sceneCreated = true;

        // Containers FIRST, so everything the pipeline itself builds is parented
        // at creation — emitted grouped, not organised after the fact (R26).
        SceneContainers.AdoptAll();
        return StageResult.Ok("fresh scene; containers created, camera + light adopted");
    }

    private static StageResult StSkeleton(Context ctx)
    {
        string boot = PlayableBootstrapSetup.Run();
        SetupAudioDirector.Setup();
        SetupVFXDirector.Setup();
        string ui = BuildRealUI.Run();

        // These tools emit at the scene root (they predate the containers), so
        // adopt their output immediately — the scene is grouped at every stage
        // boundary, and the final verify pass proves nothing was missed.
        int swept = SceneContainers.AdoptAll();
        return StageResult.Ok($"singletons, directors, UI ({swept} root(s) adopted into containers)");
    }

    private static StageResult StProtected(Context ctx)
    {
        LevelBlueprint b = ctx.blueprint;
        string synthReport = null;
        ctx.layout = b.parityLayout
            ? ShippedLayout.Get(b)
            : RouteSynthesizer.Synthesize(b, out synthReport);

        // A null layout is a blueprint problem, not a seed problem — the
        // synthesizer says which field/foldWidth/target constraint is violated.
        if (ctx.layout == null)
            return StageResult.Fail("route synthesis refused this blueprint:\n" + synthReport);
        if (synthReport != null)
            Debug.Log("[R27] " + synthReport);

        // The level container lives under _Level, named for the blueprint —
        // "RefineryLevel" only for the parity map, which must mirror the scene.
        Transform levelRoot = SceneContainers.Ensure("_Level");
        string containerName = b.parityLayout ? "RefineryLevel" : $"Level_{Sanitise(b.name)}";
        var container = new GameObject(containerName);
        container.transform.SetParent(levelRoot, false);
        ctx.levelContainer = container.transform;

        Vector3 corePos = ctx.layout != null
            ? ctx.layout.corePos
            : LevelLayout.FromNormalized(b.protectedNormalizedPos, b.playfieldSize);

        ctx.coreTarget = RefineryDeltaBlockout.BuildCore(ctx.levelContainer, corePos, b.protectedPrefab);
        return StageResult.Ok($"'{containerName}' under _Level; Core at ({corePos.x:0.##}, {corePos.z:0.##})" +
                              (b.protectedPrefab != null ? $" using prefab '{b.protectedPrefab.name}'" : " (shipped stack)"));
    }

    private static StageResult StRoutes(Context ctx)
    {
        var log = new StringBuilder();
        var routesRoot = new GameObject("Routes");
        routesRoot.transform.SetParent(ctx.levelContainer, false);

        var colors = new[] { new Color(0.2f, 1f, 0.6f, 1f), new Color(0.2f, 0.6f, 1f, 1f) };
        for (int i = 0; i < ctx.layout.groundRoutes.Length; i++)
        {
            var route = RefineryDeltaBlockout.BuildRoute(
                routesRoot.transform, ctx.layout.routeNames[i], ctx.layout.groundRoutes[i],
                colors[i % colors.Length]);
            ctx.routes.Add(route);
        }

        // Two legs sharing a tail REQUIRE the R7 world-space tangent pin — the
        // AutoSmooth divergence is inherited wholesale by any merged pair (R27).
        string pinNote = "single route, no merge to pin";
        if (ctx.routes.Count == 2)
        {
            if (!MergeKnotPinning.Pin(ctx.routes[0], ctx.routes[1], out string pinReport))
                return StageResult.Fail("merge-knot pin failed:\n" + pinReport);
            pinNote = "merge pinned, shared tails identical (divergence gate PASS)";
        }

        // Spawners: created fresh (a generated scene has none to find), wired
        // by the same WireOne the shipped map used, parented under _Level.
        Transform levelRoot = SceneContainers.Ensure("_Level");
        Corehold.Core.PathRoute west = ctx.routes[0];
        Corehold.Core.PathRoute north = ctx.routes.Count > 1 ? ctx.routes[1] : null;
        RefineryDeltaBlockout.WireOne("Spawner_West", 0, west, ctx.coreTarget,
            ctx.layout.groundRoutes[0][0], log, levelRoot);
        if (north != null)
            RefineryDeltaBlockout.WireOne("Spawner_North", 1, north, ctx.coreTarget,
                ctx.layout.groundRoutes[1][0], log, levelRoot);
        if (ctx.blueprint.airCorridor)
            RefineryDeltaBlockout.WireOne("Spawner_Air", 2, null, ctx.coreTarget,
                ctx.layout.airSpawn, log, levelRoot);

        string lengths = string.Join(", ", ctx.routes.Select(r => $"{r.name} {r.Length:0.###} m"));
        return StageResult.Ok($"{lengths}; {pinNote}; spawners wired");
    }

    private static StageResult StGate1(Context ctx)
    {
        // Parity geometry is measured as-is — adjusting it would un-parity the
        // rebuild. Synthesized geometry gets the R29 loop: margin clamps only,
        // logged, ≤3 passes, full re-check between passes.
        string failure;
        string summary;
        if (ctx.blueprint.parityLayout)
        {
            failure = GenerationGates.CheckClearance(ctx.routes, ctx.blueprint, out summary);
        }
        else
        {
            failure = GenerationGates.AdjustAndRecheck(ctx.routes, ctx.blueprint,
                                                       out summary, out List<string> adjustments);
            if (adjustments.Count > 0)
                Debug.Log("[R29] Gate 1 knot adjustments:\n  " + string.Join("\n  ", adjustments));
            if (failure == null && adjustments.Count > 0)
                summary += $"; {adjustments.Count} knot(s) margin-clamped (logged)";
        }

        if (failure != null)
            return StageResult.Fail(failure);
        return StageResult.Ok(summary);
    }

    private static StageResult StPads(Context ctx)
    {
        RefineryDeltaBlockout.HP[] pads = ctx.layout.pads;
        string how = "parity set";

        if (pads == null)
        {
            // R28: clearance-filtered candidates, scored by the real validator,
            // classified from measurement, picked deterministically.
            pads = HardpointSelector.Select(ctx.blueprint, ctx.routes,
                                            ctx.layout.corePos, out string selReport);
            if (pads == null)
                return StageResult.Fail(selReport);
            Debug.Log("[R28] Hardpoint selection:\n" + selReport);
            how = "selected from measured coverage";
        }

        var log = new StringBuilder();
        // The gizmo de-duplicates shared spans, and both routes share the snake,
        // so pads are checked against the primary route — the shipped convention.
        bool satisfied = RefineryDeltaBlockout.BuildHardpoints(
            ctx.levelContainer, ctx.routes[0], pads, log);

        // The rule verdict belongs to GATE 2; this stage only reports placement.
        return StageResult.Ok($"{pads.Length} pads placed ({how})" +
                              (satisfied ? "" : " — coverage shortfalls, gate 2 will judge"));
    }

    private static StageResult StGate2(Context ctx)
    {
        string failure = GenerationGates.CheckCoverage(ctx.blueprint, out string summary);
        if (failure != null)
            return StageResult.Fail(failure);
        return StageResult.Ok(summary);
    }

    private static StageResult StCamera(Context ctx)
    {
        string log = CameraFramingSetup.Run();
        return StageResult.Ok(Summarise(log, "camera framed against generated content"));
    }

    private static StageResult StGround(Context ctx)
    {
        GroundAndSkirt.FitGroundAndFog();      // frustum-sized, never the design box (R11)

        if (ctx.theme == null)
            return StageResult.Ok("floor fit to frustum; no theme, shipped ground kept");

        var floor = SceneLookup.Find("Floor");
        var renderer = floor != null ? floor.GetComponent<Renderer>() : null;
        if (renderer == null)
            return StageResult.Fail("no Floor renderer to apply the theme ground to");

        var notes = new List<string> { "floor fit to frustum" };

        if (ctx.theme.groundMaterial != null)
        {
            renderer.sharedMaterial = ctx.theme.groundMaterial;   // scene slot → asset ref; edits nothing
            notes.Add($"material '{ctx.theme.groundMaterial.name}'");
        }

        // Tiling must be recomputed per map (the fit differs every time), and it
        // must go through an MPB: renderer.material leaks an instance, and
        // sharedMaterial edits would retile every map using the asset.
        Bounds bounds = renderer.bounds;
        Vector2 tiling = ctx.theme.GroundTilingFor(new Vector2(bounds.size.x, bounds.size.z));
        if (tiling != Vector2.zero)
        {
            var mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);
            mpb.SetVector("_BaseMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            renderer.SetPropertyBlock(mpb);
            notes.Add($"tiling {tiling.x:0.#}×{tiling.y:0.#}");
        }

        if (ctx.theme.groundPrefab != null)
            notes.Add("groundPrefab NOT honoured yet — the frustum fit sizes the primitive plane, and an " +
                      "arbitrary ground mesh needs its own fit rule; the material + tiling channels are live");

        return StageResult.Ok(string.Join(", ", notes));
    }

    private static StageResult StDressing(Context ctx)
    {
        GroundAndSkirt.BuildSilhouetteBand();  // far-band silhouettes (R11)

        if (ctx.blueprint.parityLayout)
        {
            // Parity dressing is the shipped set, placed by the same builders.
            RefineryDeltaBlockout.BuildStructures(ctx.levelContainer);
            RefineryDeltaBlockout.BuildWreckAndRadar(ctx.levelContainer);
            return StageResult.Ok("silhouette band + shipped structures/narrative (parity set)");
        }

        if (ctx.theme == null)
            return StageResult.Skip("no theme drawn — undressed beyond the silhouette band");

        string dressLog = PropPlacer.Dress(ctx.blueprint, ctx.theme, ctx.levelContainer,
                                           ctx.routes, ctx.layout.corePos, out ctx.dressingStillBlocked);
        Debug.Log("[R28] Dressing:\n" + dressLog);

        int lines = 0;
        foreach (char c in dressLog)
            if (c == '\n') lines++;
        return StageResult.Ok($"themed dressing from '{ctx.theme.themeName}' placed " +
                              $"(occlusion self-repair ran; details in the console, {lines} line(s))");
    }

    private static StageResult StOcclusion(Context ctx)
    {
        // GATE 2b (R28): coverage re-run THROUGH the sight-line test. The
        // distance count cannot see occluders — a 12 m tank between a pad and
        // its route still "covers" by distance — so every pad is recounted
        // with the placed props as occluder cylinders. The placer already
        // self-repaired by removing offenders; anything still short here means
        // the dressing and the pads cannot coexist on this seed.
        if (ctx.dressingStillBlocked != null && ctx.dressingStillBlocked.Count > 0)
            return StageResult.Fail("pads still sight-blocked after dressing repair:\n  • " +
                                    string.Join("\n  • ", ctx.dressingStillBlocked));

        var props = UnityEngine.Object.FindObjectsByType<Corehold.Systems.PlacedProp>(FindObjectsSortMode.None);
        var occluders = new List<Corehold.Towers.HardpointCoverageGizmo.Occluder>();
        foreach (var p in props)
            occluders.Add(new Corehold.Towers.HardpointCoverageGizmo.Occluder
            {
                position = p.transform.position,
                radius = p.placedFootprintRadius,
                height = p.placedHeight,
            });

        var shortfalls = new List<string>();
        foreach (var pad in UnityEngine.Object.FindObjectsByType<Corehold.Towers.HardpointCoverageGizmo>(FindObjectsSortMode.None))
        {
            int need = pad.padClass == Corehold.Towers.HardpointCoverageGizmo.PadClass.Premium ? 4 : 2;
            int have = pad.CountCoveredSpansOnCurve(occluders);
            if (have < need)
                shortfalls.Add($"{pad.name}: {have}/{need} spans with sight lines applied");
        }

        if (shortfalls.Count > 0)
            return StageResult.Fail("occlusion re-run failed:\n  • " + string.Join("\n  • ", shortfalls));

        if (occluders.Count == 0)
            return StageResult.Ok(ctx.blueprint.parityLayout
                ? "0 measured occluders (parity structures carry no PlacedProp markers — the validated shipped set)"
                : "0 occluders placed — plain recount holds");

        return StageResult.Ok($"all pads keep their class through {occluders.Count} occluder(s)");
    }

    private static StageResult StWeather(Context ctx)
    {
        SetupWeather.Setup();

        var applier = UnityEngine.Object.FindFirstObjectByType<Corehold.Systems.WeatherApplier>();
        if (applier == null)
            return StageResult.Fail("SetupWeather ran but no WeatherApplier found in the scene");

        var so = new SerializedObject(applier);
        SerializedProperty presetProp = so.FindProperty("preset");
        if (presetProp == null)
            return StageResult.Fail("WeatherApplier has no 'preset' field — R13 contract changed?");
        presetProp.objectReferenceValue = ctx.weather;
        so.ApplyModifiedPropertiesWithoutUndo();

        return StageResult.Ok(ctx.weather != null
            ? $"applier wired to '{ctx.weather.name}' (applies at map load)"
            : "applier wired to the null preset — authored look, pixel-identical (R13)");
    }

    private static StageResult StHierarchy(Context ctx)
    {
        // Pass 1 sweeps anything the sub-tools left at the root; pass 2 is the
        // R26 verify — it must find NOTHING to do. A non-zero verify means a
        // tool emitted a root the shared table does not know, and the fix is
        // one line in SceneContainers.Groups, not a hand-tidied scene.
        var sweep = OrganizeHierarchy.Organize();
        var verify = OrganizeHierarchy.Organize();

        if (verify.moved > 0)
            return StageResult.Fail($"verify pass still moved {verify.moved} object(s) — grouping is not stable");
        if (verify.unclaimed.Count > 0)
            return StageResult.Fail("unrecognised root(s): " + string.Join(", ", verify.unclaimed) +
                                    " — add them to SceneContainers.Groups");

        return StageResult.Ok($"grouped ({sweep.moved} swept); verify pass moved 0, nothing unclaimed");
    }

    private static StageResult StEmitLevel(Context ctx)
    {
        LevelBlueprint b = ctx.blueprint;
        if (b.rulesTemplate == null)
            return StageResult.Fail("rulesTemplate is unassigned — validation should have caught this");

        const string dir = "Assets/_COREHOLD/Data/Levels/Generated";
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data/Levels", "Generated");

        string assetPath = $"{dir}/Level_{Sanitise(b.name)}_s{b.randomSeed}.asset";
        var clone = UnityEngine.Object.Instantiate(b.rulesTemplate);
        clone.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(clone, assetPath);
        ctx.levelAssetPath = assetPath;

        var wave = UnityEngine.Object.FindFirstObjectByType<Corehold.Core.WaveManager>();
        if (wave == null)
            return StageResult.Fail("no WaveManager in the scene to wire the LevelDefinition into");

        var so = new SerializedObject(wave);
        SerializedProperty levelProp = so.FindProperty("level");
        if (levelProp == null)
            return StageResult.Fail("WaveManager has no 'level' field — wiring contract changed?");
        levelProp.objectReferenceValue = clone;
        so.ApplyModifiedPropertiesWithoutUndo();

        // ---- the R30 model run: solve for generated maps, verify for parity --
        Vector3 airSpawn = ctx.layout.airSpawn;
        Vector3 coreXZ = ctx.coreTarget.position;

        if (b.parityLayout)
        {
            // Parity keeps the shipped rules VERBATIM — solving would
            // un-parity them. The model still runs, as a verification.
            ctx.model = BalanceModelRunner.Run(ctx.routes, airSpawn, coreXZ,
                solveGrowth: false, hpGrowth: clone.hpGrowthPerWave,
                maxLive: clone.maxLiveEnemies, out string verifyError);
            if (ctx.model == null)
                return StageResult.Fail(verifyError);

            return StageResult.Ok($"'{b.rulesTemplate.name}' cloned VERBATIM → {assetPath}; model verified " +
                                  $"at growth {clone.hpGrowthPerWave:0.###} (gate 3 judges the margins)");
        }

        int derivedMaxLive = BalanceModelRunner.DeriveMaxLive(ctx.routes, b.airCorridor);
        ctx.model = BalanceModelRunner.Run(ctx.routes, airSpawn, coreXZ,
            solveGrowth: true, hpGrowth: 0f, maxLive: derivedMaxLive, out string error);
        if (ctx.model == null)
            return StageResult.Fail(error);
        if (!ctx.model.solved || ctx.model.solved_hp_growth <= 0f)
            return StageResult.Fail("model ran but did not report a solved hpGrowthPerWave — contract drift?");

        clone.hpGrowthPerWave = ctx.model.solved_hp_growth;
        clone.maxLiveEnemies = derivedMaxLive;
        EditorUtility.SetDirty(clone);
        AssetDatabase.SaveAssets();

        return StageResult.Ok($"cloned '{b.rulesTemplate.name}' → {assetPath}; SOLVED hpGrowthPerWave = " +
                              $"{ctx.model.solved_hp_growth:0.####} (close targeted mid-band), " +
                              $"maxLiveEnemies = {derivedMaxLive} (derived from route capacity)");
    }

    private static StageResult StModelGate(Context ctx)
    {
        // GATE 3 (R29/R30): the per-wave margins, judged from the SAME model
        // run the emission stage performed — one subprocess, one verdict. The
        // margin math lives only in docs/balance_model.py; a second
        // implementation here is exactly the drift R1 exists to prevent.
        if (ctx.model == null)
            return StageResult.Fail("the model never ran — emission should have invoked it");

        if (!ctx.model.in_band)
        {
            var flagged = new List<string>();
            foreach (var row in ctx.model.rows)
                if (row.flags != null && row.flags.Length > 0)
                    flagged.Add($"wave {row.wave}: margin {row.margin:0.00} [{string.Join(",", row.flags)}]" +
                                (string.IsNullOrEmpty(row.worst_group) ? "" : $" worst={row.worst_group}"));
            return StageResult.Fail("margins out of band at the solved/verified growth — this geometry " +
                                    "cannot be balanced by growth alone; reseed (R29):\n  • " +
                                    string.Join("\n  • ", flagged));
        }

        float open = ctx.model.rows[0].margin;
        float close = ctx.model.rows[ctx.model.rows.Length - 1].margin;
        return StageResult.Ok($"all {ctx.model.rows.Length} waves in band at growth " +
                              $"{ctx.model.hp_growth_used:0.####} — opens {open:0.00}, closes {close:0.00}");
    }

    private static StageResult StSave(Context ctx)
    {
        LevelBlueprint b = ctx.blueprint;
        const string dir = "Assets/_COREHOLD/Scenes/Generated";
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Scenes", "Generated");

        ctx.scenePath = $"{dir}/{Sanitise(b.name)}_s{b.randomSeed}.unity";

        Scene scene = SceneManager.GetActiveScene();
        if (!EditorSceneManager.SaveScene(scene, ctx.scenePath))
            return StageResult.Fail($"SaveScene refused {ctx.scenePath}");

        ctx.sceneSaved = true;
        AssetDatabase.SaveAssets();
        return StageResult.Ok($"{ctx.scenePath} — press Play to run it");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>First line of a sub-tool's log, or a fallback — stage rows are one-liners.</summary>
    private static string Summarise(string toolLog, string fallback)
    {
        if (string.IsNullOrEmpty(toolLog))
            return fallback;
        int nl = toolLog.IndexOf('\n');
        return nl < 0 ? toolLog : toolLog.Substring(0, nl);
    }

    internal static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }
}
