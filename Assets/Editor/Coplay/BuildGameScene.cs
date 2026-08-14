using System.Text;
using Corehold.Core;
using Corehold.Systems;
using Corehold.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor utility (Ticket 28 support) that assembles a minimal but fully playable
/// COREHOLD Game scene so the win/lose conditions and the ResultScreen can be
/// exercised end to end. This is scaffolding for the acceptance test — the real
/// level art is a later ticket.
/// </summary>
public static class BuildGameScene
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";
    private const string LevelPath = "Assets/_COREHOLD/Data/Levels/Level_RefineryDelta.asset";

    public static string Execute()
    {
        var log = new StringBuilder();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // Clear any existing roots so re-running is idempotent.
        foreach (var root in scene.GetRootGameObjects())
            Object.DestroyImmediate(root);

        // ---- Camera ----
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
        cam.orthographic = false;
        camGo.transform.position = new Vector3(0f, 22f, -22f);
        camGo.transform.rotation = Quaternion.Euler(45f, 0f, 0f);
        camGo.AddComponent<AudioListener>();

        // ---- Light ----
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.shadows = LightShadows.None; // GDD §5.5: directional shadows disabled
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // ---- Floor ----
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = new Vector3(6f, 1f, 6f);

        // ---- Core (target) ----
        var core = GameObject.CreatePrimitive(PrimitiveType.Cube);
        core.name = "Core";
        core.transform.position = new Vector3(0f, 1f, 0f);
        core.transform.localScale = new Vector3(2f, 2f, 2f);
        var coreMat = core.GetComponent<Renderer>().sharedMaterial;
        // (leave default material; visual only)

        // ---- GameManager ----
        var gmGo = new GameObject("GameManager");
        gmGo.AddComponent<GameManager>();

        // ---- Pool ----
        var poolGo = new GameObject("PoolRegistry");
        var pool = poolGo.AddComponent<PoolRegistry>();

        // ---- Ground route (west -> core), (north -> core) ----
        PathRoute westRoute = BuildRoute("Route_West",
            new[]
            {
                new Vector3(-24f, 0f, -18f),
                new Vector3(-10f, 0f, -6f),
                new Vector3(0f, 0f, 0f),
            });

        PathRoute northRoute = BuildRoute("Route_North",
            new[]
            {
                new Vector3(0f, 0f, 26f),
                new Vector3(0f, 0f, 12f),
                new Vector3(0f, 0f, 0f),
            });

        // ---- Spawners ----
        Spawner west = BuildSpawner("Spawner_West", 0, new Vector3(-24f, 0f, -18f), westRoute, core.transform);
        Spawner north = BuildSpawner("Spawner_North", 1, new Vector3(0f, 0f, 26f), northRoute, core.transform);
        Spawner air = BuildSpawner("Spawner_Air", 2, new Vector3(20f, 4f, 20f), null, core.transform);

        // ---- WaveManager ----
        var wmGo = new GameObject("WaveManager");
        var wm = wmGo.AddComponent<WaveManager>();

        var level = AssetDatabase.LoadAssetAtPath<Corehold.Data.LevelDefinition>(LevelPath);

        var wmSo = new SerializedObject(wm);
        wmSo.FindProperty("level").objectReferenceValue = level;
        wmSo.FindProperty("pool").objectReferenceValue = pool;
        var spawnersProp = wmSo.FindProperty("spawners");
        spawnersProp.arraySize = 3;
        spawnersProp.GetArrayElementAtIndex(0).objectReferenceValue = west;
        spawnersProp.GetArrayElementAtIndex(1).objectReferenceValue = north;
        spawnersProp.GetArrayElementAtIndex(2).objectReferenceValue = air;
        wmSo.ApplyModifiedPropertiesWithoutUndo();

        // ---- ResultScreen ----
        var rsGo = new GameObject("ResultScreen");
        var rs = rsGo.AddComponent<ResultScreen>();
        var rsSo = new SerializedObject(rs);
        rsSo.FindProperty("waveManager").objectReferenceValue = wm;
        // No scene name to set: Retry reloads whatever scene it is playing.
        rsSo.ApplyModifiedPropertiesWithoutUndo();

        // ---- DebugConsole ----
        var dcGo = new GameObject("DebugConsole");
        dcGo.AddComponent<DebugConsole>();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        log.AppendLine("Assembled Game scene:");
        log.AppendLine($"  Level assigned: {(level != null ? level.name : "NULL (fallback rules)")}");
        log.AppendLine($"  Waves in level: {(level != null && level.waves != null ? level.waves.Length : 0)}");
        log.AppendLine("  Objects: Main Camera, Directional Light (no shadows), Floor, Core,");
        log.AppendLine("           GameManager, PoolRegistry, Route_West, Route_North,");
        log.AppendLine("           Spawner_West(0), Spawner_North(1), Spawner_Air(2),");
        log.AppendLine("           WaveManager, ResultScreen, DebugConsole");
        return log.ToString();
    }

    private static PathRoute BuildRoute(string name, Vector3[] points)
    {
        var routeGo = new GameObject(name);
        var route = routeGo.AddComponent<PathRoute>();

        var waypoints = new Transform[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var wp = new GameObject($"WP_{i}");
            wp.transform.SetParent(routeGo.transform, false);
            wp.transform.position = points[i];
            waypoints[i] = wp.transform;
        }

        var so = new SerializedObject(route);
        var wpProp = so.FindProperty("waypoints");
        wpProp.arraySize = waypoints.Length;
        for (int i = 0; i < waypoints.Length; i++)
            wpProp.GetArrayElementAtIndex(i).objectReferenceValue = waypoints[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        return route;
    }

    private static Spawner BuildSpawner(string name, int index, Vector3 pos, PathRoute route, Transform core)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        var spawner = go.AddComponent<Spawner>();

        var so = new SerializedObject(spawner);
        so.FindProperty("index").intValue = index;
        if (route != null)
            so.FindProperty("route").objectReferenceValue = route;
        so.FindProperty("coreTarget").objectReferenceValue = core;
        so.ApplyModifiedPropertiesWithoutUndo();

        return spawner;
    }
}
