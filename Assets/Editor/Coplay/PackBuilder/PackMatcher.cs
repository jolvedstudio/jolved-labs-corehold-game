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

    /// <summary>A chosen SURFACE — the ground material or the skybox.</summary>
    public class SurfacePick
    {
        public PrefabIndexer.MatRec rec;
        public float score;
        public bool needsLocalizing;
        public Material Load() => UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rec.path);
    }

    public class Result
    {
        public List<Pick> picks = new List<Pick>();
        public SurfacePick ground;    // null = nothing in the index scored well enough
        public SurfacePick skybox;
        public string report;
        public EnvPack pack;   // the pack the target resolves to (null = not found)
    }

    // Name evidence for surfaces. Colour alone cannot separate "desert sand"
    // from "beige plastic crate", and a vendor pack holds hundreds of the
    // latter — so a token match is most of the score, and a candidate with
    // neither token nor a close colour is refused rather than forced.
    private static readonly string[] GroundTokens =
    {
        "sand", "ground", "terrain", "desert", "dirt", "soil", "floor",
        "gravel", "dune", "rock", "stone", "cliff", "mud", "earth",
    };

    private static readonly string[] SkyTokens =
    {
        "sky", "skybox", "day", "dusk", "dawn", "sunset", "clear", "cloud", "horizon",
    };

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
        => Match(target, index, PrefabIndexer.LoadMaterials());

    public static Result Match(ArtTarget target, List<PrefabIndexer.Rec> index,
                               List<PrefabIndexer.MatRec> materials)
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
            {
                // A pick that matched NO theme token was chosen on size and
                // shape alone — a 44 m space-station module fills a massif
                // band honestly when no mesa exists, but a human must decide
                // whether that is a look or a placeholder.
                string file = System.IO.Path.GetFileNameWithoutExtension(p.rec.path);
                string lower = file.ToLowerInvariant();
                bool tok = band.nameTokens != null && band.nameTokens.Any(t => lower.Contains(t));
                log.AppendLine($"     + {file,-34}" +
                               $" h {p.rec.height,5:0.0} m  aspect {p.rec.aspect,4:0.0}  " +
                               $"score {p.score:0.00}  ×[{p.band.scaleRange.x:0.0},{p.band.scaleRange.y:0.0}]" +
                               (p.needsLocalizing ? "  ⚠ NEEDS LOCALIZING (vendor)" : "") +
                               (tok ? "" : "  (no theme token — verify by eye)"));
            }
        }

        // ---- the entry cap ---------------------------------------------------
        // Clamped to what dropping picks can actually achieve: the first field
        // run asked this block to shed 65 entries by dropping 16 picks, which
        // silently wiped every pick and left the overflow standing anyway.
        int projected = existing.Count + totalNew;
        if (projected > target.maxEntries)
        {
            int drop = Mathf.Min(projected - target.maxEntries, res.picks.Count);
            foreach (Pick p in res.picks.OrderBy(p => p.score).Take(drop).ToList())
                res.picks.Remove(p);
            log.AppendLine($"  cap: {projected} entries would exceed {target.maxEntries} — " +
                           $"dropped the {drop} lowest-scoring pick(s) of {totalNew}.");
            if (existing.Count > target.maxEntries)
                log.AppendLine($"  ⚠ the pack's EXISTING {existing.Count} entries exceed the cap on their " +
                               "own — no amount of pick-dropping fixes that. Run Env Pack Builder → " +
                               "Prune Pack To Bands to reconcile the pack with the target's ladder.");
        }

        // ---- surfaces: ground + sky -----------------------------------------
        res.ground = PickSurface(materials, target, wantSky: false);
        res.skybox = PickSurface(materials, target, wantSky: true);
        AppendSurfaceLine(log, "ground", res.ground, res.pack != null ? res.pack.groundMaterial : null,
                          target.groundMaterial, target.overrideGroundTextures, materials.Count);
        AppendSurfaceLine(log, "skybox", res.skybox, res.pack != null ? res.pack.skyboxMaterial : null,
                          target.skyboxMaterial, target.overrideGroundTextures,
                          materials.Count(m => m.isSkybox));

        int toLocalize = res.picks.Count(p => p.needsLocalizing);
        if (toLocalize > 0)
            log.AppendLine($"  ⚠ {toLocalize} pick(s) live in git-ignored vendor folders. They work " +
                           "on THIS machine; before committing the pack, localize them under " +
                           "_COREHOLD (prefab variants, as RockyDesert did) or the pack dangles elsewhere.");

        log.AppendLine("  Dry run — nothing written. Step 4 applies these picks to the EnvPack.");
        res.report = log.ToString();
        return res;
    }

    // ---------------------------------------------------------------- surfaces

    private static SurfacePick PickSurface(List<PrefabIndexer.MatRec> materials,
                                           ArtTarget target, bool wantSky)
    {
        Color want = wantSky ? target.fogColor : target.groundTint;
        string[] tokens = wantSky ? SkyTokens : GroundTokens;

        SurfacePick best = null;
        foreach (PrefabIndexer.MatRec m in materials.OrderBy(m => m.path, System.StringComparer.Ordinal))
        {
            if (m.isSkybox != wantSky)
                continue;   // a sky is never ground, and ground is never a sky

            float colorFit = 0.5f;
            if (m.colorValid)
            {
                float dr = m.r - want.r, dg = m.g - want.g, db = m.b - want.b;
                colorFit = 1f - Mathf.Clamp01(Mathf.Sqrt(dr * dr + dg * dg + db * db) / 1.2f);
            }
            string lower = System.IO.Path.GetFileNameWithoutExtension(m.path).ToLowerInvariant();
            float token = tokens.Any(t => lower.Contains(t)) ? 1f : 0f;
            // Ground wants a real texture; a flat colour reads as a tabletop.
            float textured = wantSky || m.hasTexture ? 1f : 0f;

            float score = 1.5f * token + 1.2f * colorFit + 0.8f * textured;
            if (best == null || score > best.score)
                best = new SurfacePick { rec = m, score = score, needsLocalizing = NeedsLocalizing(m.path) };
        }

        // Refuse rather than force: without a name token AND without a close
        // colour, "best" is just the least bad of hundreds of vendor materials.
        return best != null && best.score >= 2.2f ? best : null;
    }

    private static void AppendSurfaceLine(StringBuilder log, string what, SurfacePick pick,
                                          Material packHas, Material targetHas,
                                          bool overrideOn, int candidates)
    {
        string chosen = targetHas != null
            ? $"target's own '{targetHas.name}'"
            : pick != null
                ? $"'{System.IO.Path.GetFileNameWithoutExtension(pick.rec.path)}' (score {pick.score:0.00})" +
                  (pick.needsLocalizing ? " ⚠ NEEDS LOCALIZING" : "")
                : $"nothing scored high enough among {candidates} candidate(s)";

        // The "why is nothing happening" line: an empty slot always gets
        // filled, a filled one is only replaced on request.
        string fate = packHas == null
            ? (targetHas != null || pick != null ? "→ WILL FILL (pack slot is empty)" : "→ pack slot stays empty")
            : overrideOn
                ? $"→ WILL REPLACE the pack's '{packHas.name}' (override ticked)"
                : $"→ KEPT: the pack already has '{packHas.name}'. Tick overrideGroundTextures on the " +
                  "ArtTarget to replace it";

        log.AppendLine($"  {what,-8} {chosen}  {fate}");
    }

    // ------------------------------------------------------------------ score

    /// <summary>Internal so the pruner can score EXISTING entries with the very
    /// same function the matcher scores candidates with — two scorers would
    /// disagree, and the disagreement would decide what gets deleted.</summary>
    internal static float Score(PrefabIndexer.Rec rec, ArtTarget.Band band,
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
