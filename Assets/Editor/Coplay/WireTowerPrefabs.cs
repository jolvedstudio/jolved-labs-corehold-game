using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Data;

namespace CoreholdEditor
{
    /// <summary>
    /// The TowerDefinition assets had no basePrefab assigned, so TowerHardpoint.TryBuild
    /// always failed (it early-returns when basePrefab is null) — nothing could ever be
    /// built. This links each definition to its matching chassis prefab. Idempotent.
    /// </summary>
    public static class WireTowerPrefabs
    {
        // definition asset -> prefab asset
        static readonly (string def, string prefab)[] Map =
        {
            ("Assets/_COREHOLD/Data/Towers/Tower_Autocannon.asset",     "Assets/_COREHOLD/Prefabs/Towers/Tower_Autocannon.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_MissileBattery.asset", "Assets/_COREHOLD/Prefabs/Towers/Tower_Missile.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_ArcNode.asset",        "Assets/_COREHOLD/Prefabs/Towers/Tower_ArcNode.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_SiegeMortar.asset",    "Assets/_COREHOLD/Prefabs/Towers/Tower_SiegeMortar.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_ScanRelay.asset",      "Assets/_COREHOLD/Prefabs/Towers/Tower_ScanRelay.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_Floodlight.asset",     "Assets/_COREHOLD/Prefabs/Towers/Tower_Floodlight.prefab"),
            // Roster expansion — chassis prefabs are human-authored; rows link
            // them once they exist (missing prefabs are logged, not errors).
            ("Assets/_COREHOLD/Data/Towers/Tower_Railgun.asset",        "Assets/_COREHOLD/Prefabs/Towers/Tower_Railgun.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_CryoNode.asset",       "Assets/_COREHOLD/Prefabs/Towers/Tower_CryoNode.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_FlakArray.asset",      "Assets/_COREHOLD/Prefabs/Towers/Tower_FlakArray.prefab"),
            ("Assets/_COREHOLD/Data/Towers/Tower_SalvageRig.asset",     "Assets/_COREHOLD/Prefabs/Towers/Tower_SalvageRig.prefab"),
        };

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (var (defPath, prefabPath) in Map)
            {
                var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(defPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (def == null) { sb.AppendLine($"DEF MISSING: {defPath}"); continue; }
                if (prefab == null) { sb.AppendLine($"PREFAB MISSING: {prefabPath}"); continue; }

                if (def.basePrefab != prefab)
                {
                    def.basePrefab = prefab;
                    EditorUtility.SetDirty(def);
                    sb.AppendLine($"{def.name}.basePrefab = {prefab.name}");
                }
                else sb.AppendLine($"{def.name}: already wired");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[COREHOLD] WireTowerPrefabs:\n" + sb);
            return sb.ToString();
        }
    }
}
