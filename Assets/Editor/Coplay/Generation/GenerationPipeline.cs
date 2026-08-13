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
/// The generation pipeline as CODE — an ordered list of stages that both the
/// Level Generator window and the headless menu item drive. This is the R26
/// architecture decision: the pipeline must not live in menu items, because menu
/// items cannot be sequenced, reported on, or partially re-run, and "run these
/// nine in the right order" does not scale past the person who wrote them.
///
/// v1 scope — parity path (R26). Stages that R27–R30 will replace are marked:
/// today "content" delegates to <see cref="RefineryDeltaBlockout.Build"/> (the
/// shipped map's builder), so only the parity blueprint truly generates. The
/// stage list is the contract: R27 swaps the content stage for route synthesis,
/// R28 adds selection + dressing, R29 inserts the gates, R30 replaces the model
/// stub — the window updates by itself because it renders whatever is here.
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
        public string scenePath;
        public string levelAssetPath;
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
        new Stage { title = "New scene",                 ticket = "R26",     run = StNewScene },
        new Stage { title = "Content (parity blockout)", ticket = "R26→R27/R28", run = StParityContent },
        new Stage { title = "Playable bootstrap",        ticket = "R26",     run = StBootstrap },
        new Stage { title = "Directors + UI",            ticket = "R26",     run = StDirectorsUi },
        new Stage { title = "Camera framing",            ticket = "R26",     run = StCamera },
        new Stage { title = "Floor fit + theme ground",  ticket = "R11/R26", run = StGround },
        new Stage { title = "Weather",                   ticket = "R13",     run = StWeather },
        new Stage { title = "Organize hierarchy",        ticket = "R26",     run = StOrganize },
        new Stage { title = "Emit LevelDefinition",      ticket = "R30",     run = StEmitLevel },
        new Stage { title = "GATE model margins",        ticket = "R29/R30", run = StModelGate },
        new Stage { title = "Save scene",                ticket = "R29",     run = StSave },
    };

    /// <summary>
    /// Run every stage in order, stopping at the first failure. Results arrive
    /// through <paramref name="onStage"/> as they happen so a window can paint
    /// progress; the return value is the full transcript.
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
                break;
        }
        return results;
    }

    // ---------------------------------------------------------- deterministic draw

    /// <summary>
    /// FNV-1a over the seed plus a purpose string. Plain integer math, so it is
    /// identical on every device — System.Random's algorithm is not contractually
    /// stable, and R37 (daily seed) needs draws to agree across platforms.
    /// </summary>
    private static uint Fnv1a(int seed, string purpose)
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
        return StageResult.Ok("fresh scene (Main Camera + Directional Light)");
    }

    private static StageResult StParityContent(Context ctx)
    {
        // INTERIM: the shipped map's builder supplies routes, Core, hardpoints and
        // structures, so today only the parity blueprint truly generates. R27
        // replaces this with seeded route synthesis, R28 with hardpoint selection
        // + themed dressing; the fixed geometry is the fallback until then.
        RefineryDeltaBlockout.Build();
        return StageResult.Ok("routes + Core + hardpoints + structures from the parity builder " +
                              "(seeded synthesis lands with R27/R28)");
    }

    private static StageResult StBootstrap(Context ctx)
    {
        string log = PlayableBootstrapSetup.Run();
        return StageResult.Ok(Summarise(log, "gameplay singletons wired"));
    }

    private static StageResult StDirectorsUi(Context ctx)
    {
        SetupAudioDirector.Setup();
        SetupVFXDirector.Setup();
        string ui = BuildRealUI.Run();
        return StageResult.Ok(Summarise(ui, "AudioDirector, VFXDirector, UI canvases"));
    }

    private static StageResult StCamera(Context ctx)
    {
        string log = CameraFramingSetup.Run();
        return StageResult.Ok(Summarise(log, "camera framed against scene content"));
    }

    private static StageResult StGround(Context ctx)
    {
        GroundAndSkirt.FitGroundAndFog();      // frustum-sized, never the design box (R11)
        GroundAndSkirt.BuildSilhouetteBand();

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
            notes.Add("groundPrefab set but not yet honoured — lands with R26 proper");

        return StageResult.Ok(string.Join(", ", notes));
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

    private static StageResult StOrganize(Context ctx)
    {
        // INTERIM: the parity builder emits flat roots, so organizing is a stage
        // here. R26 proper emits grouped from a shared container table, at which
        // point this stage becomes a VERIFY — it must report 0 moved.
        OrganizeHierarchy.Organize();
        return StageResult.Ok("grouped into _Systems/_Directors/_Level/_UI/_Rendering " +
                              "(R26 proper emits grouped; this becomes a no-op check)");
    }

    private static StageResult StEmitLevel(Context ctx)
    {
        LevelBlueprint b = ctx.blueprint;
        if (b.rulesTemplate == null)
            return StageResult.Fail("rulesTemplate is unassigned — validation should have caught this");

        const string dir = "Assets/_COREHOLD/Data/Levels/Generated";
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data/Levels", "Generated");

        ctx.levelAssetPath = $"{dir}/Level_{Sanitise(b.name)}_s{b.randomSeed}.asset";
        var clone = UnityEngine.Object.Instantiate(b.rulesTemplate);
        clone.name = System.IO.Path.GetFileNameWithoutExtension(ctx.levelAssetPath);
        AssetDatabase.CreateAsset(clone, ctx.levelAssetPath);

        var wave = UnityEngine.Object.FindFirstObjectByType<Corehold.Core.WaveManager>();
        if (wave == null)
            return StageResult.Fail("no WaveManager in the scene to wire the LevelDefinition into");

        var so = new SerializedObject(wave);
        SerializedProperty levelProp = so.FindProperty("level");
        if (levelProp == null)
            return StageResult.Fail("WaveManager has no 'level' field — wiring contract changed?");
        levelProp.objectReferenceValue = clone;
        so.ApplyModifiedPropertiesWithoutUndo();

        return StageResult.Ok($"cloned '{b.rulesTemplate.name}' → {ctx.levelAssetPath}, wired into WaveManager " +
                              "(hpGrowthPerWave solve + derived maxLiveEnemies land with R30)");
    }

    private static StageResult StModelGate(Context ctx)
    {
        // R30 shells out to docs/balance_model.py and reads --json. Deliberately
        // NOT approximated here: a second implementation of the margin math is
        // exactly the drift R1 exists to prevent, and a fake PASS would teach the
        // team to trust a gate that is not there.
        return StageResult.Skip("pending R30 — balance model runs as a subprocess; margins unverified until then");
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

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }
}
