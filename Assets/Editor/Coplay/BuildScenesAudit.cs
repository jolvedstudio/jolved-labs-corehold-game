using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// What is actually in the build, and how much of it nobody meant to ship.
///
/// This exists because of a 40-minute Brotli pass on what everyone believed
/// was a two-scene game. It was forty scenes. The generator appends every
/// level it emits to Build Settings — correct on its own, since Retry has to
/// be able to reload the scene being played — and the Campaign Builder appends
/// a Welcome and a Closing per campaign. Nothing ever removes a scene that
/// still EXISTS but is no longer wanted, so the list only grows, and a WebGL
/// build quietly carries every level ever generated plus every campaign ever
/// tried.
///
/// The cost lands somewhere nobody looks: scenes drag in every asset they
/// reference, the data file grows, and Unity's Brotli pass — single-threaded
/// at quality 11 — turns that growth into wall-clock minutes.
///
/// Report-only by default. The prune is a separate, confirmed action, because
/// Build Settings is the one list where a wrong automatic edit means a build
/// that cannot load the scene it starts in.
/// </summary>
public static class BuildScenesAudit
{
    private const string GeneratedPrefix = "Assets/_COREHOLD/Scenes/Generated/";
    private const string CampaignPrefix = "Assets/_COREHOLD/Scenes/Campaign/";

    [MenuItem("Tools/COREHOLD/Validate/Build Scenes Audit", false, 27)]
    public static void Run()
    {
        var sb = new StringBuilder();
        EditorBuildSettingsScene[] all = EditorBuildSettings.scenes;
        var enabled = all.Where(s => s.enabled).ToList();

        long bytes = 0;
        int missing = 0;
        foreach (EditorBuildSettingsScene s in enabled)
        {
            string p = s.path;
            if (File.Exists(p)) bytes += new FileInfo(p).Length;
            else missing++;
        }

        sb.AppendLine($"=== BUILD SCENES AUDIT — {enabled.Count} enabled of {all.Length} listed ===");
        sb.AppendLine($"  scene files on disk: {bytes / 1048576f:0.0} MB of YAML");
        sb.AppendLine("  (scene SIZE is not build size — each scene also drags in every asset it " +
                      "references — but it is the cheapest proxy, and it is what grew.)");
        sb.AppendLine();

        // The scene that BOOTS is the one that must never be pruned by accident.
        if (enabled.Count > 0)
            sb.AppendLine($"  boots into: {enabled[0].path}");
        if (missing > 0)
            sb.AppendLine($"  [note] {missing} enabled scene(s) are not on disk. On a machine that " +
                          "generated them this is normal — Scenes/Generated is git-ignored — but on a " +
                          "FRESH CLONE those entries are dead weight and Unity will warn at build.");

        var generated = enabled.Where(s => s.path.StartsWith(GeneratedPrefix)).ToList();
        var campaign = enabled.Where(s => s.path.StartsWith(CampaignPrefix)).ToList();
        var other = enabled.Where(s => !s.path.StartsWith(GeneratedPrefix) &&
                                       !s.path.StartsWith(CampaignPrefix)).ToList();

        sb.AppendLine();
        sb.AppendLine($"  generated levels : {generated.Count}");
        sb.AppendLine($"  campaign scenes  : {campaign.Count}  " +
                      $"(in {CampaignFolders(campaign).Count} campaign folder(s))");
        sb.AppendLine($"  everything else  : {other.Count}");
        foreach (EditorBuildSettingsScene s in other)
            sb.AppendLine($"      {s.path}");

        // A shipping build wants ONE campaign. Anything past that is history.
        var folders = CampaignFolders(campaign);
        if (folders.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"  WARN {folders.Count} campaign folders are in the build. A build ships " +
                          "ONE campaign; the rest are earlier attempts still riding along:");
            foreach (string f in folders.OrderBy(f => f, System.StringComparer.Ordinal))
                sb.AppendLine($"      {f}");
        }

        if (generated.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  WARN {generated.Count} raw generated level(s) are in the build. The " +
                          "generator registers each one so Retry can reload it while you are working " +
                          "on it — that is not the same as shipping it. A campaign loads its levels " +
                          "from its OWN folder, so these are usually pure payload.");
        }

        sb.AppendLine();
        sb.AppendLine("  Prune with: Tools → COREHOLD → Validate → Prune Build Scenes to One Campaign");
        sb.AppendLine("  Iterating? Player Settings → WebGL → Publishing Settings → Compression Format:");
        sb.AppendLine("  Gzip builds in a fraction of Brotli's time. Switch back to Brotli to ship.");

        bool loud = folders.Count > 1 || generated.Count > 0 || missing > 0;
        if (loud) Debug.LogWarning(sb.ToString());
        else Debug.Log(sb.ToString());
    }

    private static List<string> CampaignFolders(List<EditorBuildSettingsScene> campaign)
    {
        var set = new HashSet<string>();
        foreach (EditorBuildSettingsScene s in campaign)
        {
            string dir = Path.GetDirectoryName(s.path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
                set.Add(dir);
        }
        return set.ToList();
    }

    /// <summary>
    /// Keep the boot scene and ONE campaign folder; disable the rest.
    ///
    /// DISABLES rather than deletes the entries, so the list is a record of
    /// what was tried and one tick puts any of it back. The boot scene is kept
    /// whatever folder it is in — index 0 is what the player loads into, and a
    /// build that prunes its own entry point is a build that starts on a black
    /// screen.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Validate/Prune Build Scenes to One Campaign", false, 28)]
    public static void Prune()
    {
        EditorBuildSettingsScene[] all = EditorBuildSettings.scenes;
        var enabled = all.Where(s => s.enabled).ToList();
        if (enabled.Count == 0)
        {
            Debug.LogWarning("[BuildScenes] Nothing enabled to prune.");
            return;
        }

        string bootPath = enabled[0].path;
        string keepFolder = Path.GetDirectoryName(bootPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(keepFolder))
        {
            Debug.LogWarning("[BuildScenes] Could not read the boot scene's folder — nothing changed.");
            return;
        }

        var keep = new List<string>();
        var drop = new List<string>();
        foreach (EditorBuildSettingsScene s in all)
        {
            if (!s.enabled)
                continue;
            string dir = Path.GetDirectoryName(s.path)?.Replace('\\', '/');
            if (s.path == bootPath || dir == keepFolder) keep.Add(s.path);
            else drop.Add(s.path);
        }

        if (drop.Count == 0)
        {
            Debug.Log($"[BuildScenes] Already minimal — {keep.Count} scene(s), all in {keepFolder}.");
            return;
        }

        string preview = string.Join("\n", drop.Take(12));
        if (drop.Count > 12)
            preview += $"\n… and {drop.Count - 12} more";

        if (!EditorUtility.DisplayDialog(
                "Prune Build Scenes",
                $"KEEP {keep.Count} scene(s) in:\n{keepFolder}\n\n" +
                $"DISABLE {drop.Count} scene(s):\n{preview}\n\n" +
                "They stay in the list and can be re-ticked at any time.",
                $"Disable {drop.Count}", "Cancel"))
            return;

        var updated = all.Select(s =>
            new EditorBuildSettingsScene(s.path, s.enabled && !drop.Contains(s.path))).ToArray();
        EditorBuildSettings.scenes = updated;

        Debug.Log($"[BuildScenes] Kept {keep.Count} scene(s) in {keepFolder}; disabled {drop.Count}. " +
                  "Re-run the audit to see the new payload.");
    }
}
