using System.Collections.Generic;
using System.Text;
using Corehold.Core;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scaffolds the night lighting variant of the shipped layout (R23, [MANUAL]):
/// creates a "NightVariant" root carrying the <see cref="NightVariant"/> toggle
/// component and a "NightLights" child with up to TEN non-shadowing point lamps
/// auto-placed for readability — one over the Core, one at each route mouth,
/// the rest spread along the routes. The container starts DISABLED (day).
///
/// This is the CoPlay-assists half of the ticket. The human half: enter play,
/// press N (DebugConsole) to flip to night, then nudge/recolour the lamps until
/// the 907×510 legibility bar holds in the dark. Geometry is never touched.
///
/// Idempotent: re-running rebuilds the lamp set from scratch (hand-moved lamps
/// are replaced — adjust AFTER the layout feels right, or don't re-run).
/// </summary>
public static class SetupNightVariant
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";
    private const string RootName = "NightVariant";
    private const int MaxLamps = 10;

    [MenuItem("Tools/COREHOLD/Scene Setup/Night Variant", false, 49)]
    public static void Setup()
    {
        // Menu use: hop to the shipped scene if the human is elsewhere.
        // Pipeline use: NEVER — same guard as every scene-setup tool.
        Scene scene = SceneManager.GetActiveScene();
        if (!GenerationDriven.Active && scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var log = new StringBuilder();

        // Rebuild from scratch for idempotence.
        var stale = GameObject.Find(RootName);
        if (stale != null)
            Object.DestroyImmediate(stale);

        var root = new GameObject(RootName);
        root.AddComponent<NightVariant>();

        var lampRoot = new GameObject(NightVariant.LampContainerName);
        lampRoot.transform.SetParent(root.transform, false);

        int placed = PlaceLamps(lampRoot.transform, log);

        // Day by default — the NightVariant toggle owns this container's state.
        lampRoot.SetActive(false);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!GenerationDriven.Active)
            EditorSceneManager.SaveScene(scene);

        Debug.Log($"[COREHOLD] Night variant scaffolded: {placed} non-shadowing lamps " +
                  $"(cap {MaxLamps}), day state active.\n{log}" +
                  "Human pass: play, press N for night, adjust lamps for the 907×510 bar.");
    }

    private static int PlaceLamps(Transform parent, StringBuilder log)
    {
        var routes = new List<PathRoute>();
        foreach (var r in Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None))
            if (r != null && r.PointCount >= 2 && r.Length > 1f)
                routes.Add(r);

        int placed = 0;

        // 1. The Core: every route ends there; light it warm — it is the thing
        // the player must always be able to read.
        if (routes.Count > 0)
        {
            Vector3 core = routes[0].GetPoint(routes[0].PointCount - 1);
            AddLamp(parent, "Lamp_Core", core + Vector3.up * 6f,
                new Color(1f, 0.82f, 0.55f, 1f), 18f, 2.0f);
            placed++;
            log.AppendLine($"  Lamp_Core at {core}");
        }

        // 2. One cool lamp at each route mouth so spawns read in the dark.
        for (int i = 0; i < routes.Count && placed < MaxLamps; i++)
        {
            Vector3 mouth = routes[i].GetPoint(0);
            AddLamp(parent, $"Lamp_Mouth_{routes[i].name}", mouth + Vector3.up * 5f,
                new Color(0.65f, 0.8f, 1f, 1f), 16f, 1.5f);
            placed++;
            log.AppendLine($"  Lamp_Mouth_{routes[i].name} at {mouth}");
        }

        // 3. Spread the remaining budget along the routes (1/3 and 2/3 marks,
        // round-robin) so the marching columns stay visible between mouths and Core.
        float[] marks = { 1f / 3f, 2f / 3f };
        for (int m = 0; m < marks.Length && placed < MaxLamps; m++)
        {
            for (int i = 0; i < routes.Count && placed < MaxLamps; i++)
            {
                Vector3 p = routes[i].SamplePosition(routes[i].Length * marks[m], out _);
                AddLamp(parent, $"Lamp_Path_{routes[i].name}_{m + 1}", p + Vector3.up * 5f,
                    new Color(1f, 0.9f, 0.72f, 1f), 15f, 1.3f);
                placed++;
                log.AppendLine($"  Lamp_Path_{routes[i].name}_{m + 1} at {p}");
            }
        }

        if (routes.Count == 0)
            log.AppendLine("  ! No PathRoute in the scene — no lamps placed.");

        return placed;
    }

    private static void AddLamp(Transform parent, string name, Vector3 position,
        Color color, float range, float intensity)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;

        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.range = range;
        l.intensity = intensity;
        l.shadows = LightShadows.None; // the ticket's hard rule: non-shadowing only
    }
}
