using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Applies a match result to the theme's EnvPack (EnvPack Builder L3, the
/// write half). Upserts entries — existing hand-authored entries are preserved
/// untouched, picked entries are appended with the band's scaleRange (which is
/// the fix for a pack authored entirely at (1,1)) — then writes the
/// ArtTarget's densities, look values and weather pool.
///
/// Deliberately NOT written: groundMaterial, tiling, detail maps, skybox,
/// postProfile. Those were hand-tuned on the pack and the target has no
/// opinion about them; a builder that overwrote tuning it does not understand
/// would teach people not to run it.
/// </summary>
public static class PackWriter
{
    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/4. Build Env Pack (write)", false, 73)]
    public static void BuildMenu()
    {
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[PackWriter] Select an ArtTarget asset first.");
            return;
        }
        Debug.Log(Build(target));
    }

    public static string Build(ArtTarget target)
    {
        PackMatcher.Result match = PackMatcher.Match(target, PrefabIndexer.Load());
        return match.report + Build(target, match);
    }

    /// <summary>Apply an already-computed match — the pipeline path, which has
    /// printed the match report once already and must not print it twice.</summary>
    public static string Build(ArtTarget target, PackMatcher.Result match)
    {
        var log = new StringBuilder();

        EnvPack pack = match.pack;
        if (pack == null)
            return log.AppendLine("[PackWriter] ABORTED — no pack to write.").ToString();

        Undo.RecordObject(pack, "Build Env Pack");

        var entries = pack.entries != null ? pack.entries.ToList() : new List<EnvPack.Entry>();
        int added = 0;
        foreach (PackMatcher.Pick pick in match.picks)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(pick.rec.path);
            if (prefab == null || !EnvPackTools.TryMeasure(prefab, out EnvPackTools.Measurement m))
                continue;   // index can be stale; measure again at write time

            entries.Add(new EnvPack.Entry
            {
                prefab = prefab,
                role = pick.band.role,
                footprintRadius = m.radius,
                height = m.height,
                scaleRange = pick.band.scaleRange,
                allowInFold = false,                       // pockets are where pads live
                affinity = EnvPack.SubstrateAffinity.Auto, // name inference already works
            });
            added++;
        }
        pack.entries = entries.ToArray();

        // ---- look + composition from the target -----------------------------
        pack.landmarkDensity = target.landmarkDensity;
        pack.midFieldDensity = target.midFieldDensity;
        pack.clutterDensity = target.clutterDensity;
        pack.silhouetteDensity = target.silhouetteDensity;
        pack.outfieldDensity = target.outfieldDensity;
        pack.clusterChance = target.clusterChance;
        pack.scaleJitter = target.scaleJitter;
        pack.toneVariation = target.toneVariation;
        pack.slopeTiltMaxDegrees = target.slopeTiltMaxDegrees;
        pack.groundZoneStrength = target.groundZoneStrength;
        pack.sunIntensity = target.sunIntensity;
        pack.sunColor = target.sunColor;
        pack.sunAngles = target.sunAngles;
        pack.fogColor = target.fogColor;
        pack.fogDensity = target.fogDensity;
        if (target.weatherPool != null && target.weatherPool.Length > 0)
            pack.weatherPool = target.weatherPool.Where(w => w != null).ToArray();

        // Surfaces: FILL AN EMPTY SLOT ALWAYS, replace a filled one only when
        // asked. An empty slot has no hand tuning to destroy, so refusing to
        // fill it was the tool being timid rather than safe — and it is why
        // "nothing is happening with the ground" was the honest report.
        var surfaceNotes = new List<string>();

        Material groundPick = target.groundMaterial != null
            ? target.groundMaterial
            : match.ground?.Load();
        if (groundPick != null && (pack.groundMaterial == null || target.overrideGroundTextures))
        {
            bool filled = pack.groundMaterial == null;
            pack.groundMaterial = groundPick;
            surfaceNotes.Add($"ground {(filled ? "filled" : "REPLACED")} with '{groundPick.name}'");
        }

        Material skyPick = target.skyboxMaterial != null
            ? target.skyboxMaterial
            : match.skybox?.Load();
        if (skyPick != null && (pack.skyboxMaterial == null || target.overrideGroundTextures))
        {
            bool filled = pack.skyboxMaterial == null;
            pack.skyboxMaterial = skyPick;
            surfaceNotes.Add($"skybox {(filled ? "filled" : "REPLACED")} with '{skyPick.name}'");
        }

        // Tiling and the detail lanes are tuning, not content: they follow the
        // explicit override only.
        if (target.overrideGroundTextures)
        {
            if (target.groundTilingPerMetre > 0f) pack.groundTilingPerMetre = target.groundTilingPerMetre;
            pack.groundDetail = target.groundDetail;
            pack.groundDetailStrength = target.groundDetailStrength;
            pack.groundDetailScale = target.groundDetailScale;
            pack.groundRockDetail = target.groundRockDetail;
            surfaceNotes.Add("tiling + both detail lanes written from the target");
        }

        string groundNote = surfaceNotes.Count > 0
            ? "Surfaces: " + string.Join("; ", surfaceNotes) + ". Untouched: post profile."
            : "Surfaces untouched (pack already has them and override is off — see the match report).";

        EditorUtility.SetDirty(pack);
        AssetDatabase.SaveAssets();

        log.AppendLine($"[PackWriter] WROTE {AssetDatabase.GetAssetPath(pack)}: {added} entr(ies) added " +
                       $"({pack.entries.Length} total), densities/sun/fog/weather applied. " +
                       groundNote);
        if (added == 0 && match.picks.Count == 0)
            log.AppendLine("  note: zero picks survived matching (bands already full, or the cap ate " +
                           "them) — this run applied look values only.");
        log.AppendLine("  Next: step 5 stages lookdev scenes; the Generator window makes real levels.");
        return log.ToString();
    }
}
