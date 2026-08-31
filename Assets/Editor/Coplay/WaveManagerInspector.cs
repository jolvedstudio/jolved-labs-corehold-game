using System.Collections.Generic;
using System.Linq;
using Corehold.Core;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// The WaveManager's mutator library, and the tools that fill it and spend it.
    ///
    /// WHAT THE LIBRARY IS FOR, since the field alone does not say: it is this
    /// LEVEL'S ROSTER of mutators — which of the project's mutators this level
    /// is willing to use. Nothing in gameplay reads it. Two things do:
    ///
    ///   • the debug console's T key cycles it, so a mutator missing from the
    ///     library cannot be force-tested in play mode;
    ///   • the audit checks every wave's pool against it, and reports a wave
    ///     referencing a mutator the level never registered.
    ///
    /// So it is an authoring construct, and that is exactly what makes it the
    /// right middle rung of the cascade: the project has every mutator, the
    /// level picks the ones that belong in its world, and each wave draws from
    /// the level's set. Filling a wave pool by hand from the project-wide list
    /// is how a desert level ends up rolling a blizzard.
    /// </summary>
    [CustomEditor(typeof(WaveManager))]
    public class WaveManagerInspector : Editor
    {
        private static readonly Color Warn = new Color(1f, 0.65f, 0.15f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.3f);

        private int _stampFromWave = 5;
        private int _stampNothingWeight = 2;
        private bool _stampFoldout = true;
        private bool _overviewFoldout = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length > 1)
                return;

            var wm = (WaveManager)target;
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Mutator library — this level's roster", EditorStyles.boldLabel);

            var library = wm.MutatorLibrary.Where(d => d != null).ToList();
            var project = AllProjectMutators();

            EditorGUILayout.LabelField(
                library.Count == 0
                    ? $"Empty. {project.Count} mutator(s) exist in the project."
                    : $"{library.Count} of {project.Count} project mutator(s): " +
                      string.Join(", ", library.Select(d => d.ResolvedId)),
                EditorStyles.wordWrappedMiniLabel);

            DrawLibraryButtons(wm, library, project);
            DrawUnregisteredWarning(wm, library);

            EditorGUILayout.Space(8);
            DrawStamp(wm, library);

            EditorGUILayout.Space(8);
            DrawWaveOverview(wm);
        }

        /// <summary>
        /// Every wave's pool on one screen.
        ///
        /// The per-wave inspector answers "what does THIS wave roll?", which is
        /// the wrong question when the bug is that wave 7 has a pool the other
        /// nine do not. Reading that off ten separate assets means holding ten
        /// things in your head; reading it off one column means noticing it.
        /// Rows ping their asset, so the odd one out is one click away.
        /// </summary>
        private void DrawWaveOverview(WaveManager wm)
        {
            int count = wm.WaveCount;
            _overviewFoldout = EditorGUILayout.Foldout(
                _overviewFoldout, $"This level's waves ({count})", true);
            if (!_overviewFoldout || count == 0)
                return;

            for (int i = 0; i < count; i++)
            {
                WaveDefinition w = wm.GetWave(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{i + 1}", EditorStyles.miniLabel, GUILayout.Width(20));

                    if (w == null)
                    {
                        var missing = new GUIStyle(EditorStyles.miniLabel);
                        missing.normal.textColor = Bad;
                        EditorGUILayout.LabelField("MISSING — this slot has no wave asset", missing);
                        continue;
                    }

                    if (GUILayout.Button(w.name, EditorStyles.miniLabel, GUILayout.Width(90)))
                        EditorGUIUtility.PingObject(w);

                    var pool = w.DrawablePool();
                    int slots = pool.Count + Mathf.Max(0, w.poolNothingWeight);
                    string text = pool.Count == 0
                        ? "plain"
                        : $"{string.Join(" / ", pool.Select(d => d.ResolvedId))}" +
                          $"   ({(slots > 0 ? w.poolNothingWeight / (float)slots : 0f):P0} plain)";

                    var style = new GUIStyle(EditorStyles.miniLabel);
                    if (i == count - 1 && pool.Count > 0)
                        style.normal.textColor = Warn;      // a pool on the finale is usually a slip
                    EditorGUILayout.LabelField(text, style);
                }
            }

            WaveDefinition last = wm.GetWave(count - 1);
            if (last != null && last.DrawablePool().Count > 0)
                EditorGUILayout.HelpBox(
                    "The final wave carries a pool. That is usually unintended — the boss is the event, " +
                    "and a rolled rule on top of it is noise stacked on the loudest beat. Stamping from " +
                    "this inspector always leaves the finale alone.", MessageType.Info);
        }

        // ------------------------------------------------------------ library

        private static List<WaveMutatorDefinition> AllProjectMutators() =>
            AssetDatabase.FindAssets("t:WaveMutatorDefinition")
                .Select(g => AssetDatabase.LoadAssetAtPath<WaveMutatorDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => d != null)
                .OrderBy(d => d.ResolvedId, System.StringComparer.Ordinal)
                .ToList();

        private void DrawLibraryButtons(WaveManager wm,
                                        List<WaveMutatorDefinition> library,
                                        List<WaveMutatorDefinition> project)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var missing = project.Where(d => !library.Contains(d)).ToList();

                using (new EditorGUI.DisabledScope(missing.Count == 0))
                {
                    if (GUILayout.Button(missing.Count == 0
                            ? "Inherit all project mutators (already complete)"
                            : $"Inherit all project mutators (+{missing.Count})",
                            GUILayout.Height(22)))
                        SetLibrary(wm, project);
                }

                using (new EditorGUI.DisabledScope(library.Count == 0))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(22)) &&
                        EditorUtility.DisplayDialog("Clear mutator library",
                            $"Remove all {library.Count} mutator(s) from this level's roster?\n\n" +
                            "Wave pools are NOT touched — they keep whatever they reference, and the " +
                            "audit will then report them as unregistered.",
                            "Clear", "Cancel"))
                        SetLibrary(wm, new List<WaveMutatorDefinition>());
                }
            }
        }

        /// <summary>
        /// Waves that draw a mutator this level never registered. Harmless at
        /// runtime — the wave still draws it — but it cannot be force-tested
        /// with T, and it is usually a sign the pool was filled from the
        /// project list rather than from the level's own roster.
        /// </summary>
        private static void DrawUnregisteredWarning(WaveManager wm, List<WaveMutatorDefinition> library)
        {
            var unregistered = new SortedDictionary<string, string>();
            for (int i = 0; i < wm.WaveCount; i++)
            {
                WaveDefinition w = wm.GetWave(i);
                if (w == null) continue;
                foreach (WaveMutatorDefinition d in w.DrawablePool())
                    if (!library.Contains(d))
                        unregistered[d.ResolvedId] = w.name;
            }

            if (unregistered.Count == 0)
                return;

            EditorGUILayout.HelpBox(
                $"{unregistered.Count} mutator(s) are drawn by this level's waves but are not in the " +
                "library, so T cannot cycle them:\n" +
                string.Join("\n", unregistered.Select(kv => $"    {kv.Key}  (e.g. {kv.Value})")),
                MessageType.Warning);
        }

        private static void SetLibrary(WaveManager wm, List<WaveMutatorDefinition> to)
        {
            var so = new SerializedObject(wm);
            SerializedProperty p = so.FindProperty("mutatorLibrary");
            p.arraySize = to.Count;
            for (int i = 0; i < to.Count; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = to[i];
            so.ApplyModifiedProperties();          // records Undo for us
            EditorSceneManager.MarkSceneDirty(wm.gameObject.scene);
        }

        // -------------------------------------------------------------- stamp

        /// <summary>
        /// Write the library onto this level's waves as their draw pool, in one
        /// action.
        ///
        /// This is the operation a designer actually performs: ten waves, the
        /// same handful of mutators, plain for the opening waves and no pool on
        /// the finale because the boss is the event. Doing it by hand is ten
        /// asset selections and thirty drags, which is how levels end up with
        /// one wave quietly different from the other nine.
        /// </summary>
        private void DrawStamp(WaveManager wm, List<WaveMutatorDefinition> library)
        {
            _stampFoldout = EditorGUILayout.Foldout(_stampFoldout, "Stamp the library onto this level's waves", true);
            if (!_stampFoldout)
                return;

            int waveCount = wm.WaveCount;
            if (waveCount == 0)
            {
                EditorGUILayout.HelpBox("This WaveManager has no waves — assign a LevelDefinition or fill " +
                                        "its own wave list first.", MessageType.Info);
                return;
            }
            if (library.Count == 0)
            {
                EditorGUILayout.HelpBox("The library is empty. Inherit the project's mutators above, then " +
                                        "trim it to the ones that belong in this level.", MessageType.Info);
                return;
            }

            _stampFromWave = Mathf.Clamp(
                EditorGUILayout.IntSlider(
                    new GUIContent("From wave", "Earlier waves are left plain — the opening waves are where " +
                                                "a player learns the map, and a rule they cannot yet read is noise."),
                    _stampFromWave, 1, waveCount),
                1, waveCount);

            _stampNothingWeight = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("Nothing weight", "'Nothing drawn' slots in each wave's hat. 0 means every " +
                                                 "stamped wave always carries a mutator."),
                _stampNothingWeight));

            int affected = Mathf.Max(0, waveCount - _stampFromWave + 1 - 1); // finale excluded
            int slots = library.Count + _stampNothingWeight;
            float plain = slots > 0 ? _stampNothingWeight / (float)slots : 0f;

            EditorGUILayout.LabelField(
                $"→ waves {_stampFromWave}–{waveCount - 1} get a {library.Count}-member pool " +
                $"({plain:P0} plain). Wave {waveCount} is left alone — the finale is the event.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(affected <= 0))
            {
                if (GUILayout.Button($"Stamp {affected} wave(s)", GUILayout.Height(22)))
                    Stamp(wm, library);
            }
        }

        private void Stamp(WaveManager wm, List<WaveMutatorDefinition> library)
        {
            var touched = new List<string>();
            // Stop before the last wave: the finale is a boss moment, and a
            // rolled rule on top of it is noise stacked on the loudest beat.
            for (int i = _stampFromWave - 1; i < wm.WaveCount - 1; i++)
            {
                WaveDefinition w = wm.GetWave(i);
                if (w == null) continue;

                Undo.RecordObject(w, "Stamp mutator pool");
                w.poolMutators = library.ToArray();
                w.poolNothingWeight = _stampNothingWeight;
                EditorUtility.SetDirty(w);
                touched.Add(w.name);
            }
            AssetDatabase.SaveAssets();

            Debug.Log($"[Waves] Stamped a {library.Count}-member pool (nothing weight {_stampNothingWeight}) " +
                      $"onto {touched.Count} wave(s): {string.Join(", ", touched)}. " +
                      "Undo restores every one of them.");
        }
    }
}
