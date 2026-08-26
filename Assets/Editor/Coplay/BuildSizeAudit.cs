using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// What is actually in the build (M-d/ship): reads Unity's own build report
/// from the LAST build — no rebuild needed — and reports where the bytes went.
///
/// Three views, because they answer different questions:
///   1. OUTPUT FILES — the .data / .wasm / .framework.js the browser
///      downloads. This is the number a player feels.
///   2. BY TYPE — textures vs audio vs meshes vs shaders. This is where a
///      budget decision gets made (an audio import setting can outweigh every
///      texture in the project).
///   3. TOP ASSETS — the individual offenders, so the fix is a specific file
///      rather than a policy.
///
/// Sizes are PACKED (post-compression-settings, pre-Brotli): they are what the
/// build actually carries, not the source file sizes on disk.
///
/// The report lives at Library/LastBuild.buildreport, which is not an asset —
/// it is copied into Assets/ for a moment so the AssetDatabase can load it as
/// a BuildReport, then deleted. Nothing is left behind.
/// </summary>
public static class BuildSizeAudit
{
    private const string ReportPath = "Library/LastBuild.buildreport";
    private const string TempAsset = "Assets/~LastBuildReport.buildreport";
    private const int TopAssets = 30;

    [MenuItem("Tools/COREHOLD/Debug/Audit Build Size (last build)", false, 61)]
    public static void Run()
    {
        if (!File.Exists(ReportPath))
        {
            Debug.LogWarning($"[Size] No build report at {ReportPath} — run a build first " +
                             "(Campaign Builder → BUILD, or File → Build). The report is written " +
                             "by Unity at the end of every build.");
            return;
        }

        BuildReport report = null;
        try
        {
            File.Copy(ReportPath, TempAsset, true);
            AssetDatabase.ImportAsset(TempAsset, ImportAssetOptions.ForceUpdate);
            report = AssetDatabase.LoadAssetAtPath<BuildReport>(TempAsset);
            if (report == null)
            {
                Debug.LogWarning("[Size] The build report could not be loaded — Unity may have " +
                                 "changed its format. Nothing was modified.");
                return;
            }

            Debug.Log(Compose(report));
        }
        finally
        {
            // Never leave a stray asset behind, even on an exception.
            AssetDatabase.DeleteAsset(TempAsset);
        }
    }

    private static string Compose(BuildReport report)
    {
        var sb = new StringBuilder();
        var summary = report.summary;

        sb.AppendLine("=== BUILD SIZE AUDIT — where the bytes went ===");
        sb.AppendLine($"Output   : {summary.outputPath}");
        sb.AppendLine($"Platform : {summary.platform},  result {summary.result},  " +
                      $"built {summary.buildEndedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Total    : {Mb(summary.totalSize)}");
        sb.AppendLine();

        // ---- 1. output files: what the browser downloads --------------------
        sb.AppendLine("--- Output files (what a player downloads) ---");
        BuildFile[] files = report.GetFiles();
        foreach (BuildFile f in files.OrderByDescending(f => f.size).Take(12))
            sb.AppendLine($"  {Mb(f.size),12}   {Path.GetFileName(f.path)}   [{f.role}]");
        sb.AppendLine();

        // ---- 2 & 3: packed assets, aggregated ------------------------------
        // One asset can be split across several PackedAssets entries, so sum by
        // source path before ranking — otherwise a big texture shows up as
        // several medium ones and hides under the fold.
        var byAsset = new Dictionary<string, ulong>();
        var byType = new Dictionary<string, ulong>();
        var countByType = new Dictionary<string, int>();
        ulong accounted = 0;

        foreach (PackedAssets packed in report.packedAssets)
        {
            foreach (PackedAssetInfo info in packed.contents)
            {
                string path = string.IsNullOrEmpty(info.sourceAssetPath)
                    ? "<generated / built-in>"
                    : info.sourceAssetPath;
                string type = info.type != null ? info.type.Name : "Unknown";

                byAsset.TryGetValue(path, out ulong a);
                byAsset[path] = a + info.packedSize;

                byType.TryGetValue(type, out ulong t);
                byType[type] = t + info.packedSize;

                countByType.TryGetValue(type, out int c);
                countByType[type] = c + 1;

                accounted += info.packedSize;
            }
        }

        sb.AppendLine($"--- By asset type ({Mb(accounted)} of packed content) ---");
        foreach (var kv in byType.OrderByDescending(kv => kv.Value).Take(15))
        {
            float pct = accounted > 0 ? kv.Value * 100f / accounted : 0f;
            sb.AppendLine($"  {Mb(kv.Value),12}  {pct,5:0.0}%  {kv.Key}  ({countByType[kv.Key]} object(s))");
        }
        sb.AppendLine();

        sb.AppendLine($"--- Top {TopAssets} assets ---");
        foreach (var kv in byAsset.OrderByDescending(kv => kv.Value).Take(TopAssets))
            sb.AppendLine($"  {Mb(kv.Value),12}  {kv.Key}");
        sb.AppendLine();

        sb.AppendLine(Advice(byType, accounted));
        return sb.ToString();
    }

    /// <summary>
    /// Turn the numbers into the next action. Deliberately specific: "textures
    /// are 60%" is an observation, "your WebGL override is pinned at today's
    /// size by the POV policy, so the win is in import format" is a decision.
    /// </summary>
    private static string Advice(Dictionary<string, ulong> byType, ulong accounted)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Reading this ---");
        sb.AppendLine("  Sizes are PACKED (after import settings, before the build's Brotli/Gzip");
        sb.AppendLine("  pass), so the download is smaller than the total above.");

        ulong Get(string k) => byType.TryGetValue(k, out ulong v) ? v : 0UL;
        float Pct(ulong v) => accounted > 0 ? v * 100f / accounted : 0f;

        ulong audio = Get("AudioClip");
        ulong tex = Get("Texture2D") + Get("Cubemap") + Get("Sprite");
        ulong mesh = Get("Mesh");
        ulong shader = Get("Shader") + Get("ComputeShader");

        if (Pct(audio) >= 20f)
            sb.AppendLine($"  • AUDIO is {Pct(audio):0}% — usually an import setting, not content: set long " +
                          "clips\n    to Streaming/Compressed In Memory with Vorbis, and mono where it fits.");
        if (Pct(tex) >= 40f)
            sb.AppendLine($"  • TEXTURES are {Pct(tex):0}% — the POV policy pins WebGL at each texture's " +
                          "current\n    size deliberately, so the win here is FORMAT (crunched/ASTC) and " +
                          "unused\n    channels, not the max-size slider.");
        if (Pct(mesh) >= 20f)
            sb.AppendLine($"  • MESHES are {Pct(mesh):0}% — check Read/Write Enabled is OFF (it doubles a " +
                          "mesh)\n    and that compression is on for props the camera never sees up close.");
        if (Pct(shader) >= 10f)
            sb.AppendLine($"  • SHADERS are {Pct(shader):0}% — usually variant explosion. Strip unused " +
                          "variants\n    in Project Settings → Graphics, and check the URP asset's feature set.");

        sb.AppendLine("  • Anything under <generated / built-in> is engine content, not yours.");
        sb.AppendLine("  • A vendor kit asset high in the list that the game never shows is the");
        sb.AppendLine("    cheapest win available — it is shipping because something references it.");
        return sb.ToString();
    }

    private static string Mb(ulong bytes)
    {
        float mb = bytes / (1024f * 1024f);
        return mb >= 1f ? $"{mb:0.00} MB" : $"{bytes / 1024f:0.0} KB";
    }
}
