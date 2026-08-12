using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ticket 29 support: static audit of renderer counts per enemy prefab and per
/// turret prefab, so the draw-call breakdown can be compared against §13.2.
/// Counts both SkinnedMeshRenderers and MeshRenderers (each is its own draw).
/// </summary>
public static class CoreholdRendererAudit
{
    public static string Execute()
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== ENEMY PREFAB RENDERER COUNTS (§13.2 target: <= 2 per enemy) ===");
        AuditFolder("Assets/_COREHOLD/Prefabs/Enemies", sb);

        sb.AppendLine();
        sb.AppendLine("=== TURRET PREFAB RENDERER COUNTS (§13.2 target: <= 3 per turret) ===");
        AuditFolder("Assets/_COREHOLD/Prefabs/Towers", sb);

        return sb.ToString();
    }

    private static void AuditFolder(string folder, StringBuilder sb)
    {
        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
        foreach (var guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g)))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // Only top-level prefabs directly in the folder.
            if (System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != folder)
                continue;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;

            var skinned = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                .Where(r => r.sharedMesh != null).ToArray();
            var mesh = prefab.GetComponentsInChildren<MeshRenderer>(true)
                             .Where(r => r.GetComponent<MeshFilter>() != null &&
                                         r.GetComponent<MeshFilter>().sharedMesh != null).ToArray();

            int total = skinned.Length + mesh.Length;

            // Count unique materials on the unit (§13.1 "unique materials on units").
            int uniqueMaterials = skinned.SelectMany(r => r.sharedMaterials)
                                         .Concat(mesh.SelectMany(r => r.sharedMaterials))
                                         .Where(m => m != null)
                                         .Distinct().Count();

            sb.AppendLine($"{prefab.name,-16} : {total} renderers " +
                          $"(skinned={skinned.Length}, mesh={mesh.Length}), " +
                          $"uniqueMaterials={uniqueMaterials}");
        }
    }
}
