using System.Collections.Generic;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup for the COREHOLD VFXDirector (GDD §11). Creates (or updates) a
/// VFXDirector GameObject in the Game scene and assigns the Cartoon FX Remaster
/// prefabs (eleven slots as of R18) to its serialized effect slots. Run once;
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
    };

    // ---- Tracer configuration, as tuned in the shipped Game.unity ----------
    // Read out of the scene, not invented: thin (0.15 m, not 0.35) and an
    // intensely HDR cyan (not the warm orange the class defaulted to).
    private const float TracerWidth = 0.15f;
    private const int TracerPrewarm = 8;
    private static readonly Color DefaultTracerColor = new Color(0f, 207.88327f, 705.2075f, 1f);

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
        WriteFloat(so, "tracerWidth", TracerWidth);
        WriteInt(so, "tracerPrewarm", TracerPrewarm);
        WriteColor(so, "defaultTracerColor", DefaultTracerColor);

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
            Debug.Log($"[COREHOLD] VFXDirector setup complete: {Map.Length} effect prefabs assigned, " +
                      $"tracer width {TracerWidth} colour {DefaultTracerColor}.");
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
