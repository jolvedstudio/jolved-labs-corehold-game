using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The measured prefab inventory (EnvPack Builder L2): every candidate prefab's
/// size, proportion and dominant color, cached so the matcher can score
/// thousands of candidates without touching a mesh.
///
/// Dominant color comes from a SYNCHRONOUS render through
/// <see cref="PreviewRenderUtility"/> — the prefab is instantiated into an
/// isolated preview scene, framed, lit neutrally, and shot at 64×64; the
/// average of the non-background pixels is what the mesh actually shows.
/// Sampling the albedo texture directly would be wrong twice: it needs the
/// Read/Write import flag, and it lies for atlases (a mesh using one small UV
/// region averages the whole sheet). AssetPreview would avoid both but loads
/// asynchronously, which a menu-item context cannot pump reliably.
///
/// The cache is JSON in Library/ — machine-local BY NATURE, exactly like the
/// vendor folders it indexes, so it can never be committed with dangling
/// references. Records store paths and GUIDs as strings, never object refs.
/// </summary>
public static class PrefabIndexer
{
    private const string CachePath = "Library/CoreholdPrefabIndex.json";
    private const int PreviewSize = 64;

    /// <summary>Fraction of non-background pixels below which the color is
    /// declared unknown (a broken or fully-transparent render).</summary>
    private const float MinCoverage = 0.10f;

    [System.Serializable]
    public class Rec
    {
        public string path;
        public string guid;
        public string sourcePack;   // top-level folder identity, e.g. "Assets/Vendor/MesaPack"
        public long stamp;          // write-time ticks + length — cheap change detection
        public float height;        // authored metres (scale 1)
        public float radius;        // pivot-circumscribing XZ radius
        public float aspect;        // height / max footprint side
        public float r, g, b;       // dominant rendered color
        public bool colorValid;
    }

    /// <summary>
    /// A candidate SURFACE — a ground material or a skybox. Indexed alongside
    /// the prefabs because the scale ladder is only half a look: the first
    /// lookdev sheet's loudest problem was the ground, and the builder could
    /// not see a single ground texture in the project.
    /// </summary>
    [System.Serializable]
    public class MatRec
    {
        public string path;
        public string guid;
        public string sourcePack;
        public long stamp;
        public bool isSkybox;        // its shader is a Skybox/* family
        public bool hasTexture;      // a flat colour material makes poor ground
        public float r, g, b;        // base texture average × tint
        public bool colorValid;
    }

    [System.Serializable]
    private class IndexData
    {
        public List<Rec> recs = new List<Rec>();
        public List<MatRec> mats = new List<MatRec>();
    }

    // ------------------------------------------------------------------ menu

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/2. Scan Prefab Index", false, 71)]
    public static void ScanMenu()
    {
        // STRICT: the scan uses the selected ArtTarget's folder list, exactly —
        // no silent defaults. A fallback list once masked the real problem
        // (a purchased pack imported OUTSIDE the listed roots) behind a scan
        // that looked like it had run properly.
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[PrefabIndexer] Select an ArtTarget — the scan uses ITS scanFolders, " +
                           "exactly those roots and everything underneath.");
            return;
        }
        if (target.scanFolders == null || target.scanFolders.Length == 0)
        {
            Debug.LogError($"[PrefabIndexer] '{target.name}' has an empty scanFolders list — add the " +
                           "roots to scan. Note: Asset Store packs import to Assets/<PackName> by " +
                           "default; move them under Assets/Vendor (git-ignored) or add their folder.");
            return;
        }
        Debug.Log(Scan(target.scanFolders));
    }

    // ------------------------------------------------------------------ API

    /// <summary>Load the cached index (empty list when never scanned).</summary>
    public static List<Rec> Load() => LoadData()?.recs ?? new List<Rec>();

    /// <summary>The cached SURFACE candidates — ground materials and skyboxes.</summary>
    public static List<MatRec> LoadMaterials() => LoadData()?.mats ?? new List<MatRec>();

    private static IndexData LoadData()
    {
        if (!File.Exists(CachePath))
            return null;
        try { return JsonUtility.FromJson<IndexData>(File.ReadAllText(CachePath)); }
        catch { return null; }
    }

    /// <summary>
    /// Scan the folders, reusing cached records whose file stamp is unchanged,
    /// and rewrite the cache. Returns the report.
    /// </summary>
    public static string Scan(string[] folders)
    {
        var log = new StringBuilder();
        log.AppendLine("=== PREFAB INDEX SCAN — exactly these roots, all subfolders ===");

        // Every listed root gets its own line, INCLUDING zeros and missing
        // ones, so "the scanner skipped my pack" and "my pack is not where the
        // list points" stop being indistinguishable. Nothing inside a listed
        // root is excluded except meshless prefabs (nothing to measure).
        var valid = new List<string>();
        foreach (string f in folders)
        {
            if (!AssetDatabase.IsValidFolder(f))
            {
                log.AppendLine($"  ⚠ {f,-44} MISSING on this machine — if a purchased pack lives " +
                               "elsewhere (Asset Store imports to Assets/<PackName>), move it under " +
                               "Assets/Vendor or add its folder to scanFolders");
                continue;
            }
            int inRoot = AssetDatabase.FindAssets("t:Prefab", new[] { f }).Length;
            log.AppendLine($"  ✓ {f,-44} {inRoot,5} prefab(s), recursive");
            valid.Add(f);
        }
        if (valid.Count == 0)
        {
            log.AppendLine("  no scannable folders — nothing indexed.");
            return log.ToString();
        }

        var cached = Load().ToDictionary(r => r.guid, r => r);
        var recs = new List<Rec>();
        int reused = 0, measured = 0, failed = 0;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", valid.ToArray());
        var pru = new PreviewRenderUtility();
        try
        {
            SetupPreviewRig(pru);
            foreach (string guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g),
                                                  System.StringComparer.Ordinal))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                long stamp = Stamp(path);
                if (cached.TryGetValue(guid, out Rec old) && old.stamp == stamp)
                {
                    old.path = path;   // survives moves
                    recs.Add(old);
                    reused++;
                    continue;
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || !EnvPackTools.TryMeasure(prefab, out EnvPackTools.Measurement m)
                    || m.height <= 0f)
                {
                    failed++;
                    continue;   // no mesh to speak of — not a dressing candidate
                }

                var rec = new Rec
                {
                    path = path,
                    guid = guid,
                    sourcePack = SourcePack(path),
                    stamp = stamp,
                    height = m.height,
                    radius = m.radius,
                    aspect = m.height / Mathf.Max(0.05f, Mathf.Max(m.footprint.x, m.footprint.y)),
                };
                MeasureColor(pru, prefab, rec);
                recs.Add(rec);
                measured++;
            }
        }
        finally
        {
            pru.Cleanup();
        }

        // ---- surfaces: ground materials and skyboxes -------------------------
        var cachedMats = LoadMaterials().ToDictionary(m => m.guid, m => m);
        var mats = new List<MatRec>();
        int matReused = 0, matMeasured = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", valid.ToArray())
                     .OrderBy(g => AssetDatabase.GUIDToAssetPath(g), System.StringComparer.Ordinal))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            long stamp = Stamp(path);
            if (cachedMats.TryGetValue(guid, out MatRec oldMat) && oldMat.stamp == stamp)
            {
                oldMat.path = path;
                mats.Add(oldMat);
                matReused++;
                continue;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null)
                continue;

            var rec = new MatRec
            {
                path = path,
                guid = guid,
                sourcePack = SourcePack(path),
                stamp = stamp,
                isSkybox = mat.shader.name.StartsWith("Skybox/", System.StringComparison.Ordinal) ||
                           mat.shader.name.Contains("Skybox"),
            };
            rec.colorValid = TryMaterialColor(mat, out Color c, out bool textured);
            rec.hasTexture = textured;
            rec.r = c.r; rec.g = c.g; rec.b = c.b;
            mats.Add(rec);
            matMeasured++;
        }

        File.WriteAllText(CachePath, JsonUtility.ToJson(new IndexData { recs = recs, mats = mats }));

        log.AppendLine($"  {recs.Count} prefab(s) indexed ({measured} measured, {reused} cached, " +
                       $"{failed} skipped meshless) across {valid.Count} folder(s):");
        foreach (var group in recs.GroupBy(r => r.sourcePack).OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            var heights = group.Select(r => r.height).OrderBy(h => h).ToArray();
            log.AppendLine($"    {group.Key,-52} {group.Count(),4}  " +
                           $"heights {heights.First():0.0}–{heights.Last():0.0} m");
        }
        int skies = mats.Count(m => m.isSkybox);
        log.AppendLine($"  {mats.Count} material(s) indexed as surface candidates " +
                       $"({matMeasured} measured, {matReused} cached): " +
                       $"{skies} skybox(es), {mats.Count - skies} ground candidate(s)");

        var tall = recs.Where(r => r.height >= 40f).ToList();
        log.AppendLine(tall.Count > 0
            ? $"  massif-band candidates (≥40 m authored): {tall.Count}"
            : "  massif-band candidates (≥40 m authored): NONE — the scale ladder's top tier " +
              "has nothing to pick from; see the shopping list in docs/art_direction_wadi_rum.md");
        log.AppendLine($"  cache: {CachePath} (machine-local by design)");
        return log.ToString();
    }

    // ------------------------------------------------------------- internals

    private static long Stamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Top-level pack identity: first two path segments for vendor
    /// roots ("Assets/Vendor/MesaPack"), the theme folder for authoring trees.</summary>
    private static string SourcePack(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length >= 3 && (parts[1] == "Vendor" || parts[1] == "Yoge"))
            return $"{parts[0]}/{parts[1]}/{parts[2]}";
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : path;
    }

    /// <summary>
    /// A material's dominant colour: its base texture averaged, multiplied by
    /// its tint. The texture is read by BLITTING through a RenderTexture, so no
    /// Read/Write import flag is needed and nothing about the asset changes.
    /// A cubemap (six-sided skybox) cannot blit that way, so those fall back to
    /// their tint — which for a sky is the meaningful colour anyway.
    /// </summary>
    private static bool TryMaterialColor(Material mat, out Color color, out bool textured)
    {
        color = Color.gray;
        textured = false;

        Texture tex = null;
        foreach (string p in new[] { "_BaseMap", "_MainTex", "_Tex", "_FrontTex" })
            if (mat.HasProperty(p) && mat.GetTexture(p) != null) { tex = mat.GetTexture(p); break; }

        Color tint = Color.white;
        foreach (string p in new[] { "_BaseColor", "_Color", "_SkyTint", "_Tint" })
            if (mat.HasProperty(p)) { tint = mat.GetColor(p); break; }

        if (!(tex is Texture2D tex2d))
        {
            color = tint;
            return true;   // flat or cubemap: the tint IS the colour we can know
        }
        textured = true;

        const int S = 32;
        RenderTexture rt = RenderTexture.GetTemporary(S, S, 0);
        RenderTexture prev = RenderTexture.active;
        var tmp = new Texture2D(S, S, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            Graphics.Blit(tex2d, rt);
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, S, S), 0, 0);
            tmp.Apply();
            Color sum = Color.black;
            Color[] px = tmp.GetPixels();
            foreach (Color p in px) sum += p;
            Color avg = sum / px.Length;
            color = new Color(avg.r * tint.r, avg.g * tint.g, avg.b * tint.b);
            return true;
        }
        catch
        {
            color = tint;
            return true;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Object.DestroyImmediate(tmp);
        }
    }

    private static void SetupPreviewRig(PreviewRenderUtility pru)
    {
        pru.camera.clearFlags = CameraClearFlags.SolidColor;
        pru.camera.backgroundColor = Background;
        pru.camera.fieldOfView = 30f;
        pru.camera.nearClipPlane = 0.1f;
        pru.camera.farClipPlane = 5000f;
        // Neutral rig: a white key plus a soft fill, so the measured color is
        // the prefab's own rather than the lighting's.
        pru.lights[0].intensity = 1.1f;
        pru.lights[0].color = Color.white;
        pru.lights[0].transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        pru.lights[1].intensity = 0.4f;
        pru.lights[1].color = Color.white;
        pru.ambientColor = new Color(0.25f, 0.25f, 0.25f);
    }

    /// <summary>An improbable background the averaging can subtract. Magenta
    /// would collide with broken-shader pink; this green-cyan does not occur in
    /// desert packs, and a render that comes back mostly background is flagged
    /// colorValid = false rather than scored on garbage.</summary>
    private static readonly Color Background = new Color(0f, 0.9f, 0.6f);

    private static void MeasureColor(PreviewRenderUtility pru, GameObject prefab, Rec rec)
    {
        rec.colorValid = false;
        GameObject instance = Object.Instantiate(prefab);
        pru.AddSingleGO(instance);   // preview scene takes ownership

        // Frame the instance from a three-quarter view off its render bounds.
        var renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Object.DestroyImmediate(instance);
            return;
        }
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        float dist = Mathf.Max(0.5f, b.extents.magnitude) /
                     Mathf.Tan(pru.camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
        Vector3 dir = new Vector3(0.5f, 0.35f, -1f).normalized;
        pru.camera.transform.position = b.center + dir * dist;
        pru.camera.transform.rotation = Quaternion.LookRotation(b.center - pru.camera.transform.position);

        pru.BeginStaticPreview(new Rect(0, 0, PreviewSize, PreviewSize));
        pru.camera.Render();
        Texture2D shot = pru.EndStaticPreview();
        if (shot == null)
        {
            Object.DestroyImmediate(instance);
            return;
        }

        Color32[] px = shot.GetPixels32();
        float r = 0f, g = 0f, bl = 0f;
        int n = 0;
        foreach (Color32 c in px)
        {
            float dr = c.r / 255f - Background.r;
            float dg = c.g / 255f - Background.g;
            float db = c.b / 255f - Background.b;
            if (dr * dr + dg * dg + db * db < 0.01f)
                continue;   // background
            r += c.r / 255f; g += c.g / 255f; bl += c.b / 255f;
            n++;
        }
        if (n >= px.Length * MinCoverage)
        {
            rec.r = r / n; rec.g = g / n; rec.b = bl / n;
            rec.colorValid = true;
        }
        Object.DestroyImmediate(instance);
    }
}
