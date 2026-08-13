using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Group the scene's root objects into readable containers.
/// Run: Tools → COREHOLD → Scene Setup → Organize Hierarchy.
///
/// The container table itself lives in <see cref="SceneContainers"/> — ONE
/// authority that this organiser and the level generator both read (R26), so
/// the hand-built scene and generated scenes cannot drift apart on where an
/// object belongs. This file is the human-facing wrapper: it sweeps, promotes
/// known strays, reports duplicates, and prints what it did.
///
/// Two things make this safe rather than a reshuffle:
///
///   • Objects move via <c>SetParent(..., worldPositionStays: true)</c>, so
///     nothing moves in world space, and containers are pinned to the origin
///     (see SceneContainers for why CameraShake makes that load-bearing).
///   • Every editor tool addresses objects through <see cref="SceneLookup"/>,
///     which resolves a path's first segment by name at any depth.
///
/// It is idempotent, it never deletes anything, and anything it does not
/// recognise is LEFT AT THE ROOT and reported rather than swept into a bucket.
/// </summary>
public static class OrganizeHierarchy
{
    /// <summary>What a pass did — the generator's verify stage reads this (R26).</summary>
    public struct Report
    {
        /// <summary>Objects reparented this pass. The generator requires 0 on its verify pass.</summary>
        public int moved;
        /// <summary>Roots the table does not recognise — left in place.</summary>
        public List<string> unclaimed;
        /// <summary>Human-readable transcript.</summary>
        public string log;
    }

    [MenuItem("Tools/COREHOLD/Scene Setup/Organize Hierarchy", false, 40)]
    public static void OrganizeMenu() => Organize();

    /// <summary>Run a full grouping pass and return what happened.</summary>
    public static Report Organize()
    {
        Scene scene = SceneManager.GetActiveScene();
        var log = new StringBuilder();
        log.AppendLine($"=== Organize hierarchy — scene '{scene.name}' ===");

        int rootsBefore = scene.GetRootGameObjects().Length;

        var unclaimed = new List<string>();
        int moved = SceneContainers.AdoptAll(unclaimed);
        moved += PromoteStrays(log);

        foreach (var (containerName, _) in SceneContainers.Groups)
        {
            var container = SceneLookup.Find(containerName);
            int n = container != null ? container.transform.childCount : 0;
            log.AppendLine($"  {containerName,-12} {n} object(s)");
        }
        log.AppendLine($"Roots {rootsBefore} → {SceneContainers.Groups.Length + unclaimed.Count} " +
                       $"({moved} object(s) grouped, nothing deleted, world positions preserved)");

        if (unclaimed.Count > 0)
        {
            log.AppendLine();
            log.AppendLine("Left at the root (unrecognised — add them to SceneContainers.Groups " +
                           "rather than letting them drift):");
            foreach (string name in unclaimed)
                log.AppendLine($"  • {name}");
        }

        ReportDuplicates(scene, log);

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log(log.ToString());

        return new Report { moved = moved, unclaimed = unclaimed, log = log.ToString() };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Rescue components that ended up nested somewhere accidental. The
    /// OverlayManager sits under a RangeRing in the shipped scene (BuildRealUI
    /// emits it there), which is incidental — it is a director and belongs with
    /// the others. Returns how many objects it moved.
    /// </summary>
    private static int PromoteStrays(StringBuilder log)
    {
        var overlay = Object.FindFirstObjectByType<Corehold.UI.OverlayManager>();
        if (overlay == null)
            return 0;

        Transform directors = SceneContainers.Ensure("_Directors");
        if (overlay.transform.parent == directors)
            return 0;

        string was = overlay.transform.parent != null ? overlay.transform.parent.name : "<root>";
        Undo.SetTransformParent(overlay.transform, directors, "Organize Hierarchy");
        overlay.transform.SetParent(directors, true);
        log.AppendLine($"  [moved] OverlayManager was parented under '{was}' — promoted to _Directors");
        return 1;
    }

    /// <summary>
    /// Report duplicate names without touching them. Five RangeRings look like
    /// leftovers from repeated Build Real UI runs, but serialized references may
    /// point at specific instances, so deleting them is a human's call.
    /// </summary>
    private static void ReportDuplicates(Scene scene, StringBuilder log)
    {
        var seen = new Dictionary<string, int>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.transform)
            {
                seen.TryGetValue(child.name, out int c);
                seen[child.name] = c + 1;
            }
        }

        var dupes = new List<string>();
        foreach (var kv in seen)
            if (kv.Value > 1)
                dupes.Add($"{kv.Key} ×{kv.Value}");

        if (dupes.Count == 0)
            return;

        log.AppendLine();
        log.AppendLine("Duplicate names (NOT touched — serialized references may point at " +
                       "specific instances, so pruning is your call):");
        foreach (string d in dupes)
            log.AppendLine($"  • {d}");
    }
}
