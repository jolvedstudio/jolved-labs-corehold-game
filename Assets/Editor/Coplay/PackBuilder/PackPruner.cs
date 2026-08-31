using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reconciles an over-full EnvPack with its ArtTarget's scale ladder
/// (EnvPack Builder repair tool).
///
/// Exists because the first field run deadlocked: the verify gate failed on a
/// 99-entry pack against a 50 cap, and nothing could fix it — the matcher only
/// ADDS, dropping picks cannot shrink what already exists, and entries had
/// accumulated from several tools over time. A gate whose failure has no
/// repair path teaches people to stop running the pipeline.
///
/// The rule: each band KEEPS its best <c>wantDistinct</c> entries — scored by
/// the SAME function the matcher scores candidates with, so what gets kept and
/// what would be picked can never disagree — and everything else in that
/// band's role goes, with a listed reason. Entries whose role has no band in
/// the target are kept and warned about, never silently destroyed: the ladder
/// not describing something is a reason to look at it, not to delete it.
///
/// DESTRUCTIVE BY DESIGN, so it is never run implicitly: the pipeline's verify
/// gate points here but does not call it, the menu path confirms with a
/// summary first, and the whole operation is one Undo step.
/// </summary>
public static class PackPruner
{
    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/Prune Pack To Bands…", false, 75)]
    public static void PruneMenu()
    {
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[PackPruner] Select an ArtTarget asset first.");
            return;
        }

        string plan = Prune(target, apply: false, out int keep, out int remove);
        if (remove == 0)
        {
            Debug.Log(plan);
            return;
        }
        if (!EditorUtility.DisplayDialog("Prune Pack To Bands",
                $"Keep {keep} entr(ies), REMOVE {remove} — reconciling the pack with " +
                $"'{target.name}'.\n\nEvery removal is listed in the console report. " +
                "One Undo step reverses it.\n\nProceed?", "Prune", "Cancel"))
        {
            Debug.Log(plan + "\n[PackPruner] Cancelled — nothing changed.");
            return;
        }
        Debug.Log(Prune(target, apply: true, out _, out _));
    }

    /// <summary>Compute (and optionally apply) the prune. Headless-callable;
    /// <paramref name="apply"/> false is the dry plan the dialog shows.</summary>
    public static string Prune(ArtTarget target, bool apply, out int keepCount, out int removeCount)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== PRUNE PACK TO BANDS — {target.name} ({(apply ? "APPLY" : "dry plan")}) ===");
        keepCount = 0;
        removeCount = 0;

        EnvPack pack = PackMatcher.FindPack(target.themeName);
        if (pack == null)
            return log.AppendLine($"  FAIL: no EnvPack with themeName '{target.themeName}'.").ToString();
        var entries = pack.entries != null
            ? pack.entries.Where(e => e.prefab != null).ToList()
            : new List<EnvPack.Entry>();
        var bands = target.bands ?? System.Array.Empty<ArtTarget.Band>();

        var keep = new List<EnvPack.Entry>();
        var removed = new List<(EnvPack.Entry e, string why)>();
        var claimed = new HashSet<int>();   // indices into `entries`

        foreach (ArtTarget.Band band in bands)
        {
            // Same window the matcher counts "existing" with — the two answers
            // must agree or the pipeline argues with the pruner forever.
            var members = new List<(int idx, float score)>();
            for (int i = 0; i < entries.Count; i++)
            {
                EnvPack.Entry e = entries[i];
                if (claimed.Contains(i) || e.role != band.role)
                    continue;
                bool inWindow = e.height >= band.minHeight * 0.5f &&
                                e.height <= band.maxHeight * 2f;
                if (!inWindow)
                    continue;
                claimed.Add(i);
                members.Add((i, PackMatcher.Score(EntryAsRec(e), band, target,
                                                  new List<PackMatcher.Pick>())));
            }

            var ordered = members
                .OrderByDescending(m => m.score)
                .ThenBy(m => entries[m.idx].prefab.name, System.StringComparer.Ordinal)
                .ToList();
            foreach (var (idx, score) in ordered.Take(band.wantDistinct))
                keep.Add(entries[idx]);
            foreach (var (idx, score) in ordered.Skip(band.wantDistinct))
                removed.Add((entries[idx],
                    $"{band.name} keeps its best {band.wantDistinct}; scored {score:0.00}"));
            log.AppendLine($"  {band.name,-8} {ordered.Count} in window → keep " +
                           $"{Mathf.Min(band.wantDistinct, ordered.Count)}, " +
                           $"remove {Mathf.Max(0, ordered.Count - band.wantDistinct)}");
        }

        // Role in a band, height outside every window for that role: the old
        // wallpaper case (a 5 m "silhouette" against a 40-80 m massif band).
        for (int i = 0; i < entries.Count; i++)
        {
            if (claimed.Contains(i))
                continue;
            EnvPack.Entry e = entries[i];
            bool roleHasBand = bands.Any(b => b.role == e.role);
            if (roleHasBand)
                removed.Add((e, "outside its band's height window " +
                                $"(role {e.role}, h {e.height:0.0} m)"));
            else
            {
                keep.Add(e);
                log.AppendLine($"  (kept) {e.prefab.name} — role {e.role} has no band in the " +
                               "target; the ladder not describing it is a reason to look, not delete");
            }
        }

        // Global cap, should the ladder itself exceed it.
        if (keep.Count > target.maxEntries)
        {
            log.AppendLine($"  ladder total {keep.Count} exceeds cap {target.maxEntries} — " +
                           "trimming lowest-band overflow (check the target's wantDistinct sum).");
            keep = keep.Take(target.maxEntries).ToList();
        }

        keepCount = keep.Count;
        removeCount = removed.Count + Mathf.Max(0, entries.Count - keep.Count - removed.Count);

        foreach (var (e, why) in removed)
            log.AppendLine($"    − {e.prefab.name,-36} {why}");
        log.AppendLine($"  result: {keepCount} kept, {removed.Count} removed " +
                       $"(pack had {entries.Count}).");

        if (apply)
        {
            Undo.RecordObject(pack, "Prune Pack To Bands");
            pack.entries = keep.ToArray();
            EditorUtility.SetDirty(pack);
            AssetDatabase.SaveAssets();
            log.AppendLine($"  WROTE {AssetDatabase.GetAssetPath(pack)} — one Undo step reverses this. " +
                           "Re-run the pipeline: the matcher now tops bands up from the index again.");
        }
        else
        {
            log.AppendLine("  Dry plan — nothing changed.");
        }
        return log.ToString();
    }

    /// <summary>An existing entry, shaped like an index record so the matcher's
    /// scorer can rank it. Color is unknown (no preview was rendered for it) —
    /// the scorer treats that as neutral, not as bad.</summary>
    private static PrefabIndexer.Rec EntryAsRec(EnvPack.Entry e)
    {
        return new PrefabIndexer.Rec
        {
            path = AssetDatabase.GetAssetPath(e.prefab),
            guid = "",
            sourcePack = "(pack entry)",
            height = e.height,
            radius = e.footprintRadius,
            aspect = e.height / Mathf.Max(0.05f, 2f * e.footprintRadius),
            colorValid = false,
        };
    }
}
