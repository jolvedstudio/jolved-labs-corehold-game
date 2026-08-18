using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Fork the OPEN level into a fully independent copy — the "path 2" clone
    /// for hand-edited levels and linear, incremental campaigns.
    ///
    /// A bare scene duplicate is 80% of a clone and the missing 20% is the
    /// shared-asset trap: the copy's WaveManager still points at the SAME
    /// LevelDefinition (edit the clone's difficulty, retune the original), and
    /// that definition's waves may be the SHARED shipped tables (edit one wave,
    /// retune every level). This tool does the whole job:
    ///
    ///   scene copy → LevelDefinition clone (new records identity) → wave-table
    ///   deep clone → rewire the copy's WaveManager → Build Settings.
    ///
    /// The result is a HAND-AUTHORED scene, like Game.unity: the generator will
    /// never touch or overwrite it, whatever seed it came from. Cloned data
    /// lands in committed folders (Data/Levels/Cloned/<name>/), never in the
    /// git-ignored Generated folders.
    /// </summary>
    public static class CloneLevel
    {
        private const string ClonedDataRoot = "Assets/_COREHOLD/Data/Levels/Cloned";

        [MenuItem("Tools/COREHOLD/Level/Clone Level (fork the open scene)…", false, 64)]
        public static void Run()
        {
            // ---- validate the source: the OPEN scene, playable, with rules ----
            var scene = EditorSceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                EditorUtility.DisplayDialog("Clone Level",
                    "The open scene has never been saved — save it first, then clone.", "OK");
                return;
            }

            var wm = Object.FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                EditorUtility.DisplayDialog("Clone Level",
                    $"'{scene.name}' has no WaveManager — this clones playable LEVELS. " +
                    "Open the level you want to fork, then re-run.", "OK");
                return;
            }

            var wmSo = new SerializedObject(wm);
            var sourceDef = wmSo.FindProperty("level").objectReferenceValue as LevelDefinition;
            if (sourceDef == null)
            {
                EditorUtility.DisplayDialog("Clone Level",
                    $"'{scene.name}' has a WaveManager but no wired LevelDefinition — " +
                    "nothing to clone the rules from.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return; // the copy must capture what the user sees, not a stale file

            // ---- ask where the fork lives ----
            string sourceDir = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/');
            string suggestedDir = sourceDir.Contains("/Scenes/Generated")
                ? "Assets/_COREHOLD/Scenes"    // forks are hand-authored; never the git-ignored scratch folder
                : sourceDir;
            string newScenePath = EditorUtility.SaveFilePanelInProject(
                "Clone Level", scene.name + "_Fork", "unity",
                "Name the fork. It becomes a hand-authored scene — the generator will never overwrite it.",
                suggestedDir);
            if (string.IsNullOrEmpty(newScenePath))
                return;
            if (newScenePath == scene.path)
            {
                EditorUtility.DisplayDialog("Clone Level", "That IS the source scene — pick a new name.", "OK");
                return;
            }

            var log = new StringBuilder();
            string cloneName = System.IO.Path.GetFileNameWithoutExtension(newScenePath);

            // ---- 1. scene copy ----
            AssetDatabase.DeleteAsset(newScenePath);
            if (!AssetDatabase.CopyAsset(scene.path, newScenePath))
            {
                EditorUtility.DisplayDialog("Clone Level", $"CopyAsset refused {newScenePath}.", "OK");
                return;
            }
            log.AppendLine($"Scene   {newScenePath}");

            // ---- 2. definition clone — its NAME is the records identity, so the
            //         fork gets fresh personal bests instead of sharing them ----
            string dataDir = $"{ClonedDataRoot}/{cloneName}";
            EnsureFolder(dataDir);
            string defPath = $"{dataDir}/Level_{cloneName}.asset";
            AssetDatabase.DeleteAsset(defPath);
            var defClone = Object.Instantiate(sourceDef);
            defClone.name = $"Level_{cloneName}";
            AssetDatabase.CreateAsset(defClone, defPath);
            log.AppendLine($"Rules   {defPath} (cloned from {sourceDef.name})");

            // ---- 3. wave-table deep clone — the shared-asset trap, again ----
            var defSo = new SerializedObject(defClone);
            var waves = defSo.FindProperty("waves");
            int cloned = 0;
            for (int i = 0; i < waves.arraySize; i++)
            {
                var el = waves.GetArrayElementAtIndex(i);
                var shared = el.objectReferenceValue as WaveDefinition;
                if (shared == null) continue;
                var copy = Object.Instantiate(shared);
                copy.name = shared.name;
                AssetDatabase.CreateAsset(copy, $"{dataDir}/{shared.name}.asset");
                el.objectReferenceValue = copy;
                cloned++;
            }
            defSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(defClone);
            log.AppendLine($"Waves   {cloned} table(s) deep-cloned into {dataDir} — edits stay in the fork");

            // ---- 4. rewire the copy's WaveManager to the cloned rules ----
            var cloneScene = EditorSceneManager.OpenScene(newScenePath, OpenSceneMode.Single);
            var cloneWm = Object.FindFirstObjectByType<WaveManager>();
            if (cloneWm == null)
            {
                // Should be impossible (we copied a scene that had one) — but a
                // half-wired fork must not survive looking finished.
                AssetDatabase.DeleteAsset(newScenePath);
                AssetDatabase.DeleteAsset(dataDir);
                EditorUtility.DisplayDialog("Clone Level",
                    "The copied scene lost its WaveManager (?) — clone rolled back.", "OK");
                return;
            }
            var cloneWmSo = new SerializedObject(cloneWm);
            cloneWmSo.FindProperty("level").objectReferenceValue = defClone;
            cloneWmSo.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(cloneScene);
            EditorSceneManager.SaveScene(cloneScene);
            log.AppendLine("Wired   the fork's WaveManager → cloned rules");

            // ---- 5. Build Settings, so Retry and campaign loads accept it ----
            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == newScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(newScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
                log.AppendLine("Build   registered in Build Settings");
            }

            AssetDatabase.SaveAssets();

            log.AppendLine("\nThe fork is HAND-AUTHORED from here on: the generator never touches it, and " +
                           "a Campaign Builder stage can point straight at its scene path. Its records are " +
                           $"its own (identity '{defClone.name}').");
            Debug.Log($"[CloneLevel] Forked '{scene.name}' → '{cloneName}'.\n{log}");
            EditorUtility.DisplayDialog("Clone Level",
                $"Forked '{scene.name}' → '{cloneName}'.\n\nThe fork is open now; details in the Console.", "OK");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
