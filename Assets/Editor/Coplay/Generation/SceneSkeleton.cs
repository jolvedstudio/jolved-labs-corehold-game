using System.Collections.Generic;
using Corehold.Core;
using Corehold.Systems;
using Corehold.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

/// <summary>
/// The gameplay singletons a playable COREHOLD scene needs (R26).
///
/// This exists because the setup tools do not cover the whole set. Between
/// them, <c>PlayableBootstrapSetup</c> (GameManager + GameFlow),
/// <c>SetupAudioDirector</c>, <c>SetupVFXDirector</c> and <c>BuildRealUI</c>
/// create most of a scene — but <see cref="WaveManager"/>,
/// <see cref="PoolRegistry"/>, <see cref="RouteTraffic"/> and
/// <see cref="DebugConsole"/> only ever existed in <c>BuildGameScene.cs</c>,
/// the stale Ticket-28 scaffolding the roadmap forbids running because it
/// destroys scene roots. A generated scene therefore came out with no
/// WaveManager and nothing to run its waves.
///
/// Everything here is FIND-OR-CREATE, so it composes with the tools that ran
/// before it and is safe to re-run on an existing scene.
/// </summary>
public static class SceneSkeleton
{
    /// <summary>
    /// Ensure every gameplay singleton exists and is wired to the others.
    /// Spawner wiring is deliberately NOT done here — spawners do not exist
    /// until routes are built — see <see cref="WireSpawners"/>.
    /// </summary>
    public static string EnsureSingletons()
    {
        var made = new List<string>();

        PoolRegistry pool = Ensure<PoolRegistry>("PoolRegistry", made);
        WaveManager wave = Ensure<WaveManager>("WaveManager", made);
        Ensure<RouteTraffic>("RouteTraffic", made);
        Ensure<DebugConsole>("DebugConsole", made);
        ResultScreen result = SceneQuery.FirstInActiveScene<ResultScreen>()
                              ?? Ensure<ResultScreen>("ResultScreen", made);

        // THE EVENT SYSTEM. Nothing else creates one: BuildRealUI adds a
        // GraphicRaycaster to every canvas but no EventSystem, and the shipped
        // scene only has one because a human made it. Without it Unity performs
        // no UI raycasting at all — menus render and no click ever lands, while
        // pad taps keep working because those are physics raycasts. That is
        // exactly the "welcome screen shows but nothing is clickable" symptom.
        EnsureEventSystem(made);

        // WaveManager → PoolRegistry.
        var waveSo = new SerializedObject(wave);
        SerializedProperty poolProp = waveSo.FindProperty("pool");
        if (poolProp != null && poolProp.objectReferenceValue == null)
        {
            poolProp.objectReferenceValue = pool;
            waveSo.ApplyModifiedPropertiesWithoutUndo();
        }

        // ResultScreen → WaveManager. BuildRealUI may already have made one and
        // left it unwired; either way the reference must point at THIS scene's
        // manager, never a stale one.
        var resultSo = new SerializedObject(result);
        SerializedProperty wmProp = resultSo.FindProperty("waveManager");
        if (wmProp != null && wmProp.objectReferenceValue as WaveManager != wave)
        {
            wmProp.objectReferenceValue = wave;
            resultSo.ApplyModifiedPropertiesWithoutUndo();
        }

        return made.Count == 0
            ? "singletons already present"
            : $"created {string.Join(", ", made)}";
    }

    /// <summary>
    /// Point the WaveManager at the scene's spawners, ordered by their own
    /// index (0 west ground / 1 north ground / 2 air) rather than by creation
    /// order — the wave tables address spawners by that index, so a mismatch
    /// would send ground groups down the air corridor.
    /// </summary>
    public static string WireSpawners()
    {
        WaveManager wave = SceneQuery.FirstInActiveScene<WaveManager>();
        if (wave == null)
            return "no WaveManager to wire spawners into";

        var spawners = new List<Spawner>(SceneQuery.InActiveScene<Spawner>());
        if (spawners.Count == 0)
            return "no spawners in the scene";
        spawners.Sort((a, b) => a.Index.CompareTo(b.Index));

        var so = new SerializedObject(wave);
        SerializedProperty arr = so.FindProperty("spawners");
        if (arr == null)
            return "WaveManager has no 'spawners' field — wiring contract changed?";

        arr.arraySize = spawners.Count;
        for (int i = 0; i < spawners.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = spawners[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        var labels = new List<string>();
        foreach (Spawner s in spawners)
            labels.Add($"{s.Index}:{s.name}");
        return $"WaveManager spawners = [{string.Join(", ", labels)}]";
    }

    /// <summary>
    /// Find or create the EventSystem, carrying the NEW input system's UI module.
    /// The project runs on the Input System package, where the legacy
    /// StandaloneInputModule throws every frame and breaks UI raycasts — so the
    /// module choice is not cosmetic (see FixEventSystemInputModule, which
    /// repairs scenes that already have the wrong one).
    /// </summary>
    private static void EnsureEventSystem(List<string> made)
    {
        EventSystem existing = SceneQuery.FirstInActiveScene<EventSystem>();
        if (existing == null)
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
            Undo.RegisterCreatedObjectUndo(go, "Scene Skeleton");
            made.Add("EventSystem");
            return;
        }

        if (existing.GetComponent<InputSystemUIInputModule>() == null)
        {
            existing.gameObject.AddComponent<InputSystemUIInputModule>();
            made.Add("EventSystem input module");
        }
    }

    private static T Ensure<T>(string objectName, List<string> made) where T : Component
    {
        T existing = SceneQuery.FirstInActiveScene<T>();
        if (existing != null)
            return existing;

        var go = new GameObject(objectName);
        T added = go.AddComponent<T>();
        Undo.RegisterCreatedObjectUndo(go, "Scene Skeleton");
        made.Add(objectName);
        return added;
    }
}
