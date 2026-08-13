using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 verification pass. Reports the real on-disk / in-scene state of the
    /// camera framing, lighting, blob shadows and rotate overlay so we can confirm the
    /// ticket is actually done rather than just coded.
    /// </summary>
    public static class Ticket32Verify
    {
        const string EnemyDir = "Assets/_COREHOLD/Prefabs/Enemies";
        const string TowerDir = "Assets/_COREHOLD/Prefabs/Towers";
        const string BlobMatPath = "Assets/_COREHOLD/Art/Materials/Mat_BlobShadow.mat";

        [MenuItem("Tools/COREHOLD/Validate/Verify Ticket 32", false, 26)]
        public static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== TICKET 32 VERIFICATION =====\n");

            VerifyCamera(sb);
            VerifyLighting(sb);
            VerifyBlobShadows(sb);
            VerifyOverlay(sb);

            var s = sb.ToString();
            Debug.Log("[COREHOLD] " + s);
            return s;
        }

        static void VerifyCamera(StringBuilder sb)
        {
            sb.AppendLine("--- CAMERA ---");
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) { sb.AppendLine("  NO CAMERA FOUND\n"); return; }

            var e = cam.transform.rotation.eulerAngles;
            sb.AppendLine($"  pos={cam.transform.position}  pitch={e.x:0.0}  yaw={e.y:0.0}  roll={e.z:0.0}");
            sb.AppendLine($"  vFOV={cam.fieldOfView:0.0}  ortho={cam.orthographic}  usePhysical={cam.usePhysicalProperties}");
            VerifyCameraFraming(cam, sb);
        }

        // Collect the true world-space extents of everything the player must see (route,
        // hardpoints, spawners, Core) — the level occupies only part of the 130x75 box.
        static void GameplayExtents(out float minX, out float maxX, out float minZ, out float maxZ)
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            void Collect(string root)
            {
                var go = GameObject.Find(root);
                if (go == null) return;
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) pts.Add(t.position);
            }
            Collect("RefineryLevel/Routes");
            Collect("RefineryLevel/Hardpoints");
            var core = GameObject.Find("RefineryLevel/Core_Blockout/Core_Target");
            if (core != null) pts.Add(core.transform.position);
            foreach (var s in new[] { "Spawner_West", "Spawner_North", "Spawner_Air" })
            { var g = GameObject.Find(s); if (g != null) pts.Add(g.transform.position); }

            if (pts.Count == 0) { minX = -65; maxX = 65; minZ = -37.5f; maxZ = 37.5f; return; }
            minX = pts.Min(p => p.x); maxX = pts.Max(p => p.x);
            minZ = pts.Min(p => p.z); maxZ = pts.Max(p => p.z);
        }

        static void VerifyCameraFraming(Camera cam, StringBuilder sb)
        {
            // Test the REAL gameplay extents (route/hardpoints/spawners/Core), not the nominal
            // 130x75 design box — the box cannot fit at 35deg vertical FOV and is not what the
            // "does the route get cut off" question is actually about.
            GameplayExtents(out float minX, out float maxX, out float minZ, out float maxZ);
            sb.AppendLine($"  Gameplay content: X[{minX:0.0}..{maxX:0.0}] Z[{minZ:0.0}..{maxZ:0.0}]");

            (string name, float ar)[] aspects = { ("16:9", 16f/9f), ("16:10", 16f/10f), ("20:9", 20f/9f) };
            float vHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            var rot = cam.transform.rotation;
            var pos = cam.transform.position;

            var corners = new[]
            {
                new Vector3(minX,0,minZ), new Vector3(maxX,0,minZ),
                new Vector3(minX,0,maxZ), new Vector3(maxX,0,maxZ),
            };

            foreach (var (name, ar) in aspects)
            {
                float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * ar);
                bool fits = true;
                float worstH = 0f, worstV = 0f;
                float nearY = 0.5f, farY = 0.5f;
                foreach (var c in corners)
                {
                    Vector3 local = Quaternion.Inverse(rot) * (c - pos);
                    if (local.z <= 0.001f) { fits = false; continue; }
                    float hFrac = Mathf.Atan2(Mathf.Abs(local.x), local.z) / hHalf;
                    float vAng = Mathf.Atan2(local.y, local.z);
                    float vFrac = Mathf.Abs(vAng) / vHalf;
                    worstH = Mathf.Max(worstH, hFrac);
                    worstV = Mathf.Max(worstV, vFrac);
                    if (hFrac > 1f || vFrac > 1f) fits = false;
                    float sy = 0.5f + 0.5f * (vAng / vHalf);
                    if (Mathf.Approximately(c.z, minZ)) nearY = sy;
                    if (Mathf.Approximately(c.z, maxZ)) farY = sy;
                }
                string verdict = fits
                    ? $"route FITS (H {worstH*100f:0.0}%, V {worstV*100f:0.0}% of half-frame)"
                    : $"route CUT OFF (H {worstH*100f:0.0}%, V {worstV*100f:0.0}%)";
                sb.AppendLine($"  {name}: hFOV {hHalf*2f*Mathf.Rad2Deg:0.0}deg | topMargin {(1f-farY)*100f:0.0}% botMargin {nearY*100f:0.0}% | {verdict}");
            }
            sb.AppendLine();
        }

        static void VerifyLighting(StringBuilder sb)
        {
            sb.AppendLine("--- LIGHTING ---");

            int dirLights = 0, dirWithShadows = 0;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                dirLights++;
                if (l.shadows != LightShadows.None) dirWithShadows++;
            }
            sb.AppendLine($"  Directional lights: {dirLights}, with shadows enabled: {dirWithShadows} (want 0)");

            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (rp == null) continue;
                bool main = GetPrivateBool(rp, "m_MainLightShadowsSupported");
                bool add = GetPrivateBool(rp, "m_AdditionalLightShadowsSupported");
                sb.AppendLine($"  URP '{Path.GetFileName(path)}': mainShadows={main} additionalShadows={add} (want false/false)");
            }

            var lpg = Object.FindFirstObjectByType<LightProbeGroup>();
            sb.AppendLine($"  LightProbeGroup: {(lpg != null ? lpg.probePositions.Length + " probes" : "MISSING")}");

            var rprobe = Object.FindFirstObjectByType<ReflectionProbe>();
            sb.AppendLine($"  ReflectionProbe: {(rprobe != null ? rprobe.mode.ToString() : "MISSING")}");

            // Static count
            int staticCount = 0, envRenderers = 0;
            foreach (var root in new[] { "RefineryLevel/Structures", "RefineryLevel/Core_Blockout", "RefineryLevel/Narrative" })
            {
                var go = GameObject.Find(root);
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    envRenderers++;
                    if (r.gameObject.isStatic) staticCount++;
                }
            }
            sb.AppendLine($"  Env renderers static: {staticCount}/{envRenderers}");

            // Baked lightmaps present?
            var lm = LightmapSettings.lightmaps;
            sb.AppendLine($"  Baked lightmap textures in scene: {(lm == null ? 0 : lm.Length)}");
            sb.AppendLine($"  Lightmapping.isRunning: {Lightmapping.isRunning}");
            sb.AppendLine();
        }

        static void VerifyBlobShadows(StringBuilder sb)
        {
            sb.AppendLine("--- BLOB SHADOWS ---");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(BlobMatPath);
            sb.AppendLine($"  Shared material: {(mat != null ? mat.name + " (shader " + mat.shader.name + ")" : "MISSING")}");

            int withBlob = 0, total = 0;
            var missing = new List<string>();
            foreach (var dir in new[] { EnemyDir, TowerDir })
            {
                if (!AssetDatabase.IsValidFolder(dir)) { sb.AppendLine($"  Folder missing: {dir}"); continue; }
                var paths = AssetDatabase.FindAssets("t:Prefab", new[] { dir })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == dir)
                    .Distinct();
                foreach (var p in paths)
                {
                    total++;
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    var t = go != null ? go.transform.Find("BlobShadow") : null;
                    bool ok = t != null;
                    if (ok)
                    {
                        var mr = t.GetComponent<MeshRenderer>();
                        bool sharesMat = mr != null && mr.sharedMaterial == mat;
                        if (sharesMat) withBlob++;
                        else missing.Add(Path.GetFileName(p) + " (wrong material)");
                    }
                    else missing.Add(Path.GetFileName(p) + " (no BlobShadow)");
                }
            }
            sb.AppendLine($"  Prefabs with shared-material blob: {withBlob}/{total}");
            if (missing.Count > 0) sb.AppendLine("  Issues: " + string.Join(", ", missing));
            sb.AppendLine();
        }

        static void VerifyOverlay(StringBuilder sb)
        {
            sb.AppendLine("--- ROTATE OVERLAY ---");
            var canvas = GameObject.Find("Canvas_RotatePrompt");
            if (canvas == null) { sb.AppendLine("  MISSING Canvas_RotatePrompt\n"); return; }
            var c = canvas.GetComponent<Canvas>();
            var overlay = canvas.GetComponent<Corehold.UI.RotateDeviceOverlay>();
            var panel = canvas.transform.Find("Panel");
            sb.AppendLine($"  Canvas sortingOrder={c?.sortingOrder}, RotateDeviceOverlay={(overlay != null ? "present" : "MISSING")}, Panel={(panel != null ? "present" : "MISSING")}");
            sb.AppendLine($"  EventSystem present: {Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null}");
            sb.AppendLine();
        }

        static bool GetPrivateBool(object obj, string field)
        {
            var f = obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null && (bool)f.GetValue(obj);
        }
    }
}
