using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Applies an ART READING — the JSON a Claude produces from reference images —
/// onto an <see cref="ArtTarget"/> asset, creating it if needed.
///
/// This is the seam that decouples the builder from WHERE the vision step
/// runs. Three producers, one consumer:
///
///   • a Claude Code session working on this repo (writes the JSON directly,
///     or ships it as factory code like the desert reading);
///   • anyone pasting reference images + docs/art_reading_prompt.md into
///     claude.ai / the Claude app, saving the reply as a .json;
///   • a future in-editor API call (Anthropic Messages API with image blocks)
///     — it would produce THIS SAME JSON and call <see cref="Import"/>, so
///     building it later changes nothing downstream.
///
/// JSON cannot carry Unity object references, so the reading covers values
/// only; referenceImages stay hand-assigned, and an empty weather pool gets
/// the factory default (clear 2:1 over dust). Band roles arrive as STRINGS
/// ("Silhouette") and are parsed strictly — JsonUtility would otherwise turn
/// an unknown role into 0 (Unassigned) silently, and a silently unassigned
/// band is exactly the kind of bug that dresses nothing and says nothing.
/// </summary>
public static class ReadingImporter
{
    // ---- the JSON contract (documented in docs/art_reading_prompt.md) ------

    [System.Serializable]
    public class Reading
    {
        public string themeName;
        public Color sunColor = Color.white;
        public Vector2 sunAngles = new Vector2(35f, -30f);
        public float sunIntensity = 2f;
        public Color fogColor = new Color(0.65f, 0.70f, 0.78f);
        public float fogDensity = 0.002f;
        public Color groundTint = Color.gray;
        public Color rockTint = Color.gray;
        public ReadingBand[] bands;
        public float landmarkDensity = 1f;
        public float midFieldDensity = 1f;
        public float clutterDensity = 1f;
        public float silhouetteDensity = 1f;
        public float outfieldDensity = 1f;
        public float clusterChance = 0.6f;
        public float scaleJitter = 0.25f;
        public float toneVariation = 0.35f;
        public float slopeTiltMaxDegrees = 10f;
        public float groundZoneStrength = 0.6f;
        public int maxEntries = 50;
        public string[] scanFolders;
    }

    [System.Serializable]
    public class ReadingBand
    {
        public string name;
        public string role;          // Landmark | MidField | Clutter | Silhouette
        public float minHeight;
        public float maxHeight;
        public float scaleMin = 1f;
        public float scaleMax = 1f;
        public int wantDistinct;
        public float aspectMin;
        public float aspectMax = 99f;
        public string[] nameTokens;
    }

    // ------------------------------------------------------------------ menu

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/Import Reading (JSON)…", false, 84)]
    public static void ImportMenu()
    {
        string path = EditorUtility.OpenFilePanel("Art reading JSON (see docs/art_reading_prompt.md)",
                                                  Application.dataPath, "json");
        if (string.IsNullOrEmpty(path))
            return;
        Debug.Log(Import(File.ReadAllText(path)));
    }

    // ------------------------------------------------------------------- API

    /// <summary>Apply a reading. Headless — the future API caller lands here too.</summary>
    public static string Import(string json)
    {
        var log = new StringBuilder();
        log.AppendLine("=== ART READING IMPORT ===");

        Reading r;
        try
        {
            r = JsonUtility.FromJson<Reading>(json);
        }
        catch (System.Exception e)
        {
            return log.AppendLine($"  FAIL: not parseable JSON — {e.Message}").ToString();
        }
        if (r == null || string.IsNullOrEmpty(r.themeName))
            return log.AppendLine("  FAIL: themeName is required — it names the EnvPack this targets.")
                      .ToString();
        if (r.bands == null || r.bands.Length == 0)
            return log.AppendLine("  FAIL: at least one band is required (the scale ladder).").ToString();

        // Strict role parse: an unknown role must fail loudly, not become
        // Unassigned and dress nothing.
        var bands = new List<ArtTarget.Band>();
        foreach (ReadingBand b in r.bands)
        {
            if (!System.Enum.TryParse(b.role, true, out EnvPack.PropRole role) ||
                role == EnvPack.PropRole.Unassigned)
                return log.AppendLine($"  FAIL: band '{b.name}' has role '{b.role}' — must be one of " +
                                      "Landmark, MidField, Clutter, Silhouette.").ToString();
            bands.Add(new ArtTarget.Band
            {
                name = b.name,
                role = role,
                minHeight = b.minHeight,
                maxHeight = b.maxHeight,
                scaleRange = new Vector2(b.scaleMin, b.scaleMax),
                wantDistinct = b.wantDistinct,
                aspectMin = b.aspectMin,
                aspectMax = b.aspectMax,
                nameTokens = b.nameTokens ?? new string[0],
            });
        }

        ArtTargetFactory.EnsureTargetDir();
        string assetPath = $"{ArtTargetFactory.TargetDir}/ArtTarget_{r.themeName}.asset";
        var target = AssetDatabase.LoadAssetAtPath<ArtTarget>(assetPath);
        bool created = target == null;
        if (created)
        {
            target = ScriptableObject.CreateInstance<ArtTarget>();
            AssetDatabase.CreateAsset(target, assetPath);
        }
        else
        {
            Undo.RecordObject(target, "Import Reading");
        }

        target.themeName = r.themeName;
        target.sunColor = r.sunColor;
        target.sunAngles = r.sunAngles;
        target.sunIntensity = r.sunIntensity;
        target.fogColor = r.fogColor;
        target.fogDensity = r.fogDensity;
        target.groundTint = r.groundTint;
        target.rockTint = r.rockTint;
        target.bands = bands.ToArray();
        target.landmarkDensity = r.landmarkDensity;
        target.midFieldDensity = r.midFieldDensity;
        target.clutterDensity = r.clutterDensity;
        target.silhouetteDensity = r.silhouetteDensity;
        target.outfieldDensity = r.outfieldDensity;
        target.clusterChance = r.clusterChance;
        target.scaleJitter = r.scaleJitter;
        target.toneVariation = r.toneVariation;
        target.slopeTiltMaxDegrees = r.slopeTiltMaxDegrees;
        target.groundZoneStrength = r.groundZoneStrength;
        target.maxEntries = r.maxEntries;
        if (r.scanFolders != null && r.scanFolders.Length > 0)
            target.scanFolders = r.scanFolders;
        if (target.weatherPool == null || target.weatherPool.Length == 0)
            target.weatherPool = ArtTargetFactory.DefaultWeatherPool();

        EditorUtility.SetDirty(target);
        AssetDatabase.SaveAssets();
        Selection.activeObject = target;

        log.AppendLine($"  {(created ? "created" : "updated")} {assetPath}");
        log.AppendLine($"  theme '{r.themeName}', {bands.Count} band(s): " +
                       string.Join(", ", bands.Select(b => $"{b.name}({b.wantDistinct})")));
        if (PackMatcher.FindPack(r.themeName) == null)
            log.AppendLine($"  WARNING no EnvPack has themeName '{r.themeName}' yet — " +
                           "create one before running the pipeline.");
        log.AppendLine("  referenceImages and weather pool are asset references — JSON cannot " +
                       "carry them; assign by hand if wanted (pool defaulted to clear/dust).");
        return log.ToString();
    }
}
