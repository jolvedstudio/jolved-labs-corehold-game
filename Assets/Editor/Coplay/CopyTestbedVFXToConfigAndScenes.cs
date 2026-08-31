using System.Collections.Generic;
using System.Text;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-shot editor utility: copy the tuned VFXDirector wiring from the
/// CombatVFX_Testbed scene into the shared VFXDirectorConfig asset, then push that
/// config onto the VFXDirector in every currently-open scene. Uses the existing
/// VFXConfigIO bridge so the exact serialized fields (effects[], tracer settings)
/// are transferred faithfully.
/// </summary>
public static class CopyTestbedVFXToConfigAndScenes
{
    private const string TestbedScenePath = "Assets/_COREHOLD/Scenes/CombatVFX_Testbed.unity";

    public static string Execute()
    {
        var log = new StringBuilder();

        // --- 1. Snapshot the testbed director into the config asset -----------------
        bool testbedWasOpen = false;
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).path == TestbedScenePath)
                testbedWasOpen = true;

        Scene testbedScene = testbedWasOpen
            ? SceneManager.GetSceneByPath(TestbedScenePath)
            : EditorSceneManager.OpenScene(TestbedScenePath, OpenSceneMode.Additive);

        VFXDirector testbedDirector = FindDirectorInScene(testbedScene);
        if (testbedDirector == null)
        {
            if (!testbedWasOpen)
                EditorSceneManager.CloseScene(testbedScene, true);
            return "ERROR: No VFXDirector found in CombatVFX_Testbed.";
        }

        VFXDirectorConfig config = VFXConfigIO.WriteFromDirector(testbedDirector, log);
        if (config == null)
        {
            if (!testbedWasOpen)
                EditorSceneManager.CloseScene(testbedScene, true);
            return "ERROR: Failed to write VFXDirectorConfig from the testbed director.";
        }

        // Close the testbed again only if we opened it ourselves.
        if (!testbedWasOpen)
            EditorSceneManager.CloseScene(testbedScene, true);

        AssetDatabase.SaveAssets();

        // --- 2. Apply the config onto every open scene's director -------------------
        int applied = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded || scene.path == TestbedScenePath)
                continue;

            VFXDirector director = FindDirectorInScene(scene);
            if (director == null)
            {
                log.AppendLine($"• {scene.name}: no VFXDirector — skipped.");
                continue;
            }

            var missing = new List<string>();
            if (VFXConfigIO.ApplyToDirector(director, config, missing))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                applied++;
                log.AppendLine($"• {scene.name}: applied {config.effects.Length} slot(s)" +
                               (missing.Count > 0 ? $" (unassigned: {string.Join(", ", missing)})" : "") + ".");
            }
        }

        if (applied > 0)
            EditorSceneManager.SaveOpenScenes();

        return $"Copied testbed VFX wiring -> config, then applied to {applied} open scene(s).\n" + log;
    }

    private static VFXDirector FindDirectorInScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (var root in scene.GetRootGameObjects())
        {
            var director = root.GetComponentInChildren<VFXDirector>(true);
            if (director != null)
                return director;
        }
        return null;
    }
}
