using System.Collections.Generic;
using System.Linq;
using Corehold.Systems;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a dedicated, self-contained scene that lines up EVERY tower prefab
/// against EVERY enemy prefab so all combat VFX interactions can be watched in
/// play mode without running a real game session. Creates:
///   - a camera framed on the two rows,
///   - a directional light + a dark ground so HDR effects read,
///   - a fully-wired VFXDirector (via SetupVFXDirector) and an AudioDirector,
///   - a CombatVFXArena that self-assembles the towers/enemies on Play.
///
/// Run 'Tools/COREHOLD/Validate/Build Combat VFX Test Scene', then press Play.
/// </summary>
public static class BuildCombatVFXTestScene
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/CombatVFX_Testbed.unity";
    private const string TowerDir = "Assets/_COREHOLD/Prefabs/Towers";
    private const string EnemyDir = "Assets/_COREHOLD/Prefabs/Enemies";

    [MenuItem("Tools/COREHOLD/Validate/Build Combat VFX Test Scene", false, 30)]
    public static void Build()
    {
        // New empty scene.
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- Camera ---
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
        cam.transform.position = new Vector3(0f, 22f, -30f);
        cam.transform.rotation = Quaternion.Euler(34f, 0f, 0f);
        cam.farClipPlane = 300f;
        camGo.AddComponent<AudioListener>();
        // Cinemachine 3 brain: renders whichever CinemachineCamera is live.
        camGo.AddComponent<CinemachineBrain>();

        // --- Orbit camera rig (yaw pivot -> pitch arm -> camera node) ---
        var yawPivot = new GameObject("OrbitRig");
        yawPivot.transform.position = Vector3.zero;
        var pitchArm = new GameObject("PitchArm");
        pitchArm.transform.SetParent(yawPivot.transform, false);
        pitchArm.transform.localRotation = Quaternion.Euler(30f, 0f, 0f);
        var camNode = new GameObject("CameraNode");
        camNode.transform.SetParent(pitchArm.transform, false);
        camNode.transform.localPosition = new Vector3(0f, 0f, -38f);
        var cmCam = camNode.AddComponent<CinemachineCamera>();
        cmCam.Priority = 100;

        var orbit = yawPivot.AddComponent<TestbedOrbitCamera>();
        orbit.yawPivot = yawPivot.transform;
        orbit.pitchArm = pitchArm.transform;
        orbit.cameraNode = camNode.transform;
        orbit.distance = 38f;

        // --- Light ---
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.color = new Color(0.9f, 0.92f, 1f);
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // --- Ground (dark, so HDR muzzle/tracer/impacts pop) ---
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(12f, 1f, 12f);
        var groundRenderer = ground.GetComponent<Renderer>();
        var groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        groundMat.color = new Color(0.10f, 0.11f, 0.13f, 1f);
        groundRenderer.sharedMaterial = groundMat;

        // --- Directors ---
        var vfxGo = new GameObject("VFXDirector");
        vfxGo.AddComponent<VFXDirector>();

        var audioGo = new GameObject("AudioDirector");
        audioGo.AddComponent<AudioDirector>();

        // Wire the VFXDirector's effect prefabs using the canonical setup tool,
        // scoped so it operates on THIS active scene and does not hop to Game.unity.
        using (GenerationDriven.Scope())
        {
            SetupVFXDirector.Setup();
            // Audio is optional for a visual test; wire it if the tool is safe to run.
            TryRun(() => SetupAudioDirector.Setup());
        }

        // --- Arena driver ---
        var arenaGo = new GameObject("CombatVFXArena");
        var arena = arenaGo.AddComponent<CombatVFXArena>();
        arena.towerPrefabs = LoadPrefabs(TowerDir);
        arena.enemies = LoadEnemyEntries(EnemyDir);
        arena.damageTable = LoadFirst<Corehold.Data.DamageTable>();
        arena.towerSpacing = 10f;
        arena.targetDistance = 8f;
        arena.airAltitude = 4f;
        arena.showcaseGap = 12f;

        EditorSceneManager.MarkSceneDirty(scene);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log($"[COREHOLD] Combat VFX Test Scene built at {ScenePath} — " +
                  $"{arena.towerPrefabs.Length} towers, {arena.enemies.Length} enemies. Press Play to watch.");
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(ScenePath));
    }

    private static GameObject[] LoadPrefabs(string dir)
    {
        var list = new List<GameObject>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null)
                list.Add(go);
        }
        return list.OrderBy(g => g.name).ToArray();
    }

    private static CombatVFXArena.EnemyEntry[] LoadEnemyEntries(string dir)
    {
        var list = new List<CombatVFXArena.EnemyEntry>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;

            // Read the definition off the prefab's mover (authored there) so armour
            // and the air flag are correct even after the mover is stripped at runtime.
            Corehold.Data.EnemyDefinition def = null;
            var mover = go.GetComponent<Corehold.Enemies.EnemyMover>();
            if (mover != null)
            {
                var so = new SerializedObject(mover);
                def = so.FindProperty("definition").objectReferenceValue as Corehold.Data.EnemyDefinition;
            }
            list.Add(new CombatVFXArena.EnemyEntry { prefab = go, definition = def });
        }
        return list.OrderBy(e => e.prefab.name).ToArray();
    }

    private static T LoadFirst<T>() where T : Object
    {
        string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
        return guids.Length > 0
            ? AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]))
            : null;
    }

    private static void TryRun(System.Action a)
    {
        try { a(); }
        catch (System.Exception e) { Debug.LogWarning($"[COREHOLD] Test scene: optional setup skipped — {e.Message}"); }
    }
}
