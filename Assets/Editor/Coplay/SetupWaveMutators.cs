using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Authors the four starter mutators as assets and registers this level's
/// mutator library, plus the audit that keeps authored mutators honest.
///
/// These four are just the ones that ship. There is nothing special about
/// them: each is an ordinary asset carrying its own words, weather and
/// numbers, and a fifth is a fifth asset, not a code change.
///
/// Unlike the weather setup, this NEVER changes scene: it works on whatever is
/// open, because the thing it wires — the WaveManager's library — is per scene.
/// </summary>
public static class SetupWaveMutators
{
    private const string MutatorDir = "Assets/_COREHOLD/Data/Mutators";
    private const string WeatherDir = "Assets/_COREHOLD/Data/Weather";

    [MenuItem("Tools/COREHOLD/Scene Setup/Wave Mutators", false, 44)]
    public static void Setup()
    {
        var log = new StringBuilder();
        log.AppendLine("=== wave mutator setup ===");

        if (!AssetDatabase.IsValidFolder(MutatorDir))
        {
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "Mutators");
            log.AppendLine($"[ok] created {MutatorDir}");
        }

        // The four that ship. Ordering note: Storm and Blackout want weather layers the
        // WEATHER setup authors. Running that first is the happy path; running
        // this first is caught by the backfill in Author().
        if (Load($"{WeatherDir}/WeatherLayer_Storm.asset") == null)
            log.AppendLine("[note] no weather layers found — run Scene Setup → Weather, then this " +
                           "tool again to give Storm and Blackout their looks");

        WaveMutatorDefinition storm = Author(
            "Mutator_Storm", "storm", "STORM", "Air units move faster",
            log, airSpeed: 1.3f,
            weather: Load($"{WeatherDir}/WeatherLayer_Storm.asset"));

        WaveMutatorDefinition convoy = Author(
            "Mutator_Convoy", "convoy", "CONVOY", "Everything comes down one approach",
            log, singleApproach: true);

        WaveMutatorDefinition overcharge = Author(
            "Mutator_Overcharge", "overcharge", "OVERCHARGE", "Tougher units, richer salvage",
            log, health: 1.3f, bounty: 1.5f);

        WaveMutatorDefinition blackout = Author(
            "Mutator_Blackout", "blackout", "BLACKOUT", "Turrets see half as far — light them up",
            log, turretRange: 0.5f,
            weather: Load($"{WeatherDir}/WeatherLayer_Blackout.asset"));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        RegisterLibrary(log, storm, convoy, overcharge, blackout);
        Debug.Log(log.ToString());
    }

    /// <summary>Create or update one mutator asset. Idempotent, and it does NOT
    /// clobber hand edits: an asset that already exists keeps its numbers, its
    /// words and its weather. Only a missing asset is authored from these
    /// defaults — otherwise re-running the setup would silently undo tuning,
    /// which is the failure mode that makes people stop running setup tools.</summary>
    private static WaveMutatorDefinition Author(
        string fileName, string id, string title, string clause,
        StringBuilder log,
        float airSpeed = 1f, float groundSpeed = 1f, float health = 1f, float bounty = 1f,
        float turretRange = 1f, float spawnGap = 1f, bool singleApproach = false,
        WeatherPreset weather = null)
    {
        string path = $"{MutatorDir}/{fileName}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<WaveMutatorDefinition>(path);
        if (existing != null)
        {
            // One exception to leaving an existing asset alone: BACKFILL a
            // missing weather layer. Running this tool before the weather setup
            // authors an asset whose layer resolved to null, and since the
            // asset then exists, no later run would ever fix it — the mutator
            // would silently have no look forever, and the ordering that caused
            // it happened once, minutes ago, with no error. Only a NULL slot is
            // filled: a layer someone chose is never overruled.
            if (existing.weatherLayer == null && weather != null)
            {
                existing.weatherLayer = weather;
                EditorUtility.SetDirty(existing);
                log.AppendLine($"[ok] {fileName} exists — backfilled its weather layer ({weather.name})");
            }
            else
            {
                log.AppendLine($"[ok] {fileName} exists — left untouched");
            }
            return existing;
        }

        var d = ScriptableObject.CreateInstance<WaveMutatorDefinition>();
        d.id = id;
        d.title = title;
        d.clause = clause;
        d.airSpeedMultiplier = airSpeed;
        d.groundSpeedMultiplier = groundSpeed;
        d.healthMultiplier = health;
        d.bountyMultiplier = bounty;
        d.turretRangeMultiplier = turretRange;
        d.spawnGapMultiplier = spawnGap;
        d.singleApproach = singleApproach;
        d.weatherLayer = weather;
        AssetDatabase.CreateAsset(d, path);
        log.AppendLine($"[ok] {fileName} authored" +
                       (weather != null ? $" (weather: {weather.name})" : ""));
        return d;
    }

    private static WeatherPreset Load(string path) =>
        AssetDatabase.LoadAssetAtPath<WeatherPreset>(path);

    /// <summary>Fill the open scene's WaveManager library with every mutator
    /// asset in the project. Additive: assets already listed keep their order,
    /// so a designer's arrangement of the debug cycle survives.</summary>
    private static void RegisterLibrary(StringBuilder log, params WaveMutatorDefinition[] builtins)
    {
        var wm = Object.FindFirstObjectByType<WaveManager>();
        if (wm == null)
        {
            log.AppendLine("[warn] no WaveManager in the open scene — library not registered. " +
                           "Open a level scene and run this again.");
            return;
        }

        var all = new List<WaveMutatorDefinition>();
        foreach (string guid in AssetDatabase.FindAssets("t:WaveMutatorDefinition"))
        {
            var d = AssetDatabase.LoadAssetAtPath<WaveMutatorDefinition>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (d != null)
                all.Add(d);
        }
        foreach (WaveMutatorDefinition b in builtins)
            if (b != null && !all.Contains(b))
                all.Add(b);

        var so = new SerializedObject(wm);
        SerializedProperty list = so.FindProperty("mutatorLibrary");
        if (list == null)
        {
            log.AppendLine("[warn] WaveManager has no mutatorLibrary field — is the R33 code compiled?");
            return;
        }

        var ordered = new List<WaveMutatorDefinition>();
        for (int i = 0; i < list.arraySize; i++)
        {
            var d = list.GetArrayElementAtIndex(i).objectReferenceValue as WaveMutatorDefinition;
            if (d != null && !ordered.Contains(d))
                ordered.Add(d);
        }
        int added = 0;
        foreach (WaveMutatorDefinition d in all)
            if (!ordered.Contains(d)) { ordered.Add(d); added++; }

        list.arraySize = ordered.Count;
        for (int i = 0; i < ordered.Count; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = ordered[i];
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(wm);
        EditorSceneManager.MarkSceneDirty(wm.gameObject.scene);

        log.AppendLine($"[ok] WaveManager library: {ordered.Count} mutator(s), {added} newly added. " +
                       "SAVE THE SCENE to keep it. ⇧T in play mode cycles them.");
    }

    // ------------------------------------------------------------------ audit

    /// <summary>
    /// Report-only check over every mutator asset and every wave that uses one.
    /// The mistakes it names are the ones that survive compilation and then
    /// quietly corrupt a certification run or a banner.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Validate/Wave Mutators Audit", false, 26)]
    public static void Audit()
    {
        var sb = new StringBuilder();
        int warns = 0;

        var all = AssetDatabase.FindAssets("t:WaveMutatorDefinition")
            .Select(g => AssetDatabase.LoadAssetAtPath<WaveMutatorDefinition>(
                AssetDatabase.GUIDToAssetPath(g)))
            .Where(d => d != null)
            .OrderBy(d => d.name, System.StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"=== WAVE MUTATOR AUDIT — {all.Count} asset(s) ===");

        var byId = new Dictionary<string, WaveMutatorDefinition>();
        foreach (WaveMutatorDefinition d in all)
        {
            string id = d.ResolvedId;
            sb.AppendLine($"\n{d.name}  (id '{id}')");

            // An id collision is the worst one here: the exporter writes the id
            // into the wave table, so two mutators sharing one make the gate
            // report about a mutator that is not the one running.
            if (byId.TryGetValue(id, out WaveMutatorDefinition other))
            {
                sb.AppendLine($"  [WARN] id '{id}' is also used by '{other.name}' — the wave table " +
                              "cannot tell them apart, and certification would name the wrong one");
                warns++;
            }
            else byId[id] = d;

            if (id != id.Trim().ToLowerInvariant() || id.Contains(' '))
            {
                sb.AppendLine("  [WARN] id should be lowercase with no spaces — it is a table key");
                warns++;
            }

            if (string.IsNullOrWhiteSpace(d.title) || string.IsNullOrWhiteSpace(d.clause))
            {
                sb.AppendLine("  [WARN] empty title or clause — the wave-start banner would show a blank");
                warns++;
            }

            MutatorEffects e = d.Effects;
            if (e.IsIdentity)
            {
                sb.AppendLine("  [WARN] changes nothing mechanically. Legitimate for a look-only " +
                              "mutator (weather + banner), but say so deliberately");
                warns++;
            }

            // A mutator that only takes is a punishment; one that pays for what
            // it takes is a bargain. Not an error — a design nudge.
            bool harder = e.health > 1.01f || e.turretRange < 0.99f ||
                          e.airSpeed > 1.01f || e.singleApproach || e.spawnGap < 0.99f;
            if (harder && e.bounty <= 1.001f)
                sb.AppendLine("  [note] harder with no extra salvage — reads as a punishment. " +
                              "Overcharge pays 1.5x for its 1.3x health");

            if (e.turretRange < 0.4f)
            {
                sb.AppendLine($"  [WARN] range x{e.turretRange:0.00} — range is AREA, so this leaves " +
                              $"{e.turretRange * e.turretRange:P0} of the ground covered. Blackout's " +
                              "0.5 is already the harshest shipped value");
                warns++;
            }

            if (d.weatherLayer == null)
                sb.AppendLine("  [note] no weather layer — the wave will look like any other");

            sb.AppendLine($"  effects: air x{e.airSpeed:0.##}  ground x{e.groundSpeed:0.##}  " +
                          $"hp x{e.health:0.##}  bounty x{e.bounty:0.##}  range x{e.turretRange:0.##}  " +
                          $"gap x{e.spawnGap:0.##}{(e.singleApproach ? "  one-approach" : "")}");
        }

        // Waves referencing a mutator no scene library knows about: the debug
        // cycle will not offer it, which is the symptom people report.
        var registered = new HashSet<WaveMutatorDefinition>();
        foreach (WaveManager wm in Object.FindObjectsByType<WaveManager>(FindObjectsSortMode.None))
            foreach (WaveMutatorDefinition d in wm.MutatorLibrary)
                if (d != null) registered.Add(d);

        var used = new HashSet<WaveMutatorDefinition>();
        foreach (string guid in AssetDatabase.FindAssets("t:WaveDefinition"))
        {
            var w = AssetDatabase.LoadAssetAtPath<WaveDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (w == null)
                continue;

            // An empty pool slot is the quiet authoring slip: it silently
            // reweights the draw away from what the gate priced.
            if (w.poolMutators == null)
                continue;
            foreach (WaveMutatorDefinition d in w.poolMutators)
            {
                if (d == null)
                {
                    sb.AppendLine($"\n[WARN] '{w.name}' has an EMPTY mutator pool slot");
                    warns++;
                    continue;
                }
                used.Add(d);
            }
        }

        if (registered.Count > 0)
            foreach (WaveMutatorDefinition d in used)
                if (!registered.Contains(d))
                {
                    sb.AppendLine($"\n[WARN] '{d.name}' is used by a wave but is not in the open " +
                                  "scene's WaveManager library — run Scene Setup → Wave Mutators");
                    warns++;
                }

        sb.AppendLine($"\n=== {warns} warning(s) ===");
        if (warns > 0) Debug.LogWarning(sb.ToString());
        else Debug.Log(sb.ToString());
    }
}
