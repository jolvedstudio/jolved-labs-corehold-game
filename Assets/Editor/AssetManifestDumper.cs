using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class AssetManifestDumper
{
    const string Root = "Assets/Vendor";
    static int _processed;

    [MenuItem("Tools/COREHOLD/Dump Asset Manifest")]
    public static void Dump()
    {
        if (!AssetDatabase.IsValidFolder(Root))
        {
            Debug.LogError($"[COREHOLD] '{Root}' does not exist. Move the vendor packs under {Root} first, " +
                           "or change the Root constant. Aborting — no manifest written.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# COREHOLD Asset Manifest");
        sb.AppendLine($"Generated {System.DateTime.Now:yyyy-MM-dd HH:mm} · Unity {Application.unityVersion}");
        sb.AppendLine();

        int prefabCount = 0;
        try
        {
            prefabCount = Section(sb, "Prefabs", "t:Prefab", path =>
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) return null;
                int tris = 0;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                    tris += CountTris(mf.sharedMesh);
                foreach (var sr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    tris += CountTris(sr.sharedMesh);
                var rends = go.GetComponentsInChildren<Renderer>(true);
                var mats = new HashSet<string>(rends.SelectMany(r => r.sharedMaterials)
                                                    .Where(m => m != null).Select(m => m.name));
                int skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                return $"{tris} tris · {rends.Length} renderers ({skinned} skinned) · mats: {string.Join(", ", mats)}";
            });

            Section(sb, "Animation clips", "t:AnimationClip", path =>
            {
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                              .Where(c => !c.name.StartsWith("__preview")).ToArray();
                return clips.Length == 0 ? null
                     : string.Join(", ", clips.Select(c => $"{c.name} ({c.length:0.00}s, {(c.isLooping ? "loop" : "once")})"));
            });

            Section(sb, "Materials", "t:Material", path =>
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) return null;
                var shader = m.shader == null ? "<MISSING SHADER>" : m.shader.name;
                bool builtIn = shader.StartsWith("Standard") || shader.StartsWith("Legacy") || shader.StartsWith("Mobile/");
                return $"shader: {shader}{(builtIn ? "   <-- BUILT-IN, needs URP conversion" : "")}";
            });

            Section(sb, "Textures", "t:Texture2D", path =>
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) return null;
                ti.GetSourceTextureWidthAndHeight(out int sw, out int sh);
                bool mult4 = sw % 4 == 0 && sh % 4 == 0;
                return $"source {sw}x{sh} · maxSize {ti.maxTextureSize}"
                     + (mult4 ? "" : "   <-- NOT multiple of 4, blocks block compression");
            });

            Section(sb, "Audio clips", "t:AudioClip", path =>
            {
                var a = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (a == null || ai == null) return null;
                var web = ai.GetOverrideSampleSettings("WebGL");
                return $"{a.length:0.00}s · {a.frequency}Hz · {a.channels}ch · WebGL loadType: {web.loadType}";
            });
        }
        finally { EditorUtility.ClearProgressBar(); }

        var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../AssetManifest.md"));
        File.WriteAllText(outPath, sb.ToString());

        if (prefabCount == 0)
            Debug.LogError($"[COREHOLD] Manifest written to {outPath} but found ZERO prefabs under '{Root}'. " +
                           "This is a failure, not a success — check the vendor packs are actually there.");
        else
            Debug.Log($"[COREHOLD] Manifest written to {outPath} — {prefabCount} prefabs catalogued.");
    }

    static int CountTris(Mesh mesh)
    {
        if (mesh == null) return 0;
        int t = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) t += (int)(mesh.GetIndexCount(i) / 3);
        return t;   // GetIndexCount avoids the big int[] copy that mesh.triangles allocates
    }

    static int Section(StringBuilder sb, string title, string filter, System.Func<string, string> describe)
    {
        var paths = AssetDatabase.FindAssets(filter, new[] { Root })
                        .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).ToList();
        if (paths.Count == 0) { sb.AppendLine($"## {title}").AppendLine().AppendLine("_none found_").AppendLine(); return 0; }

        sb.AppendLine($"## {title}  ({paths.Count} assets scanned)").AppendLine();
        string lastFolder = null;
        int written = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (EditorUtility.DisplayCancelableProgressBar($"Manifest: {title}", path, (float)i / paths.Count)) break;

            string info;
            try { info = describe(path); }
            catch (System.Exception e)
            {
                sb.AppendLine($"- `{Path.GetFileName(path)}` — **ERROR: {e.Message}**");
                continue;   // surface broken assets; they are exactly what we are hunting for
            }
            if (info == null) continue;

            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (folder != lastFolder) { sb.AppendLine().AppendLine($"### {folder}").AppendLine(); lastFolder = folder; }
            sb.AppendLine($"- `{Path.GetFileName(path)}` — {info}");
            written++;

            if (++_processed % 200 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
        }
        sb.AppendLine();
        return written;
    }
}
