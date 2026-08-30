using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// The fast half of the two-speed review (EnvPack Builder L5): ten STAGED
/// scenes of the pack — ground, sun, fog, sky, gameplay-pose camera, and the
/// pack's props arranged in their bands — saved to disk for walking around,
/// plus a 5×2 sheet PNG shot through each scene's camera.
///
/// These are NOT levels. No routes, no gates, no balance — seconds per scene
/// instead of a full pipeline run — because the question they answer is "does
/// this pack look like the references?", not "does this pack make a fair map?".
/// The second question already has its tool: the ContactSheet game view runs
/// nine seeds through the REAL generator. Keeping the two apart is what lets
/// this one stage massifs on a horizon arc with no clearance rules at all.
///
/// Deliberately shows the pack's BASE look (its own sun and fog, no weather):
/// the art-direction finding was that the base look had never been on screen
/// because the only weather preset overrode it on every map. Lookdev exists to
/// finally judge that base.
///
/// Scenes are disposable by design — regenerate at will, delete from the menu.
/// </summary>
public static class LookdevStager
{
    private const string OutDir = "Assets/_COREHOLD/Lookdev";
    private const int Variants = 10;
    private const int CellW = 512, CellH = 288;
    private const int SheetCols = 5;

    // Gameplay-pose approximation: pitch 38° from 85 m up looks at ~z+9,
    // which centres the standard 130×75 field. Printed per scene so a
    // composition judgement is anchored to a framing.
    private static readonly Vector3 CamPos = new Vector3(0f, 85f, -100f);
    private const float CamPitch = 38f;
    private const float CamFov = 55f;

    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 2463534242u : seed; }
        public uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
        public float Range(float min, float max) => min + (NextU() / 4294967296f) * (max - min);
    }

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/5. Stage Lookdev Scenes (10)", false, 74)]
    public static void StageMenu()
    {
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[Lookdev] Select an ArtTarget asset first.");
            return;
        }
        Debug.Log(Stage(target));
    }

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/Delete Lookdev Scenes", false, 86)]
    public static void DeleteAll()
    {
        AssetDatabase.DeleteAsset(OutDir);
        AssetDatabase.Refresh();
        Debug.Log("[Lookdev] Deleted " + OutDir + " — scenes are disposable by design; re-run step 5 any time.");
    }

    public static string Stage(ArtTarget target)
    {
        EnvPack pack = PackMatcher.FindPack(target.themeName);
        if (pack == null)
            return $"[Lookdev] No EnvPack with themeName '{target.themeName}'.";

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return "[Lookdev] Cancelled by user.";
        string returnScene = SceneManager.GetActiveScene().path;

        if (!AssetDatabase.IsValidFolder(OutDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD", "Lookdev");

        // Entries by role — the staging bands. Massifs are whatever the pack's
        // Silhouette role holds; an empty list stages an empty horizon, which
        // is itself the honest review of today's pack.
        var byRole = new Dictionary<EnvPack.PropRole, List<EnvPack.Entry>>();
        foreach (EnvPack.PropRole role in new[] { EnvPack.PropRole.Silhouette, EnvPack.PropRole.Landmark,
                                                  EnvPack.PropRole.MidField, EnvPack.PropRole.Clutter })
            byRole[role] = pack.entries != null
                ? pack.entries.Where(e => e.prefab != null && e.role == role)
                    .OrderBy(e => e.prefab.name, System.StringComparer.Ordinal).ToList()
                : new List<EnvPack.Entry>();

        var log = new StringBuilder();
        log.AppendLine($"=== LOOKDEV — '{pack.themeName}', {Variants} scene(s) ===");
        log.AppendLine($"  pool: {byRole[EnvPack.PropRole.Silhouette].Count} silhouette (massif band), " +
                       $"{byRole[EnvPack.PropRole.Landmark].Count} landmark, " +
                       $"{byRole[EnvPack.PropRole.MidField].Count} midfield, " +
                       $"{byRole[EnvPack.PropRole.Clutter].Count} clutter");
        log.AppendLine($"  camera: {CamPitch}° pitch, {CamPos.y:0} m up, FOV {CamFov}° — base look only, no weather");

        // Pixels go straight into MANAGED memory and the texture dies on the
        // spot: the editor destroys non-asset objects on scene operations, and
        // this loop performs twenty of them — holding live Texture2Ds across
        // it is how the first field run lost all ten captures. A Color[] is
        // beyond Unity's reach.
        var shots = new List<Color[]>();
        var rows = new List<string>();
        for (int v = 0; v < Variants; v++)
        {
            int seed = 1000 + v;
            var rng = new Rng((uint)(target.themeName.GetHashCode() * 31 + seed));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            float yawJitter = rng.Range(-8f, 8f);
            Camera cam = BuildStage(pack, target, ref rng, yawJitter,
                                    out int massifs, out int outcrops, out int mids, out int bits);

            string scenePath = $"{OutDir}/Lookdev_{pack.themeName}_s{seed}.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);

            Texture2D shot = EditorShot.Capture(cam, CellW, CellH);
            shots.Add(shot.GetPixels());
            Object.DestroyImmediate(shot);

            rows.Add($"| {v + 1} | {seed} | {massifs} | {outcrops} | {mids} | {bits} | " +
                     $"{target.sunAngles.y + yawJitter:0}° |");
        }

        WriteSheet(pack.themeName, shots, rows, log);

        if (!string.IsNullOrEmpty(returnScene))
            EditorSceneManager.OpenScene(returnScene, OpenSceneMode.Single);
        else
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        log.AppendLine($"  {Variants} scene(s) under {OutDir} — open any to walk around; " +
                       "Delete Lookdev Scenes discards the lot.");
        return log.ToString();
    }

    // ------------------------------------------------------------- the stage

    private static Camera BuildStage(EnvPack pack, ArtTarget target, ref Rng rng, float yawJitter,
                                     out int massifs, out int outcrops, out int mids, out int bits)
    {
        // Ground: one big plane wearing the pack's material. A scene-embedded
        // material INSTANCE (the TerrainStage "(Baked)" pattern) so tiling can
        // be set without editing the shared asset.
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "LookdevGround";
        ground.transform.localScale = Vector3.one * 70f;   // 700×700 m
        var mr = ground.GetComponent<MeshRenderer>();
        Material mat = pack.groundMaterial != null
            ? new Material(pack.groundMaterial)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.name = "LookdevGround(Baked)";
        if (pack.groundTilingPerMetre > 0f)
            mat.mainTextureScale = Vector2.one * (700f * pack.groundTilingPerMetre);
        mr.sharedMaterial = mat;

        // Sun + fog + sky: the pack's BASE look, straight from the target.
        var sunGo = new GameObject("Sun");
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = target.sunColor;
        sun.intensity = target.sunIntensity;
        sun.shadows = LightShadows.Soft;
        sunGo.transform.rotation = Quaternion.Euler(target.sunAngles.x, target.sunAngles.y + yawJitter, 0f);

        RenderSettings.fog = target.fogDensity > 0f;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = target.fogColor;
        RenderSettings.fogDensity = target.fogDensity;
        if (pack.skyboxMaterial != null)
            RenderSettings.skybox = pack.skyboxMaterial;

        // Camera at the gameplay pose, with post so the review sees the grade.
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        camGo.transform.position = CamPos;
        camGo.transform.rotation = Quaternion.Euler(CamPitch, 0f, 0f);
        cam.fieldOfView = CamFov;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 1500f;
        var data = camGo.GetComponent<UniversalAdditionalCameraData>();
        if (data == null)
            data = camGo.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        if (pack.postProfile != null)
        {
            var volGo = new GameObject("Lookdev Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1f;
            vol.sharedProfile = pack.postProfile;
        }

        var props = new GameObject("Staging");

        // Massif band where the GENERATOR actually puts silhouettes: the
        // north band just past the field edge (z ≈ halfD + 8–22 m, x within
        // ±halfW×1.2 — PropPlacer's !inField draw), roughly 150 m from the
        // camera. The first sheet placed them at 180–320 m and undersold how
        // much they loom in play by 2×; a review surface that flatters is
        // worse than none. One distinct entry per slot before any repeats,
        // and footprint spacing so meshes read as a range, not a fused wall.
        var sil = ListFor(pack, EnvPack.PropRole.Silhouette);
        massifs = 0;
        int slots = sil.Count > 0 ? 12 : 0;
        float lastX = float.NegativeInfinity, lastR = 0f;
        for (int i = 0; i < slots; i++)
        {
            EnvPack.Entry e = sil[i % sil.Count];
            float x = Mathf.Lerp(-85f, 85f, (i + 0.5f) / slots) + rng.Range(-10f, 10f);
            float z = rng.Range(46f, 68f);
            float estR = e.footprintRadius *
                         Mathf.Max(1f, (e.scaleRange.x + e.scaleRange.y) * 0.5f);
            if (x - lastX < (lastR + estR) * 0.6f)
                continue;   // would fuse with the previous massif — skip the slot
            Place(e, new Vector3(x, 0f, z), ref rng, props.transform);
            lastX = x;
            lastR = estR;
            massifs++;
        }

        outcrops = PlaceBand(pack, EnvPack.PropRole.Landmark, 8, ref rng, props.transform,
            r => { float x = r.Range(-85f, 85f); return new Vector3(x, 0f,
                   Mathf.Abs(x) > 48f ? r.Range(-25f, 65f) : r.Range(38f, 70f)); });
        mids = PlaceBand(pack, EnvPack.PropRole.MidField, 10, ref rng, props.transform,
            r => new Vector3(r.Range(-45f, 45f), 0f, r.Range(-30f, 45f)));
        bits = PlaceBand(pack, EnvPack.PropRole.Clutter, 14, ref rng, props.transform,
            r => new Vector3(r.Range(-50f, 50f), 0f, r.Range(-35f, 50f)));

        return cam;
    }

    private static List<EnvPack.Entry> ListFor(EnvPack pack, EnvPack.PropRole role)
        => pack.entries != null
            ? pack.entries.Where(e => e.prefab != null && e.role == role)
                .OrderBy(e => e.prefab.name, System.StringComparer.Ordinal).ToList()
            : new List<EnvPack.Entry>();

    private static int PlaceBand(EnvPack pack, EnvPack.PropRole role, int count, ref Rng rng,
                                 Transform parent, System.Func<Rng, Vector3> position)
    {
        var pool = ListFor(pack, role);
        if (pool.Count == 0)
            return 0;
        int placed = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = position(rng);
            rng.NextU();   // keep the stream moving whether or not we accept
            if (Mathf.Abs(pos.x) < 8f && pos.z > -40f && pos.z < 40f)
                continue;   // the corridor axis stays clear, like a real map's lane
            Place(pool[(int)(rng.NextU() % (uint)pool.Count)], pos, ref rng, parent);
            placed++;
        }
        return placed;
    }

    private static void Place(EnvPack.Entry e, Vector3 pos, ref Rng rng, Transform parent)
    {
        float lo = e.scaleRange.x > 0f ? e.scaleRange.x : 1f;
        float hi = e.scaleRange.y > 0f ? e.scaleRange.y : 1f;
        float scale = rng.Range(lo, hi);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(e.prefab);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(pos.x, -0.05f * scale, pos.z);
        go.transform.rotation = Quaternion.Euler(0f, rng.Range(0f, 360f), 0f);
        go.transform.localScale = Vector3.one * scale;
    }

    // ------------------------------------------------------------- the sheet

    private static void WriteSheet(string theme, List<Color[]> shots,
                                   List<string> rows, StringBuilder log)
    {
        int cols = SheetCols, rowsN = Mathf.CeilToInt(shots.Count / (float)cols);
        var sheet = new Texture2D(cols * CellW, rowsN * CellH, TextureFormat.RGB24, false)
        { hideFlags = HideFlags.HideAndDontSave };
        var filler = Enumerable.Repeat(new Color(0.06f, 0.07f, 0.09f, 1f), CellW * CellH).ToArray();
        for (int cell = 0; cell < cols * rowsN; cell++)
        {
            int cx = (cell % cols) * CellW;
            int cy = (rowsN - 1 - cell / cols) * CellH;
            sheet.SetPixels(cx, cy, CellW, CellH, cell < shots.Count ? shots[cell] : filler);
        }
        sheet.Apply();

        string png = $"{OutDir}/Lookdev_{theme}_sheet.png";
        File.WriteAllBytes(png, sheet.EncodeToPNG());
        Object.DestroyImmediate(sheet);

        var md = new StringBuilder();
        md.AppendLine($"# Lookdev — {theme}");
        md.AppendLine();
        md.AppendLine("Staged pack review, base look, no weather. Cells read row-major from the top-left.");
        md.AppendLine();
        md.AppendLine("| cell | seed | massifs | outcrops | midfield | clutter | sun yaw |");
        md.AppendLine("|-----:|-----:|--------:|---------:|---------:|--------:|--------:|");
        foreach (string r in rows)
            md.AppendLine(r);
        md.AppendLine();
        md.AppendLine("A bare horizon here is the honest state of the Silhouette role — " +
                      "see the match report's gap section for what to buy.");
        File.WriteAllText($"{OutDir}/Lookdev_{theme}_sheet.md", md.ToString());

        AssetDatabase.Refresh();
        log.AppendLine($"  sheet: {png}");
        var pngAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(png);
        if (pngAsset != null)
            EditorGUIUtility.PingObject(pngAsset);
    }
}
