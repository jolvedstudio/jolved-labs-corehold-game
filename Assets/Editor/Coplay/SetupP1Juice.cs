using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// P1 juice wiring (roadmap R2+). Run once after pulling the P1 code:
/// Tools/COREHOLD/Setup P1 Juice.
///
///   • Creates Assets/_COREHOLD/Data/StreakConfig.asset (R2) if missing and
///     assigns it to the scene GameManager.
///   • Appends the new Sfx rows (StreakStep, …) to the AudioDirector's
///     serialized sfx table — the scene component was serialized before the
///     enum grew, so new entries do NOT appear on their own — and assigns a
///     clip from candidate vendor paths (Vendor/ is not in version control;
///     paths resolve on the dev machine). Missing clips are logged for manual
///     assignment; AudioDirector safely no-ops on an unassigned clip.
///
/// Safe to re-run: existing rows/assets are updated, never duplicated.
/// </summary>
public static class SetupP1Juice
{
    private const string ScenePath = "Assets/_COREHOLD/Scenes/Game.unity";
    private const string StreakConfigPath = "Assets/_COREHOLD/Data/StreakConfig.asset";
    private const string CreepySound = "Assets/Vendor/Creepy_Cat/3D Scifi Kit Vol 4/Sound/";

    // New one-shots wired by this ticket batch: enum id, base volume, pitch
    // spread, candidate clip paths (first that exists wins).
    private static readonly (AudioDirector.Sfx id, float volume, float spread, string[] candidates)[] NewSfx =
    {
        (AudioDirector.Sfx.StreakStep, 0.8f, 0f, new[]
        {
            CreepySound + "Interfaces/Light_Switch_B.wav",
            CreepySound + "Interfaces/Beep_A.wav",
            // Fallback: same clip as UIClick — acceptable because the streak
            // replays it pitch-scaled; swap for a distinct blip when auditioning.
            CreepySound + "Interfaces/Light_Switch_A.wav",
        }),
        (AudioDirector.Sfx.CloseCall, 0.9f, 0f, new[]
        {
            CreepySound + "Vocal/Msg_Alert.wav",
            CreepySound + "Vocal/Msg_Warning Intruder Alert.wav",
            CreepySound + "Interfaces/Gravity_Switch_B.wav",
            // Fallback: the Build puff switch — placeholder; swap when auditioning.
            CreepySound + "Interfaces/Gravity_Switch_A.wav",
        }),
    };

    [MenuItem("Tools/COREHOLD/Setup P1 Juice")]
    public static void Setup()
    {
        var log = new StringBuilder();

        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        SetupStreakConfig(log);
        SetupAudioRows(log);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[SetupP1Juice]\n" + log);
    }

    private static void SetupStreakConfig(StringBuilder log)
    {
        var config = AssetDatabase.LoadAssetAtPath<StreakConfig>(StreakConfigPath);
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<StreakConfig>();
            AssetDatabase.CreateAsset(config, StreakConfigPath);
            AssetDatabase.SaveAssets();
            log.AppendLine($"[ok] created {StreakConfigPath}");
        }
        else
        {
            log.AppendLine($"[ok] {StreakConfigPath} already exists");
        }

        var gm = Object.FindFirstObjectByType<GameManager>();
        if (gm == null)
        {
            log.AppendLine("[warn] no GameManager in scene — streakConfig not assigned.");
            return;
        }

        var so = new SerializedObject(gm);
        var prop = so.FindProperty("streakConfig");
        if (prop == null)
        {
            log.AppendLine("[warn] GameManager has no 'streakConfig' field — is the R2 code compiled?");
            return;
        }
        prop.objectReferenceValue = config;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(gm);
        log.AppendLine("[ok] GameManager.streakConfig assigned");
    }

    private static void SetupAudioRows(StringBuilder log)
    {
        var director = Object.FindFirstObjectByType<AudioDirector>();
        if (director == null)
        {
            log.AppendLine("[warn] no AudioDirector in scene — run Tools/COREHOLD/Setup Audio Director first.");
            return;
        }

        var so = new SerializedObject(director);
        var sfx = so.FindProperty("sfx");
        if (sfx == null || !sfx.isArray)
        {
            log.AppendLine("[warn] AudioDirector.sfx not found.");
            return;
        }

        foreach (var (id, volume, spread, candidates) in NewSfx)
        {
            // Find (or append) the row for this id.
            int rowIndex = -1;
            for (int i = 0; i < sfx.arraySize; i++)
            {
                var row = sfx.GetArrayElementAtIndex(i);
                if (row.FindPropertyRelative("id").enumValueIndex == (int)id)
                {
                    rowIndex = i;
                    break;
                }
            }
            if (rowIndex < 0)
            {
                rowIndex = sfx.arraySize;
                sfx.arraySize++;
                log.AppendLine($"[ok] appended sfx row for {id}");
            }

            var target = sfx.GetArrayElementAtIndex(rowIndex);
            target.FindPropertyRelative("id").enumValueIndex = (int)id;
            target.FindPropertyRelative("volume").floatValue = volume;
            target.FindPropertyRelative("pitchSpread").floatValue = spread;

            var clipProp = target.FindPropertyRelative("clip");
            if (clipProp.objectReferenceValue == null)
            {
                AudioClip clip = null;
                string used = null;
                foreach (var path in candidates)
                {
                    clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip != null) { used = path; break; }
                }
                if (clip != null)
                {
                    clipProp.objectReferenceValue = clip;
                    log.AppendLine($"[ok] {id} clip <- {used}");
                }
                else
                {
                    log.AppendLine($"[MANUAL] {id}: no candidate clip found under Vendor/ — " +
                                   "assign one on the AudioDirector inspector (plays silently until then).");
                }
            }
            else
            {
                log.AppendLine($"[ok] {id} clip already assigned — left untouched");
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
    }
}
