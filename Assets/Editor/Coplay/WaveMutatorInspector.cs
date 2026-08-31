using System.Collections.Generic;
using System.Linq;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// The mutator asset's inspector: the banner as the player will see it, and
    /// the effects as the model will price them.
    ///
    /// A mutator is authored as seven multipliers, and seven multipliers do not
    /// read as a feeling. The preview closes that gap — you write "Turrets see
    /// half as far", set range to 0.5, and the inspector tells you that is a
    /// quarter of the ground covered, which is the sentence that makes you
    /// reconsider 0.5.
    /// </summary>
    [CustomEditor(typeof(WaveMutatorDefinition))]
    [CanEditMultipleObjects]
    public class WaveMutatorInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length > 1)
                return;

            var d = (WaveMutatorDefinition)target;
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("The banner the player reads", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                (string.IsNullOrWhiteSpace(d.title) ? "SPECIAL WAVE" : d.title) + "\n" +
                (string.IsNullOrWhiteSpace(d.clause) ? "(no clause set)" : d.clause),
                MessageType.None);

            MutatorEffects e = d.Effects;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("What it actually does", EditorStyles.boldLabel);

            if (e.IsIdentity)
            {
                EditorGUILayout.HelpBox(
                    "This mutator changes nothing mechanically. That is legitimate for a look-only rule " +
                    "(weather plus a banner), but the banner is then a promise the wave does not keep — " +
                    "so make it a deliberate choice, not a half-finished asset.", MessageType.Warning);
            }
            else
            {
                foreach (string line in Lines(e))
                    EditorGUILayout.LabelField("• " + line, EditorStyles.wordWrappedMiniLabel);
            }

            // The design nudge the audit tool also makes, said at the moment the
            // number is being typed rather than in a report read later.
            bool harder = e.health > 1.01f || e.turretRange < 0.99f || e.airSpeed > 1.01f ||
                          e.singleApproach || e.spawnGap < 0.99f;
            if (harder && e.bounty <= 1.001f)
                EditorGUILayout.HelpBox(
                    "Harder with no extra salvage — this reads to a player as a punishment rather than a " +
                    "trade. Overcharge pays 1.5× for its 1.3× health.", MessageType.Info);

            if (e.turretRange < 0.4f)
                EditorGUILayout.HelpBox(
                    $"Range ×{e.turretRange:0.##} leaves {e.turretRange * e.turretRange:P0} of the ground " +
                    "covered. Range is area, so this is the harshest term in the list — Blackout's 0.5 is " +
                    "already the harshest shipped value.", MessageType.Warning);

            if (d.weatherLayer == null)
                EditorGUILayout.HelpBox(
                    "No weather layer, so a wave carrying this looks like any other. The banner will be the " +
                    "only sign anything changed.", MessageType.Info);

            EditorGUILayout.Space(4);
            DrawUsage(d);
        }

        /// <summary>
        /// Which waves reference this mutator, and whether any level actually
        /// offers it. A mutator nobody uses is the most common half-finished
        /// state, and the inspector is where someone is standing when they
        /// wonder about it.
        /// </summary>
        private static void DrawUsage(WaveMutatorDefinition d)
        {
            var always = new List<string>();
            var sometimes = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:WaveDefinition"))
            {
                var w = AssetDatabase.LoadAssetAtPath<WaveDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (w == null || w.poolMutators == null || !w.poolMutators.Contains(d))
                    continue;
                // A one-member pool that can never draw nothing IS an always-on
                // rule, and saying so is more use than "in the pool of".
                bool guaranteed = w.DrawablePool().Count == 1 && w.poolNothingWeight == 0;
                (guaranteed ? always : sometimes).Add(w.name);
            }

            EditorGUILayout.LabelField("Used by", EditorStyles.boldLabel);
            if (always.Count == 0 && sometimes.Count == 0)
            {
                EditorGUILayout.LabelField("No wave references this yet.", EditorStyles.miniLabel);
                return;
            }
            if (always.Count > 0)
                EditorGUILayout.LabelField($"always: {Join(always)}", EditorStyles.wordWrappedMiniLabel);
            if (sometimes.Count > 0)
                EditorGUILayout.LabelField($"in the pool of: {Join(sometimes)}", EditorStyles.wordWrappedMiniLabel);
        }

        private static string Join(List<string> names) =>
            names.Count <= 6
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(6)) + $" … and {names.Count - 6} more";

        private static IEnumerable<string> Lines(MutatorEffects e)
        {
            if (!Mathf.Approximately(e.airSpeed, 1f))
                yield return $"Air units move at ×{e.airSpeed:0.##} speed — they also spend " +
                             $"{(e.airSpeed > 1f ? "less" : "more")} time under every tower on the corridor.";
            if (!Mathf.Approximately(e.groundSpeed, 1f))
                yield return $"Ground units move at ×{e.groundSpeed:0.##} speed.";
            if (!Mathf.Approximately(e.health, 1f))
                yield return $"Every unit has ×{e.health:0.##} health.";
            if (!Mathf.Approximately(e.bounty, 1f))
                yield return $"Every kill pays ×{e.bounty:0.##} salvage.";
            if (!Mathf.Approximately(e.turretRange, 1f))
                yield return $"Turrets reach ×{e.turretRange:0.##} as far — " +
                             $"{e.turretRange * e.turretRange:P0} of the ground they normally cover.";
            if (!Mathf.Approximately(e.spawnGap, 1f))
                yield return $"Spawns are ×{e.spawnGap:0.##} apart — the wave is " +
                             $"{(e.spawnGap < 1f ? "denser and shorter" : "thinner and longer")}.";
            if (e.singleApproach)
                yield return "Every ground group funnels onto ONE approach.";
        }
    }
}
