using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Applies the balance model's counts-only tune (suggested_changes) to a
/// LevelDefinition's wave assets. ONE implementation, shared by the Campaign
/// Builder's step-6 button and the Level Generator's adopt-on-gate-fail offer
/// — two appliers of one tune is the drift this project's doctrine forbids.
///
/// Contract: validate-all-then-apply. Every change names the enemy id and the
/// count it expects to find; any mismatch (the assets moved since the model
/// ran) refuses the WHOLE tune — a half-applied tune is worse than none.
/// Zero-count groups are deleted last, highest index first, so the recorded
/// group indices stay valid throughout. The CALLER owns the ownership
/// question (which wave assets may be edited at all) and the SaveAssets.
/// </summary>
public static class WaveTuneApplier
{
    /// <summary>
    /// Validate and apply <paramref name="changes"/> to
    /// <paramref name="def"/>'s wave assets. <paramref name="label"/> prefixes
    /// the log lines ("Level 3", "adopt"). False = refused whole, nothing
    /// edited, the log says why.
    /// </summary>
    public static bool Apply(LevelDefinition def, BalanceModelRunner.SuggestedChange[] changes,
                             string label, StringBuilder log)
    {
        if (def == null || def.waves == null || changes == null || changes.Length == 0)
        {
            log.AppendLine($"  {label}: nothing to apply.");
            return false;
        }

        // Pass 1 — VALIDATE everything before writing anything.
        foreach (var c in changes)
        {
            string stale = null;
            if (c.wave < 1 || c.wave > def.waves.Length || def.waves[c.wave - 1] == null)
            {
                stale = $"wave {c.wave} is missing";
            }
            else
            {
                var so = new SerializedObject(def.waves[c.wave - 1]);
                var groups = so.FindProperty("groups");
                if (groups == null || c.group < 0 || c.group >= groups.arraySize)
                {
                    stale = $"wave {c.wave} group {c.group} is gone";
                }
                else
                {
                    var g = groups.GetArrayElementAtIndex(c.group);
                    var enemy = g.FindPropertyRelative("enemy").objectReferenceValue as EnemyDefinition;
                    int count = g.FindPropertyRelative("count").intValue;
                    if (enemy == null || enemy.id != c.enemy || count != c.prev)
                        stale = $"wave {c.wave}: expected {c.prev}×{c.enemy}, found " +
                                $"{count}×{(enemy != null ? enemy.id : "?")}";
                }
            }
            if (stale != null)
            {
                log.AppendLine($"  {label}: STALE tune ({stale}) — the assets changed since the " +
                               "model ran; nothing was edited.");
                return false;
            }
        }

        // Pass 2 — apply counts; delete zero-count groups last, descending.
        var deletions = new List<(int wave, int group)>();
        foreach (var c in changes)
        {
            var waveAsset = def.waves[c.wave - 1];
            AssetDatabase.MakeEditable(AssetDatabase.GetAssetPath(waveAsset));
            var so = new SerializedObject(waveAsset);
            so.FindProperty("groups").GetArrayElementAtIndex(c.group)
              .FindPropertyRelative("count").intValue = c.next;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(waveAsset);
            log.AppendLine($"  {label} wave {c.wave}: {c.enemy} {c.prev}→{c.next}" +
                           (c.next == 0 ? " (group removed)" : ""));
            if (c.next == 0)
                deletions.Add((c.wave, c.group));
        }
        foreach (var d in deletions.OrderByDescending(d => d.wave * 1000 + d.group))
        {
            var waveAsset = def.waves[d.wave - 1];
            var so = new SerializedObject(waveAsset);
            var groups = so.FindProperty("groups");
            if (d.group < groups.arraySize)
            {
                groups.DeleteArrayElementAtIndex(d.group);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(waveAsset);
            }
        }
        return true;
    }
}
