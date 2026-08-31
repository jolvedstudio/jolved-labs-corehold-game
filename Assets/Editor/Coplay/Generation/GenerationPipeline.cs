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
/// Layout comes from one seam (<see cref="LevelLayout"/>): R27's synthesizer
/// returns a seeded layout, and every stage after that seam works from it —
/// gates, grouping, emission. (The R26 parity path, which used to return the
/// shipped map through the same seam, retired when the generator became
/// new-level-only.) Failure anywhere DISCARDS the run: no scene saved,
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

        // ---- scene-adapt intake (mode b) ----
        /// <summary>True when this run certifies an AUTHORED scene in place.
        /// Changes the repair policy (nudge/remove instead of reseed) and the
        /// discard policy (never close or delete the author's scene).</summary>
        public bool adaptMode;
        /// <summary>The authored props the adapt loop may move or remove —
        /// inventoried and stamped by the intake stage.</summary>
        public List<Corehold.Systems.PlacedProp> adaptProps;
        /// <summary>Interventions performed (moves + removals), for the report.</summary>
        public int adaptInterventions;
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
        new Stage { title = "Terrain relief",            ticket = "M-b",     run = TerrainStage.Run },
        new Stage { title = "Look & lighting",           ticket = "M-d",     run = LookStage.Run },
        new Stage { title = "Group & verify hierarchy",  ticket = "R26",     run = StHierarchy },
        new Stage { title = "Emit LevelDefinition",      ticket = "R30",     run = StEmitLevel },
        new Stage { title = "GATE 3 — model margins",    ticket = "R29/R30", run = StModelGate, gate = true },
        new Stage { title = "Save scene",                ticket = "R29",     run = StSave },
        new Stage { title = "Localize vendor assets",    ticket = "SHIP",    run = StLocalize },
    };

    /// <summary>
    /// Mode b's stage list (docs/generator_intake_modes.md): the intake and the
    /// dressing policy differ; every GATE and the whole back half are the very
    /// same functions the procedural list runs — one certifier, several front
    /// doors. No NewScene (the authored scene IS the scene), no theme ground,
    /// no weather/terrain/look (an authored scene owns its look), and the
    /// dressing stage adapts what stands instead of placing from a pack.
    /// </summary>
    public static readonly Stage[] AdaptStages =
    {
        new Stage { title = "Validate blueprint",        ticket = "R25/R29", run = StValidate },
        new Stage { title = "Draw theme & weather",      ticket = "P6",      run = StDraw },
        new Stage { title = "Adapt intake",              ticket = "MODE-B",  run = SceneAdapt.StIntake },
        new Stage { title = "Scene skeleton",            ticket = "R26",     run = StSkeleton },
        new Stage { title = "Layout from authored core", ticket = "MODE-B",  run = SceneAdapt.StLayout },
        new Stage { title = "Routes + spawners",         ticket = "R26/R27", run = StRoutes },
        new Stage { title = "GATE 1 — clearance",        ticket = "R29",     run = StGate1, gate = true },
        new Stage { title = "Adopt authored pads",       ticket = "MODE-B",  run = SceneAdapt.StPadsAdopt },
        new Stage { title = "GATE 2 — coverage",         ticket = "R28/R29", run = StGate2, gate = true },
        new Stage { title = "Camera framing",            ticket = "R26",     run = StCamera },
        new Stage { title = "Adapt dressing",            ticket = "MODE-B",  run = SceneAdapt.StDress },
        new Stage { title = "GATE 2b — occlusion re-run", ticket = "R28",    run = StOcclusion, gate = true },
        new Stage { title = "Group & verify hierarchy",  ticket = "R26",     run = StHierarchy },
        new Stage { title = "Emit LevelDefinition",      ticket = "R30",     run = StEmitLevel },
        new Stage { title = "GATE 3 — model margins",    ticket = "R29/R30", run = StModelGate, gate = true },
        new Stage { title = "Save scene in place",       ticket = "MODE-B",  run = SceneAdapt.StSaveInPlace },
        new Stage { title = "Localize vendor assets",    ticket = "SHIP",    run = StLocalize },
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
        return Run(Stages, new Context { blueprint = blueprint }, onStage);
    }

    /// <summary>
    /// Mode b — certify an AUTHORED scene in place (docs/generator_intake_modes.md).
    /// Same gates, same back half, different intake and different repair
    /// policy: authored dressing is nudged or removed under named constraints
    /// with every intervention logged, never reseeded; the author's scene is
    /// never closed or deleted on failure.
    /// </summary>
    public static List<StageRun> RunAdapt(
        LevelBlueprint blueprint, Action<Stage, StageResult> onStage = null)
    {
        return Run(AdaptStages, new Context { blueprint = blueprint, adaptMode = true }, onStage);
    }

    private static List<StageRun> Run(
        Stage[] stages, Context ctx, Action<Stage, StageResult> onStage)
    {
        var results = new List<StageRun>();
        var watch = new System.Diagnostics.Stopwatch();

        GenerationProgress.Begin(stages.Length);

        // Tell the scene-setup tools they are pipeline steps, not menu actions:
        // they must not open Game.unity over the scene being generated, and must
        // not save (this pipeline owns its save stage). See GenerationDriven.
        using (GenerationDriven.Scope())
        {
            try
            {
                RunStages(stages, ctx, results, watch, onStage);
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
    private static void RunStages(Stage[] stages, Context ctx, List<StageRun> results,
                                  System.Diagnostics.Stopwatch watch,
                                  Action<Stage, StageResult> onStage)
    {
        for (int i = 0; i < stages.Length; i++)
        {
            Stage stage = stages[i];
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

            // An adopt offer may have cloned level-owned wave tables beside the
            // definition — they die with it, or a failed adopt leaves orphans.
            string wavesFolder = ctx.levelAssetPath.Substring(0, ctx.levelAssetPath.Length - ".asset".Length)
                                 + "_Waves";
            if (AssetDatabase.IsValidFolder(wavesFolder))
            {
                AssetDatabase.DeleteAsset(wavesFolder);
                notes.Add("deleted its cloned wave tables");
            }
        }

        if (ctx.sceneCreated && !ctx.sceneSaved)
        {
            // Replacing the unsaved scene with an empty one is the discard —
            // there is no file to delete because none was ever written.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            notes.Add("half-built scene closed unsaved");
        }

        notes.Add(ctx.adaptMode
            // Mode b never discards the author's work: the scene stays open
            // with its in-memory changes (each intervention is one Undo step),
            // and nothing on disk was touched — StSaveInPlace only runs after
            // every gate passed.
            ? "authored scene left OPEN and untouched on disk — review the report, fix the " +
              "named conflict (or Undo the interventions), and re-run Adapt"
            : "re-seed rather than repair (R29) — try Seed +1");
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

        // The theme's route-occlusion tolerance, pushed to the one place every
        // consumer reads it. RESET FIRST, unconditionally: a themeless run must
        // not inherit the last theme's tolerance, and neither must the next run
        // if this one is discarded.
        RouteVisibility.ToleranceMultiplier =
            ctx.theme != null ? Mathf.Max(1f, ctx.theme.occlusionTolerance) : 1f;

        if (ctx.theme == null)
            return StageResult.Skip("envPackPool empty — generating undressed, authored look");

        string themeLabel = string.IsNullOrEmpty(ctx.theme.themeName) ? ctx.theme.name : ctx.theme.themeName;
        return StageResult.Ok($"theme '{themeLabel}', weather " +
                              (ctx.weather != null ? $"'{ctx.weather.name}'" : "null preset (authored look)") +
                              (RouteVisibility.ToleranceMultiplier > 1.001f
                                  ? $", route-occlusion tolerance ×{RouteVisibility.ToleranceMultiplier:0.##} " +
                                    $"({RouteVisibility.HiddenBudgetFraction * RouteVisibility.ToleranceMultiplier:P0} " +
                                    "of route may hide; pad sight lines unaffected)"
                                  : ""));
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
        // New-level generation only: the parity rebuild retired with the
        // hand-built map's role as design anchor (user decision) — every run
        // synthesizes from the seed.
        ctx.layout = RouteSynthesizer.Synthesize(b, out string synthReport);

        // A null layout is a blueprint problem, not a seed problem — the
        // synthesizer says which field/foldWidth/target constraint is violated.
        if (ctx.layout == null)
            return StageResult.Fail("route synthesis refused this blueprint:\n" + synthReport);
        if (synthReport != null)
            Debug.Log("[R27] " + synthReport);

        // The level container lives under _Level, named for the blueprint.
        Transform levelRoot = SceneContainers.Ensure("_Level");
        string containerName = $"Level_{Sanitise(b.name)}";
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
        // The R29 loop: margin clamps only, logged, ≤3 passes, full re-check
        // between passes.
        string failure = GenerationGates.AdjustAndRecheck(ctx.routes, ctx.blueprint,
                                                          out string summary, out List<string> adjustments);
        if (adjustments.Count > 0)
            Debug.Log("[R29] Gate 1 knot adjustments:\n  " + string.Join("\n  ", adjustments));
        if (failure == null && adjustments.Count > 0)
            summary += $"; {adjustments.Count} knot(s) margin-clamped (logged)";

        if (failure != null)
            return StageResult.Fail(failure);
        return StageResult.Ok(summary);
    }

    private static StageResult StPads(Context ctx)
    {
        // R28: clearance-filtered candidates, scored by the real validator,
        // classified from measurement, picked deterministically.
        RefineryDeltaBlockout.HP[] pads = HardpointSelector.Select(
            ctx.blueprint, ctx.routes, ctx.layout.corePos, ctx.layout.sharedTail,
            out string selReport);
        if (pads == null)
            return StageResult.Fail(selReport);
        Debug.Log("[R28] Hardpoint selection:\n" + selReport);
        const string how = "selected from measured coverage";

        var log = new StringBuilder();
        // Pads are wired to the SAME route set the selector scored against —
        // primary-only when a shared tail carries every shared span, all of
        // them when the approaches are disjoint. Gate 2 must measure what
        // selection promised, or the census fails for bookkeeping reasons.
        Corehold.Core.PathRoute[] scoringRoutes = ctx.layout.sharedTail
            ? new[] { ctx.routes[0] }
            : ctx.routes.ToArray();
        bool satisfied = RefineryDeltaBlockout.BuildHardpoints(
            ctx.levelContainer, scoringRoutes, pads, log);

        // A TowerHardpoint has no renderer of its own, so without this the build
        // pads are INVISIBLE and the player cannot see where to build. The disc
        // also becomes the pad's rimRenderer, which is what pulses while empty
        // (GDD §5.3) — the shipped scene's eight PadMarkers come from here.
        HardpointMarkers.Run();

        // The glow halo that breathes over an empty pad (GDD §5.3, ticket f).
        // The shipped scene has these from the menu tool; a generated map only
        // has them if the pipeline runs the same pass — this call is why pads
        // on generated maps glow at all.
        HardpointAura.Run();

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
        float pitch = ctx.blueprint != null ? ctx.blueprint.cameraPitchDegrees : 38f;
        string log = CameraFramingSetup.Run(pitch);
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
        // Far-band silhouettes (R11): each map gets its own theme's silhouettes,
        // or a bare horizon if the theme has none.
        GroundAndSkirt.BuildSilhouetteBand(ctx.theme, ctx.blueprint.randomSeed);

        if (ctx.theme == null)
            return StageResult.Skip("no theme drawn — undressed beyond the silhouette band");

        string dressLog = PropPlacer.Dress(ctx.blueprint, ctx.theme, ctx.levelContainer,
                                           ctx.routes, ctx.layout.corePos, ctx.layout.airSpawn,
                                           out ctx.dressingStillBlocked);
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
            return StageResult.Ok("0 occluders placed — plain recount holds");

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

        // Per-wave weather rolls from the THEME'S OWN pool — the same list the
        // base preset was drawn from. That is what keeps a rolled sky coherent
        // for free: an ice map cannot roll desert dust, because the pool that
        // could not offer it as a base cannot offer it as a roll either. One
        // authored list, two uses, no second thing to keep in sync.
        SerializedProperty poolProp = so.FindProperty("waveWeatherPool");
        if (poolProp != null && poolProp.isArray)
        {
            WeatherPreset[] pool = ctx.theme != null && ctx.theme.weatherPool != null
                ? ctx.theme.weatherPool.Where(w => w != null).ToArray()
                : System.Array.Empty<WeatherPreset>();

            // A one-entry pool would roll the same sky it already has, so it is
            // left empty: the level simply keeps its base weather throughout.
            if (pool.Length < 2)
                pool = System.Array.Empty<WeatherPreset>();

            poolProp.arraySize = pool.Length;
            for (int i = 0; i < pool.Length; i++)
                poolProp.GetArrayElementAtIndex(i).objectReferenceValue = pool[i];
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        // Night scaffold (R23, extended to every generated map on request): the
        // lamp auto-placement reads this scene's routes, which exist by this
        // stage. StHierarchy then sweeps the "NightVariant" root into _Rendering.
        SetupNightVariant.Setup();

        int rollable = poolProp != null && poolProp.isArray ? poolProp.arraySize : 0;
        string roll = rollable > 0
            ? $"; {rollable}-entry per-wave roll from the theme pool"
            : "; no per-wave roll (theme pool has fewer than 2 presets)";
        return StageResult.Ok(ctx.weather != null
            ? $"applier wired to '{ctx.weather.name}' (applies at map load){roll}; night scaffold placed"
            : $"applier wired to the null preset — authored look, pixel-identical (R13){roll}; night scaffold placed");
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

        // ---- the R30 model run: solve this map's growth and live cap ---------
        Vector3 airSpawn = ctx.layout.airSpawn;
        Vector3 coreXZ = ctx.coreTarget.position;

        // Live certification: the model runs against the wave and enemy assets
        // THIS definition actually references, not the model's embedded hand
        // copies — so an edited wave (a boss in wave 1, a tripled HP stat)
        // moves the verdict on the very next generation.
        string wavesJson = WaveTableExporter.Export(clone, out string waveError);
        if (wavesJson == null)
            return StageResult.Fail($"live wave export failed: {waveError}");

        int derivedMaxLive = BalanceModelRunner.DeriveMaxLive(ctx.routes, b.airCorridor);
        string error;
        try
        {
            // The template may carry certified tuning (multipliers, its own
            // starting salvage) — the model judges with them, as the runtime will.
            ctx.model = BalanceModelRunner.Run(ctx.routes, airSpawn, coreXZ,
                solveGrowth: true, hpGrowth: 0f, maxLive: derivedMaxLive, out error,
                startingSalvage: clone.startingSalvage > 0 ? clone.startingSalvage : -1,
                wavesJsonPath: wavesJson,
                towerDpsMult: clone.towerDamageMultiplier,
                towerRangeMult: clone.towerRangeMultiplier);
        }
        finally
        {
            try { System.IO.File.Delete(wavesJson); } catch { /* temp file */ }
        }
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

        return StageResult.Ok($"cloned '{b.rulesTemplate.name}' → {assetPath}; certified against the " +
                              "LIVE wave/enemy assets (not the model's embedded copies); SOLVED hpGrowthPerWave = " +
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
            // Interactive runs get a CHOICE before the discard: adopt the
            // model's tune — defender package and/or wave count changes —
            // (cloned level-owned, applied, re-certified) or discard as
            // usual. Batch paths — the Campaign Builder's retry loop,
            // contact sheets — never see a dialog.
            if (InteractiveTuneOffer && !Application.isBatchMode &&
                BalanceModelRunner.HasSuggestedTune(ctx.model) &&
                OfferAdoptTune(ctx, out StageResult adopted))
                return adopted;

            return StageResult.Fail("margins out of band at the solved/verified growth. Reseed (R29) for a " +
                                    "coverage problem, or apply the fixes below to the wave/enemy/tower " +
                                    "assets this level references:\n" + DescribeFlagged(ctx.model));
        }

        float open = ctx.model.rows[0].margin;
        float close = ctx.model.rows[ctx.model.rows.Length - 1].margin;
        return StageResult.Ok($"all {ctx.model.rows.Length} waves in band at growth " +
                              $"{ctx.model.hp_growth_used:0.####} — opens {open:0.00}, closes {close:0.00}");
    }

    /// <summary>
    /// Interactive runs only (the Generator window / menu set this around
    /// RunAll): a failed margin gate OFFERS the model's counts-only tune via a
    /// dialog instead of silently discarding. Batch drivers — the Campaign
    /// Builder's auto-reseed loop, contact sheets — leave it false and keep
    /// the pure discard-on-fail contract.
    /// </summary>
    public static bool InteractiveTuneOffer;

    /// <summary>The flagged waves + the model's advice + its tune, as one
    /// bulleted block — the shared body of every margin-gate failure text.</summary>
    private static string DescribeFlagged(BalanceModelRunner.Result model)
    {
        var flagged = new List<string>();
        foreach (var row in model.rows)
            if (row.flags != null && row.flags.Length > 0)
            {
                flagged.Add($"wave {row.wave}: margin {row.margin:0.00} [{string.Join(",", row.flags)}]" +
                            (string.IsNullOrEmpty(row.worst_group) ? "" : $" worst={row.worst_group}"));
                if (row.advice != null)
                    foreach (string a in row.advice)
                        flagged.Add($"    fix → {a}");
            }
        string tune = BalanceModelRunner.DescribeSuggestedTune(model);
        return "  • " + string.Join("\n  • ", flagged) + (tune.Length > 0 ? "\n" + tune : "");
    }

    /// <summary>
    /// The adopt-or-discard dialog and, on adopt, the whole loop: clone the
    /// wave tables level-owned (the shipped/shared assets are never edited),
    /// apply the tune through the shared applier, re-export, re-solve,
    /// re-certify. Returns false when the user declined (caller falls through
    /// to the normal discard); true with <paramref name="result"/> otherwise —
    /// Ok when the adopted table certifies, Fail when even the tune cannot
    /// save this seed (one offer per seed; no dialog loops).
    /// </summary>
    private static bool OfferAdoptTune(Context ctx, out StageResult result)
    {
        result = default;
        var changes = ctx.model.suggested_changes ?? new BalanceModelRunner.SuggestedChange[0];
        string package = BalanceModelRunner.DescribeDefenderPackage(ctx.model);

        var lines = new StringBuilder();
        if (package.Length > 0)
            lines.AppendLine("  defender package (this level only): " + package);
        for (int i = 0; i < changes.Length && i < 10; i++)
            lines.AppendLine($"  wave {changes[i].wave}: {changes[i].enemy} " +
                             $"{changes[i].prev}→{changes[i].next}" +
                             (changes[i].next == 0 ? " (drop the group)" : ""));
        if (changes.Length > 10)
            lines.AppendLine($"  … and {changes.Length - 10} more");

        bool adopt = EditorUtility.DisplayDialog(
            "Margins out of band — adopt the model's tune?",
            "This map fails the margin gate as it stands. The model's smallest fix, " +
            "DEFENDER-FIRST (your waves are touched only where turret and economy buffs " +
            "within sane caps cannot carry them):\n" +
            lines +
            (ctx.model.suggested_in_band
                ? "\nThe model predicts this passes."
                : "\nBest effort — the model predicts it does NOT fully converge.") +
            "\n\nAdopt writes the package into THIS level's definition (tower assets and " +
            "shared wave tables are never edited — wave changes go to level-owned " +
            "clones), re-solves the growth and re-certifies. Decline discards this seed.",
            "Adopt & re-certify", "Discard this seed");
        if (!adopt)
            return false;

        var def = AssetDatabase.LoadAssetAtPath<LevelDefinition>(ctx.levelAssetPath);
        if (def == null)
        {
            result = StageResult.Fail("adopt: the emitted LevelDefinition is not loadable any more");
            return true;
        }

        var log = new StringBuilder();

        // Defender package → the definition (runtime TowerTuning + the
        // economy pull honour these; the re-run below certifies with them).
        if (ctx.model.suggested_tower_dps_mult > def.towerDamageMultiplier)
            def.towerDamageMultiplier = ctx.model.suggested_tower_dps_mult;
        if (ctx.model.suggested_tower_range_mult > def.towerRangeMultiplier)
            def.towerRangeMultiplier = ctx.model.suggested_tower_range_mult;
        if (ctx.model.suggested_starting_salvage > 0)
            def.startingSalvage = ctx.model.suggested_starting_salvage;
        if (package.Length > 0)
        {
            EditorUtility.SetDirty(def);
            log.AppendLine($"  adopt package: {package}");
        }

        int cloned = 0;
        if (changes.Length > 0)
        {
            cloned = EnsureLevelOwnedWaves(def, ctx.levelAssetPath);
            if (!WaveTuneApplier.Apply(def, changes, "adopt", log))
            {
                result = StageResult.Fail("adopt refused:\n" + log.ToString().TrimEnd());
                return true;
            }
        }
        AssetDatabase.SaveAssets();

        string wavesJson = WaveTableExporter.Export(def, out string exportError);
        if (wavesJson == null)
        {
            result = StageResult.Fail($"adopt: re-export failed — {exportError}");
            return true;
        }
        BalanceModelRunner.Result model;
        string error;
        try
        {
            model = BalanceModelRunner.Run(ctx.routes, ctx.layout.airSpawn, ctx.coreTarget.position,
                solveGrowth: true, hpGrowth: 0f, maxLive: def.maxLiveEnemies, out error,
                startingSalvage: def.startingSalvage > 0 ? def.startingSalvage : -1,
                wavesJsonPath: wavesJson,
                towerDpsMult: def.towerDamageMultiplier,
                towerRangeMult: def.towerRangeMultiplier);
        }
        finally
        {
            try { System.IO.File.Delete(wavesJson); } catch { /* temp file */ }
        }
        if (model == null)
        {
            result = StageResult.Fail($"adopt: model re-run failed — {error}");
            return true;
        }

        ctx.model = model;
        if (model.solved && model.solved_hp_growth > 0f)
        {
            def.hpGrowthPerWave = model.solved_hp_growth;
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();
        }

        if (!model.in_band)
        {
            result = StageResult.Fail("user adopted the tune, but margins are STILL out of band at " +
                                      "the re-solved growth — what remains is beyond the tune's caps; " +
                                      "discarding:\n" + DescribeFlagged(model));
            return true;
        }

        result = StageResult.Ok("user ADOPTED the tune" +
                                (package.Length > 0 ? $" — {package}" : "") +
                                (changes.Length > 0 ? $"; {changes.Length} wave count change(s)" : "") +
                                (cloned > 0 ? $"; {cloned} wave table(s) cloned level-owned" : "") +
                                $"; re-solved growth {model.solved_hp_growth:0.####}, all margins in band:\n" +
                                log.ToString().TrimEnd());
        return true;
    }

    /// <summary>
    /// Deep-clone any wave table the definition shares with the rest of the
    /// project (the shipped Wave_01..10, another level's tables) into this
    /// level's own {name}_Waves folder and rewire — so an adopted tune can
    /// never edit an asset some other level plays. Already-owned tables pass
    /// through untouched; returns how many were cloned.
    /// </summary>
    private static int EnsureLevelOwnedWaves(LevelDefinition def, string levelAssetPath)
    {
        string dir = System.IO.Path.GetDirectoryName(levelAssetPath).Replace('\\', '/');
        string folder = levelAssetPath.Substring(0, levelAssetPath.Length - ".asset".Length) + "_Waves";

        var defSo = new SerializedObject(def);
        var wavesProp = defSo.FindProperty("waves");
        int cloned = 0;
        for (int w = 0; w < wavesProp.arraySize; w++)
        {
            var element = wavesProp.GetArrayElementAtIndex(w);
            var shared = element.objectReferenceValue as WaveDefinition;
            if (shared == null)
                continue;
            string path = AssetDatabase.GetAssetPath(shared);
            if (path.StartsWith(folder + "/", StringComparison.Ordinal))
                continue;   // already this level's own copy
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(dir, System.IO.Path.GetFileName(folder));
            var copy = UnityEngine.Object.Instantiate(shared);
            copy.name = shared.name;
            AssetDatabase.CreateAsset(copy, $"{folder}/{shared.name}.asset");
            element.objectReferenceValue = copy;
            cloned++;
        }
        defSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(def);
        return cloned;
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
    /// Self-containment (SHIP): copy every git-ignored vendor asset the saved
    /// scene + emitted definition reference into the committed Vendored folder
    /// and remap the references — so the level a fresh clone or CI builds is
    /// the level the author saw, not one with dangling dressing. Runs AFTER
    /// the save (the scene file is the thing being rewritten) and reloads the
    /// open scene so the editor never holds stale pre-remap references.
    /// </summary>
    private static StageResult StLocalize(Context ctx)
    {
        // Never fails the run: the level is already certified and SAVED — a
        // localization hiccup must warn, not discard a good scene (preflight
        // re-detects anything left external before ship).
        var log = new StringBuilder();
        try
        {
            int copied = VendorLocalizer.Localize(
                new[] { ctx.scenePath, ctx.levelAssetPath }, log);

            if (copied > 0 && !string.IsNullOrEmpty(ctx.scenePath))
            {
                // The remap rewrote the scene FILE; reload so what is open in
                // the editor matches disk (a later manual save must not revert it).
                EditorSceneManager.OpenScene(ctx.scenePath, OpenSceneMode.Single);
            }
        }
        catch (Exception ex)
        {
            return StageResult.Ok($"localize WARNED — {ex.GetType().Name}: {ex.Message} " +
                                  "(scene kept; preflight will re-detect external refs)\n" +
                                  log.ToString().Trim());
        }
        return StageResult.Ok(log.ToString().Trim());
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
