using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The generation entry point (roadmap R25) — menu + blueprint validation, with
/// the pipeline itself stubbed until R26–R30 land.
///
/// The stub is not empty on purpose. Every constraint the pipeline will enforce
/// is knowable from the blueprint alone, and catching a bad blueprint here costs
/// nothing while catching it at stage 11 of a generation run costs a full pass.
/// So this validates first and prints the twelve-stage plan it *will* run,
/// naming which ticket owns each stage.
///
/// Two things it deliberately does NOT do, matching R29's doctrine: it never
/// emits a partial scene, and it never repairs a blueprint. A blueprint that
/// cannot satisfy the constraints is rejected — reseeding is cheap, hand-repair
/// is what the generator exists to eliminate.
/// </summary>
public static class GenerateLevel
{
    /// <summary>Route clearance envelope: laneHalfWidth 0.9 + maxBodyRadius 1.35 + padRadius 1.5.</summary>
    private const float ClearanceEnvelope = 3.75f;

    /// <summary>The coverage rule needs at least this many pads covering 4+ spans.</summary>
    private const int MinPremiumPads = 3;

    /// <summary>
    /// Headless one-shot: run the SAME pipeline the Level Generator window drives
    /// and print the transcript. The window is the everyday surface; this exists
    /// for scripted use and for the R31 contact sheet, which will call the
    /// pipeline once per seed.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Level/Generate Level (headless)", false, 20)]
    public static void Generate()
    {
        LevelBlueprint blueprint = ResolveBlueprint(out string how);
        if (blueprint == null)
        {
            Debug.LogWarning("[R25] no LevelBlueprint found. Create one via " +
                             "Assets → Create → COREHOLD → Level Blueprint, or select one in the Project window.");
            return;
        }

        var log = new StringBuilder();
        log.AppendLine($"=== COREHOLD level generation — blueprint '{blueprint.name}' ({how}) ===");
        AppendPlan(blueprint, log);
        log.AppendLine();

        bool failed = false;
        foreach (var run in GenerationPipeline.RunAll(blueprint))
        {
            string icon = !run.result.ok ? "✗" : run.result.skipped ? "–" : "✓";
            string gate = run.stage.gate ? "[GATE] " : "";
            string time = run.seconds >= 0.05f ? $" ({run.seconds:0.0} s)" : "";
            log.AppendLine($"  {icon} {gate}{run.stage.title,-26} {run.result.message}{time}");
            failed |= !run.result.ok;
        }

        log.AppendLine();
        log.AppendLine(failed
            ? "FAILED — stopped at the first ✗ above; nothing after it ran."
            : "Done. Open the saved scene and press Play.");

        if (failed) Debug.LogWarning(log.ToString());
        else Debug.Log(log.ToString());
    }

    /// <summary>Blueprint resolution for tools that must not log (the window's OnEnable).</summary>
    internal static LevelBlueprint ResolveBlueprintQuiet() => ResolveBlueprint(out _);

    /// <summary>
    /// Author a blueprint describing the SHIPPED map, which is what R26's parity
    /// rebuild is measured against ("a blueprint configured to the shipped values
    /// rebuilds a scene with the full live root set").
    /// </summary>
    [MenuItem("Tools/COREHOLD/Level/Create Refinery Delta Blueprint", false, 2)]
    public static void CreateShippedBlueprint()
    {
        const string dir = "Assets/_COREHOLD/Data/Blueprints";
        const string path = dir + "/Blueprint_RefineryDelta.asset";

        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "Blueprints");

        var bp = AssetDatabase.LoadAssetAtPath<LevelBlueprint>(path);
        if (bp == null)
        {
            bp = ScriptableObject.CreateInstance<LevelBlueprint>();
            AssetDatabase.CreateAsset(bp, path);
        }

        // Values measured off the live map earlier in this project, not guessed.
        bp.parityLayout = true;   // this blueprint IS the parity target — no seed variation
        bp.randomSeed = 1;
        bp.playfieldSize = new Vector2(130f, 75f);
        bp.protectedNormalizedPos = new Vector2(0.765f, 0.413f);  // Core at (34.5, -6.5)
        bp.routeLengthTarget = 154f;                              // splines measure 153.7 / 154.5
        bp.foldWidth = 11f;                                       // shipped folds are 10 and 11 m
        bp.groundSpawnLegs = 2;
        bp.airCorridor = true;
        bp.hardpointCount = 8;
        bp.classMix = new LevelBlueprint.PadClassMix
        {
            premium = 3, standard = 2, rear = 2, overwatch = 1
        };
        bp.rulesTemplate = AssetDatabase.LoadAssetAtPath<LevelDefinition>(
            "Assets/_COREHOLD/Data/Levels/Level_RefineryDelta.asset");

        EditorUtility.SetDirty(bp);
        AssetDatabase.SaveAssets();
        Selection.activeObject = bp;

        Debug.Log($"[R25] {path} authored to the shipped map's values — this is R26's parity target. " +
                  "envPackPool and weatherPool are left empty; Create Refinery Env Pack pins the parity " +
                  "theme into envPackPool once the pack exists.");
    }

    // ------------------------------------------------------------- validation

    /// <summary>
    /// Blueprint admission — shared by the pipeline's first stage and the window's
    /// live panel, so the window can never disagree with what generation enforces.
    /// </summary>
    internal static void ValidateBlueprint(LevelBlueprint b, List<string> errors, List<string> warnings)
    {
        if (b.playfieldSize.x <= 0f || b.playfieldSize.y <= 0f)
            errors.Add($"playfieldSize must be positive (is {b.playfieldSize}).");

        if (b.protectedNormalizedPos.x < 0f || b.protectedNormalizedPos.x > 1f ||
            b.protectedNormalizedPos.y < 0f || b.protectedNormalizedPos.y > 1f)
            errors.Add($"protectedNormalizedPos must be inside [0,1] (is {b.protectedNormalizedPos}).");

        if (b.protectedPrefab == null)
        {
            warnings.Add("protectedPrefab is unassigned — the level falls back to the shipped platform + " +
                         "shield-generator stack. Assign a PROJECT PREFAB (a ScriptableObject cannot " +
                         "reference a scene object, which is why the picker refuses one).");
        }
        else if (b.protectedPrefab.GetComponentInChildren<Renderer>() == null)
        {
            // Same failure shape as a ground object with no renderer: it exists,
            // the pipeline reports success, and the player sees nothing to defend.
            warnings.Add($"protectedPrefab '{b.protectedPrefab.name}' has no renderer anywhere in it — " +
                         "the Core would be invisible.");
        }

        if (b.routeLengthTarget <= 0f)
            errors.Add("routeLengthTarget must be positive.");

        // Fold width is the constraint that decides whether good pads can exist at
        // all, so it is validated against the turret numbers rather than a taste.
        if (b.foldWidth < 2f * ClearanceEnvelope)
            errors.Add($"foldWidth {b.foldWidth:0.##} m is below {2f * ClearanceEnvelope:0.##} m — " +
                       "a pad centred in that pocket cannot clear the 3.75 m envelope from both legs.");
        if (b.foldWidth > 20f)
            errors.Add($"foldWidth {b.foldWidth:0.##} m exceeds 20 m — the shortest-ranged turret " +
                       "(Arc Node, 10 m) cannot reach both legs from the pocket centre.");
        if (b.classMix.overwatch > 0 && b.foldWidth < 12f)
            warnings.Add($"foldWidth {b.foldWidth:0.##} m is under 12 m while the mix asks for an Overwatch pad — " +
                         "a Mortar centred in that pocket has both legs inside its 6 m dead zone, so its " +
                         "pad will have to sit outside the folds.");

        if (b.groundSpawnLegs < 1 || b.groundSpawnLegs > 2)
            errors.Add($"groundSpawnLegs must be 1 or 2 (is {b.groundSpawnLegs}).");

        if (b.hardpointCount != b.classMix.Total)
            errors.Add($"hardpointCount {b.hardpointCount} does not match the class mix total " +
                       $"{b.classMix.Total} ({b.classMix.premium}P/{b.classMix.standard}S/" +
                       $"{b.classMix.rear}R/{b.classMix.overwatch}O).");

        if (b.classMix.premium < MinPremiumPads)
            errors.Add($"classMix.premium is {b.classMix.premium} — the coverage rule requires at least " +
                       $"{MinPremiumPads} pads covering 4+ spans, so this blueprint can never pass the gate.");

        if (b.rulesTemplate == null)
            errors.Add("rulesTemplate is unassigned — R30 clones it to emit the LevelDefinition.");

        if (b.groundSpawnLegs == 1)
            warnings.Add("groundSpawnLegs is 1 but the shipped wave tables send groups to spawner 1 (north). " +
                         "The balance model reroutes those to the primary route; verify the wave table's " +
                         "spawner indices before shipping a 1-leg map (wave regeneration is R33).");

        if (b.envPackPool == null || b.envPackPool.Length == 0)
        {
            warnings.Add("envPackPool is empty — the level will generate undressed.");
        }
        else
        {
            // Every theme in the pool must be shippable, not just the one this seed picks.
            // A pool validated only on the drawn theme fails on a different seed, which is
            // the worst time to find out.
            for (int i = 0; i < b.envPackPool.Length; i++)
            {
                EnvPack pack = b.envPackPool[i];
                if (pack == null)
                {
                    errors.Add($"envPackPool[{i}] is null — remove the slot or assign a pack.");
                    continue;
                }

                int invalid = pack.CountInvalid();
                if (invalid > 0)
                    errors.Add($"envPack '{pack.name}' has {invalid} entr(ies) with no prefab, a zero " +
                               "footprint/height, or an Unassigned role — the clearance and occlusion tests " +
                               "would silently pass them. Run Tools → COREHOLD → Level → Measure Env Pack Metadata.");
                if (pack.CountInRole(EnvPack.PropRole.Silhouette) == 0)
                    warnings.Add($"envPack '{pack.name}' has no Silhouette entries — the far band (R11) will be bare.");
                if ((b.weatherPool == null || b.weatherPool.Length == 0) &&
                    (pack.weatherPool == null || pack.weatherPool.Length == 0))
                    warnings.Add($"envPack '{pack.name}' has no weatherPool and the blueprint sets no override — " +
                                 "this theme generates on the null preset.");
            }
        }

        // No warning for an empty blueprint weatherPool: empty is the CORRECT state now.
        // It means "the chosen theme decides", which is what keeps an ice map off desert
        // dust. Setting it forces one weather across every theme, so it is the override,
        // not the default. The per-pack check above covers the case where neither is set.
    }

    // ------------------------------------------------------------------ plan

    private static void AppendPlan(LevelBlueprint b, StringBuilder log)
    {
        log.AppendLine($"seed {b.randomSeed} · field {b.playfieldSize.x:0}×{b.playfieldSize.y:0} m · " +
                       $"routes {b.groundSpawnLegs} ground + {(b.airCorridor ? "air" : "no air")} · " +
                       $"target {b.routeLengthTarget:0.#} m · folds {b.foldWidth:0.#} m · " +
                       $"{b.hardpointCount} pads ({b.classMix.premium}P/{b.classMix.standard}S/" +
                       $"{b.classMix.rear}R/{b.classMix.overwatch}O)");
        log.AppendLine();
        log.AppendLine("Pipeline (stage order is load-bearing — see the P6 preamble):");
        log.AppendLine("   1 skeleton .............. R26   root set, reusing the existing setup tools");
        log.AppendLine("   2 protected structure ... R26   Core at the normalized position");
        log.AppendLine("   3 route synthesis ....... R27   pinned splines, folds in band, Length ±5%");
        log.AppendLine("   4 GATE clearance ........ R29   knot nudges allowed, logged, ≤3 passes");
        log.AppendLine("   5 hardpoint selection ... R28   clearance-filtered candidates, classified by measurement");
        log.AppendLine("   6 GATE coverage ......... R28   every pad ≥2 spans, ≥3 Premium at ≥4");
        log.AppendLine("   7 camera framing ........ R26   solved against the GENERATED content bounds");
        log.AppendLine("   8 floor fit ............. R11   ground sized from the frustum, never the design box");
        log.AppendLine("   9 dressing .............. R28   props + skirt, then coverage re-run THROUGH occlusion");
        log.AppendLine("  10 emission .............. R30   LevelDefinition; model solved via subprocess");
        log.AppendLine("  11 GATE model margins .... R29   per-wave margins in band");
        log.AppendLine("  12 save or discard ....... R29   pass ⇒ scene + asset; fail ⇒ nothing, with a report");
    }

    private static LevelBlueprint ResolveBlueprint(out string how)
    {
        if (Selection.activeObject is LevelBlueprint selected)
        {
            how = "selected in the Project window";
            return selected;
        }

        string[] guids = AssetDatabase.FindAssets("t:LevelBlueprint");
        if (guids.Length > 0)
        {
            how = guids.Length == 1
                ? "the only one in the project"
                : $"first of {guids.Length} — select one to choose";
            return AssetDatabase.LoadAssetAtPath<LevelBlueprint>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        how = null;
        return null;
    }
}
