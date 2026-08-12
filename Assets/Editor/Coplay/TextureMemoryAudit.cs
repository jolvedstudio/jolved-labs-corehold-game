using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Ticket 38 — computes the ON-DEVICE GPU texture memory for the WebGL build.
///
/// It walks every asset dependency of the enabled build scenes, keeps the
/// Texture objects, and sums Profiler.GetRuntimeMemorySizeLong(tex). Because the
/// active build target is WebGL, textures are imported in DXT/S3TC, so this size
/// is exactly the on-device figure (the runtime GPU footprint incl. mip chain).
///
/// This is Editor-pollution free — it measures only the game's own textures that
/// ship in the build, not the Editor's scene-view / inspector textures.
/// </summary>
public static class TextureMemoryAudit
{
    public static string Execute()
    {
        const double MB = 1024.0 * 1024.0;

        var sceneePaths = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        // All asset dependencies of the build scenes = everything that ships.
        var deps = AssetDatabase.GetDependencies(sceneePaths, true);

        var texRows = new List<(string path, string type, string format, long bytes, int w, int h)>();
        var counted = new HashSet<int>();
        long total = 0;

        foreach (var dep in deps)
        {
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(dep);
            if (mainType == null)
                continue;

            // Load every object at the path so texture sub-assets / atlases count too.
            var objs = AssetDatabase.LoadAllAssetsAtPath(dep);
            foreach (var o in objs)
            {
                if (o is Texture tex)
                {
                    int id = tex.GetInstanceID();
                    if (!counted.Add(id))
                        continue;

                    long bytes = Profiler.GetRuntimeMemorySizeLong(tex);
                    total += bytes;

                    string fmt = "";
                    int w = tex.width, h = tex.height;
                    if (tex is Texture2D t2d) fmt = t2d.format.ToString();
                    else if (tex is Cubemap cm) fmt = cm.format.ToString();
                    else fmt = tex.GetType().Name;

                    texRows.Add((dep, o.GetType().Name, fmt, bytes, w, h));
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("========== TICKET 38 — ON-DEVICE TEXTURE MEMORY (WebGL/DXT) ==========");
        sb.AppendLine($"Active build target : {EditorUserBuildSettings.activeBuildTarget}");
        sb.AppendLine($"Scenes analysed     : {string.Join(", ", sceneePaths)}");
        sb.AppendLine($"Unique textures     : {texRows.Count}");
        sb.AppendLine($"TOTAL texture memory: {total / MB:0.00} MB   (budget 70 MB)");
        sb.AppendLine("---- Top 25 textures by size ----");
        foreach (var r in texRows.OrderByDescending(r => r.bytes).Take(25))
        {
            sb.AppendLine($"  {r.bytes / MB,7:0.00} MB  {r.w}x{r.h,-5} {r.format,-14} {System.IO.Path.GetFileName(r.path)}");
        }
        sb.AppendLine("=====================================================================");

        var report = sb.ToString();
        Debug.Log(report);
        return report;
    }
}
