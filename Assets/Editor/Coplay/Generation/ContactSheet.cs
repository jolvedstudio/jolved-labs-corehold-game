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
/// Generator window uses) until NINE pass, captures a shot of each passing
/// map, and writes:
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
///
/// TWO VIEWS, and the default changed for a reason.
///
/// This tool used to shoot only an orthographic plan from 150 m up — the one
/// view the player never sees. That mattered more than it sounds: the whole
/// generator reasons about maps as PLANS (every gate is a top-down constraint),
/// and its review surface reasoned in plans too, so nothing in the loop ever
/// looked at the thing the player actually stares at for fifteen minutes. A
/// fixed-camera game is one composed shot per map; you cannot judge a shot from
/// a floor plan.
///
/// So the default is now the GAME VIEW, rendered through the scene's own
/// gameplay camera — real pitch, real distance, real post, real fog. The plan
/// stays available on its own menu item, because reading route topology at a
/// glance is a genuinely different job.
/// </summary>
public static class ContactSheet
{
    private const int GridCols = 3;
    private const int GridRows = 3;

    /// <summary>Game-view cells are 16:9 because that is the shape of the thing
    /// being judged. A square crop of a wide shot is a different composition.</summary>
    private const int GameCellW = 512, GameCellH = 288;
    private const int PlanCellPx = 512;

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
        public string camPose;     // "38° pitch, 142 m back" — anchors a composition judgement
        public Texture2D shot;     // null when failed
    }

    [MenuItem("Tools/COREHOLD/Level/Contact Sheet (9 seeds)", false, 62)]
    public static void RunGameView() => Run(planView: false);

    [MenuItem("Tools/COREHOLD/Level/Contact Sheet (9 seeds, plan view)", false, 63)]
    public static void RunPlanView() => Run(planView: true);

    private static void Run(bool planView)
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

            records.Add(CaptureCurrent(seed, planView));
            passes++;
        }

        // Back to wherever the human was.
        if (!string.IsNullOrEmpty(returnScene))
            EditorSceneManager.OpenScene(returnScene, OpenSceneMode.Single);
        else
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        WriteOutputs(bpName, startSeed, records, passes, planView);
    }

    // ------------------------------------------------------------ capture

    /// <summary>
    /// The active scene is a freshly generated, gate-passing map. Read its
    /// stats, stamp a label, shoot it, then DELETE its artifacts so the picker
    /// leaves the project exactly as it found it.
    /// </summary>
    private static SeedRecord CaptureCurrent(int seed, bool planView)
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

        // The gameplay camera is the one the generator itself consults for its
        // sight-line gates, so the sheet judges the map through exactly the
        // frame the gates were protecting.
        Camera gameCam = SceneQuery.FirstInActiveScene<Camera>();
        if (gameCam != null && !gameCam.orthographic)
        {
            // Height and pitch define a fixed-camera framing on their own, so
            // the pose is read off the transform alone — no dependency on
            // finding the Core, which is an untyped Transform in this scene.
            float pitch = gameCam.transform.eulerAngles.x;
            float height = gameCam.transform.position.y;
            float reach = pitch > 1f && pitch < 89f
                ? height / Mathf.Tan(pitch * Mathf.Deg2Rad)
                : 0f;
            rec.camPose = $"{pitch:0}° pitch, {height:0} m up, {reach:0} m out, {gameCam.fieldOfView:0}° FOV";
        }

        // Fall back to the plan when there is no perspective camera to borrow —
        // a blank cell would hide the map instead of showing it badly.
        rec.shot = planView || gameCam == null || gameCam.orthographic
            ? ShootTopDown(seed, rec.theme)
            : ShootGameView(gameCam, seed, rec.theme);

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

    /// <summary>
    /// Shoot the map THROUGH the scene's own gameplay camera: its pitch, its
    /// distance, its FOV, its post stack, its fog and its sky. Nothing is
    /// reconstructed, because a reconstruction is a second opinion about the
    /// frame and the whole point is to see the first one.
    ///
    /// The camera is borrowed and handed back: its target texture is restored
    /// even though the scene is deleted moments later, since a capture that
    /// quietly mutates the thing it measures is how measurement tools start
    /// lying.
    /// </summary>
    private static Texture2D ShootGameView(Camera cam, int seed, string theme)
    {
        // The label rides ON the camera, two metres in front, so it lands in
        // the same corner of every cell at the same size regardless of how the
        // map's camera solve placed the rig. The flat world-space label the
        // plan view uses would be nearly edge-on at a gameplay pitch.
        // Clear of the near plane, whatever this map's camera solve chose for it.
        float dist = Mathf.Max(2f, cam.nearClipPlane * 2f);
        float visH = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float visW = visH * (GameCellW / (float)GameCellH);

        var labelGo = new GameObject("ContactSheetLabel");
        var tmp = labelGo.AddComponent<TextMeshPro>();
        tmp.text = $"s{seed}  {theme}";
        // World-space TMP measures a line at roughly fontSize/10 world units at
        // scale 1, so this targets ~9% of the frame height. fontSize is the
        // knob if the stamp reads too small or too large in the sheet.
        tmp.fontSize = Mathf.Max(0.5f, visH * 0.9f);
        tmp.color = new Color(0.3f, 0.95f, 1f, 1f);
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.rectTransform.sizeDelta = new Vector2(visW * 0.9f, visH * 0.25f);
        tmp.rectTransform.pivot = new Vector2(0f, 1f);
        labelGo.transform.SetParent(cam.transform, false);
        // Identity rotation under the camera = the label's forward matches the
        // camera's, which is the orientation TMP renders readable from.
        labelGo.transform.localRotation = Quaternion.identity;
        labelGo.transform.localPosition =
            new Vector3(-visW * 0.45f, visH * 0.45f, dist);

        var rt = new RenderTexture(GameCellW, GameCellH, 24);
        var shot = new Texture2D(GameCellW, GameCellH, TextureFormat.RGB24, false);
        RenderTexture previousTarget = cam.targetTexture;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, GameCellW, GameCellH), 0, 0);
            shot.Apply();
        }
        finally
        {
            RenderTexture.active = null;
            cam.targetTexture = previousTarget;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(labelGo);
        }
        return shot;
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

        var rt = new RenderTexture(PlanCellPx, PlanCellPx, 24);
        var shot = new Texture2D(PlanCellPx, PlanCellPx, TextureFormat.RGB24, false);
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, PlanCellPx, PlanCellPx), 0, 0);
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
        List<SeedRecord> records, int passes, bool planView)
    {
        if (!AssetDatabase.IsValidFolder(OutDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Docs", "ContactSheets");

        string view = planView ? "plan" : "game";
        string baseName = $"ContactSheet_{GenerationPipeline.Sanitise(blueprintName)}_from{startSeed}_{view}";
        string pngPath = $"{OutDir}/{baseName}.png";
        string mdPath = $"{OutDir}/{baseName}.md";

        // Cells take the shape of the view: 16:9 for the game frame, square for
        // the plan. A shot captured in one shape and pasted into the other is
        // a different composition, which defeats the point of looking.
        var shots = records.Where(r => r.passed && r.shot != null).ToList();
        int cellW = planView ? PlanCellPx : GameCellW;
        int cellH = planView ? PlanCellPx : GameCellH;
        if (shots.Count > 0)
        {
            cellW = shots[0].shot.width;
            cellH = shots[0].shot.height;
        }

        // 3×3 grid, row-major from the top-left, dark filler for empty cells.
        var sheet = new Texture2D(GridCols * cellW, GridRows * cellH, TextureFormat.RGB24, false);
        var filler = Enumerable.Repeat(new Color(0.06f, 0.07f, 0.09f, 1f), cellW * cellH).ToArray();
        for (int cell = 0; cell < GridCols * GridRows; cell++)
        {
            int cx = (cell % GridCols) * cellW;
            int cy = (GridRows - 1 - cell / GridCols) * cellH;   // row 0 at the TOP
            // A fallback shot can differ in shape from the sheet's cells (no
            // gameplay camera in that one scene); the filler keeps the grid
            // aligned rather than throwing on a size mismatch.
            if (cell < shots.Count &&
                shots[cell].shot.width == cellW && shots[cell].shot.height == cellH)
                sheet.SetPixels(cx, cy, cellW, cellH, shots[cell].shot.GetPixels());
            else
                sheet.SetPixels(cx, cy, cellW, cellH, filler);
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
        md.AppendLine(planView
            ? "**Plan view** — orthographic from above, for reading route topology at a glance. " +
              "This is not what the player sees; use the game view to judge composition."
            : "**Game view** — shot through each map's own gameplay camera: real pitch, distance, " +
              "post and fog. This is the frame the player looks at for the length of a level.");
        md.AppendLine();
        md.AppendLine($"{passes}/{GridCols * GridRows} passing seeds in " +
                      $"{records.Count} attempt(s). Grid reads row-major from the top-left.");
        md.AppendLine();
        md.AppendLine("| cell | seed | verdict | theme | routes (m) | pads | maxLive | hpGrowth | camera |");
        md.AppendLine("|-----:|-----:|---------|-------|------------|------|--------:|---------:|--------|");
        int cellNo = 0;
        foreach (var r in records.Where(r => r.passed))
        {
            cellNo++;
            md.AppendLine($"| {cellNo} | {r.seed} | PASS | {r.theme} | {r.routes} " +
                          $"| {r.padMix} | {r.maxLive} | {r.hpGrowth:0.###} | {r.camPose ?? "—"} |");
        }
        foreach (var r in records.Where(r => !r.passed))
            md.AppendLine($"| — | {r.seed} | FAIL @ {r.failStage} | | | | | | |");
        md.AppendLine();
        md.AppendLine("Pick a (seed, theme), set the seed on the blueprint in the Generator " +
                      "window, and generate it for real — the sheet deleted every artifact " +
                      "it produced.");
        if (!planView)
        {
            md.AppendLine();
            md.AppendLine("## Reading the game view");
            md.AppendLine();
            md.AppendLine("Things worth judging here that a plan cannot show:");
            md.AppendLine();
            md.AppendLine("- **Is there a foreground?** Anything near the lens that frames the shot " +
                          "and creates depth, or does the field start at the middle distance?");
            md.AppendLine("- **Is the Core the subject?** It should be the most contrasted and most " +
                          "led-to thing on screen. The routes are the strongest lines in the image.");
            md.AppendLine("- **Do depth bands separate?** Foreground, stage and horizon should differ " +
                          "in value and saturation, not just in distance.");
            md.AppendLine("- **Would a unit read against this?** Dressing has a second job: to lose " +
                          "gracefully to the things the player must track. Props the size of an " +
                          "enemy, at an enemy's contrast, are a readability cost.");
            md.AppendLine("- **Is the flat band obvious?** Relief is masked to zero inside the play " +
                          "corridor, so the part the eye lives in is the flattest part of the map.");
        }
        File.WriteAllText(mdPath, md.ToString());

        AssetDatabase.Refresh();
        Debug.Log($"[ContactSheet] {passes}/{GridCols * GridRows} passing seeds " +
                  $"({records.Count} attempts) → {pngPath} + {mdPath}");
        var png = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
        if (png != null)
            EditorGUIUtility.PingObject(png);
    }
}
