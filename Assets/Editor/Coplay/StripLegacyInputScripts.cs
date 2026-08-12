using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 cleanup: the Creepy Cat set-dressing ships demo behaviours
    /// (ContainerOpen, and any other MonoBehaviour reading legacy UnityEngine.Input)
    /// that throw an InvalidOperationException every frame under the new Input System,
    /// flooding the console and burning main-thread time. These are decorative door
    /// interactions with no role in COREHOLD, so we strip the components from the
    /// active scene. The container art itself is untouched.
    /// </summary>
    public static class StripLegacyInputScripts
    {
        [MenuItem("Tools/COREHOLD/Strip Legacy-Input Demo Scripts")]
        public static string Run()
        {
            var sb = new StringBuilder();
            int removed = 0;

            var behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var toRemove = new List<MonoBehaviour>();
            foreach (var mb in behaviours)
            {
                if (mb == null) continue;
                var typeName = mb.GetType().FullName ?? "";
                // Target the known Creepy Cat demo behaviours that use legacy Input.
                if (typeName.Contains("creepycat.scifikitvol4.ContainerOpen"))
                    toRemove.Add(mb);
            }

            foreach (var mb in toRemove)
            {
                var owner = mb.gameObject.name;
                Object.DestroyImmediate(mb, true);
                removed++;
                sb.AppendLine($"  Removed {mb?.GetType().Name} from '{owner}'");
            }

            if (removed > 0)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            var msg = $"Stripped {removed} legacy-input demo component(s) from the scene.";
            sb.Insert(0, msg + "\n");
            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }
    }
}
