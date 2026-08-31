using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildReportDump
{
    public static string Execute()
    {
        const string src = "Library/LastBuild.buildreport";
        if (!File.Exists(src))
            return "No LastBuild.buildreport found.";

        // Copy into Assets so we can load it as an asset (Unity stores it as a native asset).
        const string tmp = "Assets/Editor/Coplay/_LastBuild.buildreport";
        File.Copy(src, tmp, true);
        AssetDatabase.ImportAsset(tmp, ImportAssetOptions.ForceSynchronousImport);

        var report = AssetDatabase.LoadAssetAtPath<BuildReport>(tmp);
        if (report == null)
        {
            AssetDatabase.DeleteAsset(tmp);
            return "Failed to load BuildReport asset.";
        }

        var sb = new StringBuilder();
        var summary = report.summary;
        sb.AppendLine($"Platform: {summary.platform}");
        sb.AppendLine($"Result: {summary.result}   BuildEndedAt: {summary.buildEndedAt}");
        sb.AppendLine($"Total build size (uncompressed output): {summary.totalSize / (1024f * 1024f):N1} MB");
        sb.AppendLine($"Total build time: {summary.totalTime}");
        sb.AppendLine();

        var packed = report.packedAssets;
        // Aggregate by category (top-level Assets/Vendor/... folder or type).
        var byCat = new System.Collections.Generic.Dictionary<string, long>();
        var byExt = new System.Collections.Generic.Dictionary<string, long>();
        var all = new System.Collections.Generic.List<(string path, long size, string type)>();

        foreach (var po in packed)
        {
            foreach (var c in po.contents)
            {
                long sz = (long)c.packedSize;
                string path = c.sourceAssetPath ?? "(built-in)";
                string cat = CategoryOf(path);
                if (!byCat.ContainsKey(cat)) byCat[cat] = 0;
                byCat[cat] += sz;

                string ext = string.IsNullOrEmpty(path) ? "(none)" : Path.GetExtension(path).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = c.type != null ? c.type.Name : "(builtin)";
                if (!byExt.ContainsKey(ext)) byExt[ext] = 0;
                byExt[ext] += sz;

                all.Add((path, sz, c.type != null ? c.type.Name : "?"));
            }
        }

        sb.AppendLine("==== PACKED SIZE BY CATEGORY (top folders) ====");
        foreach (var kv in byCat.OrderByDescending(k => k.Value))
            sb.AppendLine($"{kv.Value / (1024f * 1024f),9:N2} MB  {kv.Key}");

        sb.AppendLine();
        sb.AppendLine("==== PACKED SIZE BY EXTENSION/TYPE ====");
        foreach (var kv in byExt.OrderByDescending(k => k.Value).Take(25))
            sb.AppendLine($"{kv.Value / (1024f * 1024f),9:N2} MB  {kv.Key}");

        sb.AppendLine();
        sb.AppendLine("==== TOP 60 LARGEST PACKED ASSETS ====");
        foreach (var a in all.OrderByDescending(x => x.size).Take(60))
            sb.AppendLine($"{a.size / (1024f * 1024f),8:N2} MB  [{a.type}]  {a.path}");

        AssetDatabase.DeleteAsset(tmp);

        var outPath = "docs/build_report_breakdown.txt";
        Directory.CreateDirectory("docs");
        File.WriteAllText(outPath, sb.ToString());

        return sb.ToString();
    }

    static string CategoryOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(built-in / engine)";
        var parts = path.Split('/');
        if (parts.Length >= 3 && parts[0] == "Assets")
            return $"{parts[0]}/{parts[1]}/{parts[2]}";
        if (parts.Length >= 2)
            return $"{parts[0]}/{parts[1]}";
        return parts[0];
    }
}
