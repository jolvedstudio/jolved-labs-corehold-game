using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Corehold.Core;
using Corehold.Enemies;

/// <summary>
/// One-shot scene/prefab setup for the 1-D lane navigation:
///   • Ensures a RouteTraffic manager exists in the open scene.
///   • Sets sensible bodyRadius per enemy prefab (footprint-derived, wide bodies
///     flagged by radius > wideBodyThreshold occupy all lanes at runtime).
///   • Sets ground routes to 2 lanes; leaves the air corridor implicit (1 lane).
/// </summary>
public static class SetupLaneNavigation
{
    // Footprint-appropriate radii (metres). Kept modest so lanes fit the ±0.9 band;
    // Breaker/Strider exceed the wide threshold and will occupy both lanes.
    private static readonly (string path, float radius)[] Radii =
    {
        ("Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab", 0.6f),
        ("Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",   0.6f),
        ("Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",   0.65f),
        ("Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",     0.55f),
        ("Assets/_COREHOLD/Prefabs/Enemies/Drone.prefab",    0.4f),
        ("Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",  1.0f),   // wide
        ("Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",  1.15f),  // wide
    };

    public static string Execute()
    {
        var sb = new StringBuilder();

        // 1. RouteTraffic manager in the scene.
        var rt = Object.FindFirstObjectByType<RouteTraffic>();
        if (rt == null)
        {
            var go = new GameObject("RouteTraffic");
            go.AddComponent<RouteTraffic>();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            sb.AppendLine("Created RouteTraffic in scene.");
        }
        else sb.AppendLine("RouteTraffic already present.");

        // 2. Enemy body radii on prefabs.
        foreach (var (path, radius) in Radii)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) { sb.AppendLine($"MISS {path}"); continue; }
            try
            {
                var mover = root.GetComponentInChildren<EnemyMover>(true);
                if (mover == null) { sb.AppendLine($"no mover {path}"); continue; }
                var so = new SerializedObject(mover);
                var p = so.FindProperty("bodyRadius");
                if (p != null) { p.floatValue = radius; so.ApplyModifiedPropertiesWithoutUndo(); }
                PrefabUtility.SaveAsPrefabAsset(root, path);
                sb.AppendLine($"{System.IO.Path.GetFileNameWithoutExtension(path)}: bodyRadius={radius}");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // 3. Route lane counts (2 lanes ground). Routes are children under RefineryLevel/Routes.
        foreach (var route in Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None))
        {
            var so = new SerializedObject(route);
            var lc = so.FindProperty("laneCount");
            var hw = so.FindProperty("laneHalfWidth");
            if (lc != null) lc.intValue = 2;
            if (hw != null && hw.floatValue < 0.1f) hw.floatValue = 0.9f;
            so.ApplyModifiedPropertiesWithoutUndo();
            sb.AppendLine($"route '{route.name}': lanes=2 halfWidth={route.LaneHalfWidth:0.##}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorSceneManager.SaveOpenScenes();
        return sb.ToString();
    }
}
