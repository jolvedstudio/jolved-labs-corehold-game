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
    /// <summary>Where generated scenes are saved — and the prefix Build Settings pruning trusts.</summary>
    private const string GeneratedDir = "Assets/_COREHOLD/Scenes/Generated";

    /// <summary>The air spawner's index in every shipped wave table.</summary>
    private const int AirSpawnerIndex = 2;

    /// <summary>
    /// Spawner index for ground approach <paramref name="sector"/>. The shipped
    /// tables address ground 0 and 1 and air 2, so ground approaches beyond the
    /// second step over the air index and continue at 3.
    /// </summary>
    internal static int GroundSpawnerIndex(int sector) =>
        sector < AirSpawnerIndex ? sector : sector + 1;

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
        public bool gate;                  // the R29 gates — surfaced distinctly in every UI
        public Func<Context, StageResult> run;
    }

    /// <summary>One executed stage: what ran, what happened, how long it took.</summary>
    public struct StageRun
    {
        public Stage stage;
        public StageResult result;
        public float seconds;
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
        new Stage { title = "GATE 1 — clearance",        ticket = "R29",     run = StGate1, gate = true },
        new Stage { title = "Hardpoints",                ticket = "R26/R28", run = StPads },
        new Stage { title = "GATE 2 — coverage",         ticket = "R28/R29", run = StGate2, gate = true },
        new Stage { title = "Camera framing",            ticket = "R26",     run = StCamera },
        new Stage { title = "Floor fit + theme ground",  ticket = "R11/R26", run = StGround },
        new Stage { title = "Dressing",                  ticket = "R26/R28", run = StDressing },
        new Stage { title = "GATE 2b — occlusion re-run", ticket = "R28",    run = StOcclusion, gate = true },
        new Stage { title = "Weather",                   ticket = "R13",     run = StWeather },
        new Stage { title = "Group & verify hierarchy",  ticket = "R26",     run = StHierarchy },
        new Stage { title = "Emit LevelDefinition",      ticket = "R30",     run = StEmitLevel },
        new Stage { title = "GATE 3 — model margins",    ticket = "R29/R30", run = StModelGate, gate = true },
        new Stage { title = "Save scene",                ticket = "R29",     run = StSave },
    };

    /// <summary>
    /// Run every stage in order. A failure stops the run AND discards it (R29:
    /// a blueprint that fails any stage emits no scene) — the half-built scene
    /// is closed unsaved and any created LevelDefinition asset is deleted, so
    /// the only artifacts a failed run leaves are its report lines.
    ///
    /// Progress paints through <see cref="GenerationProgress"/>'s cancelable
    /// bar (a synchronous run cannot repaint an editor window), stages are
    /// timed, and a user CANCEL is handled as a failure at the current stage —
    /// the same discard path, so an abandoned run leaves nothing behind.
    /// </summary>
    public static List<StageRun> RunAll(
        LevelBlueprint blueprint, Action<Stage, StageResult> onStage = null)
    {
        var ctx = new Context { blueprint = blueprint };
        var results = new List<StageRun>();
        var watch = new System.Diagnostics.Stopwatch();

        GenerationProgress.Begin(Stages.Length);

        // Tell the scene-setup tools they are pipeline steps, not menu actions:
        // they must not open Game.unity over the scene being generated, and must
        // not save (this pipeline owns its save stage). See GenerationDriven.
        using (GenerationDriven.Scope())
        {
            try
            {
                RunStages(ctx, results, watch, onStage);
            }
            finally
            {
                GenerationProgress.End();
            }
        }
        return results;
    }

    /// <summary>The stage loop itself — extracted so the driven-scope and the
    /// progress-bar teardown read as the plain nesting they are.</summary>
    private static void RunStages(Context ctx, List<StageRun> results,
                                  System.Diagnostics.Stopwatch watch,
                                  Action<Stage, StageResult> onStage)
    {
        for (int i = 0; i < Stages.Length; i++)
        {
            Stage stage = Stages[i];
            bool keepGoing = GenerationProgress.Stage(i, $"{stage.title}  ({stage.ticket})");

            StageResult r;
            watch.Restart();
            if (!keepGoing)
            {
                r = StageResult.Fail("cancelled by user");
            }
            else
            {
                try { r = stage.run(ctx); }
                catch (Exception ex) { r = StageResult.Fail($"{ex.GetType().Name}: {ex.Message}"); }

                // A stage may also have returned early because the user hit
                // Cancel mid-stage (the grid, the model subprocess).
                if (r.ok && GenerationProgress.Cancelled)
                    r = StageResult.Fail("cancelled by user");
            }
            watch.Stop();

            results.Add(new StageRun { stage = stage, result = r, seconds = (float)watch.Elapsed.TotalSeconds });
            onStage?.Invoke(stage, r);

            if (!r.ok)
            {
                var discard = Discard(ctx);
                results.Add(new StageRun
                {
                    stage = new Stage { title = "Discard", ticket = "R29", run = null },
                    result = discard,
                    seconds = 0f,
                });
                onStage?.Invoke(results[results.Count - 1].stage, discard);
                break;
            }
        }
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

        // Single-mode NewScene should leave exactly one scene loaded. If it did
        // not, say so LOUDLY rather than generating quietly alongside a
        // stranger: every query in this pipeline is scoped to the active scene,
        // but a second loaded scene still means the Hierarchy, the Game view and
        // anything the human eyeballs are showing two maps at once.
        int loaded = SceneManager.sceneCount;
        if (loaded > 1)
        {
            var others = new List<string>();
            for (int i = 0; i < loaded; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s != SceneManager.GetActiveScene())
                    others.Add(string.IsNullOrEmpty(s.name) ? "<untitled>" : s.name);
            }
            return StageResult.Ok($"fresh scene; containers created — NOTE: {loaded} scenes are loaded " +
                                  $"({string.Join(", ", others)} alongside it). Generation is scoped to the " +
                                  "active scene, but close the others to keep the Hierarchy honest.");
        }

        return StageResult.Ok("fresh scene; containers created, camera + light adopted");
    }

    private static StageResult StSkeleton(Context ctx)
    {
        string boot = PlayableBootstrapSetup.Run();
        SetupAudioDirector.Setup();
        SetupVFXDirector.Setup();
        string ui = BuildRealUI.Run();

        // The setup tools do not cover the whole singleton set — WaveManager,
        // PoolRegistry, RouteTraffic and DebugConsole only ever existed in the
        // forbidden BuildGameScene scaffolding, so a generated scene had nothing
        // to run its waves (R26's "full live object set").
        string singletons = SceneSkeleton.EnsureSingletons();

        // BuildRealUI emits an EventSystem carrying the LEGACY StandaloneInputModule,
        // which throws every frame under the new Input System and leaves the UI
        // dead — the menu renders and nothing responds to a click.
        FixEventSystemInputModule.Run();

        // These tools emit at the scene root (they predate the containers), so
        // adopt their output immediately — the scene is grouped at every stage
        // boundary, and the final verify pass proves nothing was missed.
        int swept = SceneContainers.AdoptAll();
        return StageResult.Ok($"directors, UI, {singletons} " +
                              $"({swept} root(s) adopted into containers)");
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
        // Siege approaches never share a tail, so there is no join to pin.
        string pinNote = ctx.layout.sharedTail ? null : "no merge (approaches converge only at the Core)";
        if (ctx.layout.sharedTail && ctx.routes.Count == 2)
        {
            if (!MergeKnotPinning.Pin(ctx.routes[0], ctx.routes[1], out string pinReport))
                return StageResult.Fail("merge-knot pin failed:\n" + pinReport);
            pinNote = "merge pinned, shared tails identical (divergence gate PASS)";
        }
        pinNote ??= "single route, no merge to pin";

        // Spawners: created fresh (a generated scene has none to find), wired
        // by the same WireOne the shipped map used, parented under _Level.
        //
        // Index 2 is the AIR spawner in every shipped wave table, so ground
        // approaches step over it rather than take it. Renumbering air instead
        // would silently send the air groups of every existing table down a
        // ground route.
        Transform levelRoot = SceneContainers.Ensure("_Level");
        for (int i = 0; i < ctx.routes.Count; i++)
        {
            RefineryDeltaBlockout.WireOne($"Spawner_{ctx.layout.routeNames[i].Replace("Route_", "")}",
                GroundSpawnerIndex(i), ctx.routes[i], ctx.coreTarget,
                ctx.layout.groundRoutes[i][0], log, levelRoot);
        }
        if (ctx.blueprint.airCorridor)
            RefineryDeltaBlockout.WireOne("Spawner_Air", AirSpawnerIndex, null, ctx.coreTarget,
                ctx.layout.airSpawn, log, levelRoot);

        // Spawners exist only now, so this is where the WaveManager learns about
        // them — by spawner INDEX, because the wave tables address them that way.
        string spawnerWiring = SceneSkeleton.WireSpawners();

        string lengths = string.Join(", ", ctx.routes.Select(r => $"{r.name} {r.Length:0.###} m"));
        return StageResult.Ok($"{lengths}; {pinNote}; {spawnerWiring}");
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

        // A TowerHardpoint has no renderer of its own, so without this the build
        // pads are INVISIBLE and the player cannot see where to build. The disc
        // also becomes the pad's rimRenderer, which is what pulses while empty
        // (GDD §5.3) — the shipped scene's eight PadMarkers come from here.
        HardpointMarkers.Run();

        // The rule verdict belongs to GATE 2; this stage only reports placement.
        return StageResult.Ok($"{pads.Length} pads placed ({how}) with visible PadMarkers" +
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
        // GroundAndSkirt only FINDS and fits a Floor — it never creates one, and
        // a generated scene has none, so the level had no ground at all.
        var notes = new List<string>();
        // SceneQuery.FindGround, never a bare name search: the pads (a vendor
        // floor-cache prefab) and the Core platform are built BEFORE this stage,
        // and a mesh named "Floor" inside one of them was being adopted as the
        // ground — 43 m of prop wearing the terrain material, with no real
        // ground ever created.
        GameObject floor = SceneQuery.FindGround();
        bool fromPrefab = false;

        if (floor == null)
        {
            GameObject groundPrefab = ctx.theme != null ? ctx.theme.groundPrefab : null;
            if (groundPrefab != null)
            {
                floor = (GameObject)PrefabUtility.InstantiatePrefab(groundPrefab);
                fromPrefab = true;
                notes.Add($"ground from theme prefab '{groundPrefab.name}'");
            }
            else
            {
                floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                notes.Add("ground plane created");
            }
            floor.name = "Floor";
            floor.transform.SetParent(SceneContainers.Ensure("_Level"), false);
            floor.transform.position = Vector3.zero;
            floor.AddComponent<Corehold.Systems.LevelGround>().source =
                fromPrefab ? "theme groundPrefab" : "primitive plane";
            Undo.RegisterCreatedObjectUndo(floor, "Generate Level");
        }
        else
        {
            notes.Add($"existing ground reused ('{floor.name}')");
        }

        GroundAndSkirt.FitGroundAndFog();      // frustum-sized, never the design box (R11)

        // FitGroundAndFog assumes a Unity plane (10 m per unit of scale). An
        // arbitrary ground mesh needs its own fit, measured from what it
        // actually is, so re-fit from renderer bounds when the theme supplied one.
        if (fromPrefab)
            notes.Add(FitByBounds(floor));

        // Combined bounds, not the first renderer's: a ground prefab may be
        // several meshes, and both the extent report and the tiling need what
        // the whole ground actually covers.
        var renderers = floor.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return StageResult.Fail($"the ground object '{floor.name}' has no renderer — " +
                                    "it cannot be seen, and a theme material would have nothing to apply to");

        Bounds worldBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            worldBounds.Encapsulate(renderers[i].bounds);
        notes.Add($"covers {worldBounds.size.x:0} × {worldBounds.size.z:0} m");

        if (ctx.theme == null)
            return StageResult.Ok(string.Join(", ", notes) + "; no theme, default ground kept");

        // A GROUND PREFAB SHIPS ITS OWN LOOK. It was authored with its material
        // and UV tiling already right, so the only thing it needs from the
        // generator is the frustum fit. Applying the pack's material over it
        // would discard that authoring — and on a multi-mesh ground it could
        // only reach one submesh, which is worse than either choice. So the
        // material and tiling channels exist FOR THE PLANE PATH, and are inert
        // whenever a prefab is supplied.
        if (fromPrefab)
        {
            if (ctx.theme.groundMaterial != null || ctx.theme.groundTilingPerMetre > 0f)
                notes.Add("groundMaterial/tiling ignored — the prefab carries its own look; " +
                          "those fields apply only when groundPrefab is empty");
            return StageResult.Ok(string.Join(", ", notes));
        }

        // ---- plane path: the pack's material + tiling ARE the ground's look --
        if (ctx.theme.groundMaterial != null)
        {
            foreach (Renderer r in renderers)
                r.sharedMaterial = ctx.theme.groundMaterial;   // scene slot → asset ref; edits nothing
            notes.Add($"material '{ctx.theme.groundMaterial.name}'");
        }
        else
        {
            notes.Add("no groundMaterial on the theme — default plane material");
        }

        // Tiling must be recomputed per map (the fit differs every time), and it
        // must go through an MPB: renderer.material leaks an instance, and
        // sharedMaterial edits would retile every map using the asset.
        Vector2 tiling = ctx.theme.GroundTilingFor(new Vector2(worldBounds.size.x, worldBounds.size.z));
        if (tiling != Vector2.zero)
        {
            var mpb = new MaterialPropertyBlock();
            foreach (Renderer r in renderers)
            {
                r.GetPropertyBlock(mpb);
                mpb.SetVector("_BaseMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
                r.SetPropertyBlock(mpb);
            }
            notes.Add($"tiling {tiling.x:0.#}×{tiling.y:0.#} " +
                      $"({ctx.theme.groundTilingPerMetre:0.##}/m over {worldBounds.size.x:0} m)");
        }

        return StageResult.Ok(string.Join(", ", notes));
    }

    /// <summary>
    /// Scale an arbitrary ground mesh so it covers the camera's ground
    /// footprint, measured from its own renderer bounds at its authored scale
    /// rather than assuming Unity-plane dimensions.
    /// </summary>
    private static string FitByBounds(GameObject floor)
    {
        Camera cam = SceneQuery.FirstInActiveScene<Camera>();
        if (cam == null)
            return "no camera to fit the ground prefab to";

        floor.transform.localScale = Vector3.one;
        var renderers = floor.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return "ground prefab has no renderer to measure";

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        float halfX = Mathf.Max(0.01f, b.size.x * 0.5f);
        float halfZ = Mathf.Max(0.01f, b.size.z * 0.5f);
        Vector2 need = GroundAndSkirt.RequiredHalfExtent(cam, out _);

        // Uniform, and rounded up: a non-uniform scale stretches ground UVs
        // unevenly, which reads as smeared texels under a fixed camera.
        float scale = Mathf.Ceil(Mathf.Max(need.x / halfX, need.y / halfZ) * 100f) / 100f;
        floor.transform.localScale = Vector3.one * scale;
        return $"prefab ground scaled ×{scale:0.##} to cover the frustum";
    }

    private static StageResult StDressing(Context ctx)
    {
        // Far-band silhouettes (R11). Parity gets the shipped refinery horizon
        // because that IS the shipped map; every other map gets its own theme's
        // silhouettes, or a bare horizon if the theme has none.
        if (ctx.blueprint.parityLayout)
            GroundAndSkirt.BuildSilhouetteBand();
        else
            GroundAndSkirt.BuildSilhouetteBand(ctx.theme, ctx.blueprint.randomSeed);

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

        var props = SceneQuery.InActiveScene<Corehold.Systems.PlacedProp>();
        var occluders = new List<Corehold.Towers.HardpointCoverageGizmo.Occluder>();
        foreach (var p in props)
            occluders.Add(new Corehold.Towers.HardpointCoverageGizmo.Occluder
            {
                position = p.transform.position,
                radius = p.placedFootprintRadius,
                height = p.placedHeight,
            });

        var shortfalls = new List<string>();
        var hidden = new List<string>();
        Camera cam = SceneQuery.FirstInActiveScene<Camera>();

        foreach (var pad in SceneQuery.InActiveScene<Corehold.Towers.HardpointCoverageGizmo>())
        {
            int need = pad.padClass == Corehold.Towers.HardpointCoverageGizmo.PadClass.Premium ? 4 : 2;
            int have = pad.CountCoveredSpansOnCurve(occluders);
            if (have < need)
                shortfalls.Add($"{pad.name}: {have}/{need} spans with sight lines applied");

            // Two DIFFERENT sight lines, and both matter: the turret's view of
            // the route (above) decides whether the pad works, the camera's view
            // of the pad (here) decides whether the player can find it at all.
            if (cam != null)
            {
                Vector3 padPoint = pad.transform.position +
                                   Vector3.up * Corehold.Towers.HardpointCoverageGizmo.PadVisibleHeight;
                if (Corehold.Towers.HardpointCoverageGizmo.LineBlocked(
                        cam.transform.position, padPoint, occluders))
                    hidden.Add(pad.name);
            }
        }

        if (shortfalls.Count > 0)
            return StageResult.Fail("occlusion re-run failed:\n  • " + string.Join("\n  • ", shortfalls));

        if (hidden.Count > 0)
            return StageResult.Fail("pads hidden from the camera by dressing — the player cannot see " +
                                    "where to build:\n  • " + string.Join("\n  • ", hidden));

        // Route occlusion, re-measured from the FINISHED scene rather than
        // trusting the placer's running total. The two can legitimately differ:
        // the placer charges a candidate when it places it, and the occlusion
        // repair may later delete a prop, which only ever frees route back up.
        // Re-measuring is what makes the budget a guarantee instead of a hope.
        float routeHiddenMetres = 0f, routeBudget = 0f, routeTotal = 0f;
        if (cam != null && ctx.routes.Count > 0)
        {
            List<Vector3> routeSamples = RouteVisibility.SampleRoutes(ctx.routes);
            routeTotal = RouteVisibility.TotalMetres(routeSamples);
            routeBudget = RouteVisibility.BudgetMetres(routeSamples);
            routeHiddenMetres = RouteVisibility.HiddenMetres(routeSamples, cam, occluders);

            if (routeHiddenMetres > routeBudget)
                return StageResult.Fail(
                    $"dressing hides {routeHiddenMetres:0.#} m of the {routeTotal:0} m route from the " +
                    $"camera, over the {routeBudget:0.#} m budget " +
                    $"({RouteVisibility.HiddenBudgetFraction:P0}) — the player cannot watch the " +
                    "approach. Reseed (R29).");
        }

        if (occluders.Count == 0)
            return StageResult.Ok(ctx.blueprint.parityLayout
                ? "0 measured occluders (parity structures carry no PlacedProp markers — the validated shipped set)"
                : "0 occluders placed — plain recount holds");

        return StageResult.Ok($"through {occluders.Count} occluder(s): every pad keeps its class and is " +
                              $"visible; {routeHiddenMetres:0.#} m of {routeTotal:0} m route hidden " +
                              $"(budget {routeBudget:0.#} m)");
    }

    private static StageResult StWeather(Context ctx)
    {
        SetupWeather.Setup();

        var applier = SceneQuery.FirstInActiveScene<Corehold.Systems.WeatherApplier>();
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

        var wave = SceneQuery.FirstInActiveScene<Corehold.Core.WaveManager>();
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

        // The wave tables address two ground spawners. A siege map has up to five,
        // so without this its extra approaches are built, gated, dressed — and
        // never walked. Regenerating the tables properly is R33; dealing the
        // existing groups out across the spawners that exist is the honest
        // interim, and it is recorded in the transcript rather than done quietly.
        clone.spreadGroundGroupsAcrossSpawners = ctx.routes.Count > 2;
        EditorUtility.SetDirty(clone);
        AssetDatabase.SaveAssets();

        return StageResult.Ok($"cloned '{b.rulesTemplate.name}' → {assetPath}; SOLVED hpGrowthPerWave = " +
                              $"{ctx.model.solved_hp_growth:0.####} (close targeted mid-band), " +
                              (clone.spreadGroundGroupsAcrossSpawners
                                  ? $"ground groups dealt across {ctx.routes.Count} approaches (R33 regenerates tables), "
                                  : "") +
                              $"maxLiveEnemies = {derivedMaxLive} (the shipped {BalanceModelRunner.ShippedMaxLive} " +
                              "scaled by route length, clamped to what fits)");
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
        if (!AssetDatabase.IsValidFolder(GeneratedDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Scenes", "Generated");

        ctx.scenePath = $"{GeneratedDir}/{Sanitise(b.name)}_s{b.randomSeed}.unity";

        Scene scene = SceneManager.GetActiveScene();
        if (!EditorSceneManager.SaveScene(scene, ctx.scenePath))
            return StageResult.Fail($"SaveScene refused {ctx.scenePath}");

        ctx.sceneSaved = true;
        AssetDatabase.SaveAssets();

        string build = RegisterInBuildSettings(ctx.scenePath);
        return StageResult.Ok($"{ctx.scenePath} — press Play to run it{build}");
    }

    /// <summary>
    /// Put the generated scene in Build Settings.
    ///
    /// Not a packaging nicety — <see cref="SceneManager.LoadScene(string)"/> only
    /// accepts scenes in that list, so an unregistered map plays fine but dies on
    /// Retry, which is the one control a tester presses most. Registering here
    /// keeps it with the save that created the scene.
    ///
    /// Entries under Scenes/Generated whose file is gone are dropped in the same
    /// pass, so deleting a map you did not like cleans up after itself instead of
    /// leaving the list to grow one dead row per seed.
    /// </summary>
    private static string RegisterInBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>();
        bool present = false;
        int pruned = 0;

        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (s.path == scenePath)
            {
                present = true;
            }
            else if (s.path.StartsWith(GeneratedDir, System.StringComparison.Ordinal) &&
                     AssetDatabase.LoadAssetAtPath<SceneAsset>(s.path) == null)
            {
                pruned++;
                continue;
            }
            scenes.Add(s);
        }

        if (present && pruned == 0)
            return ", already in Build Settings";

        if (!present)
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();

        string added = present ? "" : ", added to Build Settings so Retry can reload it";
        string gone = pruned > 0 ? $", {pruned} deleted generated scene(s) pruned from it" : "";
        return added + gone;
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
