using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Corehold.Core;
using Corehold.Towers;

/// <summary>
/// Final adjustment: place HP_Premium_2 in the open SOUTH band alongside the other
/// two premium pads (rather than north of the weave where the solver pushed it), so
/// the three premium slots form a coherent forward defensive line facing the core,
/// then run the full visual-clearance report on every pad.
/// </summary>
public static class FinalizeHardpointFix
{
    public static string Execute()
    {
        var sb = new StringBuilder();
        var pads = Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None);

        foreach (var p in pads)
        {
            if (p.name == "HP_Premium_2")
            {
                var old = p.transform.position;
                p.transform.position = new Vector3(7.5f, old.y, 1.5f);
                sb.AppendLine($"HP_Premium_2 repositioned ({old.x:0.0},{old.z:0.0}) -> (7.5,1.5)");
            }
        }

        // Full clearance report (visual envelope).
        var routes = new List<PathRoute>(Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None));
        var samples = new List<Vector3>();
        foreach (var r in routes)
        {
            int steps = Mathf.CeilToInt(r.Length / 0.5f);
            for (int i = 0; i <= steps; i++)
            {
                var c = r.SamplePosition(r.Length * (i / (float)steps), out _);
                c.y = 0f; samples.Add(c);
            }
        }

        sb.AppendLine("\n=== FINAL PAD CLEARANCE (route-centreline distance) ===");
        bool allClear = true;
        foreach (var p in pads)
        {
            Vector3 pos = p.transform.position; pos.y = 0f;
            float best = float.PositiveInfinity;
            foreach (var s in samples) { float d = (s - pos).sqrMagnitude; if (d < best) best = d; }
            best = Mathf.Sqrt(best);
            bool ok = best >= 6.5f;
            allClear &= ok;
            sb.AppendLine($"  {p.name,-14} ({pos.x:0.0},{pos.z:0.0})  routeDist={best:0.00}m  {(ok ? "OK" : "TIGHT")}");
        }

        // Also confirm the strict nav-clearance validator passes.
        var padList = new List<TowerHardpoint>(pads);
        var conflicts = RouteClearance.Check(routes, padList, 1.15f, 1.5f);
        sb.AppendLine("\n" + RouteClearance.Report(conflicts));
        sb.AppendLine($"\nAll pads clear visual envelope: {allClear}");

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        sb.AppendLine($"Scene '{scene.name}' saved.");

        Debug.Log(sb.ToString());
        return sb.ToString();
    }
}
