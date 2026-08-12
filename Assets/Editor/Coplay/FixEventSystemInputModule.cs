using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 cleanup: the project uses the new Input System (active input handling set to
    /// "Input System Package"), but the scene's EventSystem carries the legacy
    /// StandaloneInputModule, which reads UnityEngine.Input.mousePosition and throws an
    /// InvalidOperationException every frame — flooding the console and breaking UI raycasts.
    /// Swap it for InputSystemUIInputModule so pointer/tap input works on desktop and mobile
    /// browser alike, which is exactly what the single-touch tap-to-build design needs.
    /// </summary>
    public static class FixEventSystemInputModule
    {
        [MenuItem("Tools/COREHOLD/Fix EventSystem Input Module")]
        public static string Run()
        {
            var sb = new StringBuilder();
            int fixedCount = 0;

            foreach (var es in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var go = es.gameObject;

                // Remove any legacy modules.
                foreach (var legacy in go.GetComponents<StandaloneInputModule>())
                {
                    Object.DestroyImmediate(legacy, true);
                    sb.AppendLine($"  Removed StandaloneInputModule from '{go.name}'.");
                }
#pragma warning disable CS0618
                foreach (var touch in go.GetComponents<TouchInputModule>())
                {
                    Object.DestroyImmediate(touch, true);
                    sb.AppendLine($"  Removed TouchInputModule from '{go.name}'.");
                }
#pragma warning restore CS0618

                // Ensure the new-input UI module is present.
                if (go.GetComponent<InputSystemUIInputModule>() == null)
                {
                    go.AddComponent<InputSystemUIInputModule>();
                    sb.AppendLine($"  Added InputSystemUIInputModule to '{go.name}'.");
                }
                fixedCount++;
            }

            if (fixedCount > 0)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            var msg = $"Fixed {fixedCount} EventSystem(s) to use the Input System UI module.";
            sb.Insert(0, msg + "\n");
            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }
    }
}
