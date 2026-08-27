using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Follow-up to CompressAllTextures512. That pass enabled crunch on the DEFAULT
/// importer settings, but any texture with a pre-existing per-platform override
/// (WebGL / Standalone) kept crunch OFF on that override — and the override wins.
/// The Mech_Constructor_Spiders pack (2nd-largest in the build) shipped exactly
/// this way: capped to 512 but uncrunched.
///
/// This pass forces every overridden WebGL/Standalone/Android/iPhone platform
/// setting to: cap size, Compressed, Crunched. UI/icons keep their 1024 cap.
/// </summary>
public static class EnforceCrunchOnOverrides
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
        int scanned = 0, changed = 0;

        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (ExcludePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)) continue;

                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                if (imp.textureType == TextureImporterType.SingleChannel) continue;

                scanned++;
                int cap = IsUi(path, imp) ? UiCap : ModelCap;
                bool dirty = false;

                foreach (var plat in Platforms)
                {
                    var ps = imp.GetPlatformTextureSettings(plat);
                    if (ps == null || !ps.overridden) continue;

                    bool pd = false;
                    if (ps.maxTextureSize > cap) { ps.maxTextureSize = cap; pd = true; }
                    if (ps.textureCompression != TextureImporterCompression.Compressed)
                    { ps.textureCompression = TextureImporterCompression.Compressed; pd = true; }
                    if (!ps.crunchedCompression) { ps.crunchedCompression = true; pd = true; }
                    // Automatic format so crunch-capable DXT/ETC is chosen per platform.
                    if (ps.format != TextureImporterFormat.Automatic)
                    { ps.format = TextureImporterFormat.Automatic; pd = true; }

                    if (pd) { imp.SetPlatformTextureSettings(ps); dirty = true; }
                }

                if (dirty) { imp.SaveAndReimport(); changed++; }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        return $"Crunch-on-overrides pass: scanned={scanned} changed={changed}.";
    }
}
