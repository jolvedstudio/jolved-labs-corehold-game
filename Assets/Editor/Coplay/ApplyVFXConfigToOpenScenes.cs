using System.Collections.Generic;
using System.Text;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Push the VFXDirectorConfig ASSET — as currently edited — onto the
/// VFXDirector of every open scene. The missing half of
/// <c>CopyTestbedVFXToConfigAndScenes</c>, which always overwrites the config
/// from the testbed first.
///
/// Why this exists: the director builds its pools in Awake from its OWN baked
/// effects array in the scene; it never reads the config asset at runtime.
/// Editing the asset therefore changes nothing in an already-stamped scene
/// until re-applied — and editing the scene component during Play Mode
/// neither rebuilds the pools (built once in Awake) nor survives exiting
/// play. The working loop is: edit the config asset in EDIT mode → run this
/// → save → play.
/// </summary>
public static class ApplyVFXConfigToOpenScenes
{
    [MenuItem("Tools/COREHOLD/VFX/Apply VFX Config To Open Scene(s)", false, 61)]
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("[VFX] Apply the config in EDIT mode — play-mode changes do not rebuild " +
                             "pools and revert on exit.");
            return;
        }

        var config = AssetDatabase.LoadAssetAtPath<VFXDirectorConfig>(VFXConfigIO.ConfigPath);
        if (config == null)
        {
            Debug.LogError($"[VFX] No config asset at {VFXConfigIO.ConfigPath}.");
            return;
        }

        var log = new StringBuilder();
        log.AppendLine("Apply VFX config → open scene(s):");
        var missing = new List<string>();
        int applied = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var director = root.GetComponentInChildren<VFXDirector>(true);
                if (director == null)
                    continue;
                if (VFXConfigIO.ApplyToDirector(director, config, missing))
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    applied++;
                    log.AppendLine($"  {scene.name}: director updated ({config.effects.Length} slot(s)).");
                }
                break;   // one director per scene
            }
        }

        foreach (string m in missing)
            log.AppendLine($"  warn: {m}");
        log.AppendLine(applied > 0
            ? "SAVE the scene(s), then play — pools rebuild in Awake from the scene's baked array."
            : "No VFXDirector found in the open scene(s).");
        Debug.Log(log.ToString());
    }
}
