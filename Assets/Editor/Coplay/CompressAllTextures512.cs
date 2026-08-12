using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ticket 38 — second compression pass to reach the 70 MB WebGL budget.
///
/// The DXT+crunch pass got us to ~99 MB; the residual is dominated by ~50 model
/// maps still at 1024 (~1.6 MB each as DXT5Crunched). Halving those to 512 quarters
/// each. This pass caps model/world textures at 512 while KEEPING UI sprites and
/// gameplay icons at 1024 (they are read at native res on screen and would look
/// soft at 512). Compression + crunch already set by the previous pass is retained.
///
/// "Model/world" = default/normal/other 3D content. "UI/icon" is detected by path
/// (Icons, UI, Sprites, SCI-FI UI Pack, Panel_) or SPRITE texture type.
/// </summary>
public static class CompressAllTextures512
{
    private const int ModelCap = 512;
    private const int UiCap = 1024;
    private static readonly string[] Platforms = { "Standalone", "WebGL", "Android", "iPhone" };

    private static readonly string[] ExcludePrefixes =
    {
        "Assets/Editor/", "Assets/Gizmos/", "Assets/TutorialInfo/",
    };

    private static bool IsUi(string path, TextureImporter imp)
    {
        if (imp.textureType == TextureImporterType.Sprite) return true;
        string p = path.Replace('\\', '/');
        return p.IndexOf("/Icons/", StringComparison.OrdinalIgnoreCase) >= 0
            || p.IndexOf("/UI/", StringComparison.OrdinalIgnoreCase) >= 0
            || p.IndexOf("/Sprites/", StringComparison.OrdinalIgnoreCase) >= 0
            || p.IndexOf("SCI-FI UI Pack", StringComparison.OrdinalIgnoreCase) >= 0
            || System.IO.Path.GetFileName(p).StartsWith("Panel_", StringComparison.OrdinalIgnoreCase);
    }

    public static string Execute()
    {
        var guids = AssetDatabase.FindAssets("t:Texture", new[] { "Assets" });
        int scanned = 0, changed = 0, skipped = 0;

        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (ExcludePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                if (path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                if (imp.textureType == TextureImporterType.SingleChannel) { skipped++; continue; }

                scanned++;
                int cap = IsUi(path, imp) ? UiCap : ModelCap;
                bool dirty = false;

                if (imp.maxTextureSize > cap) { imp.maxTextureSize = cap; dirty = true; }
                if (imp.textureCompression != TextureImporterCompression.Compressed)
                { imp.textureCompression = TextureImporterCompression.Compressed; dirty = true; }
                if (!imp.crunchedCompression) { imp.crunchedCompression = true; dirty = true; }

                foreach (var plat in Platforms)
                {
                    var ps = imp.GetPlatformTextureSettings(plat);
                    if (ps == null || !ps.overridden) continue;
                    if (ps.maxTextureSize > cap) { ps.maxTextureSize = cap; imp.SetPlatformTextureSettings(ps); dirty = true; }
                }

                if (dirty) { imp.SaveAndReimport(); changed++; }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        return $"512-cap model pass: scanned={scanned} changed={changed} skipped={skipped} (UI/icons kept at 1024).";
    }
}
