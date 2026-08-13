using System.Text;
using Corehold.Data;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Authors the Rain and Dust presets and wires the applier (roadmap R13, R14).
/// Run: Tools/COREHOLD/Scene Setup/Weather.
///
/// Both presets are deliberately restrained. R14's bar is that enemies and turret
/// states stay readable THROUGH the effect at 907×510, and the overdraw budget is
/// ≤3 alpha layers — so each preset uses ONE alpha layer (a single particle system
/// on a shared unlit material) and spends its remaining headroom on low particle
/// alpha rather than on more of them. That leaves room for a second authored layer
/// later without breaking the budget.
///
/// Safe to re-run: existing assets are updated in place, never duplicated, and an
/// existing applier keeps whichever preset it already references.
/// </summary>
public static class SetupWeather
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";
    private const string WeatherDir = "Assets/_COREHOLD/Data/Weather";
    private const string RainPath = WeatherDir + "/Weather_Rain.asset";
    private const string DustPath = WeatherDir + "/Weather_Dust.asset";

    [MenuItem("Tools/COREHOLD/Scene Setup/Weather", false, 43)]
    public static void Setup()
    {
        var log = new StringBuilder();
        log.AppendLine("=== R13/R14 weather setup ===");

        // Menu use: hop to the shipped scene if the human is elsewhere.
        // Pipeline use: NEVER — opening a scene here would replace the scene
        // being generated with Game.unity and build the map into it.
        Scene scene = SceneManager.GetActiveScene();
        if (!GenerationDriven.Active && scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        if (!AssetDatabase.IsValidFolder(WeatherDir))
        {
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "Weather");
            log.AppendLine($"[ok] created {WeatherDir}");
        }

        WeatherPreset rain = AuthorRain(log);
        AuthorDust(log);
        WireApplier(rain, log);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        // The pipeline owns saving (its final stage). Saving here would fire a
        // modal Save dialog on the untitled scene being generated.
        if (!GenerationDriven.Active)
            EditorSceneManager.SaveScene(scene);
        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------- authoring

    /// <summary>
    /// Rain: a cool, slightly brighter fog so the field reads as wet and hazy,
    /// fast thin stretched particles, and a light wind cant. Ambient is nudged
    /// cooler rather than darker — darkening ambient is what costs legibility.
    /// </summary>
    private static WeatherPreset AuthorRain(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(RainPath, log);

        p.overrideAmbient = true;
        p.ambientColor = new Color(0.26f, 0.30f, 0.40f, 1f);

        p.overrideFog = true;
        p.fogColor = new Color(0.13f, 0.16f, 0.22f, 1f);
        p.fogDensity = 0.0052f;                 // a touch denser than R11's solved baseline

        p.overrideGroundTint = true;
        p.groundTint = new Color(0.88f, 0.92f, 1.00f, 1f);   // cool, wet sheen

        p.precipitation = WeatherPreset.Precipitation.Rain;
        p.precipitationRate = 260f;
        p.fallSpeed = 18f;
        p.particleSize = 0.018f;   // ~1.2 px wide at 907x510
        p.streakLength = 18f;      // -> ~21 px long: a streak, not a dash
        p.particleColor = new Color(0.78f, 0.85f, 0.98f, 0.30f);

        p.windDirection = new Vector3(0.35f, 0f, -1f);
        p.windStrength = 3.5f;

        p.overridePostProfile = true;
        p.postProfile = AuthorGrade(WeatherDir + "/Weather_Rain_Post.asset",
                                    saturation: -15f, contrast: 5f, temperature: -12f,
                                    filter: new Color(0.92f, 0.96f, 1.00f), log: log);
        p.postWeight = 1f;

        EditorUtility.SetDirty(p);
        log.AppendLine($"[ok] Rain authored — 1 alpha layer, rate {p.precipitationRate:0}, alpha {p.particleColor.a:0.00}");
        return p;
    }

    /// <summary>
    /// Dust: warmer and dimmer, large slow drifting motes, denser fog to sell the
    /// haze. Particle alpha is lower than Rain's because the motes are far larger —
    /// same overdraw, more coverage.
    /// </summary>
    private static WeatherPreset AuthorDust(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(DustPath, log);

        p.overrideAmbient = true;
        p.ambientColor = new Color(0.38f, 0.32f, 0.24f, 1f);

        p.overrideFog = true;
        p.fogColor = new Color(0.22f, 0.18f, 0.13f, 1f);
        p.fogDensity = 0.0068f;

        p.overrideGroundTint = true;
        p.groundTint = new Color(1.00f, 0.94f, 0.82f, 1f);   // warm dust film

        p.precipitation = WeatherPreset.Precipitation.Dust;
        p.precipitationRate = 90f;
        p.fallSpeed = 1.6f;
        p.particleSize = 0.05f;    // ~3.4 px motes (was 0.12 x a hidden 3 = 24 px)
        p.particleColor = new Color(0.85f, 0.76f, 0.60f, 0.16f);

        p.windDirection = new Vector3(1f, 0.05f, -0.35f);
        p.windStrength = 5f;

        p.overridePostProfile = true;
        p.postProfile = AuthorGrade(WeatherDir + "/Weather_Dust_Post.asset",
                                    saturation: -5f, contrast: 8f, temperature: 18f,
                                    filter: new Color(1.00f, 0.96f, 0.88f), log: log);
        p.postWeight = 1f;

        EditorUtility.SetDirty(p);
        log.AppendLine($"[ok] Dust authored — 1 alpha layer, rate {p.precipitationRate:0}, alpha {p.particleColor.a:0.00}");
        return p;
    }

    /// <summary>
    /// Author a grading profile carrying ONLY the overrides weather should move.
    /// It is layered additively over the scene's base profile by the applier, so
    /// declaring nothing else is what lets Bloom and Tonemapping survive — a
    /// profile that redeclared them would fight the base look.
    /// </summary>
    private static VolumeProfile AuthorGrade(string path, float saturation, float contrast,
                                             float temperature, Color filter, StringBuilder log)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            log.AppendLine($"[ok] created {path}");
        }

        if (!profile.TryGet(out ColorAdjustments colour))
            colour = profile.Add<ColorAdjustments>(true);
        colour.active = true;
        colour.saturation.overrideState = true; colour.saturation.value = saturation;
        colour.contrast.overrideState = true;   colour.contrast.value = contrast;
        colour.colorFilter.overrideState = true; colour.colorFilter.value = filter;

        if (!profile.TryGet(out WhiteBalance balance))
            balance = profile.Add<WhiteBalance>(true);
        balance.active = true;
        balance.temperature.overrideState = true; balance.temperature.value = temperature;

        EditorUtility.SetDirty(profile);
        return profile;
    }

    private static WeatherPreset LoadOrCreate(string path, StringBuilder log)
    {
        var p = AssetDatabase.LoadAssetAtPath<WeatherPreset>(path);
        if (p == null)
        {
            p = ScriptableObject.CreateInstance<WeatherPreset>();
            AssetDatabase.CreateAsset(p, path);
            log.AppendLine($"[ok] created {path}");
        }
        return p;
    }

    // ---------------------------------------------------------------- wiring

    private static void WireApplier(WeatherPreset defaultPreset, StringBuilder log)
    {
        var applier = Object.FindFirstObjectByType<WeatherApplier>();
        if (applier == null)
        {
            var go = new GameObject("WeatherApplier");
            applier = go.AddComponent<WeatherApplier>();
            Undo.RegisterCreatedObjectUndo(go, "Create WeatherApplier");
            log.AppendLine("[ok] created the WeatherApplier root object");
        }

        var so = new SerializedObject(applier);
        SerializedProperty presetProp = so.FindProperty("preset");
        if (presetProp == null)
        {
            log.AppendLine("[warn] WeatherApplier has no 'preset' field — is the R13 code compiled?");
            return;
        }

        if (presetProp.objectReferenceValue == null)
        {
            // Ship on the NULL preset: R13 requires that to be pixel-identical to
            // today's look, so the default state changes nothing until a human
            // deliberately picks a preset.
            log.AppendLine("[ok] applier left on the NULL preset — the scene keeps its authored look. " +
                           $"Assign {System.IO.Path.GetFileName(RainPath)} or " +
                           $"{System.IO.Path.GetFileName(DustPath)} on the WeatherApplier to audition one.");
        }
        else
        {
            log.AppendLine($"[ok] applier already references '{presetProp.objectReferenceValue.name}' — left untouched");
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(applier);
    }
}
