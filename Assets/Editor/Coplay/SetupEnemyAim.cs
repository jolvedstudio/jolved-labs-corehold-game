using System.Collections.Generic;
using System.Text;
using Corehold.Enemies;
using Corehold.Systems;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes an <see cref="EnemyAim"/> onto every enemy PREFAB (with its yaw/pitch pivots
/// resolved and serialized), so enemies ship with turret aim the same way towers ship
/// with TurretAim — instead of the arena bolting it on at runtime. Run once; safe to
/// re-run (it re-resolves pivots).
/// </summary>
public static class SetupEnemyAim
{
    private const string EnemyPrefabDir = "Assets/_COREHOLD/Prefabs/Enemies";

    [MenuItem("Tools/COREHOLD/Scene Setup/Bake EnemyAim onto enemy prefabs", false, 44)]
    public static void Bake()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { EnemyPrefabDir });
        var log = new StringBuilder();
        int added = 0, baked = 0, skipped = 0;

        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                // Only real enemies (must have the Enemy + EnemyWeapon behaviours).
                var enemy = root.GetComponent<Enemy>();
                var weapon = root.GetComponent<EnemyWeapon>();
                if (enemy == null || weapon == null)
                {
                    skipped++;
                    continue;
                }

                var aim = root.GetComponent<EnemyAim>();
                if (aim == null)
                {
                    aim = root.AddComponent<EnemyAim>();
                    added++;
                }

                bool ok = aim.BakePivots();
                baked += ok ? 1 : 0;
                log.AppendLine($"• {System.IO.Path.GetFileNameWithoutExtension(path)}: " +
                               (ok ? "pivots resolved" : "NO pivots found (no muzzle?)"));

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[COREHOLD] EnemyAim bake complete: {added} added, {baked} with pivots, " +
                  $"{skipped} non-enemy prefabs skipped.\n{log}");
    }
}
