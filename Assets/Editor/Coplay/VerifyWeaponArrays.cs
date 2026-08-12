using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Enemies;

/// <summary>
/// Verifies the weapon-array refactor: every Enemy prefab exposes a WeaponCount from
/// its array, and every combat Tower tier resolves a non-empty Weapons array with a
/// sane total fire rate / damage. Read-only.
/// </summary>
public static class VerifyWeaponArrays
{
    public static string Execute()
    {
        var sb = new StringBuilder();

        string[] enemyPrefabs =
        {
            "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Drone.prefab",
        };

        sb.AppendLine("== Enemies ==");
        foreach (var path in enemyPrefabs)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { sb.AppendLine($"MISS: {path}"); continue; }
            var w = go.GetComponent<EnemyWeapon>();
            sb.AppendLine($"{go.name}: WeaponCount={(w != null ? w.WeaponCount : 0)}");
        }

        string[] towerDefs =
        {
            "Assets/_COREHOLD/Data/Towers/Tower_Autocannon.asset",
            "Assets/_COREHOLD/Data/Towers/Tower_MissileBattery.asset",
            "Assets/_COREHOLD/Data/Towers/Tower_ArcNode.asset",
            "Assets/_COREHOLD/Data/Towers/Tower_SiegeMortar.asset",
            "Assets/_COREHOLD/Data/Towers/Tower_ScanRelay.asset",
        };

        sb.AppendLine("== Towers ==");
        foreach (var path in towerDefs)
        {
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(path);
            if (def == null) { sb.AppendLine($"MISS: {path}"); continue; }
            sb.Append($"{def.name}: ");
            for (int t = 0; t < def.tiers.Length; t++)
            {
                var tier = def.tiers[t];
                var mounts = tier.Weapons;
                sb.Append($"[T{t + 1} mounts={mounts.Length} rate={tier.TotalFireRate:0.##} dmg={tier.TotalDamagePerVolley:0.##} dps={tier.TotalDps:0.##}] ");
            }
            sb.AppendLine();
        }

        Debug.Log("[COREHOLD] VerifyWeaponArrays:\n" + sb);
        return sb.ToString();
    }
}
