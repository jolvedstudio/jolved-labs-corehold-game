using System.Collections.Generic;
using System.IO;
using System.Text;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ticket 29 follow-up: reduce turret renderer count toward the §13.2 target of
/// ≤ 3 with a HIERARCHICAL, articulation-safe mesh combine.
///
/// CRITICAL FINDING (verified by TurretSkinProbe): on every turret the barrel/
/// weapon elevates by SKINNING — the pitch pivot (<see cref="TurretAim.PitchPivot"/>)
/// is a bone of the weapon's SkinnedMeshRenderer. Baking those skinned meshes to
/// static geometry would FREEZE the pitch and break aiming. So this pass:
///
///   • NEVER touches SkinnedMeshRenderers — they stay skinned so pitch works.
///   • Combines only the STATIC MeshRenderers, and only within a single rigid
///     zone so nothing that moves independently is welded together:
///        Zone A — Static base : static meshes NOT under the yaw pivot.
///        Zone B — Yaw group   : static meshes under the yaw pivot.
///     (No static mesh sits under a pitch pivot on any turret, so there is no
///      static pitch zone — the pitch parts are the skinned weapons, left alone.)
///
/// Each zone's static meshes are baked into that zone-root's local space and merged
/// into one MeshRenderer child (one submesh per unique material). Empty locators
/// (muzzle Barrel_End, RangeOrigin) and every script/skinned object are preserved;
/// only pure static-mesh nodes are removed.
///
/// Net effect, e.g. Autocannon: 9 static MRs + 1 skinned -> 2 combined MRs + 1
/// skinned = 3 renderers, hitting the target while keeping yaw AND pitch intact.
/// </summary>
public static class TurretMeshCombiner
{
    private const string MeshDir = "Assets/_COREHOLD/Prefabs/Towers/CombinedMeshes";

    private static readonly string[] Prefabs =
    {
        "Assets/_COREHOLD/Prefabs/Towers/Tower_Autocannon.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_Missile.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_ArcNode.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_SiegeMortar.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_ScanRelay.prefab",
    };

    public static string Execute()
    {
        Directory.CreateDirectory(MeshDir);
        var sb = new StringBuilder();

        foreach (var path in Prefabs)
            sb.AppendLine(CombineOne(path));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return sb.ToString();
    }

    private static string CombineOne(string prefabPath)
    {
        string name = Path.GetFileNameWithoutExtension(prefabPath);
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            var aim = root.GetComponent<TurretAim>();
            if (aim == null || aim.YawPivot == null)
                return $"{name}: SKIPPED (no TurretAim / yaw pivot).";

            Transform yaw = aim.YawPivot;

            int skinnedCount = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;

            // Collect ONLY static MeshRenderers with a mesh.
            var sources = new List<Source>();
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;
                sources.Add(new Source { tr = mr.transform, mesh = mf.sharedMesh, materials = mr.sharedMaterials });
            }

            int before = sources.Count + skinnedCount;

            // Partition static meshes into base vs yaw zones.
            var baseZone = new List<Source>();
            var yawZone = new List<Source>();
            foreach (var s in sources)
            {
                if (IsUnder(s.tr, yaw)) yawZone.Add(s);
                else baseZone.Add(s);
            }

            int made = 0;
            made += BuildZone(root.transform, baseZone, $"{name}_Base_Combined") ? 1 : 0;
            made += BuildZone(yaw, yawZone, $"{name}_Yaw_Combined") ? 1 : 0;

            int removed = RemoveConsumed(sources);

            int after = made + skinnedCount;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

            return $"{name}: {before} renderers ({sources.Count} static + {skinnedCount} skinned) " +
                   $"-> {after} ({made} combined static + {skinnedCount} skinned untouched). " +
                   $"base={baseZone.Count} yaw={yawZone.Count}; removed {removed} static mesh node(s).";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private struct Source
    {
        public Transform tr;
        public Mesh mesh;
        public Material[] materials;
    }

    /// <summary>
    /// Combine one zone's static meshes into a single mesh baked into
    /// <paramref name="zoneRoot"/>'s local space, emit one MeshRenderer child with
    /// one submesh per unique material, and save the mesh asset. False if empty.
    /// </summary>
    private static bool BuildZone(Transform zoneRoot, List<Source> zone, string meshName)
    {
        if (zone.Count == 0)
            return false;

        var byMaterial = new Dictionary<Material, List<CombineInstance>>();
        var materialOrder = new List<Material>();
        Matrix4x4 zoneWorldToLocal = zoneRoot.worldToLocalMatrix;

        foreach (var s in zone)
        {
            int subCount = s.mesh.subMeshCount;
            for (int sub = 0; sub < subCount; sub++)
            {
                Material mat = (s.materials != null && sub < s.materials.Length) ? s.materials[sub] : null;
                if (mat == null)
                    mat = (s.materials != null && s.materials.Length > 0) ? s.materials[0] : DefaultMaterial();

                var ci = new CombineInstance
                {
                    mesh = s.mesh,
                    subMeshIndex = sub,
                    transform = zoneWorldToLocal * s.tr.localToWorldMatrix,
                };

                if (!byMaterial.TryGetValue(mat, out var list))
                {
                    list = new List<CombineInstance>();
                    byMaterial[mat] = list;
                    materialOrder.Add(mat);
                }
                list.Add(ci);
            }
        }

        var perMaterialMeshes = new List<CombineInstance>();
        var finalMaterials = new List<Material>();
        foreach (var mat in materialOrder)
        {
            var sub = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            sub.CombineMeshes(byMaterial[mat].ToArray(), true, true); // one merged submesh per material
            perMaterialMeshes.Add(new CombineInstance { mesh = sub, subMeshIndex = 0, transform = Matrix4x4.identity });
            finalMaterials.Add(mat);
        }

        var finalMesh = new Mesh { name = meshName, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        finalMesh.CombineMeshes(perMaterialMeshes.ToArray(), false, false); // keep submesh per material
        finalMesh.RecalculateBounds();

        AssetDatabase.CreateAsset(finalMesh, $"{MeshDir}/{meshName}.asset");

        var go = new GameObject(meshName);
        go.transform.SetParent(zoneRoot, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        go.AddComponent<MeshFilter>().sharedMesh = finalMesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = finalMaterials.ToArray();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // GDD §5.5

        return true;
    }

    /// <summary>
    /// Remove consumed static-mesh nodes, but ONLY when the node is a pure mesh
    /// object (no other component, no children — children could be muzzle locators).
    /// Otherwise strip just the MeshFilter/MeshRenderer and keep the GameObject so
    /// nothing referencing it breaks.
    /// </summary>
    private static int RemoveConsumed(List<Source> sources)
    {
        int removed = 0;
        foreach (var s in sources)
        {
            if (s.tr == null) continue;
            var go = s.tr.gameObject;
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();

            bool pureMeshNode = go.transform.childCount == 0 && CountRelevantComponents(go) == 0;
            if (pureMeshNode)
            {
                Object.DestroyImmediate(go);
                removed++;
            }
            else
            {
                if (mr != null) Object.DestroyImmediate(mr);
                if (mf != null) Object.DestroyImmediate(mf);
            }
        }
        return removed;
    }

    private static int CountRelevantComponents(GameObject go)
    {
        int n = 0;
        foreach (var c in go.GetComponents<Component>())
        {
            if (c is Transform) continue;
            if (c is MeshFilter) continue;
            if (c is MeshRenderer) continue;
            n++;
        }
        return n;
    }

    private static bool IsUnder(Transform node, Transform ancestor)
    {
        for (Transform t = node; t != null; t = t.parent)
            if (t == ancestor) return true;
        return false;
    }

    private static Material _defaultMat;
    private static Material DefaultMaterial()
    {
        if (_defaultMat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _defaultMat = new Material(shader);
        }
        return _defaultMat;
    }
}
