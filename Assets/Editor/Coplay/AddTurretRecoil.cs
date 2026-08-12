using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Towers;

/// <summary>Adds TurretBarrelSpin (recoil-on-fire) to combat turret prefabs.</summary>
public static class AddTurretRecoil
{
    private static readonly string[] Prefabs =
    {
        "Assets/_COREHOLD/Prefabs/Towers/Tower_Autocannon.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_ArcNode.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_Missile.prefab",
        "Assets/_COREHOLD/Prefabs/Towers/Tower_SiegeMortar.prefab",
    };

    public static string Execute()
    {
        var sb = new StringBuilder();
        foreach (var path in Prefabs)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { sb.AppendLine($"MISS {path}"); continue; }

            if (root.GetComponent<TurretBarrelSpin>() == null)
            {
                root.AddComponent<TurretBarrelSpin>();
                PrefabUtility.SaveAsPrefabAsset(root, path);
                sb.AppendLine($"OK   {path}");
            }
            else sb.AppendLine($"skip {path} (already has recoil)");

            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return sb.ToString();
    }
}
