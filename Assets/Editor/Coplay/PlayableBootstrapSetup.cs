using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Corehold.Core;

namespace CoreholdEditor
{
    /// <summary>
    /// Adds the <see cref="GameFlow"/> driver to the scene, so the game is playable
    /// end to end through the real UI (Ticket 36): the run sits on the Title screen,
    /// picking a difficulty begins the Build phase, and the HUD drives every wave.
    /// Idempotent. (The old GameBootstrap IMGUI driver was removed with Ticket 36.)
    /// </summary>
    public static class PlayableBootstrapSetup
    {
        [MenuItem("Tools/COREHOLD/Setup Playable Bootstrap")]
        public static string Run()
        {
            var sb = new StringBuilder();

            var gmGo = GameObject.Find("GameManager");
            if (gmGo == null)
            {
                gmGo = new GameObject("GameManager");
                gmGo.AddComponent<GameManager>();
                sb.AppendLine("Created GameManager.");
            }

            var flow = Object.FindFirstObjectByType<GameFlow>();
            if (flow == null)
            {
                flow = gmGo.AddComponent<GameFlow>();
                sb.AppendLine("Added GameFlow to GameManager.");
            }
            else sb.AppendLine("GameFlow already present.");

            EnsureHardpointLayer(sb);

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }

        static void EnsureHardpointLayer(StringBuilder sb)
        {
            const string layerName = "Hardpoint";
            if (LayerMask.NameToLayer(layerName) >= 0) { sb.AppendLine("Layer 'Hardpoint' exists."); return; }

            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                var el = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(el.stringValue))
                {
                    el.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    sb.AppendLine($"Created layer '{layerName}' at index {i}.");
                    return;
                }
            }
            sb.AppendLine("No free layer slot for 'Hardpoint' — using existing layers.");
        }
    }
}
