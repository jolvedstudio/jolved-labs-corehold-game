using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEngine;

/// <summary>
/// Scores the measured inventory against an <see cref="ArtTarget"/>'s scale
/// bands and picks entries per band (EnvPack Builder L3, the pure half).
///
/// PURE ON PURPOSE: inputs in, picks and a report out, no asset writes — those
/// live in <see cref="PackWriter"/>. A matcher that also wrote assets could
/// never be dry-run, and the dry run (menu step 3) is how a human approves the
/// picks before anything changes on disk.
///
/// The score is mostly what you'd expect — height fit, color fit against the
/// band's tint, proportion fit, a name-token bonus. The part that is NOT
/// obvious is the DUPLICATE PENALTY: each candidate is penalised for
/// resembling entries already picked in its band (same source pack, similar
/// color, similar proportion). Without it, fifty entries can reproduce the
/// exact disease this tool exists to cure — the current pack's horizon is two
/// silhouette prefabs repeated sixteen times, and a scorer without a diversity
/// term would happily pick fifty near-clones of whichever mesh scores best.
///
/// The GAP report is half the product: a band that cannot be filled from the
/// project IS the shopping list, generated instead of guessed.
/// </summary>
public static class PackMatcher
{
    public class Pick
    {
        public PrefabIndexer.Rec rec;
        public ArtTarget.Band band;
        public float score;
        public bool needsLocalizing;
    }

    public class Result
    {
        public List<Pick> picks = new List<Pick>();
        public string report;
        public EnvPack pack;   // the pack the target resolves to (null = not found)
    }

    /// <summary>Git-ignored roots (from .gitignore): a pick under one of these
    /// dangles on every other machine until localized under _COREHOLD.</summary>
    private static readonly string[] IgnoredRoots =
    {
        "Assets/Vendor/", "Assets/Yoge/", "Assets/Layer Lab/",
        "Assets/Eric VFX Studio/", "Assets/Free Slash VFX/",
    };

    /// <summary>Whether an asset path lives in a git-ignored vendor root and
    /// therefore needs localizing before the pack referencing it is committed.
    /// Public because the pipeline's verify gate audits the WRITTEN pack too.</summary>
    public static bool NeedsLocalizing(string path)
        => !string.IsNullOrEmpty(path) &&
           IgnoredRoots.Any(r => path.StartsWith(r, System.StringComparison.Ordinal));

    [UnityEditor.MenuItem("Tools/COREHOLD/Level/Env Pack Builder/3. Match && Report (dry run)", false, 72)]
    public static void MatchMenu()
    {
        var target = UnityEditor.Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[PackMatcher] Select an ArtTarget asset first (step 1 creates one).");
            return;
        }
        Debug.Log(Match(target, PrefabIndexer.Load()).report);
    }

    public static Result Match(ArtTarget target, List<PrefabIndexer.Rec> index)
    {
        var res = new Result();
        var log = new StringBuilder();
        log.AppendLine($"=== ENVPACK MATCH — {target.name} → theme '{target.themeName}' ===");

        res.pack = FindPack(target.themeName);
        if (res.pack == null)
            log.AppendLine($"  ERROR no EnvPack with themeName '{target.themeName}' — " +
                           "picks are computed but step 4 will refuse to write.");
        if (index.Count == 0)
            log.AppendLine("  WARNING the prefab index is empty — run step 2 (Scan) first.");

        // Existing entries count toward each band's want: the tool tops packs
        // up, it does not double them.
        var existing = res.pack != null && res.pack.entries != null
            ? res.pack.entries.Where(e => e.prefab != null).ToList()
            : new List<EnvPack.Entry>();
        var existingPaths = new HashSet<string>(
            existing.Select(e => UnityEditor.AssetDatabase.GetAssetPath(e.prefab)));

        int totalNew = 0;
        foreach (ArtTarget.Band band in target.bands ?? System.Array.Empty<ArtTarget.Band>())
        {
            int already = existing.Count(e => e.role == band.role &&
                                              e.height >= band.minHeight * 0.5f &&
                                              e.height <= band.maxHeight * 2f);
            int want = Mathf.Max(0, band.wantDistinct - already);

            var candidates = index.Where(r => !existingPaths.Contains(r.path) &&
                                              r.height >= band.minHeight * 0.5f &&
                                              r.height <= band.maxHeight * 2f)
                                  .ToList();

            var bandPicks = new List<Pick>();
            for (int slot = 0; slot < want; slot++)
            {
                Pick best = null;
                foreach (PrefabIndexer.Rec rec in candidates)
                {
                    if (bandPicks.Any(p => p.rec.guid == rec.guid) ||
                        res.picks.Any(p => p.rec.guid == rec.guid))
                        continue;
                    float s = Score(rec, band, target, bandPicks);
                    if (best == null || s > best.score ||
                        (Mathf.Approximately(s, best.score) &&
                         string.CompareOrdinal(rec.path, best.rec.path) < 0))
                        best = new Pick { rec = rec, band = band, score = s };
                }
                if (best == null || best.score < 0.75f)
                    break;   // nothing acceptable left — the rest is the gap
                best.needsLocalizing = NeedsLocalizing(best.rec.path);
                bandPicks.Add(best);
            }
            res.picks.AddRange(bandPicks);
            totalNew += bandPicks.Count;

            string verdict = already + bandPicks.Count >= band.wantDistinct
                ? "filled"
                : $"UNFILLED — wants {band.wantDistinct - already - bandPicks.Count} more " +
                  $"{band.minHeight:0}–{band.maxHeight:0} m (shopping list)";
            log.AppendLine($"  {band.name,-8} {already} existing + {bandPicks.Count} picked " +
                           $"of {band.wantDistinct} wanted — {verdict}");
            foreach (Pick p in bandPicks)
                log.AppendLine($"     + {System.IO.Path.GetFileNameWithoutExtension(p.rec.path),-34}" +
                               $" h {p.rec.height,5:0.0} m  aspect {p.rec.aspect,4:0.0}  " +
                               $"score {p.score:0.00}  ×[{p.band.scaleRange.x:0.0},{p.band.scaleRange.y:0.0}]" +
                               (p.needsLocalizing ? "  ⚠ NEEDS LOCALIZING (vendor)" : ""));
        }

        // ---- the entry cap ---------------------------------------------------
        int projected = existing.Count + totalNew;
        if (projected > target.maxEntries)
        {
            int drop = projected - target.maxEntries;
            foreach (Pick p in res.picks.OrderBy(p => p.score).Take(drop).ToList())
                res.picks.Remove(p);
            log.AppendLine($"  cap: {projected} entries would exceed {target.maxEntries} — " +
                           $"dropped the {drop} lowest-scoring pick(s).");
        }

        int toLocalize = res.picks.Count(p => p.needsLocalizing);
        if (toLocalize > 0)
            log.AppendLine($"  ⚠ {toLocalize} pick(s) live in git-ignored vendor folders. They work " +
                           "on THIS machine; before committing the pack, localize them under " +
                           "_COREHOLD (prefab variants, as RockyDesert did) or the pack dangles elsewhere.");

        log.AppendLine("  Dry run — nothing written. Step 4 applies these picks to the EnvPack.");
        res.report = log.ToString();
        return res;
    }

    // ------------------------------------------------------------------ score

    private static float Score(PrefabIndexer.Rec rec, ArtTarget.Band band,
                               ArtTarget target, List<Pick> picked)
    {
        // Height: 1 inside the window, linear falloff to 0 at half/double.
        float h = rec.height;
        float heightFit;
        if (h < band.minHeight)
            heightFit = Mathf.InverseLerp(band.minHeight * 0.5f, band.minHeight, h);
        else if (h > band.maxHeight)
            heightFit = Mathf.InverseLerp(band.maxHeight * 2f, band.maxHeight, h);
        else
            heightFit = 1f;

        // Color: distance to the band's tint. Rock-ish bands measure against
        // rockTint, scatter against groundTint (scrub lives on the ground).
        // A prefab whose preview failed scores neutral — unknown, not bad.
        float colorFit = 0.5f;
        if (rec.colorValid)
        {
            Color tint = band.role == EnvPack.PropRole.Clutter ? target.groundTint : target.rockTint;
            float dr = rec.r - tint.r, dg = rec.g - tint.g, db = rec.b - tint.b;
            colorFit = 1f - Mathf.Clamp01(Mathf.Sqrt(dr * dr + dg * dg + db * db) / 1.2f);
        }

        float aspectFit = rec.aspect >= band.aspectMin && rec.aspect <= band.aspectMax ? 1f
            : rec.aspect < band.aspectMin
                ? Mathf.InverseLerp(band.aspectMin * 0.5f, band.aspectMin, rec.aspect)
                : Mathf.InverseLerp(band.aspectMax * 2f, band.aspectMax, rec.aspect);

        string lower = System.IO.Path.GetFileNameWithoutExtension(rec.path).ToLowerInvariant();
        float tokenBonus = band.nameTokens != null &&
                           band.nameTokens.Any(t => lower.Contains(t)) ? 1f : 0f;

        // Diversity: resembling what the band already took costs more than any
        // single fit term can buy back twice over.
        float dup = 0f;
        foreach (Pick p in picked)
        {
            bool samePack = p.rec.sourcePack == rec.sourcePack;
            bool sameColor = rec.colorValid && p.rec.colorValid &&
                             Mathf.Abs(p.rec.r - rec.r) + Mathf.Abs(p.rec.g - rec.g) +
                             Mathf.Abs(p.rec.b - rec.b) < 0.36f;
            float ratio = rec.aspect / Mathf.Max(0.01f, p.rec.aspect);
            bool sameShape = ratio > 0.75f && ratio < 1.33f;
            if (samePack && sameColor && sameShape)
                dup += 0.75f;
        }
        dup = Mathf.Min(dup, 1.5f);

        return 2.0f * heightFit + 1.5f * colorFit + 1.0f * aspectFit + 0.5f * tokenBonus - dup;
    }

    public static EnvPack FindPack(string themeName)
    {
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:EnvPack"))
        {
            var pack = UnityEditor.AssetDatabase.LoadAssetAtPath<EnvPack>(
                UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if (pack != null && pack.themeName == themeName)
                return pack;
        }
        return null;
    }
}
