using UnityEditor;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// Bake a <see cref="UISkin"/> into the OPEN scene, so a skin can be judged
    /// in five seconds instead of a campaign regeneration.
    ///
    /// Skins apply at build time — the Campaign Builder sets the ambient skin
    /// around generation and the scene builders bake it in. That is correct for
    /// shipping and useless for iterating: change an accent, regenerate ten
    /// levels, look. This does exactly what the builder does, to one scene:
    /// set the ambient skin, re-run Build Real UI, clear it.
    ///
    /// Consequence worth knowing: this REBUILDS the scene's UI canvases (the
    /// same thing Build Real UI always does), so hand-edits made directly to
    /// HUD/menu objects in that scene are replaced.
    /// </summary>
    public class ApplyUISkinWindow : EditorWindow
    {
        private UISkin _skin;

        [MenuItem("Tools/COREHOLD/Campaign/Apply UI Skin to Open Scene…", false, 31)]
        public static void Open()
        {
            var w = GetWindow<ApplyUISkinWindow>("Apply UI Skin");
            w.minSize = new Vector2(420, 210);
            w.maxSize = new Vector2(600, 260);
            // Preselect a skin the user is already looking at.
            if (w._skin == null) w._skin = Selection.activeObject as UISkin;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Preview a skin in the open scene", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Rebuilds this scene's UI with the chosen skin — the same bake the Campaign Builder " +
                "does during generation, applied to one scene so you can look at it now.\n\n" +
                "It REPLACES the scene's HUD and menu canvases (Build Real UI always does), and it is a " +
                "preview only: the campaign's own look still comes from its authoring asset's skin at " +
                "generation time.", MessageType.Info);

            _skin = (UISkin)EditorGUILayout.ObjectField("Skin", _skin, typeof(UISkin), false);
            EditorGUILayout.LabelField("Scene", EditorSceneManagerName(), EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_skin != null ? "APPLY SKIN" : "APPLY DEFAULT LOOK", GUILayout.Height(28)))
                    Apply(_skin);
            }
            EditorGUILayout.LabelField(
                _skin != null ? "" : "No skin assigned — this rebuilds the UI with the shipped default look.",
                EditorStyles.miniLabel);
        }

        private static string EditorSceneManagerName()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            return string.IsNullOrEmpty(scene.name) ? "(untitled)" : scene.name;
        }

        private static void Apply(UISkin skin)
        {
            UISkin.Active = skin;
            try
            {
                BuildRealUI.Run();
                Debug.Log($"[UISkin] Applied '{(skin != null ? skin.name : "default look")}' to " +
                          $"'{EditorSceneManagerName()}'. Preview only — a campaign's look comes from its " +
                          "authoring asset's skin when its stages are generated.");
            }
            finally
            {
                UISkin.Active = null;   // ambient state never outlives the bake
            }
        }
    }
}
