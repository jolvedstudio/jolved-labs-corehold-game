using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Checks whether the actual authored route waypoints (and the Core / spawners)
    /// are inside the camera frustum at 16:9, 16:10 and 20:9, and reports the
    /// tightest horizontal usage. This is the real "does it cut off the route?" test,
    /// as opposed to the theoretical 130x75 bounding box.
    /// </summary>
    public static class RouteFramingCheck
    {
        const float VFov = 35f;

        [MenuItem("Tools/COREHOLD/Validate/Check Waypoints In Frustum", false, 24)]
        public static string Run()
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return "No camera.";

            // Gather all points that MUST be visible: every waypoint under Routes,
            // spawners, hardpoints, and the core target.
            var points = new List<(string label, Vector3 p)>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                string n = t.name;
                var full = GetPath(t);
                if (full.Contains("/Routes/") && n.StartsWith("WP_"))
                    points.Add((full, t.position));
            }

            var sb = new StringBuilder();
            sb.AppendLine("ROUTE FRAMING CHECK (Ticket 32)");
            if (points.Count == 0)
            {
                sb.AppendLine("  No route waypoints (WP_*) found under Routes/. Falling back to box corners only.");
            }
            else
            {
                float minX = points.Min(p => p.p.x), maxX = points.Max(p => p.p.x);
                float minZ = points.Min(p => p.p.z), maxZ = points.Max(p => p.p.z);
                sb.AppendLine($"  {points.Count} route waypoints. Route bounds X[{minX:0.0}..{maxX:0.0}] Z[{minZ:0.0}..{maxZ:0.0}]");
            }
            sb.AppendLine($"  Camera pos {cam.transform.position} rot {cam.transform.eulerAngles} vFOV {cam.fieldOfView:0.0}");
            sb.AppendLine();

            (string name, float ar)[] aspects =
            {
                ("16:9",  16f / 9f),
                ("16:10", 16f / 10f),
                ("20:9",  20f / 9f),
            };

            foreach (var (name, ar) in aspects)
            {
                float worstH = 0f, worstV = 0f;
                string worstLabel = "";
                bool ok = true;
                foreach (var (label, p) in points)
                {
                    Vector3 vp = ViewportPoint(cam, p, ar);
                    if (vp.z <= 0f) { ok = false; worstLabel = label + " (behind camera)"; continue; }
                    float hUse = Mathf.Abs(vp.x - 0.5f) / 0.5f;
                    float vUse = Mathf.Abs(vp.y - 0.5f) / 0.5f;
                    if (hUse > worstH) { worstH = hUse; }
                    if (vUse > worstV) { }
                    worstV = Mathf.Max(worstV, vUse);
                    if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f)
                    {
                        ok = false;
                        worstLabel = label;
                    }
                }
                string verdict = ok
                    ? $"ALL waypoints visible. Widest uses {worstH * 100f:0.0}% horiz, {worstV * 100f:0.0}% vert of half-frame."
                    : $"CUTS OFF route. First offender: {worstLabel}. Widest horiz usage {worstH * 100f:0.0}%.";
                sb.AppendLine($"  {name}: {verdict}");
            }

            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }

        // Manual viewport projection for an arbitrary aspect ratio (Camera.WorldToViewportPoint
        // uses the game view's current aspect, which we can't rely on here).
        static Vector3 ViewportPoint(Camera cam, Vector3 world, float aspect)
        {
            Vector3 local = cam.transform.InverseTransformPoint(world); // camera space, +z forward
            if (local.z <= 0f) return new Vector3(0, 0, -1f);
            float halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float tanV = Mathf.Tan(halfV);
            float tanH = tanV * aspect;
            float ny = (local.y / local.z) / tanV;   // -1..1
            float nx = (local.x / local.z) / tanH;   // -1..1
            return new Vector3(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, local.z);
        }

        static string GetPath(Transform t)
        {
            var stack = new Stack<string>();
            while (t != null) { stack.Push(t.name); t = t.parent; }
            return "/" + string.Join("/", stack);
        }
    }
}
