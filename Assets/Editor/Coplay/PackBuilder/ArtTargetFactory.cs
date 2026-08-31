using System.Linq;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The vision step's output, as code that creates data (EnvPack Builder L1).
///
/// The BUILDER is generic — any theme is an ArtTarget, made three ways: this
/// factory (a reading Claude already did, shipped as code), an imported
/// reading JSON (<see cref="ReadingImporter"/> — produced by any Claude
/// surface from reference images, see docs/art_reading_prompt.md), or a blank
/// asset via Create → COREHOLD → Art Target, filled by hand. Only the VALUES
/// here are theme-specific: they transcribe docs/art_direction_wadi_rum.md,
/// Claude's reading of the desert references, targeting the SandyDesert pack.
///
/// Also creates Weather_Clear if missing: the art doc's finding was that the
/// pack's own look had never been on screen because its only weather preset
/// (dust) overrides the fog on every map. A preset with every override OFF is
/// the explicit "base look" option; duplicated in the pool it outweighs dust.
///
/// An existing ArtTarget asset is NOT overwritten — it may carry hand edits,
/// which outrank this transcription. Delete it to re-create.
/// </summary>
public static class ArtTargetFactory
{
    internal const string TargetDir = "Assets/_COREHOLD/Data/ArtTargets";
    private const string TargetPath = TargetDir + "/ArtTarget_SandyDesert.asset";
    private const string WeatherDir = "Assets/_COREHOLD/Data/Weather";
    private const string ClearPath = WeatherDir + "/Weather_Clear.asset";
    private const string DustPath = WeatherDir + "/Weather_Dust.asset";

    /// <summary>Ensure the folder the targets live in exists.</summary>
    internal static void EnsureTargetDir()
    {
        if (!AssetDatabase.IsValidFolder(TargetDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "ArtTargets");
    }

    /// <summary>The default weather split for any new target: a clear preset
    /// (created if missing) weighted 2:1 over dust, so the base look is what
    /// most maps actually draw.</summary>
    internal static WeatherPreset[] DefaultWeatherPool()
    {
        var clear = AssetDatabase.LoadAssetAtPath<WeatherPreset>(ClearPath);
        if (clear == null)
        {
            clear = ScriptableObject.CreateInstance<WeatherPreset>();
            clear.name = "Weather_Clear";
            AssetDatabase.CreateAsset(clear, ClearPath);
        }
        var dust = AssetDatabase.LoadAssetAtPath<WeatherPreset>(DustPath);
        return dust != null ? new[] { clear, clear, dust } : new[] { clear };
    }

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/1. Create Art Target (Claude's desert reading)", false, 70)]
    public static void CreateSandyDesert()
    {
        if (AssetDatabase.LoadAssetAtPath<ArtTarget>(TargetPath) != null)
        {
            Debug.LogWarning($"[ArtTarget] {TargetPath} already exists — not overwritten " +
                             "(it may carry hand edits, which outrank this transcription). " +
                             "Delete it to re-create from the art-direction doc.");
            return;
        }

        EnsureTargetDir();

        var t = ScriptableObject.CreateInstance<ArtTarget>();
        t.themeName = "SandyDesert";

        // ---- palette (art doc §parameter table) -----------------------------
        t.sunColor = new Color(1f, 0.9266f, 0.6745f);
        t.sunAngles = new Vector2(24f, -55f);   // lower + more across-frame
        t.sunIntensity = 2f;
        t.fogColor = new Color(0.65f, 0.70f, 0.78f);   // aerial-perspective blue
        t.fogDensity = 0.002f;                          // 8% at 150 m, 30% at 300 m
        t.groundTint = new Color(0.80f, 0.55f, 0.38f); // red-orange sand
        t.rockTint = new Color(0.72f, 0.50f, 0.40f);   // rose sandstone

        // ---- scale ladder (art doc §scale ladder) ---------------------------
        t.bands = new[]
        {
            new ArtTarget.Band
            {
                name = "Massif", role = EnvPack.PropRole.Silhouette,
                minHeight = 40f, maxHeight = 80f, scaleRange = new Vector2(1.5f, 3f),
                wantDistinct = 12, aspectMin = 0.4f, aspectMax = 2.5f,
                nameTokens = new[] { "mesa", "butte", "massif", "cliff", "mountain", "plateau" },
            },
            new ArtTarget.Band
            {
                name = "Outcrop", role = EnvPack.PropRole.Landmark,
                minHeight = 8f, maxHeight = 25f, scaleRange = new Vector2(0.8f, 1.8f),
                wantDistinct = 14, aspectMin = 0.5f, aspectMax = 4f,
                nameTokens = new[] { "rock", "outcrop", "spire", "fin", "stack", "boulder", "crag" },
            },
            new ArtTarget.Band
            {
                name = "Boulder", role = EnvPack.PropRole.MidField,
                minHeight = 2.5f, maxHeight = 8f, scaleRange = new Vector2(0.7f, 1.4f),
                wantDistinct = 14, aspectMin = 0.3f, aspectMax = 2.5f,
                nameTokens = new[] { "rock", "boulder", "stone" },
            },
            new ArtTarget.Band
            {
                name = "Scatter", role = EnvPack.PropRole.Clutter,
                minHeight = 0.05f, maxHeight = 1.8f, scaleRange = new Vector2(0.6f, 1.5f),
                wantDistinct = 10, aspectMin = 0f, aspectMax = 99f,
                nameTokens = new[] { "rock", "stone", "pebble", "bush", "scrub", "grass", "plant" },
            },
        };

        // ---- densities (art doc: the floor is empty; drama moves to the edges)
        t.landmarkDensity = 1.5f;
        t.midFieldDensity = 1.2f;
        t.clutterDensity = 1.0f;     // down from 4 — bare sand is the point
        t.silhouetteDensity = 3.0f;
        t.outfieldDensity = 2.5f;
        t.clusterChance = 0.85f;
        t.scaleJitter = 0.55f;
        t.toneVariation = 0.35f;     // Wadi Rum rock is near-uniform; value comes from light
        t.slopeTiltMaxDegrees = 8f;  // a leaning massif is a mistake, not geology
        t.groundZoneStrength = 0.45f;

        t.weatherPool = DefaultWeatherPool();
        t.maxEntries = 50;

        AssetDatabase.CreateAsset(t, TargetPath);
        AssetDatabase.SaveAssets();
        Selection.activeObject = t;
        EditorGUIUtility.PingObject(t);

        Debug.Log($"[ArtTarget] Created {TargetPath} from docs/art_direction_wadi_rum.md" +
                  (t.weatherPool.Length == 1 ? "  (Weather_Dust not found — pool is clear-only)" : "") +
                  "\n  Drop the reference images into its referenceImages slots for the record" +
                  " (and for Extract Palette, if wanted). Then run step 2: Scan Prefab Index.");
    }
}
