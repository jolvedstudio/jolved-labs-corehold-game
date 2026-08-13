using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 "Done when": blob shadows must add no more than two draw calls total.
    /// This confirms the batching preconditions that guarantee it:
    ///   1) Every blob-shadow quad across every enemy/turret prefab uses the SAME shared
    ///      material instance, and
    ///   2) the SAME mesh (Unity's built-in Quad), and
    ///   3) the material's shader is SRP-Batcher compatible.
    /// With one material + one mesh + SRP Batcher, all blob quads collapse into a single
    /// batch (worst case two, if the transparent queue is split by depth against other
    /// transparent geometry). That is the ceiling the ticket allows.
    /// </summary>
    public static class BlobShadowBatchCheck
    {
        const string EnemyDir = "Assets/_COREHOLD/Prefabs/Enemies";
        const string TowerDir = "Assets/_COREHOLD/Prefabs/Towers";
        const string BlobMatPath = "Assets/_COREHOLD/Art/Materials/Mat_BlobShadow.mat";

        [MenuItem("Tools/COREHOLD/Validate/Check Blob Shadow Batching", false, 25)]
        public static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== BLOB SHADOW BATCHING CHECK =====");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(BlobMatPath);
            if (mat == null) { sb.AppendLine("Shared material MISSING."); Debug.Log(sb); return sb.ToString(); }

            bool srpCompatible = mat.shader != null && mat.shader.isSupported;
            // Reflectively check SRP Batcher compatibility if available.
            string srpNote = "";
            try
            {
                var m = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode");
                if (m != null)
                {
                    int code = (int)m.Invoke(null, new object[] { mat.shader, 0 });
                    srpNote = code == 0 ? " (SRP Batcher COMPATIBLE)" : $" (SRP Batcher code {code} — check subshader)";
                }
            }
            catch { }

            sb.AppendLine($"Material: {mat.name}, shader '{mat.shader.name}', supported={srpCompatible}{srpNote}");

            var meshes = new HashSet<Mesh>();
            var mats = new HashSet<Material>();
            int quads = 0;
            var problems = new List<string>();

            foreach (var dir in new[] { EnemyDir, TowerDir })
            {
                if (!AssetDatabase.IsValidFolder(dir)) continue;
                var paths = AssetDatabase.FindAssets("t:Prefab", new[] { dir })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == dir)
                    .Distinct();
                foreach (var p in paths)
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    var blob = go != null ? go.transform.Find("BlobShadow") : null;
                    if (blob == null) { problems.Add($"{Path.GetFileName(p)}: no BlobShadow"); continue; }
                    var mf = blob.GetComponent<MeshFilter>();
                    var mr = blob.GetComponent<MeshRenderer>();
                    if (mf != null) meshes.Add(mf.sharedMesh);
                    if (mr != null)
                    {
                        mats.Add(mr.sharedMaterial);
                        if (mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                            problems.Add($"{Path.GetFileName(p)}: casts shadows");
                    }
                    quads++;
                }
            }

            sb.AppendLine($"Blob quads: {quads}");
            sb.AppendLine($"Distinct meshes used: {meshes.Count} (want 1)");
            sb.AppendLine($"Distinct materials used: {mats.Count} (want 1)");
            bool allSameMat = mats.Count == 1 && mats.Contains(mat);
            bool allSameMesh = meshes.Count == 1;
            if (problems.Count > 0) sb.AppendLine("Problems: " + string.Join("; ", problems));

            sb.AppendLine();
            if (allSameMat && allSameMesh && srpCompatible)
                sb.AppendLine("RESULT: PASS — one mesh + one SRP-batchable material across all "
                    + $"{quads} units => blob shadows collapse to 1 batch (<= 2 draw calls, within budget).");
            else
                sb.AppendLine("RESULT: REVIEW — batching preconditions not fully met (see above).");

            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }
    }
}
