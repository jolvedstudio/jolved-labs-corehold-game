using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Core;
using Corehold.Data;
using Corehold.Towers;

namespace CoreholdEditor
{
    /// <summary>
    /// Play-mode test: grants salvage, builds an Autocannon on an empty hardpoint the
    /// same way a tap would (TowerHardpoint.TryBuild), and reports whether a Tower was
    /// created with a mesh. Verifies the build path end to end.
    /// </summary>
    public static class BuildTurretTest
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            var gm = GameManager.Instance ?? Object.FindFirstObjectByType<GameManager>();
            if (gm == null) return "No GameManager (enter play mode).";
            gm.AddSalvage(1000);

            var def = AssetDatabase.FindAssets("t:TowerDefinition", new[] { "Assets/_COREHOLD/Data/Towers" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<TowerDefinition>)
                .FirstOrDefault(d => d != null && d.name.Contains("Autocannon"));
            sb.AppendLine($"Autocannon def: {(def != null ? def.name : "NULL")}, basePrefab={(def != null && def.basePrefab != null ? def.basePrefab.name : "NULL")}");

            var pad = Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None)
                .FirstOrDefault(p => !p.IsOccupied);
            if (pad == null) return sb + "\nNo empty pad.";

            bool built = pad.TryBuild(def);
            sb.AppendLine($"TryBuild on '{pad.name}': {built}, occupied={pad.IsOccupied}, salvage now={gm.Salvage}");

            if (pad.Occupant != null)
            {
                var t = pad.Occupant;
                var rends = t.GetComponentsInChildren<Renderer>(true);
                sb.AppendLine($"Tower '{t.name}' tier={t.TierIndex} renderers={rends.Length} pos={t.transform.position}");
            }

            Debug.Log("[COREHOLD] BuildTurretTest:\n" + sb);
            return sb.ToString();
        }
    }
}
