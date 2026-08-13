using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Measures the true world-space extents of everything the player must SEE — the
    /// route waypoints, spawners, hardpoints and the Core — then checks whether the
    /// current camera clips any of them, at 16:9, 16:10 and 20:9. This answers the
    /// ticket's actual question (does any aspect cut off the route?) using the route
    /// geometry rather than the nominal 130x75 design box.
    /// </summary>
    public static class RouteExtentsCheck
    {
        [MenuItem("Tools/COREHOLD/Validate/Check Content Extents vs Camera", false, 23)]
        public static string Run()
        {
            var sb = new StringBuilder();
            var pts = new List<Vector3>();

            void Collect(string rootPath)
            {
                var go = GameObject.Find(rootPath);
                if (go == null) return;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    pts.Add(t.position);
            }

            Collect("RefineryLevel/Routes");
            Collect("RefineryLevel/Hardpoints");
            var core = GameObject.Find("RefineryLevel/Core_Blockout/Core_Target");
            if (core != null) pts.Add(core.transform.position);
            foreach (var s in new[] { "Spawner_West", "Spawner_North", "Spawner_Air" })
            {
                var g = GameObject.Find(s);
                if (g != null) pts.Add(g.transform.position);
            }

            if (pts.Count == 0) { sb.AppendLine("No route/hardpoint/core points found."); Debug.Log(sb); return sb.ToString(); }

            float minX = pts.Min(p => p.x), maxX = pts.Max(p => p.x);
            float minZ = pts.Min(p => p.z), maxZ = pts.Max(p => p.z);
            sb.AppendLine("===== ROUTE FRAMING CHECK =====");
            sb.AppendLine($"Gameplay points: {pts.Count}");
            sb.AppendLine($"  X range [{minX:0.0} .. {maxX:0.0}]  width {maxX - minX:0.0} m");
            sb.AppendLine($"  Z range [{minZ:0.0} .. {maxZ:0.0}]  depth {maxZ - minZ:0.0} m");
            sb.AppendLine();

            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) { sb.AppendLine("No camera."); Debug.Log(sb); return sb.ToString(); }

            var rot = cam.transform.rotation;
            var pos = cam.transform.position;
            float vHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;

            (string name, float ar)[] aspects = { ("16:9", 16f/9f), ("16:10", 16f/10f), ("20:9", 20f/9f) };
            foreach (var (name, ar) in aspects)
            {
                float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * ar);
                float worstH = 0f, worstV = 0f;
                bool fits = true;
                foreach (var p in pts)
                {
                    Vector3 local = Quaternion.Inverse(rot) * (p - pos);
                    if (local.z <= 0.001f) { fits = false; continue; }
                    float hFrac = Mathf.Atan2(Mathf.Abs(local.x), local.z) / hHalf;
                    float vFrac = Mathf.Atan2(Mathf.Abs(local.y), local.z) / vHalf;
                    worstH = Mathf.Max(worstH, hFrac);
                    worstV = Mathf.Max(worstV, vFrac);
                    if (hFrac > 1f || vFrac > 1f) fits = false;
                }
                string verdict = fits
                    ? $"ALL POINTS VISIBLE (worst H {worstH*100f:0.0}%, worst V {worstV*100f:0.0}% of half-frame)"
                    : $"CLIPS (worst H {worstH*100f:0.0}%, worst V {worstV*100f:0.0}%)";
                sb.AppendLine($"  {name} (hFOV {hHalf*2f*Mathf.Rad2Deg:0.0}deg): {verdict}");
            }

            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }
    }
}
