using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Enemies;

namespace CoreholdEditor
{
    /// <summary>
    /// Root cause of "enemies spawn stacked and never move": the enemy prefabs were
    /// built with Enemy + EnemyAnimatorBridge + Animator but WITHOUT an EnemyMover.
    /// With no mover, WaveManager.ConfigureSpawn's GetComponent&lt;EnemyMover&gt;() is
    /// null, so the route is never applied and nothing walks — every unit sits at its
    /// spawn point. This adds an EnemyMover to every enemy prefab that lacks one. The
    /// EnemyAnimatorBridge auto-finds the mover in Awake/OnEnable, so no extra wiring
    /// is needed. Idempotent.
    /// </summary>
    public static class AddEnemyMovers
    {
        static readonly string[] Prefabs =
        {
            "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",
            "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",
        };

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (var path in Prefabs)
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                if (root == null) { sb.AppendLine($"{path}: MISSING"); continue; }

                bool changed = false;
                var mover = root.GetComponent<EnemyMover>();
                if (mover == null)
                {
                    mover = root.AddComponent<EnemyMover>();
                    changed = true;
                    sb.AppendLine($"{root.name}: added EnemyMover");
                }
                else sb.AppendLine($"{root.name}: EnemyMover already present");

                // Ensure the animator does not use root motion (it would fight scripted movement).
                var anim = root.GetComponentInChildren<Animator>(true);
                if (anim != null && anim.applyRootMotion)
                {
                    anim.applyRootMotion = false;
                    changed = true;
                    sb.AppendLine($"{root.name}: disabled root motion");
                }

                if (changed)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[COREHOLD] AddEnemyMovers:\n" + sb);
            return sb.ToString();
        }
    }
}
