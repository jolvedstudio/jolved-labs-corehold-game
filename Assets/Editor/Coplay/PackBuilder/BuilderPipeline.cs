using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The EnvPack Builder as a SEQUENTIAL, GATED pipeline — same shape as the
/// Level Generator: numbered stages, each returning OK or FAIL with a reason,
/// a failure halting everything after it, and one report telling the whole
/// story. The numbered menu items remain for stepwise use; this is the
/// one-click "build me the pack and show me" path.
///
/// Gate philosophy, matching the generator's: a gate protects an INVARIANT
/// (no pack to write into, an index with nothing in it, entries whose metadata
/// would make the clearance tests silently pass everything). A SHORTFALL is
/// not an invariant — a massif band at 4/12 while assets are still being
/// bought must not brick the tool, so shortfalls and vendor-localization
/// debts are reported loudly and the pipeline continues. Refuse honestly,
/// but only over things that are actually wrong.
/// </summary>
public static class BuilderPipeline
{
    private class Ctx
    {
        public ArtTarget target;
        public EnvPack pack;
        public List<PrefabIndexer.Rec> index;
        public PackMatcher.Result match;
        public readonly StringBuilder detail = new StringBuilder();
    }

    private struct Stage
    {
        public string title;
        public System.Func<Ctx, string> run;   // null = OK; otherwise the failure reason
    }

    private static readonly Stage[] Stages =
    {
        new Stage { title = "Validate target", run = StValidate },
        new Stage { title = "Scan prefab index", run = StScan },
        new Stage { title = "Match against bands", run = StMatch },
        new Stage { title = "Write env pack", run = StWrite },
        new Stage { title = "Verify pack", run = StVerify },
        new Stage { title = "Stage lookdev", run = StLookdev },
    };

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/Run Full Pipeline (gated)", false, 69)]
    public static void RunMenu()
    {
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[BuilderPipeline] Select an ArtTarget asset first " +
                           "(step 1, Import Reading, or Create → COREHOLD → Art Target).");
            return;
        }
        string report = Run(target, out bool ok);
        if (ok) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run(ArtTarget target, out bool ok)
    {
        var ctx = new Ctx { target = target };
        var summary = new StringBuilder();
        summary.AppendLine($"=== ENVPACK BUILDER PIPELINE — {target.name} ===");
        ok = true;

        for (int i = 0; i < Stages.Length; i++)
        {
            string fail = null;
            try
            {
                fail = Stages[i].run(ctx);
            }
            catch (System.Exception e)
            {
                fail = $"threw {e.GetType().Name}: {e.Message}";
            }

            if (fail == null)
            {
                summary.AppendLine($"  [{i + 1}/{Stages.Length}] {Stages[i].title} — OK");
            }
            else
            {
                summary.AppendLine($"  [{i + 1}/{Stages.Length}] {Stages[i].title} — FAIL: {fail}");
                for (int j = i + 1; j < Stages.Length; j++)
                    summary.AppendLine($"  [{j + 1}/{Stages.Length}] {Stages[j].title} — not reached");
                ok = false;
                break;
            }
        }

        summary.AppendLine(ok
            ? "  PIPELINE PASSED — review the lookdev sheet, then generate real levels " +
              "(Generator window) and judge them with the ContactSheet game view."
            : "  PIPELINE HALTED — nothing after the failed gate ran.");
        summary.AppendLine();
        summary.Append(ctx.detail);
        return summary.ToString();
    }

    // ----------------------------------------------------------------- gates

    private static string StValidate(Ctx ctx)
    {
        ArtTarget t = ctx.target;
        if (string.IsNullOrEmpty(t.themeName))
            return "themeName is empty — it names the EnvPack this target builds.";
        if (t.bands == null || t.bands.Length == 0)
            return "no bands — the scale ladder is the whole point.";
        foreach (ArtTarget.Band b in t.bands)
        {
            if (b.role == EnvPack.PropRole.Unassigned)
                return $"band '{b.name}' has role Unassigned.";
            if (b.maxHeight <= b.minHeight)
                return $"band '{b.name}' height window is empty ({b.minHeight}–{b.maxHeight} m).";
        }
        ctx.pack = PackMatcher.FindPack(t.themeName);
        if (ctx.pack == null)
            return $"no EnvPack has themeName '{t.themeName}' — create the pack first.";

        ctx.detail.AppendLine($"[validate] pack {AssetDatabase.GetAssetPath(ctx.pack)}, " +
                              $"{t.bands.Length} band(s), cap {t.maxEntries}, " +
                              $"weather pool {(t.weatherPool != null ? t.weatherPool.Count(w => w != null) : 0)} preset(s)");
        return null;
    }

    private static string StScan(Ctx ctx)
    {
        string report = PrefabIndexer.Scan(ctx.target.scanFolders);
        ctx.detail.AppendLine(report);
        ctx.index = PrefabIndexer.Load();
        if (ctx.index.Count == 0)
            return "the index is empty — none of the scan folders exist on this machine, " +
                   "or they hold no measurable prefabs.";
        return null;
    }

    private static string StMatch(Ctx ctx)
    {
        ctx.match = PackMatcher.Match(ctx.target, ctx.index);
        ctx.detail.AppendLine(ctx.match.report);

        // Structural gate only: nothing to write AND nothing already there
        // means the pack would come out empty — that is broken, not short.
        bool anyExisting = ctx.pack.entries != null && ctx.pack.entries.Any(e => e.prefab != null);
        if (ctx.match.picks.Count == 0 && !anyExisting)
            return "no picks and the pack is empty — the index has no candidates for any band.";
        return null;
    }

    private static string StWrite(Ctx ctx)
    {
        string report = PackWriter.Build(ctx.target);
        ctx.detail.AppendLine(report);
        if (report.Contains("ABORTED"))
            return "the writer refused (see detail).";
        return null;
    }

    private static string StVerify(Ctx ctx)
    {
        // The invariant the generator depends on: entry metadata must be
        // usable, or the clearance and occlusion tests silently pass
        // everything (EnvPack.CountInvalid's whole reason to exist).
        int invalid = ctx.pack.CountInvalid();
        if (invalid > 0)
            return $"{invalid} entr(ies) with unusable metadata (missing prefab, zero footprint/" +
                   "height, or Unassigned role) — run Measure Env Pack Metadata, then rerun.";

        int total = ctx.pack.entries?.Count(e => e.prefab != null) ?? 0;
        if (total > ctx.target.maxEntries)
            return $"{total} entries exceed the {ctx.target.maxEntries} cap — prune the pack.";

        int vendor = ctx.pack.entries != null
            ? ctx.pack.entries.Count(e => e.prefab != null &&
                PackMatcher.NeedsLocalizing(AssetDatabase.GetAssetPath(e.prefab)))
            : 0;
        ctx.detail.AppendLine($"[verify] {total} valid entr(ies), 0 invalid" +
                              (vendor > 0
                                  ? $"; ⚠ {vendor} reference git-ignored vendor folders — localize " +
                                    "under _COREHOLD before committing the pack, or it dangles elsewhere"
                                  : "; no vendor debts"));
        return null;
    }

    private static string StLookdev(Ctx ctx)
    {
        string report = LookdevStager.Stage(ctx.target);
        ctx.detail.AppendLine(report);
        if (report.Contains("Cancelled"))
            return "cancelled by user.";

        int scenes = Directory.Exists("Assets/_COREHOLD/Lookdev")
            ? Directory.GetFiles("Assets/_COREHOLD/Lookdev", $"Lookdev_{ctx.target.themeName}_s*.unity").Length
            : 0;
        if (scenes == 0)
            return "no lookdev scenes were produced.";
        ctx.detail.AppendLine($"[lookdev] {scenes} scene(s) on disk.");
        return null;
    }
}
