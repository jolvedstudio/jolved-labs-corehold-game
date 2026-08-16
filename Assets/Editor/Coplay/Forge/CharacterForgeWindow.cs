using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor.Forge
{
    /// <summary>
    /// The Character Forge surface (plan v2 §B): pick/create a recipe, confirm
    /// what the auto-detection sees, Forge. The window owns no build logic —
    /// <see cref="CharacterForge.Build"/> is the engine, and the transcript it
    /// returns is the deliverable (model row, dependency audit, next steps).
    /// </summary>
    public class CharacterForgeWindow : EditorWindow
    {
        private CharacterRecipe _recipe;
        private Vector2 _scroll;
        private string _transcript = "";
        private string[] _detectedMuzzles;
        private GameObject _detectedFor;

        [MenuItem("Tools/COREHOLD/Characters/Character Forge", false, 1)]
        public static void Open()
        {
            var w = GetWindow<CharacterForgeWindow>("Character Forge");
            w.minSize = new Vector2(460, 420);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            using (new EditorGUILayout.HorizontalScope())
            {
                _recipe = (CharacterRecipe)EditorGUILayout.ObjectField("Recipe", _recipe, typeof(CharacterRecipe), false);
                if (GUILayout.Button("Create New", GUILayout.Width(90)))
                {
                    string path = EditorUtility.SaveFilePanelInProject(
                        "New Character Recipe", "Recipe_", "asset",
                        "Editor-side recipe (references vendor art; never ships).",
                        "Assets/_COREHOLD/Data");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var r = CreateInstance<CharacterRecipe>();
                        AssetDatabase.CreateAsset(r, path);
                        AssetDatabase.SaveAssets();
                        _recipe = r;
                    }
                }
            }

            if (_recipe == null)
            {
                EditorGUILayout.HelpBox(
                    "A recipe = template definition + assembly hints. Pick the closest existing " +
                    "definition as the template (its stats, audio and enrage tuning carry over), " +
                    "drop in the developer-supplied prefab, forge.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            // The recipe asset itself is edited in the Inspector; the window
            // shows the derived/diagnostic view.
            var so = new SerializedObject(_recipe);
            so.Update();
            var prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "m_Script") continue;
                EditorGUILayout.PropertyField(prop, true);
            }
            so.ApplyModifiedProperties();

            DrawMuzzleProbe();

            EditorGUILayout.Space(10);
            GUI.enabled = _recipe.sourcePrefab != null;
            if (GUILayout.Button("FORGE", GUILayout.Height(32)))
            {
                _transcript = CharacterForge.Build(_recipe);
                // Ping what got made, if anything did.
                string firstPath = _transcript.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("Prefab ") || l.StartsWith("Chassis "))
                    .Select(l => l.Split(' ')[1])
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(firstPath))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<Object>(firstPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_transcript))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Transcript", EditorStyles.boldLabel);
                    if (GUILayout.Button("Copy", GUILayout.Width(60)))
                        EditorGUIUtility.systemCopyBuffer = _transcript;
                }
                EditorGUILayout.TextArea(_transcript, GUILayout.MinHeight(160));
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Auto-detect (plan v2 §B): list what the muzzle hints resolve to on
        /// the actual source hierarchy, so the developer confirms rather than
        /// types names blind — plus likely candidates the hints miss.
        /// </summary>
        private void DrawMuzzleProbe()
        {
            if (_recipe.sourcePrefab == null || !_recipe.IsEnemy) return;

            if (_detectedFor != _recipe.sourcePrefab)
            {
                _detectedFor = _recipe.sourcePrefab;
                var names = new List<string>();
                foreach (var t in _recipe.sourcePrefab.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name;
                    string low = n.ToLowerInvariant();
                    if (low.Contains("muzzle") || low.Contains("barrel") || low.Contains("gun") ||
                        low.Contains("cannon") || low.Contains("weapon"))
                        if (!names.Contains(n)) names.Add(n);
                }
                _detectedMuzzles = names.ToArray();
            }

            if (_detectedMuzzles == null || _detectedMuzzles.Length == 0)
            {
                EditorGUILayout.HelpBox("No weapon-ish child names found in the source — an armed unit will get " +
                                        "a generated forward muzzle (fine for most bodies).", MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox("Weapon-ish children in the source (add the right ones to Muzzle Marker Names):\n  " +
                                    string.Join(", ", _detectedMuzzles.Take(12)), MessageType.None);
        }
    }
}
