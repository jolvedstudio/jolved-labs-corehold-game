using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Enemies;

/// <summary>
/// Adds an EnemyWeapon to the enemy prefabs so units fire back at turrets (item b).
/// Values are modest per type so return fire is felt without trivialising the run.
/// </summary>
public static class AddEnemyWeapons
{
    private struct Stats { public string path; public float range; public float rate; public float dmg; }

    public static string Execute()
    {
        var sb = new StringBuilder();
        var list = new[]
        {
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab", range = 10f, rate = 0.7f, dmg = 6f },
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",  range = 12f, rate = 0.6f, dmg = 9f },
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",   range = 14f, rate = 0.5f, dmg = 14f },
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",   range = 9f,  rate = 0.9f, dmg = 7f },
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",  range = 11f, rate = 0.4f, dmg = 22f },
            new Stats { path = "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",     range = 13f, rate = 0.8f, dmg = 5f },
        };

        foreach (var s in list)
        {
            var root = PrefabUtility.LoadPrefabContents(s.path);
            if (root == null) { sb.AppendLine($"MISS: {s.path}"); continue; }

            var weapon = root.GetComponent<EnemyWeapon>();
            if (weapon == null)
                weapon = root.AddComponent<EnemyWeapon>();

            // Author the weapons ARRAY directly (enemies may carry several weapons).
            // Here each unit gets a single mount; multi-weapon units author more.
            var so = new SerializedObject(weapon);
            var arr = so.FindProperty("weapons");
            arr.arraySize = 1;
            var el = arr.GetArrayElementAtIndex(0);
            el.FindPropertyRelative("range").floatValue = s.range;
            el.FindPropertyRelative("fireRate").floatValue = s.rate;
            el.FindPropertyRelative("damage").floatValue = s.dmg;
            el.FindPropertyRelative("tracerColor").colorValue = new Color(4f, 1.2f, 0.3f, 1f); // HDR-bright hostile
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, s.path);
            PrefabUtility.UnloadPrefabContents(root);
            sb.AppendLine($"OK: {s.path} range={s.range} rate={s.rate} dmg={s.dmg}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return sb.ToString();
    }
}
