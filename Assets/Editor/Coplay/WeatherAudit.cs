using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Audit every WeatherPreset in the project — the "a new SO works right off
/// the bat" guarantee. Drop in a Heavy Snow Storm asset, run this (or trust
/// the pool: EnvPack weather pools are exactly presets, and the generate path
/// draws them blind), and the mistakes that produce invisible or absurd
/// weather are named before a map ever draws the preset:
///
///   • falling snow with zero ground film (settles on nothing) and the
///     reverse (film with nothing falling — legitimate for aftermath scenes,
///     so a note rather than a warning);
///   • particles configured but rate or size zero — an invisible sheet;
///   • gust with no wind to gust, lightning brighter than readability allows;
///   • layer cycles, layer depth past the applier's cap, null layer slots;
///   • a post override with no profile assigned.
///
/// Report-only. The composition rules live in WeatherApplier.Merge; this
/// checks data against them, never re-implements them.
/// </summary>
public static class WeatherAudit
{
    [MenuItem("Tools/COREHOLD/Validate/Weather Presets Audit", false, 25)]
    public static void Run()
    {
        var sb = new StringBuilder();
        int warns = 0;

        string[] guids = AssetDatabase.FindAssets("t:WeatherPreset");
        sb.AppendLine($"=== WEATHER PRESETS AUDIT — {guids.Length} preset(s) ===");

        foreach (string guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g),
                                              System.StringComparer.Ordinal))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var p = AssetDatabase.LoadAssetAtPath<WeatherPreset>(path);
            if (p == null)
                continue;

            var notes = new List<string>();

            // ---- layers: cycles, depth, holes ------------------------------
            var seen = new HashSet<WeatherPreset>();
            int depth = MaxDepth(p, seen, 0, notes);
            if (depth > 4)
                notes.Add($"WARN layer depth {depth} exceeds the applier's cap of 4 — deeper layers are ignored");
            if (p.layers != null && p.layers.Any(l => l == null))
                notes.Add("WARN a layer slot is empty — harmless but probably not intended");

            // ---- precipitation sanity --------------------------------------
            if (p.precipitation != WeatherPreset.Precipitation.None)
            {
                if (p.precipitationRate <= 0f)
                    notes.Add("WARN precipitation set but rate is 0 — nothing will fall");
                if (p.particleSize <= 0.004f && p.precipitationPrefab == null)
                    notes.Add("WARN particle size ~0 — the sheet will be invisible");
                if (p.precipitation == WeatherPreset.Precipitation.Snow && p.groundSnow <= 0f)
                    notes.Add("note  snow falls but groundSnow is 0 — it settles on nothing " +
                              "(set groundSnow for accumulation)");
                if (p.precipitation == WeatherPreset.Precipitation.Rain && p.groundWetness <= 0f)
                    notes.Add("note  rain falls but groundWetness is 0 — the ground stays dry");
            }
            else if (p.groundSnow > 0f)
            {
                notes.Add("note  surface film with nothing falling — fine for aftermath looks, " +
                          "just confirming it is intended");
            }

            // ---- wind / gust / lightning ------------------------------------
            if (p.gustStrength > 0f && p.windStrength <= 0f && !HasWindInLayers(p))
                notes.Add("WARN gustStrength set but windStrength is 0 and no layer supplies wind — " +
                          "there is nothing to gust");
            if (p.lightningStrikesPerMinute > 12f)
                notes.Add("WARN more than 12 strikes/min — at that rate the flashing costs " +
                          "readability more than it buys drama");

            // ---- post ------------------------------------------------------
            if (p.overridePostProfile && p.postProfile == null)
                notes.Add("WARN overridePostProfile ticked with no profile assigned — the flag does nothing");

            // ---- summary line ----------------------------------------------
            var channels = new List<string>();
            if (p.overrideAmbient) channels.Add("ambient");
            if (p.overrideSun) channels.Add("sun");
            if (p.overrideFog) channels.Add("fog");
            if (p.overrideGroundTint) channels.Add("tint");
            if (p.overridePostProfile) channels.Add("post");
            if (p.precipitation != WeatherPreset.Precipitation.None) channels.Add(p.precipitation.ToString().ToLower());
            if (p.groundSnow > 0f) channels.Add($"film {p.groundSnow:0.##}");
            if (p.groundWetness > 0f) channels.Add($"wet {p.groundWetness:0.##}");
            if (p.windStrength > 0f) channels.Add($"wind {p.windStrength:0.#}" + (p.gustStrength > 0f ? "+gust" : ""));
            if (p.lightningStrikesPerMinute > 0f) channels.Add($"lightning {p.lightningStrikesPerMinute:0.#}/min");
            if (p.layers != null && p.layers.Any(l => l != null))
                channels.Add($"layers[{p.layers.Count(l => l != null)}]");

            sb.AppendLine($"  {p.name,-30} {(channels.Count > 0 ? string.Join(", ", channels) : "(null preset — authored look)")}");
            foreach (string n in notes)
            {
                sb.AppendLine($"      {n}");
                if (n.StartsWith("WARN"))
                    warns++;
            }
        }

        // ---- theme pools: the thing that silently switches the feature off ----
        // A theme whose weatherPool has fewer than two presets cannot vary: the
        // base draw has one answer and the generator leaves the per-wave roll
        // empty, so every run of every level on that theme shows one sky from
        // wave 1 to wave 10. That is invisible from inside the preset assets —
        // they all look fine — which is exactly why it belongs in an audit.
        var packGuids = AssetDatabase.FindAssets("t:EnvPack");
        sb.AppendLine($"\n--- theme weather pools ({packGuids.Length} pack(s)) ---");
        foreach (string pg in packGuids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g),
                                                System.StringComparer.Ordinal))
        {
            var pack = AssetDatabase.LoadAssetAtPath<EnvPack>(AssetDatabase.GUIDToAssetPath(pg));
            if (pack == null)
                continue;
            int n = pack.weatherPool != null ? pack.weatherPool.Count(w => w != null) : 0;
            if (n >= 2)
            {
                sb.AppendLine($"  {pack.name}: {n} preset(s) — rolls per wave");
                continue;
            }
            sb.AppendLine($"  WARN {pack.name}: weatherPool has {n} preset(s). Levels on this theme " +
                          "show ONE sky for the whole run, every run — the per-wave roll needs at " +
                          "least two. Duplicates weight the draw: [Clear, Clear, Dust] is 2:1 clear.");
            warns++;
        }

        sb.AppendLine($"  {warns} warning(s).");
        if (warns > 0) Debug.LogWarning(sb.ToString()); else Debug.Log(sb.ToString());
    }

    private static int MaxDepth(WeatherPreset p, HashSet<WeatherPreset> seen, int depth,
                                List<string> notes)
    {
        if (p == null)
            return depth;
        if (!seen.Add(p))
        {
            notes.Add($"WARN layer cycle through '{p.name}' — the applier guards it, but the intent is unclear");
            return depth;
        }
        int max = depth;
        if (p.layers != null)
            foreach (WeatherPreset l in p.layers)
                max = Mathf.Max(max, MaxDepth(l, seen, depth + 1, notes));
        return max;
    }

    private static bool HasWindInLayers(WeatherPreset p)
        => p.layers != null && p.layers.Any(l => l != null && l.windStrength > 0f);
}
