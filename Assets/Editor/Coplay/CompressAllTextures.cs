using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ticket 38 — force MAXIMUM compression on every game texture so the WebGL
/// on-device GPU texture memory fits the 70 MB budget.
///
/// For each game texture importer this:
///   • Caps Max Size at 1024 (never higher; leaves smaller as-is).
///   • Sets Compression = Compressed + Crunch (high crunch), so Unity picks a
///     block-compressed GPU format automatically — DXT1 (0.5 B/px) for opaque,
///     DXT5 (1 B/px) for alpha — instead of uncompressed RGBA32 (4 B/px).
///   • Removes per-platform overrides that were pinning an UNCOMPRESSED format
///     (RGBA32 / RGB24 / ARGB32 / etc.), which is what blew the wall atlases up.
///     Overrides that already use a compressed/crunched format are left intact.
///
/// EXR lightmaps/reflection probes (HDR BC6H) and single-channel font SDF atlases
/// (Alpha8) are left alone — they are already appropriate and small.
/// </summary>
public static class CompressAllTextures
{
    private const int Cap = 1024;
    private static readonly string[] Platforms = { "Standalone", "WebGL", "Android", "iPhone" };

    private static readonly string[] ExcludePrefixes =
    {
        "Assets/Editor/",
        "Assets/Gizmos/",
        "Assets/TutorialInfo/",
    };

    // Uncompressed formats we want to eliminate via override removal.
    private static readonly HashSet<TextureImporterFormat> Uncompressed = new HashSet<TextureImporterFormat>
    {
        TextureImporterFormat.RGBA32, TextureImporterFormat.RGB24, TextureImporterFormat.ARGB32,
        TextureImporterFormat.RGBA16, TextureImporterFormat.RGB16, TextureImporterFormat.Alpha8,
        TextureImporterFormat.RGBAHalf, TextureImporterFormat.RGBAFloat, TextureImporterFormat.R8,
        TextureImporterFormat.R16, TextureImporterFormat.RG16, TextureImporterFormat.RG32,
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
                // Leave HDR EXR (lightmaps / reflection probes) as-is: BC6H already.
                if (path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                // Font SDF atlases must stay single-channel (Alpha8) — do not crunch.
                if (importer.textureType == TextureImporterType.SingleChannel)
                {
                    skipped++;
                    continue;
                }

                scanned++;
                bool dirty = false;
                var before = new StringBuilder();

                // 1) Cap default max size.
                if (importer.maxTextureSize > Cap)
                {
                    before.Append($"size {importer.maxTextureSize}->1024 ");
                    importer.maxTextureSize = Cap;
                    dirty = true;
                }

                // 2) Force compressed + crunch on the default settings.
                if (importer.textureCompression != TextureImporterCompression.Compressed)
                {
                    before.Append($"comp {importer.textureCompression}->Compressed ");
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    dirty = true;
                }
                if (!importer.crunchedCompression)
                {
                    before.Append("crunch->on ");
                    importer.crunchedCompression = true;
                    dirty = true;
                }
                if (importer.compressionQuality != 50)
                {
                    importer.compressionQuality = 50; // crunch quality; smaller download
                    dirty = true;
                }

                // 3) Fix per-platform overrides pinning an uncompressed format.
                foreach (var plat in Platforms)
                {
                    var ps = importer.GetPlatformTextureSettings(plat);
                    if (ps == null || !ps.overridden)
                        continue;

                    bool psDirty = false;
                    if (ps.maxTextureSize > Cap)
                    {
                        ps.maxTextureSize = Cap;
                        psDirty = true;
                    }
                    if (Uncompressed.Contains(ps.format))
                    {
                        before.Append($"{plat}:{ps.format}->auto ");
                        // Drop the override entirely so the compressed default applies.
                        ps.overridden = false;
                        psDirty = true;
                    }
                    else if (ps.textureCompression != TextureImporterCompression.Compressed)
                    {
                        ps.textureCompression = TextureImporterCompression.Compressed;
                        ps.crunchedCompression = true;
                        ps.compressionQuality = 50;
                        psDirty = true;
                    }

                    if (psDirty)
                    {
                        importer.SetPlatformTextureSettings(ps);
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    importer.SaveAndReimport();
                    changed.Add($"  {System.IO.Path.GetFileName(path),-45} {before}");
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
        sb.AppendLine("========== FORCE MAX COMPRESSION (DXT + crunch, cap 1024) ==========");
        sb.AppendLine($"Scanned      : {scanned}");
        sb.AppendLine($"Changed      : {changed.Count}");
        sb.AppendLine($"Already OK   : {alreadyOk}");
        sb.AppendLine($"Skipped      : {skipped}  (EXR HDR / SingleChannel SDF / non-game)");
        sb.AppendLine("====================================================================");
        var report = sb.ToString();
        Debug.Log(report + "\n" + string.Join("\n", changed.Take(60)));
        return report;
    }
}
