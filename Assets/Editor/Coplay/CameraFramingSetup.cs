using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32: fix the Main Camera at 38 deg pitch, 35 deg vertical FOV, positioned so the
    /// playfield fills the frame with ~12% margin at the top and ~18% at the bottom, with no
    /// pan/zoom/rotation. Then verify framing at 16:9, 16:10 and 20:9 and report whether any
    /// cuts off the route.
    ///
    /// The vertical framing is solved against the ACTUAL gameplay content — the route
    /// waypoints, spawners, hardpoints and the Core — rather than the nominal 130x75 design
    /// box. The real level occupies ~94.5 m x ~45 m of that box, folded into the lower-left;
    /// framing the design box instead would waste most of the frame on empty ground and, worse,
    /// pull the camera so close that the route clips horizontally at the taller 16:10 aspect.
    /// Framing the real content keeps the requested 38 deg / 35 deg / 12%-18% look while
    /// guaranteeing the whole route is visible with margin at all three aspects.
    /// </summary>
    public static class CameraFramingSetup
    {
        const float Pitch = 38f;           // degrees looking down
        const float VFov = 35f;            // vertical FOV degrees
        const float TopMargin = 0.12f;     // fraction of screen height empty at top
        const float BottomMargin = 0.18f;  // fraction of screen height empty at bottom

        [MenuItem("Tools/COREHOLD/Look/Fix Camera Framing", false, 61)]
        public static string Run()
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return "No camera found in the active scene.";

            GameplayExtents(out float minX, out float maxX, out float minZ, out float maxZ, out int pointCount);

            SolveAndPlace(minX, maxX, minZ, maxZ, out Vector3 pos);

            cam.transform.rotation = Quaternion.Euler(Pitch, 0f, 0f);
            cam.transform.position = pos;
            cam.fieldOfView = VFov;
            cam.usePhysicalProperties = false; // ensure vertical-FOV interpretation
            cam.orthographic = false;

            EditorUtility.SetDirty(cam);
            EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
            EditorSceneManager.SaveScene(cam.gameObject.scene);

            var report = Verify(cam, minX, maxX, minZ, maxZ, pointCount);
            Debug.Log("[COREHOLD] " + report);
            return report;
        }

        /// <summary>
        /// Collect the world-space bounds of everything the player must see: route waypoints,
        /// hardpoints, spawners and the Core.
        /// </summary>
        static void GameplayExtents(out float minX, out float maxX, out float minZ, out float maxZ, out int count)
        {
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

            if (pts.Count == 0)
            {
                // Fallback to the design box if the level isn't loaded.
                minX = -65f; maxX = 65f; minZ = -37.5f; maxZ = 37.5f; count = 0;
                return;
            }

            minX = pts.Min(p => p.x); maxX = pts.Max(p => p.x);
            minZ = pts.Min(p => p.z); maxZ = pts.Max(p => p.z);
            count = pts.Count;
        }

        // Narrowest horizontal FOV of the three target aspects is 16:10 (the tallest frame),
        // so the camera must be far enough back that the full content width fits there.
        const float NarrowestAspect = 16f / 10f;
        // Leave a little horizontal safety margin so nothing kisses the frame edge.
        const float HorizontalSafety = 0.92f; // width uses at most 92% of the half-frame

        static void SolveAndPlace(float minX, float maxX, float minZ, float maxZ, out Vector3 pos)
        {
            float halfV = VFov * 0.5f * Mathf.Deg2Rad;
            float tanHalf = Mathf.Tan(halfV);
            float pitchRad = Pitch * Mathf.Deg2Rad;

            // Centre horizontally on the content mid-X so the (asymmetric) route sits centred
            // in frame with yaw held at 0, giving the widest symmetric horizontal budget.
            float midX = (minX + maxX) * 0.5f;

            // 1) VERTICAL SOLVE (design intent): place height + depth so the content sits
            //    between the 12% top and 18% bottom margins. This alone frames the level as a
            //    diorama and matches the GDD's ~100 m out / ~60 m up note for this depth.
            float fTop = 1f - TopMargin;
            float fBot = BottomMargin;
            float angTop = Mathf.Atan((2f * fTop - 1f) * tanHalf);
            float angBot = Mathf.Atan((2f * fBot - 1f) * tanHalf);
            float depFar = pitchRad - angTop;
            float depNear = pitchRad - angBot;
            float invFar = 1f / Mathf.Tan(depFar);
            float invNear = 1f / Mathf.Tan(depNear);
            float depth = maxZ - minZ;
            float h = depth / (invFar - invNear);
            float camZ = maxZ - h * invFar;

            // 2) HORIZONTAL FIT (constraint): at that framing, verify the content WIDTH fits
            //    inside the narrowest (16:10) horizontal frustum with safety margin. If it does
            //    not, dolly the camera straight back along its own view axis — which widens the
            //    apparent frame both ways equally, loosening the vertical margins symmetrically
            //    rather than cramming the level to the top. This keeps pitch and FOV fixed and
            //    the diorama centred; it just backs off until the widest route point clears.
            float hHalf = Mathf.Atan(Mathf.Tan(halfV) * NarrowestAspect) * HorizontalSafety;
            float tanHHalf = Mathf.Tan(hHalf);
            var rot = Quaternion.Euler(Pitch, 0f, 0f);
            Vector3 forward = rot * Vector3.forward;

            var corners = new[]
            {
                new Vector3(minX, 0f, minZ), new Vector3(maxX, 0f, minZ),
                new Vector3(minX, 0f, maxZ), new Vector3(maxX, 0f, maxZ),
            };

            var camPos = new Vector3(midX, h, camZ);
            for (int i = 0; i < 60; i++)
            {
                float worst = 0f;
                foreach (var c in corners)
                {
                    Vector3 local = Quaternion.Inverse(rot) * (c - camPos);
                    if (local.z <= 0.01f) { worst = 999f; break; }
                    worst = Mathf.Max(worst, Mathf.Abs(local.x) / local.z);
                }
                if (worst <= tanHHalf * 1.001f) break;
                // Dolly back a step along the view axis (backwards = -forward).
                camPos -= forward * 4f;
            }

            pos = camPos;
        }

        static string Verify(Camera cam, float minX, float maxX, float minZ, float maxZ, int pointCount)
        {
            var sb = new StringBuilder();
            var rot = Quaternion.Euler(Pitch, 0f, 0f);
            var pos = cam.transform.position;
            float vHalf = VFov * 0.5f * Mathf.Deg2Rad;

            sb.AppendLine("CAMERA FRAMING (Ticket 32)");
            sb.AppendLine($"  Pitch {Pitch} deg, Vertical FOV {VFov} deg, no pan/zoom/rotation.");
            sb.AppendLine($"  Position = ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0})  height {pos.y:0.0} m, set back {(-pos.z):0.0} m");
            sb.AppendLine($"  Framed against {pointCount} real gameplay points:");
            sb.AppendLine($"    content X [{minX:0.0} .. {maxX:0.0}] (width {maxX - minX:0.0} m), Z [{minZ:0.0} .. {maxZ:0.0}] (depth {maxZ - minZ:0.0} m)");
            sb.AppendLine($"  Nearest visible content ~ {(minZ - pos.z):0.0} m ahead.");
            sb.AppendLine();

            // The set of points to test = the corners of the gameplay AABB at y=0, plus the
            // Core / hardpoint tops are near ground so corners are the worst case.
            var testPts = new List<Vector3>
            {
                new Vector3(minX, 0, minZ), new Vector3(maxX, 0, minZ),
                new Vector3(minX, 0, maxZ), new Vector3(maxX, 0, maxZ),
            };

            (string name, float ar)[] aspects = { ("16:9", 16f/9f), ("16:10", 16f/10f), ("20:9", 20f/9f) };
            foreach (var (name, ar) in aspects)
            {
                float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * ar);
                float worstH = 0f, worstV = 0f;
                float nearY = 0f, farY = 0f;
                bool fits = true;
                foreach (var p in testPts)
                {
                    Vector3 local = Quaternion.Inverse(rot) * (p - pos);
                    if (local.z <= 0.001f) { fits = false; continue; }
                    float hFrac = Mathf.Atan2(Mathf.Abs(local.x), local.z) / hHalf;
                    float vAng = Mathf.Atan2(local.y, local.z);
                    float vFrac = Mathf.Abs(vAng) / vHalf;
                    worstH = Mathf.Max(worstH, hFrac);
                    worstV = Mathf.Max(worstV, vFrac);
                    if (hFrac > 1f || vFrac > 1f) fits = false;

                    float sy = 0.5f + 0.5f * (vAng / vHalf);
                    if (Mathf.Approximately(p.z, minZ)) nearY = sy;
                    if (Mathf.Approximately(p.z, maxZ)) farY = sy;
                }
                string verdict = fits
                    ? $"route FITS (worst H {worstH*100f:0.0}%, worst V {worstV*100f:0.0}% of half-frame)"
                    : $"route CUT OFF (worst H {worstH*100f:0.0}%, worst V {worstV*100f:0.0}%)";
                sb.AppendLine($"  {name} (hFOV {hHalf*2f*Mathf.Rad2Deg:0.0}deg | topMargin {(1f-farY)*100f:0.0}% botMargin {nearY*100f:0.0}%): {verdict}");
            }

            return sb.ToString();
        }
    }
}
