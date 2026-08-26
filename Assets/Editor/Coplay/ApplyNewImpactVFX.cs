using System.Collections.Generic;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-shot: assign the three new counter-readability impact effects (R22) onto the
/// VFXDirector in the CURRENTLY ACTIVE scene, without opening Game.unity. Used to
/// validate the new effects in the generated blueprint scene. Idempotent.
/// </summary>
public static class ApplyNewImpactVFX
{
    private const string CfxrRoot = "Assets/Vendor/JMO Assets/Cartoon FX Remaster/CFXR Prefabs/";

    private static readonly (VFXDirector.Effect id, string[] paths, int prewarm)[] NewEntries =
    {
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

    public static string Execute()
    {
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
            return "ERROR: no VFXDirector in the active scene.";

        var so = new SerializedObject(director);
        SerializedProperty effects = so.FindProperty("effects");

        // Build a lookup of existing entries by enum value.
        var existing = new Dictionary<int, int>();
        for (int i = 0; i < effects.arraySize; i++)
            existing[effects.GetArrayElementAtIndex(i).FindPropertyRelative("id").enumValueIndex] = i;

        var log = new List<string>();
        foreach (var entry in NewEntries)
        {
            GameObject prefab = null;
            foreach (string path in entry.paths)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) break;
            }
            if (prefab == null)
            {
                log.Add($"{entry.id}: MISSING all candidates");
                continue;
            }

            int idx;
            if (existing.TryGetValue((int)entry.id, out idx))
            {
                // already present
            }
            else
            {
                effects.arraySize++;
                idx = effects.arraySize - 1;
            }

            SerializedProperty element = effects.GetArrayElementAtIndex(idx);
            element.FindPropertyRelative("id").enumValueIndex = (int)entry.id;
            element.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            element.FindPropertyRelative("prewarm").intValue = entry.prewarm;
            log.Add($"{entry.id}: {prefab.name}");
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
        EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
        EditorSceneManager.SaveScene(director.gameObject.scene);

        return "Applied new impact VFX to active scene VFXDirector:\n- " + string.Join("\n- ", log);
    }
}
