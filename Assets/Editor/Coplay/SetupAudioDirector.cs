using System.Collections.Generic;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup for the COREHOLD AudioDirector (GDD §10). Creates (or updates) an
/// AudioDirector + TurretRotationAudio GameObject in the Game scene, assigns the
/// one-shot clips from the Turret SFX and Creepy Cat sound folders, the rotation
/// loop and a music/ambient bed, and forces every referenced clip to
/// CompressedInMemory on the WebGL platform override (GDD §10). Run once; safe to
/// re-run.
/// </summary>
public static class SetupAudioDirector
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";

    private const string TurretSfx = "Assets/Vendor/IndieGameModels/SFX/Turret SFX/sounds/";
    private const string CreepySound = "Assets/Vendor/Creepy_Cat/3D Scifi Kit Vol 4/Sound/";

    // Logical one-shot -> clip path (GDD §10).
    private static readonly (AudioDirector.Sfx id, string path)[] SfxMap =
    {
        (AudioDirector.Sfx.FireKinetic, TurretSfx + "Turret/turret_fire.wav"),
        (AudioDirector.Sfx.FireEnergy,  TurretSfx + "Laser turret/laser_turret_fire.wav"),
        (AudioDirector.Sfx.FireMissile, TurretSfx + "Missle Turret/missle_fire.wav"),
        (AudioDirector.Sfx.FireMortar,  TurretSfx + "Turret/turret_fire3.wav"),
        (AudioDirector.Sfx.Impact,      TurretSfx + "Laser turret/energyimpact_small.wav"),
        (AudioDirector.Sfx.Explosion,   TurretSfx + "Missle Turret/explosion missile 01.wav"),
        (AudioDirector.Sfx.EnemyDeath,  CreepySound + "Explosions/Explode_02.wav"),
        (AudioDirector.Sfx.UIClick,     CreepySound + "Interfaces/Light_Switch_A.wav"),
        (AudioDirector.Sfx.Build,       CreepySound + "Interfaces/Gravity_Switch_A.wav"),
        (AudioDirector.Sfx.CoreAlarm,   CreepySound + "Vocal/Msg_Warning Restricted Access.wav"),
    };

    private const string RotationLoopPath = TurretSfx + "Turret/rotate_loop.wav";
    private const string MusicPath = CreepySound + "Ambiant/FreeMusic_By_CreepyCat_01.wav";

    [MenuItem("Tools/COREHOLD/Setup Audio Director")]
    public static void Setup()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var director = Object.FindFirstObjectByType<AudioDirector>();
        if (director == null)
        {
            var go = new GameObject("AudioDirector");
            director = go.AddComponent<AudioDirector>();
            go.AddComponent<TurretRotationAudio>();
            Undo.RegisterCreatedObjectUndo(go, "Create AudioDirector");
        }
        else if (director.GetComponent<TurretRotationAudio>() == null)
        {
            Undo.AddComponent<TurretRotationAudio>(director.gameObject);
        }

        var missing = new List<string>();
        var so = new SerializedObject(director);

        // One-shot slots.
        SerializedProperty sfx = so.FindProperty("sfx");
        sfx.arraySize = SfxMap.Length;
        for (int i = 0; i < SfxMap.Length; i++)
        {
            var entry = SfxMap[i];
            var clip = LoadAndCompress(entry.path, missing);

            SerializedProperty element = sfx.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("id").enumValueIndex = (int)entry.id;
            element.FindPropertyRelative("clip").objectReferenceValue = clip;
            // Preserve any authored volume/pitch already set in the array element;
            // only initialise them when this slot is freshly grown (volume == 0).
            SerializedProperty vol = element.FindPropertyRelative("volume");
            if (vol.floatValue <= 0f)
                vol.floatValue = DefaultVolume(entry.id);
            SerializedProperty spread = element.FindPropertyRelative("pitchSpread");
            if (spread.floatValue <= 0f)
                spread.floatValue = DefaultSpread(entry.id);
        }

        // Rotation loop + music.
        so.FindProperty("rotationLoopClip").objectReferenceValue = LoadAndCompress(RotationLoopPath, missing);
        so.FindProperty("musicClip").objectReferenceValue = LoadAndCompress(MusicPath, missing);

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        if (missing.Count > 0)
            Debug.LogError("[COREHOLD] AudioDirector setup: missing clips:\n- " + string.Join("\n- ", missing));
        else
            Debug.Log($"[COREHOLD] AudioDirector setup complete: {SfxMap.Length} one-shots + rotation loop + music assigned, WebGL load type set to CompressedInMemory, scene saved.");
    }

    /// <summary>
    /// Load a clip and force its WebGL platform override to CompressedInMemory
    /// (GDD §10). Returns null and records the path if it does not exist.
    /// </summary>
    private static AudioClip LoadAndCompress(string path, List<string> missing)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (clip == null)
        {
            missing.Add(path);
            return null;
        }

        var importer = AssetImporter.GetAtPath(path) as AudioImporter;
        if (importer != null)
        {
            var web = importer.GetOverrideSampleSettings("WebGL");
            bool changed = !importer.ContainsSampleSettingsOverride("WebGL")
                           || web.loadType != AudioClipLoadType.CompressedInMemory;

            web.loadType = AudioClipLoadType.CompressedInMemory;
            if (web.compressionFormat == AudioCompressionFormat.PCM)
                web.compressionFormat = AudioCompressionFormat.Vorbis;

            if (changed)
            {
                importer.SetOverrideSampleSettings("WebGL", web);
                importer.SaveAndReimport();
            }
        }

        return clip;
    }

    private static float DefaultVolume(AudioDirector.Sfx id)
    {
        switch (id)
        {
            case AudioDirector.Sfx.Impact: return 0.6f;
            case AudioDirector.Sfx.EnemyDeath: return 0.7f;
            case AudioDirector.Sfx.UIClick: return 0.8f;
            case AudioDirector.Sfx.Build: return 0.8f;
            case AudioDirector.Sfx.FireMortar: return 1.0f;
            case AudioDirector.Sfx.CoreAlarm: return 1.0f;
            default: return 0.9f;
        }
    }

    private static float DefaultSpread(AudioDirector.Sfx id)
    {
        switch (id)
        {
            case AudioDirector.Sfx.FireKinetic:
            case AudioDirector.Sfx.FireEnergy:
            case AudioDirector.Sfx.FireMissile:
            case AudioDirector.Sfx.FireMortar:
                return 0.08f; // ±8% on turret fire (GDD §10)
            case AudioDirector.Sfx.Impact:
            case AudioDirector.Sfx.Explosion:
            case AudioDirector.Sfx.EnemyDeath:
                return 0.06f;
            default:
                return 0f;
        }
    }
}
