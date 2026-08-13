using System.Collections.Generic;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup for the COREHOLD VFXDirector (GDD §11). Creates (or updates) a
/// VFXDirector GameObject in the Game scene and assigns the nine Cartoon FX
/// Remaster prefabs to its serialized effect slots. Run once; safe to re-run.
/// </summary>
public static class SetupVFXDirector
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";

    // Logical effect -> Cartoon FX Remaster prefab (GDD §11).
    private static readonly (VFXDirector.Effect id, string path, int prewarm)[] Map =
    {
        (VFXDirector.Effect.MuzzleKinetic,   "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Misc/CFXR Flash.prefab", 4),
        (VFXDirector.Effect.MuzzleEnergy,    "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Electric/CFXR3 Hit Electric C (Air).prefab", 4),
        (VFXDirector.Effect.MuzzleExplosive, "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Impacts/CFXR2 Ground Hit.prefab", 4),
        (VFXDirector.Effect.ImpactSpark,     "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Impacts/CFXR Hit D 3D (Yellow).prefab", 8),
        (VFXDirector.Effect.ExplosionSmall,  "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Explosions/CFXR Explosion 1.prefab", 4),
        (VFXDirector.Effect.ExplosionLarge,  "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Explosions/CFXR3 Fire Explosion B.prefab", 4),
        (VFXDirector.Effect.EnemyDeath,      "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Eerie/CFXR2 WW Enemy Explosion.prefab", 6),
        (VFXDirector.Effect.CoreHit,         "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Impacts/CFXR Impact Glowing HDR (Blue).prefab", 2),
        (VFXDirector.Effect.BuildPuff,       "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/Misc/CFXR Magic Poof.prefab", 2),
    };

    [MenuItem("Tools/COREHOLD/Scene Setup/VFX Director", false, 42)]
    public static void Setup()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
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
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.path);
            if (prefab == null)
                missing.Add(entry.path);

            SerializedProperty element = effects.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("id").enumValueIndex = (int)entry.id;
            element.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            element.FindPropertyRelative("prewarm").intValue = entry.prewarm;
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        if (missing.Count > 0)
            Debug.LogError("[COREHOLD] VFXDirector setup: missing prefabs:\n- " + string.Join("\n- ", missing));
        else
            Debug.Log($"[COREHOLD] VFXDirector setup complete: {Map.Length} effect prefabs assigned and scene saved.");
    }
}
