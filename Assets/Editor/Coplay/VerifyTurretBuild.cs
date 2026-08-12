using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

public static class VerifyTurretBuild
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "Not in play mode.";

        var sb = new StringBuilder();
        var gm = GameManager.Instance;
        sb.AppendLine($"GameManager.Instance = {(gm != null ? "OK" : "NULL")} Salvage={(gm != null ? gm.Salvage : -1)}");

        var pads = Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None);
        TowerHardpoint target = null;
        foreach (var p in pads) if (!p.IsOccupied) { target = p; break; }
        if (target == null) return sb.AppendLine("No empty pad.").ToString();

        TowerDefinition def = null;
        foreach (var g in AssetDatabase.FindAssets("t:TowerDefinition"))
        {
            var d = AssetDatabase.LoadAssetAtPath<TowerDefinition>(AssetDatabase.GUIDToAssetPath(g));
            if (d != null && d.name == "Tower_Autocannon") { def = d; break; }
        }

        int before = gm != null ? gm.Salvage : -1;
        bool ok = target.TryBuild(def);
        sb.AppendLine($"TryBuild on '{target.name}' returned {ok}. Salvage {before} -> {(gm != null ? gm.Salvage : -1)}. Occupied={target.IsOccupied}");
        return sb.ToString();
    }
}
