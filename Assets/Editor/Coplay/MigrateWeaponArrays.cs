using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Towers;

/// <summary>
/// Migrates the single-weapon data on existing Enemy prefabs and Tower definitions
/// into the new weapon ARRAYS (EnemyWeapon.weapons, TowerTier.weapons), so weapon
/// data is authored as an array everywhere. Idempotent: skips any array already
/// populated. Also demonstrates genuine multi-weapon setups on units that visibly
/// carry two weapons (the Strider's twin grenade launchers, the Autocannon's twin
/// barrels).
/// </summary>
public static class MigrateWeaponArrays
{
    private static readonly string[] EnemyPrefabs =
    {
        "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",
        "Assets/_COREHOLD/Prefabs/Enemies/Drone.prefab",
    };

    // Enemies that visibly carry TWO weapons get two mounts (split the authored
    // damage across both so total output is unchanged).
    private static readonly System.Collections.Generic.HashSet<string> TwinWeaponEnemies =
        new System.Collections.Generic.HashSet<string> { "Strider" };

    private static readonly string[] TowerDefs =
    {
        "Assets/_COREHOLD/Data/Towers/Tower_Autocannon.asset",
        "Assets/_COREHOLD/Data/Towers/Tower_MissileBattery.asset",
        "Assets/_COREHOLD/Data/Towers/Tower_ArcNode.asset",
        "Assets/_COREHOLD/Data/Towers/Tower_SiegeMortar.asset",
        "Assets/_COREHOLD/Data/Towers/Tower_ScanRelay.asset",
    };

    // Turrets that visibly carry twin barrels get two hitscan mounts per tier.
    private static readonly System.Collections.Generic.HashSet<string> TwinBarrelTowers =
        new System.Collections.Generic.HashSet<string> { "Tower_Autocannon" };

    public static string Execute()
    {
        var sb = new StringBuilder();
        MigrateEnemies(sb);
        MigrateTowers(sb);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] MigrateWeaponArrays:\n" + sb);
        return sb.ToString();
    }

    private static void MigrateEnemies(StringBuilder sb)
    {
        foreach (var path in EnemyPrefabs)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { sb.AppendLine($"MISS: {path}"); continue; }

            var weapon = root.GetComponent<EnemyWeapon>();
            if (weapon == null) { PrefabUtility.UnloadPrefabContents(root); sb.AppendLine($"NO EnemyWeapon: {path}"); continue; }

            var so = new SerializedObject(weapon);
            var arr = so.FindProperty("weapons");

            string enemyName = System.IO.Path.GetFileNameWithoutExtension(path);
            int count = TwinWeaponEnemies.Contains(enemyName) ? 2 : 1;

            // Prefer any values already authored on the first mount (so a re-run does
            // not clobber hand tuning), else fall back to the retained legacy fields.
            float range, rate, damage;
            Color tracer;
            Object muzzle;
            if (arr.arraySize > 0)
            {
                var el0 = arr.GetArrayElementAtIndex(0);
                range = el0.FindPropertyRelative("range").floatValue;
                rate = el0.FindPropertyRelative("fireRate").floatValue;
                // Recover the pre-split total damage by multiplying by the old count.
                damage = el0.FindPropertyRelative("damage").floatValue * arr.arraySize;
                tracer = el0.FindPropertyRelative("tracerColor").colorValue;
                muzzle = el0.FindPropertyRelative("muzzle").objectReferenceValue;
            }
            else
            {
                range = so.FindProperty("range").floatValue;
                rate = so.FindProperty("fireRate").floatValue;
                damage = so.FindProperty("damage").floatValue;
                tracer = so.FindProperty("tracerColor").colorValue;
                muzzle = so.FindProperty("muzzle").objectReferenceValue;
            }

            arr.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("range").floatValue = range;
                el.FindPropertyRelative("fireRate").floatValue = rate;
                // Split damage across twin weapons so total output is unchanged.
                el.FindPropertyRelative("damage").floatValue = damage / count;
                el.FindPropertyRelative("muzzle").objectReferenceValue = i == 0 ? muzzle : null;
                el.FindPropertyRelative("tracerColor").colorValue = tracer.a > 0f ? tracer : new Color(4f, 1.2f, 0.3f, 1f);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            sb.AppendLine($"OK enemy: {path} weapons={count}");
        }
    }

    private static void MigrateTowers(StringBuilder sb)
    {
        foreach (var path in TowerDefs)
        {
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(path);
            if (def == null) { sb.AppendLine($"MISS: {path}"); continue; }

            var so = new SerializedObject(def);
            var tiers = so.FindProperty("tiers");
            if (tiers == null || tiers.arraySize == 0) { sb.AppendLine($"NO tiers: {path}"); continue; }

            string towerName = System.IO.Path.GetFileNameWithoutExtension(path);
            int barrels = TwinBarrelTowers.Contains(towerName) ? 2 : 1;

            int migrated = 0;
            for (int t = 0; t < tiers.arraySize; t++)
            {
                var tier = tiers.GetArrayElementAtIndex(t);
                var weapons = tier.FindPropertyRelative("weapons");

                // Recover the tier's per-shot combat values, preferring an already
                // authored first mount so a re-run does not clobber hand tuning.
                float damage, fireRate, splash, chainFalloff, projSpeed;
                int chainTargets;
                Object proj;
                if (weapons.arraySize > 0)
                {
                    var e0 = weapons.GetArrayElementAtIndex(0);
                    damage = e0.FindPropertyRelative("damage").floatValue;
                    fireRate = e0.FindPropertyRelative("fireRate").floatValue;
                    splash = e0.FindPropertyRelative("splashRadius").floatValue;
                    chainTargets = e0.FindPropertyRelative("chainTargets").intValue;
                    chainFalloff = e0.FindPropertyRelative("chainFalloff").floatValue;
                    proj = e0.FindPropertyRelative("projectilePrefab").objectReferenceValue;
                    projSpeed = e0.FindPropertyRelative("projectileSpeed").floatValue;
                    // If a previous run split damage across two mounts, recover the total.
                    if (weapons.arraySize == 2)
                        damage *= 2f;
                }
                else
                {
                    damage = tier.FindPropertyRelative("damage").floatValue;
                    fireRate = tier.FindPropertyRelative("fireRate").floatValue;
                    splash = tier.FindPropertyRelative("splashRadius").floatValue;
                    chainTargets = tier.FindPropertyRelative("chainTargets").intValue;
                    chainFalloff = tier.FindPropertyRelative("chainFalloff").floatValue;
                    proj = tier.FindPropertyRelative("projectilePrefab").objectReferenceValue;
                    projSpeed = tier.FindPropertyRelative("projectileSpeed").floatValue;
                }

                // Support relays (Scan Relay) fire nothing — leave the array empty so
                // the tier reads as non-combat (fireRate 0). Only author combat tiers.
                if (fireRate <= 0f && damage <= 0f)
                {
                    weapons.arraySize = 0;
                    continue;
                }

                // Author ONE weapon per tier. A twin-barrel turret is a SINGLE weapon
                // that alternates between two muzzles (muzzleIndices 0 and 1) — this is
                // purely visual and keeps fire rate and DPS identical to the original.
                weapons.arraySize = 1;
                var el = weapons.GetArrayElementAtIndex(0);
                el.FindPropertyRelative("damage").floatValue = damage;
                el.FindPropertyRelative("fireRate").floatValue = fireRate;
                el.FindPropertyRelative("chainTargets").intValue = chainTargets;
                el.FindPropertyRelative("chainFalloff").floatValue = chainFalloff;
                el.FindPropertyRelative("projectilePrefab").objectReferenceValue = proj;
                el.FindPropertyRelative("projectileSpeed").floatValue = projSpeed;
                el.FindPropertyRelative("splashRadius").floatValue = splash;
                el.FindPropertyRelative("tracerColor").colorValue = new Color(3.5f, 2.2f, 0.8f, 1f);

                var muzzleIndices = el.FindPropertyRelative("muzzleIndices");
                if (barrels > 1)
                {
                    el.FindPropertyRelative("muzzleIndex").intValue = 0;
                    muzzleIndices.arraySize = barrels;
                    for (int m = 0; m < barrels; m++)
                        muzzleIndices.GetArrayElementAtIndex(m).intValue = m;
                }
                else
                {
                    el.FindPropertyRelative("muzzleIndex").intValue = -1;
                    muzzleIndices.arraySize = 0;
                }
                migrated++;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(def);
            sb.AppendLine($"OK tower: {path} tiersMigrated={migrated} barrels={barrels}");
        }
    }
}
