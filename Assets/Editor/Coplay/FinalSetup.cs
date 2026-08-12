using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Corehold.Enemies;
using Corehold.Systems;

/// <summary>
/// One-shot setup: strengthen enemy separation on all enemy prefabs, and add a
/// CoreDestruction component to the Shield Generator so a lost run ends with a big
/// explosion. Run in edit mode.
/// </summary>
public static class FinalSetup
{
    private static readonly string[] EnemyPrefabs =
    {
        "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Drone.prefab",
    };

    public static string Execute()
    {
        var sb = new StringBuilder();

        foreach (var path in EnemyPrefabs)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { sb.AppendLine($"MISS {path}"); continue; }

            var mover = root.GetComponent<EnemyMover>();
            if (mover != null)
            {
                var so = new SerializedObject(mover);
                var sepProp = so.FindProperty("separation");
                if (sepProp != null) sepProp.boolValue = true;
                // Lane + queue model fields (older separationRadius/Strength removed).
                var laneProp = so.FindProperty("maxLaneOffset");
                if (laneProp != null) laneProp.floatValue = 1.8f;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
                sb.AppendLine($"separation OK {path}");
            }
            PrefabUtility.UnloadPrefabContents(root);
        }

        // Add CoreDestruction to the Shield Generator in the scene.
        var gen = GameObject.Find("RefineryLevel/Core_Blockout/Core_ShieldGenerator");
        if (gen == null)
        {
            // Search loosely.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                if (t.name.Contains("ShieldGenerator")) { gen = t.gameObject; break; }
        }
        if (gen != null)
        {
            var cd = gen.GetComponent<CoreDestruction>();
            if (cd == null) cd = gen.AddComponent<CoreDestruction>();
            sb.AppendLine($"CoreDestruction on {gen.name}");
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
        else sb.AppendLine("Shield generator not found in scene.");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorSceneManager.SaveOpenScenes();
        return sb.ToString();
    }
}
