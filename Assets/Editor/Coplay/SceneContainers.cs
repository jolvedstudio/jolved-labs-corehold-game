using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// THE container table — the one authority on which top-level container every
/// scene object belongs in (roadmap R26).
///
/// Two readers, by design:
///   • <see cref="OrganizeHierarchy"/> imposes it on the hand-built Game scene.
///   • <see cref="GenerationPipeline"/> emits generated scenes already grouped,
///     and verifies against it.
///
/// Keeping one table is the point: two lists that must agree will eventually
/// disagree, and "where does a new object go?" must have exactly one answer.
/// Adding an object to the game means adding it HERE, once.
///
/// Containers are pinned to the origin at identity every run. Not cosmetic:
/// <c>CameraShake</c> records the camera's LOCAL pose as its rest position, so a
/// container carrying a transform would silently redefine what "at rest" means.
/// </summary>
public static class SceneContainers
{
    /// <summary>
    /// Containers in display order, with the roots each adopts. Matching is
    /// exact, or by prefix when the entry ends in '*'. "Level*" covers generated
    /// level containers (R26), which are named after their blueprint rather than
    /// the shipped "RefineryLevel" literal.
    /// </summary>
    public static readonly (string container, string[] members)[] Groups =
    {
        ("_Systems", new[]
        {
            "GameManager", "WaveManager", "RouteTraffic", "PoolRegistry",
            "DebugConsole", "GameFlow", "InputRouter"
        }),
        ("_Directors", new[]
        {
            "AudioDirector", "VFXDirector", "OverlayManager", "WeatherApplier"
        }),
        ("_Level", new[]
        {
            "RefineryLevel", "Level*", "Floor", "SilhouetteBand", "Spawner_*"
        }),
        ("_UI", new[]
        {
            "EventSystem", "UITheme", "Canvas_HUD", "Canvas_Menus",
            "Canvas_RotatePrompt", "ResultScreen", "RangeRing*"
        }),
        ("_Rendering", new[]
        {
            "Main Camera", "Directional Light", "Global Volume",
            "ReflectionProbe", "LightProbeGroup"
        }),
    };

    /// <summary>True when the name is one of the five containers itself.</summary>
    public static bool IsContainer(string name)
    {
        foreach (var (containerName, _) in Groups)
            if (name == containerName)
                return true;
        return false;
    }

    /// <summary>The container a root of this name belongs in, or null when unrecognised.</summary>
    public static string ContainerFor(string objectName)
    {
        foreach (var (containerName, members) in Groups)
            foreach (string m in members)
            {
                if (m.EndsWith("*"))
                {
                    if (objectName.StartsWith(m.Substring(0, m.Length - 1)))
                        return containerName;
                }
                else if (objectName == m)
                {
                    return containerName;
                }
            }
        return null;
    }

    /// <summary>
    /// Find or create a container in the active scene, pinned to the origin at
    /// identity (see the class remarks for why the pin is load-bearing).
    /// </summary>
    public static Transform Ensure(string name)
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name != name)
                continue;
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        var created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(created, "Scene Containers");
        created.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        return created.transform;
    }

    /// <summary>
    /// Create all five containers (idempotent) and order them at the top of the
    /// hierarchy. The generator calls this FIRST so everything it builds can be
    /// parented at creation — emitted grouped, not organised after the fact.
    /// </summary>
    public static Dictionary<string, Transform> EnsureAll()
    {
        var map = new Dictionary<string, Transform>();
        foreach (var (containerName, _) in Groups)
            map[containerName] = Ensure(containerName);

        int index = 0;
        foreach (var (containerName, _) in Groups)
            map[containerName].SetSiblingIndex(index++);

        return map;
    }

    /// <summary>
    /// Sweep loose scene roots into their containers by the table, preserving
    /// world pose. Returns how many objects moved; anything unrecognised is left
    /// at the root and reported in <paramref name="unclaimed"/> rather than
    /// swept into a bucket. The verify pass of generation requires this to
    /// return ZERO — a non-zero count there means some tool emitted a root the
    /// table does not know, and the fix is to add it to <see cref="Groups"/>.
    /// </summary>
    public static int AdoptAll(List<string> unclaimed = null)
    {
        Scene scene = SceneManager.GetActiveScene();
        var containers = EnsureAll();

        int moved = 0;
        var roots = new List<GameObject>(scene.GetRootGameObjects());
        foreach (GameObject go in roots)
        {
            if (go == null || go.transform.parent != null || IsContainer(go.name))
                continue;

            string target = ContainerFor(go.name);
            if (target == null)
            {
                unclaimed?.Add(go.name);
                continue;
            }

            Undo.SetTransformParent(go.transform, containers[target], "Scene Containers");
            go.transform.SetParent(containers[target], true);   // keep world pose
            moved++;
        }
        return moved;
    }
}
