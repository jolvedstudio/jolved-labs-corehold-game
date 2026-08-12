using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ticket 38 follow-up — clamp every GAME texture's import Max Size so it never
/// exceeds 1024. Textures already authored at 512 or below are left as-is (the
/// "leave it at 512 if possible" rule). Both the default importer setting AND any
/// per-platform overrides (WebGL / Android / iPhone / Standalone) are clamped, so
/// nothing sneaks a larger size in on a specific target.
///
/// Scope: everything under Assets/ except the Editor tooling folder and gizmo
/// icons (not shipped as game content).
/// </summary>
public static class ClampTextureMaxSize
{
    private const int Cap = 1024;
    private static readonly string[] Platforms = { "Standalone", "WebGL", "Android", "iPhone" };

    // Folders we do NOT treat as game textures.
    private static readonly string[] ExcludePrefixes =
    {
        "Assets/Editor/",
        "Assets/Gizmos/",
        "Assets/TutorialInfo/",
    };

    public static string Execute()
    {
        var guids = AssetDatabase.FindAssets("t:Texture", new[] { "Assets" });
        var changed = new List<string>();
        int scanned = 0, alreadyOk = 0, skipped = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    continue;
                if (ExcludePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue; // e.g. render textures / non-imported textures

                scanned++;
                bool dirty = false;
                int oldDefault = importer.maxTextureSize;

                // Default (all-platform) setting.
                if (importer.maxTextureSize > Cap)
                {
                    importer.maxTextureSize = Cap;
                    dirty = true;
                }

                // Per-platform overrides.
                foreach (var plat in Platforms)
                {
                    var ps = importer.GetPlatformTextureSettings(plat);
                    if (ps != null && ps.overridden && ps.maxTextureSize > Cap)
                    {
                        ps.maxTextureSize = Cap;
                        importer.SetPlatformTextureSettings(ps);
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    importer.SaveAndReimport();
                    changed.Add($"  {oldDefault,5} -> {importer.maxTextureSize,-5} {path}");
                }
                else
                {
                    alreadyOk++;
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        var sb = new StringBuilder();
        sb.AppendLine("========== CLAMP TEXTURE MAX SIZE (cap 1024) ==========");
        sb.AppendLine($"Scanned game textures : {scanned}");
        sb.AppendLine($"Clamped to 1024       : {changed.Count}");
        sb.AppendLine($"Already <= 1024       : {alreadyOk}");
        sb.AppendLine($"Skipped (non-game)    : {skipped}");
        sb.AppendLine("---- Clamped textures ----");
        foreach (var line in changed.OrderBy(s => s))
            sb.AppendLine(line);
        sb.AppendLine("=======================================================");

        var report = sb.ToString();
        Debug.Log(report);
        return report;
    }
}
