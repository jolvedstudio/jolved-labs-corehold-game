using System.Collections.Generic;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup for the COREHOLD VFXDirector (GDD §11). Creates (or updates) a
/// VFXDirector GameObject in the Game scene and assigns the Cartoon FX Remaster
/// prefabs (fifteen slots as of R22) to its serialized effect slots. Run once;
/// safe to re-run — and MUST be re-run on scenes built before a slot was added,
/// because a scene's serialized array keeps its old length until this rewrites it.
/// </summary>
public static class SetupVFXDirector
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";

    private const string CfxrRoot = "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/";

    // Logical effect -> Cartoon FX Remaster prefab CANDIDATES (GDD §11). The first
    // path that loads wins. Every list ends on a path known to exist in the kit,
    // so an earlier, nicer-looking candidate that this kit edition lacks degrades
    // to a working effect instead of an empty slot.
    private static readonly (VFXDirector.Effect id, string[] paths, int prewarm)[] Map =
    {
        (VFXDirector.Effect.MuzzleKinetic,   new[] { CfxrRoot + "Misc/CFXR Flash.prefab" }, 4),
        (VFXDirector.Effect.MuzzleEnergy,    new[] { CfxrRoot + "Electric/CFXR3 Hit Electric C (Air).prefab" }, 4),
        (VFXDirector.Effect.MuzzleExplosive, new[] { CfxrRoot + "Impacts/CFXR2 Ground Hit.prefab" }, 4),
        (VFXDirector.Effect.ImpactSpark,     new[] { CfxrRoot + "Impacts/CFXR Hit D 3D (Yellow).prefab" }, 8),
        (VFXDirector.Effect.ExplosionSmall,  new[] { CfxrRoot + "Explosions/CFXR Explosion 1.prefab" }, 4),
        (VFXDirector.Effect.ExplosionLarge,  new[] { CfxrRoot + "Explosions/CFXR3 Fire Explosion B.prefab" }, 4),
        (VFXDirector.Effect.EnemyDeath,      new[] { CfxrRoot + "Eerie/CFXR2 WW Enemy Explosion.prefab" }, 6),
        (VFXDirector.Effect.CoreHit,         new[] { CfxrRoot + "Impacts/CFXR Impact Glowing HDR (Blue).prefab" }, 2),
        (VFXDirector.Effect.BuildPuff,       new[] { CfxrRoot + "Misc/CFXR Magic Poof.prefab" }, 2),
        // Status effects (R18). Stun = electric crackle (matches the R19 EM burst
        // fiction); slow = a cold blue glow. Distinct silhouettes from each other
        // and from the muzzle/impact effects above.
        (VFXDirector.Effect.Stun,            new[]
        {
            CfxrRoot + "Electric/CFXR3 Hit Electric A (Air).prefab",
            CfxrRoot + "Electric/CFXR3 Hit Electric B (Air).prefab",
            CfxrRoot + "Electric/CFXR3 Hit Electric C (Air).prefab",
        }, 6),
        (VFXDirector.Effect.Slow,            new[]
        {
            CfxrRoot + "Ice/CFXR3 Hit Ice A (Air).prefab",
            CfxrRoot + "Ice/CFXR3 Hit Ice.prefab",
            CfxrRoot + "Impacts/CFXR Impact Glowing HDR (Blue).prefab",
        }, 6),
        // Strike Wing EM burst (R19) — a big electric pop; the director plays it
        // scaled up to read at the 6 m ability radius.
        (VFXDirector.Effect.StrikeWingBurst, new[]
        {
            CfxrRoot + "Electric/CFXR3 Hit Electric B (Air).prefab",
            CfxrRoot + "Electric/CFXR3 Hit Electric C (Air).prefab",
        }, 2),
        // Counter-readability impacts (R22 — GDD §7.1 "visible counter" pillar).
        // Strong = a bright red hit burst (super-effective reads as a heavy strike);
        // Weak = a small yellow/misc spark that reads as a deflection;
        // ShieldHit = the glowing blue HDR impact that reads as an energy shield ripple.
        (VFXDirector.Effect.ImpactStrong, new[]
        {
            CfxrRoot + "Impacts/CFXR Hit A (Red).prefab",
            CfxrRoot + "Impacts/CFXR Hit D 3D (Yellow).prefab",
        }, 8),
        (VFXDirector.Effect.ImpactWeak, new[]
        {
            CfxrRoot + "Misc/CFXR3 Hit Misc A.prefab",
            CfxrRoot + "Impacts/CFXR Hit D 3D (Yellow).prefab",
        }, 8),
        (VFXDirector.Effect.ShieldHit, new[]
        {
            CfxrRoot + "Impacts/CFXR Impact Glowing HDR (Blue).prefab",
            CfxrRoot + "Electric/CFXR3 Hit Electric C (Air).prefab",
        }, 6),
    };

    // ---- Tracer configuration -----------------------------------------------
    // Per FACTION now: friendly (tower) fire is a cool blue, hostile (enemy) fire
    // a hot red — each with its own width/glow. Moderate HDR so the hue survives
    // ACES tonemapping and the alpha-blended material keeps the colour over the
    // bright desert ground (a hot additive value washed enemy red to white).
    private const int TracerPrewarm = 8;
    private const float FriendlyTracerWidth = 0.08f;
    private const float FriendlyTracerGlow = 1f;
    private static readonly Color FriendlyTracerColor = new Color(0.05f, 0.35f, 3.0f, 1f);
    private const float HostileTracerWidth = 0.08f;
    private const float HostileTracerGlow = 1f;
    private static readonly Color HostileTracerColor = new Color(3.0f, 0.05f, 0.03f, 1f);

    [MenuItem("Tools/COREHOLD/Scene Setup/VFX Director", false, 42)]
    public static void Setup()
    {
        // Menu use: hop to the shipped scene if the human is elsewhere.
        // Pipeline use: NEVER — opening a scene here would replace the scene
        // being generated with Game.unity and build the map into it.
        Scene scene = SceneManager.GetActiveScene();
        if (!GenerationDriven.Active && scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // Find or create the director GameObject.
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
        {
            var go = new GameObject("VFXDirector");
            director = go.AddComponent<VFXDirector>();
            Undo.RegisterCreatedObjectUndo(go, "Create VFXDirector");
        }

        var missing = new List<string>();

        // DATA-FIRST: if the shared VFXDirectorConfig asset exists (written by the
        // testbed's "Apply" button when a human tunes the effects), it is the source
        // of truth — the generator and every scene setup use it, so a change made
        // once in the testbed flows into every future level. The hard-coded Map
        // below is only the FALLBACK for a project that has not created the asset yet
        // (and the seed the "Create/Refresh VFX Config from code map" tool writes).
        var config = VFXConfigIO.Load();
        if (config != null && config.effects != null && config.effects.Length > 0)
        {
            VFXConfigIO.ApplyToDirector(director, config, missing);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!GenerationDriven.Active)
                EditorSceneManager.SaveScene(scene);

            if (missing.Count > 0)
                Debug.LogError("[COREHOLD] VFXDirector setup (from config asset): missing prefabs:\n- " +
                               string.Join("\n- ", missing));
            else
                Debug.Log($"[COREHOLD] VFXDirector setup complete from {VFXConfigIO.ConfigPath}: " +
                          $"{config.effects.Length} effect prefabs assigned.");
            return;
        }

        var so = new SerializedObject(director);
        SerializedProperty effects = so.FindProperty("effects");
        effects.arraySize = Map.Length;

        for (int i = 0; i < Map.Length; i++)
        {
            var entry = Map[i];
            GameObject prefab = null;
            foreach (string path in entry.paths)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    break;
            }
            if (prefab == null)
                missing.Add($"{entry.id}: none of [{string.Join(", ", entry.paths)}]");

            SerializedProperty element = effects.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("id").enumValueIndex = (int)entry.id;
            element.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            element.FindPropertyRelative("prewarm").intValue = entry.prewarm;
        }

        // The TRACER is the Autocannon/Arc Node firing effect, and the tool used
        // to write only the effects array — so these three came from the class
        // defaults in any scene the tool built, while the shipped scene carried
        // hand-tuned values. Result: generated maps fired wide warm-orange
        // tracers where the shipped map fires thin cyan ones. A setup tool that
        // owns PART of a component leaves the rest free to drift, so it now
        // writes the whole configuration.
        WriteInt(so, "tracerPrewarm", TracerPrewarm);
        WriteFloat(so, "friendlyTracerWidth", FriendlyTracerWidth);
        WriteFloat(so, "friendlyTracerGlow", FriendlyTracerGlow);
        WriteColor(so, "friendlyTracerColor", FriendlyTracerColor);
        WriteFloat(so, "hostileTracerWidth", HostileTracerWidth);
        WriteFloat(so, "hostileTracerGlow", HostileTracerGlow);
        WriteColor(so, "hostileTracerColor", HostileTracerColor);

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
        EditorSceneManager.MarkSceneDirty(scene);
        // The pipeline owns saving (its final stage). Saving here would fire a
        // modal Save dialog on the untitled scene being generated.
        if (!GenerationDriven.Active)
            EditorSceneManager.SaveScene(scene);

        if (missing.Count > 0)
            Debug.LogError("[COREHOLD] VFXDirector setup: missing prefabs:\n- " + string.Join("\n- ", missing));
        else
            Debug.Log($"[COREHOLD] VFXDirector setup complete (code map): {Map.Length} effect prefabs assigned, " +
                      $"friendly {FriendlyTracerColor} / hostile {HostileTracerColor} tracers.");
    }

    /// <summary>
    /// Seed the shared <see cref="VFXDirectorConfig"/> asset from the hard-coded
    /// <see cref="Map"/> above. Gives the data-first path a starting asset without a
    /// human first tuning a scene; after this, edits flow through the testbed Apply
    /// button. Safe to re-run — overwrites the asset from the code map.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Scene Setup/Create or Refresh VFX Config from code map", false, 43)]
    public static void CreateOrRefreshConfigFromCodeMap()
    {
        var config = VFXConfigIO.LoadOrCreate();
        var entries = new List<VFXDirectorConfig.Entry>(Map.Length);
        var missing = new List<string>();

        foreach (var entry in Map)
        {
            GameObject prefab = null;
            foreach (string path in entry.paths)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    break;
            }
            if (prefab == null)
                missing.Add(entry.id.ToString());

            entries.Add(new VFXDirectorConfig.Entry
            {
                id = entry.id,
                prefab = prefab,
                prewarm = entry.prewarm,
            });
        }

        config.effects = entries.ToArray();
        config.tracerPrewarm = TracerPrewarm;
        config.friendlyTracerWidth = FriendlyTracerWidth;
        config.friendlyTracerGlow = FriendlyTracerGlow;
        config.friendlyTracerColor = FriendlyTracerColor;
        config.hostileTracerWidth = HostileTracerWidth;
        config.hostileTracerGlow = HostileTracerGlow;
        config.hostileTracerColor = HostileTracerColor;
        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();

        if (missing.Count > 0)
            Debug.LogWarning($"[COREHOLD] VFX config seeded with {missing.Count} missing prefab(s): " +
                             string.Join(", ", missing));
        else
            Debug.Log($"[COREHOLD] VFX config seeded from code map at {VFXConfigIO.ConfigPath} " +
                      $"({config.effects.Length} slots).");
    }

    // ------------------------------------------------------------------ helpers

    private static void WriteFloat(SerializedObject so, string field, float value)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p != null) p.floatValue = value;
        else Debug.LogWarning($"[COREHOLD] VFXDirector has no '{field}' field — setup contract drifted.");
    }

    private static void WriteInt(SerializedObject so, string field, int value)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p != null) p.intValue = value;
        else Debug.LogWarning($"[COREHOLD] VFXDirector has no '{field}' field — setup contract drifted.");
    }

    private static void WriteColor(SerializedObject so, string field, Color value)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p != null) p.colorValue = value;
        else Debug.LogWarning($"[COREHOLD] VFXDirector has no '{field}' field — setup contract drifted.");
    }
}
