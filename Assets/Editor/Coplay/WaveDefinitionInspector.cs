using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// The wave asset's inspector: what this wave sends, and what it might do.
    ///
    /// The default inspector shows the fields. That was never the hard part —
    /// the hard part is that a wave's difficulty is not written down anywhere
    /// on it. It is the product of a spawn table and up to two mutator lists,
    /// and the only way to know what a run of it feels like was to read four
    /// arrays and do the multiplication in your head. So this draws the ANSWER:
    /// every outcome the wave can roll, each with its odds and its composed
    /// effect, worst one marked.
    ///
    /// The worst row is the one that matters most, because it is the one the
    /// balance model gates on. A designer widening a pool can watch the
    /// certified worst case move as they type.
    /// </summary>
    [CustomEditor(typeof(WaveDefinition))]
    [CanEditMultipleObjects]
    public class WaveDefinitionInspector : Editor
    {
        private static readonly Color Warn = new Color(1f, 0.65f, 0.15f);

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length > 1)
                return;

            var wave = (WaveDefinition)target;
            EditorGUILayout.Space(8);
            DrawSummary(wave);
            EditorGUILayout.Space(4);
            DrawOutcomes(wave);
        }

        // ---------------------------------------------------------- the attack

        private static void DrawSummary(WaveDefinition wave)
        {
            EditorGUILayout.LabelField("What this wave sends", EditorStyles.boldLabel);

            if (wave.groups == null || wave.groups.Length == 0)
            {
                EditorGUILayout.HelpBox("No spawn groups — this wave sends nothing.", MessageType.Warning);
                return;
            }

            int units = 0;
            float bounty = 0f;
            var parts = new List<string>();
            bool anyAir = false;
            foreach (SpawnGroup g in wave.groups)
            {
                if (g.enemy == null || g.count <= 0)
                    continue;
                units += g.count;
                bounty += g.count * g.enemy.bounty;
                anyAir |= g.enemy.isAir;
                parts.Add($"{g.count}×{g.enemy.id}");
            }

            EditorGUILayout.LabelField(string.Join("  +  ", parts), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                $"{units} units · {bounty:0} bounty on the field · pays {wave.clearBonus} to clear" +
                (anyAir ? " · has air" : " · ground only"),
                EditorStyles.miniLabel);

            if (wave.groups.Any(g => g.enemy == null || g.count <= 0))
                EditorGUILayout.HelpBox("Some groups have no enemy or a count of zero — they spawn nothing.",
                                        MessageType.Warning);
        }

        // -------------------------------------------------------- the outcomes

        private static void DrawOutcomes(WaveDefinition wave)
        {
            List<WaveMutatorDefinition> pool = wave.DrawablePool();
            int none = Mathf.Max(0, wave.poolNothingWeight);
            int slots = pool.Count + none;

            EditorGUILayout.LabelField("What it can roll", EditorStyles.boldLabel);

            if (pool.Count == 0)
            {
                EditorGUILayout.LabelField("Plain wave — no mutators, every run identical.",
                                           EditorStyles.miniLabel);
                return;
            }

            // Every outcome, worst marked. "Worst" is the highest threat score,
            // the same shape of judgement the balance model makes.
            var rows = new List<(string odds, List<WaveMutatorDefinition> set, float threat)>();
            if (none > 0)
                rows.Add(($"{none}/{slots}", new List<WaveMutatorDefinition>(), Threat(MutatorEffects.Identity)));
            foreach (WaveMutatorDefinition d in pool)
            {
                var set = new List<WaveMutatorDefinition> { d };
                rows.Add(($"1/{slots}", set, Threat(Compose(set))));
            }

            float worst = rows.Max(r => r.threat);
            foreach (var r in rows)
                DrawOutcome(r.odds, r.set, Mathf.Approximately(r.threat, worst));

            EditorGUILayout.Space(2);
            if (rows.Count == 1)
                EditorGUILayout.LabelField(
                    "One outcome — this wave ALWAYS carries it. That is how a set-piece wave is authored.",
                    EditorStyles.wordWrappedMiniLabel);
            else
                EditorGUILayout.LabelField(
                    $"{rows.Count} outcomes, drawn fresh every run. The gate certifies the worst one, so " +
                    "this wave is never harder than the marked row — and is usually easier.",
                    EditorStyles.wordWrappedMiniLabel);

            if (pool.Count >= 4)
                EditorGUILayout.HelpBox(
                    $"{pool.Count} pool members is wide. Pool width IS the variance: the level gets tuned " +
                    "for a worst case it rarely draws, so most runs feel under-tuned. Two or three is the " +
                    "usual shape.", MessageType.Warning);

            if (none == 0 && pool.Count > 1)
                EditorGUILayout.HelpBox(
                    "Nothing-weight is 0, so this wave ALWAYS carries one of these — it can never roll " +
                    "plain. Deliberate for a set-piece; surprising if you wanted 'sometimes'.",
                    MessageType.Info);
        }

        private static void DrawOutcome(string odds, List<WaveMutatorDefinition> set, bool isWorst)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = new GUIStyle(EditorStyles.miniLabel);
                if (isWorst) style.normal.textColor = Warn;

                string names = set.Count == 0
                    ? "plain"
                    : string.Join(" + ", set.Select(d => string.IsNullOrWhiteSpace(d.title) ? d.ResolvedId : d.title));

                EditorGUILayout.LabelField(odds, style, GUILayout.Width(46));
                EditorGUILayout.LabelField(names + (isWorst ? "   ← gated on this" : ""), style);
            }

            MutatorEffects e = Compose(set);
            if (!e.IsIdentity)
                EditorGUILayout.LabelField("        " + Describe(e), EditorStyles.miniLabel);
        }

        // ----------------------------------------------------------- the maths

        private static MutatorEffects Compose(List<WaveMutatorDefinition> set)
        {
            MutatorEffects e = MutatorEffects.Identity;
            foreach (WaveMutatorDefinition d in set)
                e.Fold(d.Effects);
            return e;
        }

        /// <summary>
        /// A rough "how hard is this" score, only ever used to ORDER outcomes
        /// against each other so the worst can be marked.
        ///
        /// Deliberately not the balance model: that runs against a real map and
        /// costs a subprocess. This is the cheap ordering that tells a designer
        /// which row to go look at, and range is squared because range is area.
        /// </summary>
        private static float Threat(MutatorEffects e)
        {
            float t = e.health * e.airSpeed;
            t /= Mathf.Max(0.01f, e.turretRange * e.turretRange);
            t /= Mathf.Max(0.01f, e.spawnGap);          // a compressed wave is a harder wave
            t /= Mathf.Max(0.01f, e.bounty);            // paying for it is a real mitigation
            if (e.singleApproach) t *= 1.15f;
            return t;
        }

        private static string Describe(MutatorEffects e)
        {
            var sb = new StringBuilder();
            void Add(string s) { if (sb.Length > 0) sb.Append(" · "); sb.Append(s); }

            if (!Mathf.Approximately(e.airSpeed, 1f)) Add($"air ×{e.airSpeed:0.##}");
            if (!Mathf.Approximately(e.groundSpeed, 1f)) Add($"ground ×{e.groundSpeed:0.##}");
            if (!Mathf.Approximately(e.health, 1f)) Add($"hp ×{e.health:0.##}");
            if (!Mathf.Approximately(e.bounty, 1f)) Add($"salvage ×{e.bounty:0.##}");
            if (!Mathf.Approximately(e.turretRange, 1f))
                Add($"range ×{e.turretRange:0.##} ({e.turretRange * e.turretRange:P0} of the ground)");
            if (!Mathf.Approximately(e.spawnGap, 1f)) Add($"spacing ×{e.spawnGap:0.##}");
            if (e.singleApproach) Add("one approach");
            return sb.ToString();
        }
    }
}
