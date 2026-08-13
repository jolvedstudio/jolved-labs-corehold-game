using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Group the Game scene's root objects into readable containers.
/// Run: Tools → COREHOLD → Scene Setup → Organize Hierarchy.
///
/// The scene had grown to 28 roots in insertion order — directors, systems,
/// canvases, spawners, probes and five stray RangeRings interleaved — which is
/// hard to read and harder to spot omissions in.
///
/// Two things make this safe rather than a reshuffle:
///
///   • It moves objects through <c>SetParent(..., worldPositionStays: true)</c>,
///     so nothing moves in world space. Containers are pinned to the origin at
///     identity every run, which matters because <see cref="Corehold.Systems.CameraShake"/>
///     records the camera's LOCAL pose as its rest position — a container with a
///     non-identity transform would silently redefine that.
///   • Every editor tool now addresses objects through <see cref="SceneLookup"/>,
///     which resolves a path's first segment by name at any depth. Before that,
///     grouping <c>RefineryLevel</c> would have broken the blockout, camera
///     framing, lighting and three validators.
///
/// It is idempotent, it never deletes anything, and anything it does not
/// recognise is LEFT AT THE ROOT and reported rather than swept into a bucket.
/// </summary>
public static class OrganizeHierarchy
{
    // Containers in the order they should appear, with the roots each adopts.
    // Matching is exact, or by prefix when the entry ends in '*'.
    private static readonly (string container, string[] members)[] Groups =
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
            "RefineryLevel", "Floor", "SilhouetteBand", "Spawner_*"
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

    [MenuItem("Tools/COREHOLD/Scene Setup/Organize Hierarchy", false, 40)]
    public static void Organize()
    {
        Scene scene = SceneManager.GetActiveScene();
        var log = new StringBuilder();
        log.AppendLine($"=== Organize hierarchy — scene '{scene.name}' ===");

        // Snapshot the roots first: reparenting mutates the root list as we go.
        var roots = new List<GameObject>(scene.GetRootGameObjects());
        int rootsBefore = roots.Count;

        int moved = 0;
        var unclaimed = new List<string>();
        var counts = new Dictionary<string, int>();

        foreach (var (containerName, members) in Groups)
        {
            Transform container = EnsureContainer(containerName, scene);
            int n = 0;

            foreach (GameObject go in roots)
            {
                if (go == null || go.transform.parent != null)
                    continue;                       // already adopted this run
                if (IsContainer(go.name))
                    continue;
                if (!Matches(go.name, members))
                    continue;

                Undo.SetTransformParent(go.transform, container, "Organize Hierarchy");
                go.transform.SetParent(container, true);   // keep world pose
                moved++;
                n++;
            }
            counts[containerName] = n;
        }

        // Anything still at the root that is not itself a container.
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go != null && !IsContainer(go.name))
                unclaimed.Add(go.name);

        OrderContainers(scene);
        PromoteStrays(log);

        foreach (var (containerName, _) in Groups)
            log.AppendLine($"  {containerName,-12} {counts[containerName]} object(s)");
        log.AppendLine($"Roots {rootsBefore} → {Groups.Length + unclaimed.Count} " +
                       $"({moved} object(s) grouped, nothing deleted, world positions preserved)");

        if (unclaimed.Count > 0)
        {
            log.AppendLine();
            log.AppendLine("Left at the root (unrecognised — add them to a group in OrganizeHierarchy.cs " +
                           "rather than letting them drift):");
            foreach (string name in unclaimed)
                log.AppendLine($"  • {name}");
        }

        ReportDuplicates(scene, log);

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log(log.ToString());
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsContainer(string name)
    {
        foreach (var (containerName, _) in Groups)
            if (name == containerName)
                return true;
        return false;
    }

    private static bool Matches(string name, string[] members)
    {
        foreach (string m in members)
        {
            if (m.EndsWith("*"))
            {
                if (name.StartsWith(m.Substring(0, m.Length - 1)))
                    return true;
            }
            else if (name == m)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Find or create a container, and pin it to the origin at identity every run.
    /// That last part is not cosmetic: children keep their world pose on reparent,
    /// but CameraShake stores the camera's LOCAL rest pose, so a container that
    /// drifted off the origin would quietly change what "at rest" means.
    /// </summary>
    private static Transform EnsureContainer(string name, Scene scene)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name != name)
                continue;
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        var created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(created, "Organize Hierarchy");
        created.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        return created.transform;
    }

    private static void OrderContainers(Scene scene)
    {
        int index = 0;
        foreach (var (containerName, _) in Groups)
        {
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (go.name == containerName)
                {
                    go.transform.SetSiblingIndex(index++);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Rescue components that ended up nested somewhere accidental. The
    /// OverlayManager sits under a RangeRing in the shipped scene, which is
    /// clearly incidental — it is a director and belongs with the others.
    /// </summary>
    private static void PromoteStrays(StringBuilder log)
    {
        var overlay = Object.FindFirstObjectByType<Corehold.UI.OverlayManager>();
        if (overlay == null)
            return;

        Transform directors = null;
        foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
            if (go.name == "_Directors") { directors = go.transform; break; }

        if (directors != null && overlay.transform.parent != directors)
        {
            string was = overlay.transform.parent != null ? overlay.transform.parent.name : "<root>";
            Undo.SetTransformParent(overlay.transform, directors, "Organize Hierarchy");
            overlay.transform.SetParent(directors, true);
            log.AppendLine($"  [moved] OverlayManager was parented under '{was}' — promoted to _Directors");
        }
    }

    /// <summary>
    /// Report duplicate root names without touching them. Five RangeRings look
    /// like leftovers from repeated Build Real UI runs, but serialized references
    /// may point at specific instances, so deleting them is a human's call.
    /// </summary>
    private static void ReportDuplicates(Scene scene, StringBuilder log)
    {
        var seen = new Dictionary<string, int>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.transform)
            {
                seen.TryGetValue(child.name, out int c);
                seen[child.name] = c + 1;
            }
        }

        var dupes = new List<string>();
        foreach (var kv in seen)
            if (kv.Value > 1)
                dupes.Add($"{kv.Key} ×{kv.Value}");

        if (dupes.Count == 0)
            return;

        log.AppendLine();
        log.AppendLine("Duplicate names (NOT touched — serialized references may point at " +
                       "specific instances, so pruning is your call):");
        foreach (string d in dupes)
            log.AppendLine($"  • {d}");
    }
}
