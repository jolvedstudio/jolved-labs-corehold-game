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

    [MenuItem("Tools/COREHOLD/Level/Generate Level", false, 3)]
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

        var errors = new List<string>();
        var warnings = new List<string>();
        Validate(blueprint, errors, warnings);

        AppendPlan(blueprint, log);

        foreach (string w in warnings)
            log.AppendLine($"  [warn] {w}");

        if (errors.Count > 0)
        {
            log.AppendLine();
            log.AppendLine($"BLUEPRINT REJECTED — {errors.Count} problem(s), nothing emitted:");
            foreach (string e in errors)
                log.AppendLine($"  • {e}");
            Debug.LogWarning(log.ToString());
            return;
        }

        log.AppendLine();
        log.AppendLine("Blueprint VALID. Generation itself is stubbed until R26–R30 land — no scene emitted.");
        Debug.Log(log.ToString());
    }

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
                  "envPack and weatherPool are left empty; assign them when the pack exists.");
    }

    // ------------------------------------------------------------- validation

    private static void Validate(LevelBlueprint b, List<string> errors, List<string> warnings)
    {
        if (b.playfieldSize.x <= 0f || b.playfieldSize.y <= 0f)
            errors.Add($"playfieldSize must be positive (is {b.playfieldSize}).");

        if (b.protectedNormalizedPos.x < 0f || b.protectedNormalizedPos.x > 1f ||
            b.protectedNormalizedPos.y < 0f || b.protectedNormalizedPos.y > 1f)
            errors.Add($"protectedNormalizedPos must be inside [0,1] (is {b.protectedNormalizedPos}).");

        if (b.protectedPrefab == null)
            warnings.Add("protectedPrefab is unassigned — the generated level would have nothing to defend.");

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

        if (b.envPack == null)
        {
            warnings.Add("envPack is unassigned — the level will generate undressed.");
        }
        else
        {
            int invalid = b.envPack.CountInvalid();
            if (invalid > 0)
                errors.Add($"envPack '{b.envPack.name}' has {invalid} entr(ies) with no prefab or a " +
                           "zero footprint/height — the clearance and occlusion tests would silently pass them.");
            if (b.envPack.CountInRole(EnvPack.PropRole.Silhouette) == 0)
                warnings.Add($"envPack '{b.envPack.name}' has no Silhouette entries — the far band (R11) will be bare.");
        }

        if (b.weatherPool == null || b.weatherPool.Length == 0)
            warnings.Add("weatherPool is empty — the level generates on the null preset, which keeps the authored look.");
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
