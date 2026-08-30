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
    private const string SnowPath = WeatherDir + "/Weather_Snow.asset";
    private const string ClearPath = WeatherDir + "/Weather_Clear.asset";
    private const string OvercastPath = WeatherDir + "/Weather_Overcast.asset";
    private const string SandstormPath = WeatherDir + "/Weather_Sandstorm.asset";
    private const string HeavySnowStormPath = WeatherDir + "/Weather_HeavySnowStorm.asset";
    private const string GustLayerPath = WeatherDir + "/WeatherLayer_GustingWind.asset";
    private const string LightningLayerPath = WeatherDir + "/WeatherLayer_Lightning.asset";
    private const string StormLayerPath = WeatherDir + "/WeatherLayer_Storm.asset";
    private const string BlackoutLayerPath = WeatherDir + "/WeatherLayer_Blackout.asset";

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
        AuthorSnow(log);
        AuthorClear(log);
        AuthorOvercast(log);
        WeatherPreset gust = AuthorGustLayer(log);
        WeatherPreset lightning = AuthorLightningLayer(log);
        WeatherPreset storm = AuthorStormLayer(log);
        WeatherPreset blackout = AuthorBlackoutLayer(log);
        AuthorSandstorm(gust, log);
        AuthorHeavySnowStorm(gust, log);
        WireApplier(rain, log);
        WireMutatorLinks(storm, blackout, log);

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
        p.ambientVolume = 0.4f;    // synthesized rain hiss unless a clip is assigned

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
        p.precipitationRate = 180f;
        p.fallSpeed = 1.6f;
        p.particleSize = 0.065f;   // ~4.4 px motes at the 12 m layer (was 0.12 × a hidden 3 = 24 px)
        p.particleColor = new Color(0.85f, 0.76f, 0.60f, 0.16f);

        p.windDirection = new Vector3(1f, 0.05f, -0.35f);
        p.windStrength = 5f;
        p.ambientVolume = 0.32f;   // synthesized wind unless a clip is assigned

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
    /// Snow: the first preset that uses the SURFACE RESPONSE rather than
    /// particles alone — flakes fall, and the ground and the props whiten with
    /// them (WeatherPreset.groundSnow, applied by the terrain shader through
    /// the surface normal and by PropSnow through property blocks).
    ///
    /// Deliberately falls SLOWLY against a gentle wind. The sheet classifies
    /// motion rather than reading the enum, and at these speeds snow lands in
    /// the volume-fill branch — flakes appear at every depth around the camera
    /// instead of raining from a top slab, which is what snow actually does.
    /// Fall much faster and it would be classified as rain and streak.
    ///
    /// Ambient goes UP, not down: snow bounces light, and a dim snow scene
    /// reads as night rather than as weather.
    /// </summary>
    private static WeatherPreset AuthorSnow(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(SnowPath, log);

        p.overrideAmbient = true;
        p.ambientColor = new Color(0.44f, 0.48f, 0.56f, 1f);

        p.overrideSun = true;
        p.sunTemperatureKelvin = 7800f;                      // cool overcast light
        p.sunFilter = new Color(0.94f, 0.96f, 1.00f, 1f);
        p.sunIntensityMult = 0.85f;
        p.sunShadowStrengthMult = 0.6f;                      // overcast = soft, weak shadows

        p.overrideFog = true;
        p.fogColor = new Color(0.74f, 0.78f, 0.84f, 1f);
        p.fogDensity = 0.005f;

        // The ground does the heavy lifting here, not the particles.
        p.groundSnow = 0.75f;
        p.groundWetness = 0.15f;                             // snow damps what it does not cover
        p.snowColor = new Color(0.92f, 0.94f, 0.98f, 1f);
        p.overrideGroundTint = false;                        // groundSnow owns the ground's look

        p.precipitation = WeatherPreset.Precipitation.Snow;
        p.precipitationRate = 130f;                          // fewer, larger, slower than dust
        p.fallSpeed = 1.2f;
        p.particleSize = 0.09f;
        p.particleColor = new Color(0.97f, 0.98f, 1.00f, 0.55f);

        p.windDirection = new Vector3(0.45f, -0.05f, -0.2f);
        p.windStrength = 1.8f;                               // gentle: keeps it in the drift branch
        p.ambientVolume = 0.22f;                             // quiet — snow muffles

        p.overridePostProfile = true;
        p.postProfile = AuthorGrade(WeatherDir + "/Weather_Snow_Post.asset",
                                    saturation: -18f, contrast: -4f, temperature: -12f,
                                    filter: new Color(0.96f, 0.98f, 1.00f), log: log);
        p.postWeight = 1f;

        EditorUtility.SetDirty(p);
        log.AppendLine($"[ok] Snow authored — rate {p.precipitationRate:0}, " +
                       $"groundSnow {p.groundSnow:0.00}, wetness {p.groundWetness:0.00}");
        return p;
    }

    /// <summary>Clear: everything off. The explicit "base look" pool entry —
    /// exists so a pool can WEIGHT clear days, not just fall back to them.</summary>
    private static WeatherPreset AuthorClear(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(ClearPath, log);
        p.precipitation = WeatherPreset.Precipitation.None;
        p.groundSnow = 0f;
        p.groundWetness = 0f;
        p.windStrength = 0f;
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Clear authored — the base look, as an explicit choice");
        return p;
    }

    /// <summary>Overcast: fully expressible with the base channels — soft weak
    /// shadows, flat cool light, a touch more haze. No particles at all.</summary>
    private static WeatherPreset AuthorOvercast(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(OvercastPath, log);
        p.overrideAmbient = true;
        p.ambientColor = new Color(0.40f, 0.42f, 0.47f, 1f);
        p.overrideSun = true;
        p.sunTemperatureKelvin = 7200f;
        p.sunFilter = new Color(0.95f, 0.96f, 1f, 1f);
        p.sunIntensityMult = 0.75f;
        p.sunShadowStrengthMult = 0.45f;
        p.overrideFog = true;
        p.fogColor = new Color(0.70f, 0.72f, 0.76f, 1f);
        p.fogDensity = 0.0035f;
        p.precipitation = WeatherPreset.Precipitation.None;
        p.windStrength = 2.5f;
        p.gustStrength = 0.3f;
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Overcast authored — no particles, all light");
        return p;
    }

    /// <summary>Gusting wind as a pure LAYER: wind + gust and nothing else, so
    /// it composes over any base without touching its light or fog.</summary>
    private static WeatherPreset AuthorGustLayer(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(GustLayerPath, log);
        p.precipitation = WeatherPreset.Precipitation.None;
        p.windDirection = new Vector3(1f, 0f, -0.3f);
        p.windStrength = 7f;
        p.gustStrength = 0.65f;
        p.gustPeriodSeconds = 6f;
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] GustingWind layer authored — wind channel only");
        return p;
    }

    /// <summary>Lightning as a pure LAYER: strikes and nothing else.</summary>
    private static WeatherPreset AuthorLightningLayer(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(LightningLayerPath, log);
        p.precipitation = WeatherPreset.Precipitation.None;
        p.windStrength = 0f;
        p.lightningStrikesPerMinute = 5f;
        p.lightningIntensity = 3.5f;
        p.lightningColor = new Color(0.85f, 0.9f, 1f, 1f);
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Lightning layer authored — strikes channel only");
        return p;
    }

    /// <summary>The Storm MUTATOR's weather: hard rain, dark light, gusts and
    /// lightning, composed from the layers. Stacks over whatever the map drew,
    /// arrives with the wave, leaves with it.</summary>
    private static WeatherPreset AuthorStormLayer(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(StormLayerPath, log);
        p.overrideAmbient = true;
        p.ambientColor = new Color(0.26f, 0.28f, 0.34f, 1f);
        p.overrideSun = true;
        p.sunTemperatureKelvin = 8500f;
        p.sunFilter = new Color(0.85f, 0.88f, 0.98f, 1f);
        p.sunIntensityMult = 0.6f;
        p.sunShadowStrengthMult = 0.5f;
        p.overrideFog = true;
        p.fogColor = new Color(0.35f, 0.38f, 0.45f, 1f);
        p.fogDensity = 0.006f;
        p.precipitation = WeatherPreset.Precipitation.Rain;
        p.precipitationRate = 340f;
        p.fallSpeed = 17f;
        p.particleSize = 0.02f;
        p.streakLength = 22f;
        p.particleColor = new Color(0.75f, 0.82f, 0.95f, 0.4f);
        p.groundWetness = 0.55f;
        p.surfaceChangeSeconds = 6f;   // a storm arrives fast
        p.windDirection = new Vector3(1f, 0.05f, -0.4f);
        p.windStrength = 9f;
        p.gustStrength = 0.5f;
        p.gustPeriodSeconds = 5f;
        p.lightningStrikesPerMinute = 6f;
        p.lightningIntensity = 4f;
        p.ambientVolume = 0.45f;
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Storm layer authored — linked to the Storm wave mutator");
        return p;
    }

    /// <summary>The Blackout MUTATOR's weather: the light itself fails. No
    /// particles — darkness is the phenomenon.</summary>
    private static WeatherPreset AuthorBlackoutLayer(StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(BlackoutLayerPath, log);
        p.overrideAmbient = true;
        p.ambientColor = new Color(0.10f, 0.11f, 0.16f, 1f);
        p.overrideSun = true;
        p.sunTemperatureKelvin = 9500f;
        p.sunFilter = new Color(0.7f, 0.75f, 0.9f, 1f);
        p.sunIntensityMult = 0.35f;
        p.sunShadowStrengthMult = 0.8f;
        p.overrideFog = true;
        p.fogColor = new Color(0.08f, 0.09f, 0.13f, 1f);
        p.fogDensity = 0.0045f;
        p.precipitation = WeatherPreset.Precipitation.None;
        p.windStrength = 0f;
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Blackout layer authored — linked to the Blackout wave mutator");
        return p;
    }

    /// <summary>Sandstorm: the dust lane at full force, with the surface FILM
    /// in a warm sand colour — the same lane snow uses, different colour, which
    /// is why no new shader work was needed. Gusts via the shared layer.</summary>
    private static WeatherPreset AuthorSandstorm(WeatherPreset gustLayer, StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(SandstormPath, log);
        p.overrideAmbient = true;
        p.ambientColor = new Color(0.42f, 0.34f, 0.22f, 1f);
        p.overrideSun = true;
        p.sunTemperatureKelvin = 4300f;
        p.sunFilter = new Color(1f, 0.82f, 0.55f, 1f);
        p.sunIntensityMult = 0.7f;
        p.sunShadowStrengthMult = 0.5f;
        p.overrideFog = true;
        p.fogColor = new Color(0.62f, 0.50f, 0.34f, 1f);
        p.fogDensity = 0.009f;
        p.precipitation = WeatherPreset.Precipitation.Dust;
        p.precipitationRate = 420f;
        p.fallSpeed = 1.2f;
        p.particleSize = 0.08f;
        p.particleColor = new Color(0.80f, 0.66f, 0.46f, 0.22f);
        p.groundSnow = 0.4f;                                   // the FILM lane…
        p.snowColor = new Color(0.84f, 0.70f, 0.50f, 1f);      // …in sand, not snow
        p.groundWetness = 0f;
        p.surfaceChangeSeconds = 15f;
        p.windDirection = new Vector3(1f, 0.02f, -0.25f);
        p.windStrength = 8f;
        p.ambientVolume = 0.5f;
        p.layers = new[] { gustLayer };
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] Sandstorm authored — dust + warm surface film + gust layer");
        return p;
    }

    /// <summary>Heavy snow storm — the composition worked example: its own
    /// heavy snowfall and deep film, gusts from the shared layer.</summary>
    private static WeatherPreset AuthorHeavySnowStorm(WeatherPreset gustLayer, StringBuilder log)
    {
        WeatherPreset p = LoadOrCreate(HeavySnowStormPath, log);
        p.overrideAmbient = true;
        p.ambientColor = new Color(0.40f, 0.44f, 0.53f, 1f);
        p.overrideSun = true;
        p.sunTemperatureKelvin = 8200f;
        p.sunFilter = new Color(0.92f, 0.95f, 1f, 1f);
        p.sunIntensityMult = 0.7f;
        p.sunShadowStrengthMult = 0.45f;
        p.overrideFog = true;
        p.fogColor = new Color(0.70f, 0.74f, 0.81f, 1f);
        p.fogDensity = 0.008f;
        p.precipitation = WeatherPreset.Precipitation.Snow;
        p.precipitationRate = 320f;
        p.fallSpeed = 1.6f;
        p.particleSize = 0.11f;
        p.particleColor = new Color(0.97f, 0.98f, 1f, 0.6f);
        p.groundSnow = 0.95f;
        p.groundWetness = 0.2f;
        p.snowColor = new Color(0.92f, 0.94f, 0.98f, 1f);
        p.surfaceChangeSeconds = 18f;
        p.windDirection = new Vector3(0.6f, -0.05f, -0.25f);
        p.windStrength = 4f;
        p.ambientVolume = 0.3f;
        p.layers = new[] { gustLayer };
        EditorUtility.SetDirty(p);
        log.AppendLine("[ok] HeavySnowStorm authored — composed with the gust layer");
        return p;
    }

    /// <summary>Wire the mutator→weather links: Storm waves look like storms,
    /// Blackout waves go dark. Idempotent; existing links are replaced.</summary>
    private static void WireMutatorLinks(WeatherPreset storm, WeatherPreset blackout, StringBuilder log)
    {
        var applier = Object.FindFirstObjectByType<WeatherApplier>();
        if (applier == null)
        {
            log.AppendLine("[warn] no WeatherApplier to wire mutator links onto");
            return;
        }

        var so = new SerializedObject(applier);
        SerializedProperty links = so.FindProperty("mutatorLinks");
        if (links == null)
        {
            log.AppendLine("[warn] WeatherApplier has no mutatorLinks field — is the code compiled?");
            return;
        }
        links.arraySize = 2;
        SerializedProperty l0 = links.GetArrayElementAtIndex(0);
        l0.FindPropertyRelative("mutator").intValue = (int)WaveMutator.Storm;
        l0.FindPropertyRelative("layer").objectReferenceValue = storm;
        SerializedProperty l1 = links.GetArrayElementAtIndex(1);
        l1.FindPropertyRelative("mutator").intValue = (int)WaveMutator.Blackout;
        l1.FindPropertyRelative("layer").objectReferenceValue = blackout;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(applier);
        log.AppendLine("[ok] mutator links wired: Storm → storm layer, Blackout → blackout layer");
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
