using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// R31 — the human's map-selection surface. Runs seeds through the FULL
/// generation gate (the same <see cref="GenerationPipeline.RunAll"/> the
/// Generator window uses) until NINE pass, captures a top-down orthographic
/// shot of each passing map, and writes:
///
///   • a 3×3 grid PNG (each cell stamped in-world with its seed + theme, so
///     the label survives inside the pixels), and
///   • a Markdown per-seed table — verdict, theme, route lengths, pad mix,
///     derived maxLive and solved hpGrowth — including the seeds that FAILED
///     and at which stage, because "which seeds died and why" is part of
///     choosing a map.
///
/// The sheet is a PICKER, not a bulk generator: every passing run's artifacts
/// (scene, LevelDefinition, Build Settings entry) are deleted after capture —
/// pick a (seed, theme) from the sheet, then generate THAT seed for real in
/// the Generator window. Failed runs discard themselves (R29).
///
/// Run on the SELECTED LevelBlueprint (falls back to the first blueprint in
/// the project). Seeds tried are blueprint.randomSeed, +1, +2, … capped at
/// MaxAttempts so a hostile blueprint refuses honestly instead of spinning.
/// </summary>
public static class ContactSheet
{
    private const int GridCols = 3;
    private const int GridRows = 3;
    private const int CellPx = 512;
    private const int MaxAttempts = 36;
    private const string OutDir = "Assets/_COREHOLD/Docs/ContactSheets";

    private struct SeedRecord
    {
        public int seed;
        public bool passed;
        public string failStage;   // failing stage title when !passed
        public string theme;
        public string routes;      // "154.2 + 153.8"
        public string padMix;      // "3P/2S/2R/1O"
        public int maxLive;
        public float hpGrowth;
        public Texture2D shot;     // null when failed
    }

    [MenuItem("Tools/COREHOLD/Level/Contact Sheet (9 seeds)", false, 62)]
    public static void Run()
    {
        var blueprint = Selection.activeObject as LevelBlueprint;
        if (blueprint == null)
        {
            string guid = AssetDatabase.FindAssets("t:LevelBlueprint").FirstOrDefault();
            if (guid != null)
                blueprint = AssetDatabase.LoadAssetAtPath<LevelBlueprint>(
                    AssetDatabase.GUIDToAssetPath(guid));
        }
        if (blueprint == null)
        {
            Debug.LogError("[ContactSheet] No LevelBlueprint selected or found in the project.");
            return;
        }

        // Capture the blueprint's DATA before anything runs. Each pipeline pass
        // tears down scenes and churns the asset database, and the ORIGINAL
        // object's managed wrapper was observed destroyed after the first pass —
        // Instantiate(blueprint) on iteration 2 threw MissingReferenceException.
        // Iterations therefore never touch the original again: every seed gets a
        // fresh clone rebuilt from this JSON snapshot (EditorJsonUtility keeps
        // asset references like rulesTemplate intact across the round-trip).
        string bpName = blueprint.name;
        string bpJson = EditorJsonUtility.ToJson(blueprint);
        int startSeed = blueprint.randomSeed;
        blueprint = null;   // deliberate: nothing below may depend on it living

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        string returnScene = SceneManager.GetActiveScene().path;

        var records = new List<SeedRecord>();
        int passes = 0;

        for (int attempt = 0; attempt < MaxAttempts && passes < GridCols * GridRows; attempt++)
        {
            int seed = startSeed + attempt;

            // Fresh, teardown-proof clone per seed (DontSave: scene loads must
            // not reap it mid-run). Name kept so emitted asset names carry the
            // blueprint's identity, not "(Clone)".
            var bp = ScriptableObject.CreateInstance<LevelBlueprint>();
            EditorJsonUtility.FromJsonOverwrite(bpJson, bp);
            bp.name = bpName;
            bp.hideFlags = HideFlags.DontSave;
            bp.randomSeed = seed;

            List<GenerationPipeline.StageRun> results;
            try
            {
                results = GenerationPipeline.RunAll(bp);
            }
            finally
            {
                if (bp != null)
                    Object.DestroyImmediate(bp);
            }

            var failed = results.FirstOrDefault(r => !r.result.ok);
            bool cancelled = results.Any(r => !r.result.ok && r.result.message.Contains("cancelled"));
            if (cancelled)
            {
                Debug.LogWarning("[ContactSheet] Cancelled by user — writing what was gathered.");
                break;
            }

            if (failed.stage.title != null && !failed.result.ok)
            {
                records.Add(new SeedRecord
                {
                    seed = seed,
                    passed = false,
                    failStage = failed.stage.title,
                });
                continue;
            }

            records.Add(CaptureCurrent(seed));
            passes++;
        }

        // Back to wherever the human was.
        if (!string.IsNullOrEmpty(returnScene))
            EditorSceneManager.OpenScene(returnScene, OpenSceneMode.Single);
        else
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        WriteOutputs(bpName, startSeed, records, passes);
    }

    // ------------------------------------------------------------ capture

    /// <summary>
    /// The active scene is a freshly generated, gate-passing map. Read its
    /// stats, stamp an in-world label, shoot it top-down, then DELETE its
    /// artifacts so the picker leaves the project exactly as it found it.
    /// </summary>
    private static SeedRecord CaptureCurrent(int seed)
    {
        var rec = new SeedRecord { seed = seed, passed = true };

        // Stats from the world.
        var routes = SceneQuery.InActiveScene<PathRoute>()
            .Where(r => r != null && r.Length > 1f).ToArray();
        rec.routes = string.Join(" + ", routes.Select(r => r.Length.ToString("0.0")));

        var pads = SceneQuery.InActiveScene<Corehold.Towers.TowerHardpoint>();
        int p = pads.Count(h => h.name.StartsWith("HP_Premium"));
        int s = pads.Count(h => h.name.StartsWith("HP_Standard"));
        int r2 = pads.Count(h => h.name.StartsWith("HP_Rear"));
        int o = pads.Count(h => h.name.StartsWith("HP_Overwatch"));
        rec.padMix = $"{p}P/{s}S/{r2}R/{o}O";

        // The emitted LevelDefinition (via the WaveManager it was wired into)
        // carries the derived cap and the solved growth — the model summary.
        string scenePath = SceneManager.GetActiveScene().path;
        string levelPath = null;
        var wm = SceneQuery.FirstInActiveScene<WaveManager>();
        if (wm != null)
        {
            var so = new SerializedObject(wm);
            var level = so.FindProperty("level")?.objectReferenceValue as LevelDefinition;
            if (level != null)
            {
                levelPath = AssetDatabase.GetAssetPath(level);
                rec.maxLive = level.maxLiveEnemies;
                rec.hpGrowth = level.hpGrowthPerWave;
                // Level_<Theme>_s<seed> — the theme rides the asset name.
                var bits = level.name.Split('_');
                rec.theme = bits.Length >= 3 ? bits[1] : "?";
            }
        }

        rec.shot = ShootTopDown(seed, rec.theme);

        // Leave nothing behind: close the scene, then delete scene + level
        // assets and the Build Settings entry StSave registered.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!string.IsNullOrEmpty(scenePath))
        {
            AssetDatabase.DeleteAsset(scenePath);
            EditorBuildSettings.scenes = EditorBuildSettings.scenes
                .Where(e => e.path != scenePath).ToArray();
        }
        if (!string.IsNullOrEmpty(levelPath))
            AssetDatabase.DeleteAsset(levelPath);

        return rec;
    }

    private static Texture2D ShootTopDown(int seed, string theme)
    {
        // Frame the floor, whatever size this map's camera solve produced.
        Bounds bounds = new Bounds(Vector3.zero, new Vector3(130f, 1f, 75f));
        var floor = GameObject.Find("Floor");
        var floorRenderer = floor != null ? floor.GetComponent<Renderer>() : null;
        if (floorRenderer != null)
            bounds = floorRenderer.bounds;

        // In-world label so seed + theme live INSIDE the pixels. TMP world
        // text, laid flat, sized off the floor so it reads at 512 px.
        var labelGo = new GameObject("ContactSheetLabel");
        var tmp = labelGo.AddComponent<TextMeshPro>();
        tmp.text = $"s{seed}  {theme}";
        tmp.fontSize = Mathf.Max(24f, bounds.size.x * 0.55f);
        tmp.color = new Color(0.3f, 0.95f, 1f, 1f);
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.rectTransform.sizeDelta = new Vector2(bounds.size.x * 0.9f, bounds.size.z * 0.2f);
        labelGo.transform.position = new Vector3(
            bounds.min.x + bounds.size.x * 0.05f, 2f, bounds.max.z - bounds.size.z * 0.04f);
        labelGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        tmp.rectTransform.pivot = new Vector2(0f, 1f);

        var camGo = new GameObject("ContactSheetCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.transform.position = bounds.center + Vector3.up * 150f;
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        cam.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x) * 1.04f;
        cam.nearClipPlane = 1f;
        cam.farClipPlane = 400f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.03f, 0.04f, 0.06f, 1f);

        var rt = new RenderTexture(CellPx, CellPx, 24);
        var shot = new Texture2D(CellPx, CellPx, TextureFormat.RGB24, false);
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, CellPx, CellPx), 0, 0);
            shot.Apply();
        }
        finally
        {
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(labelGo);
        }
        return shot;
    }

    // ------------------------------------------------------------ outputs

    private static void WriteOutputs(string blueprintName, int startSeed,
        List<SeedRecord> records, int passes)
    {
        if (!AssetDatabase.IsValidFolder(OutDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Docs", "ContactSheets");

        string baseName = $"ContactSheet_{GenerationPipeline.Sanitise(blueprintName)}_from{startSeed}";
        string pngPath = $"{OutDir}/{baseName}.png";
        string mdPath = $"{OutDir}/{baseName}.md";

        // 3×3 grid, row-major from the top-left, dark filler for empty cells.
        var sheet = new Texture2D(GridCols * CellPx, GridRows * CellPx, TextureFormat.RGB24, false);
        var filler = Enumerable.Repeat(new Color(0.06f, 0.07f, 0.09f, 1f), CellPx * CellPx).ToArray();
        var shots = records.Where(r => r.passed && r.shot != null).ToList();
        for (int cell = 0; cell < GridCols * GridRows; cell++)
        {
            int cx = (cell % GridCols) * CellPx;
            int cy = (GridRows - 1 - cell / GridCols) * CellPx;   // row 0 at the TOP
            if (cell < shots.Count)
                sheet.SetPixels(cx, cy, CellPx, CellPx, shots[cell].shot.GetPixels());
            else
                sheet.SetPixels(cx, cy, CellPx, CellPx, filler);
        }
        sheet.Apply();
        File.WriteAllBytes(pngPath, sheet.EncodeToPNG());
        Object.DestroyImmediate(sheet);
        foreach (var r in shots)
            Object.DestroyImmediate(r.shot);

        // The per-seed table — passes first (grid order), then the failures.
        var md = new StringBuilder();
        md.AppendLine($"# Contact sheet — {blueprintName}, seeds from {startSeed}");
        md.AppendLine();
        md.AppendLine($"{passes}/{GridCols * GridRows} passing seeds in " +
                      $"{records.Count} attempt(s). Grid reads row-major from the top-left.");
        md.AppendLine();
        md.AppendLine("| cell | seed | verdict | theme | routes (m) | pads | maxLive | hpGrowth |");
        md.AppendLine("|-----:|-----:|---------|-------|------------|------|--------:|---------:|");
        int cellNo = 0;
        foreach (var r in records.Where(r => r.passed))
        {
            cellNo++;
            md.AppendLine($"| {cellNo} | {r.seed} | PASS | {r.theme} | {r.routes} " +
                          $"| {r.padMix} | {r.maxLive} | {r.hpGrowth:0.###} |");
        }
        foreach (var r in records.Where(r => !r.passed))
            md.AppendLine($"| — | {r.seed} | FAIL @ {r.failStage} | | | | | |");
        md.AppendLine();
        md.AppendLine("Pick a (seed, theme), set the seed on the blueprint in the Generator " +
                      "window, and generate it for real — the sheet deleted every artifact " +
                      "it produced.");
        File.WriteAllText(mdPath, md.ToString());

        AssetDatabase.Refresh();
        Debug.Log($"[ContactSheet] {passes}/{GridCols * GridRows} passing seeds " +
                  $"({records.Count} attempts) → {pngPath} + {mdPath}");
        var png = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
        if (png != null)
            EditorGUIUtility.PingObject(png);
    }
}
