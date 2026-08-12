using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Adds a global post-processing Volume with Bloom + Tonemapping to the Game scene
/// so the HDR-bright tracer bolts and muzzle flashes visibly glow. Prioritises
/// engagement/readability over render budget.
/// </summary>
public static class SetupBloomVolume
{
    private const string ProfilePath = "Assets/_COREHOLD/Settings/COREHOLD_PostFX.asset";

    public static string Execute()
    {
        // Build (or load) a shared volume profile with Bloom.
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
        }

        if (!profile.TryGet(out Bloom bloom))
            bloom = profile.Add<Bloom>(true);
        bloom.active = true;
        // Moderate bloom: glow on bright bolts without blowing out muzzle flashes.
        bloom.threshold.overrideState = true; bloom.threshold.value = 1.1f;
        bloom.intensity.overrideState = true; bloom.intensity.value = 0.9f;
        bloom.scatter.overrideState = true; bloom.scatter.value = 0.6f;
        bloom.tint.overrideState = true; bloom.tint.value = Color.white;

        if (!profile.TryGet(out Tonemapping tone))
            tone = profile.Add<Tonemapping>(true);
        tone.active = true;
        tone.mode.overrideState = true; tone.mode.value = TonemappingMode.ACES;

        EditorUtility.SetDirty(profile);

        // Place a global Volume in the scene.
        var existing = Object.FindFirstObjectByType<Volume>();
        Volume vol = existing;
        if (vol == null)
        {
            var go = new GameObject("Global Volume");
            vol = go.AddComponent<Volume>();
        }
        vol.isGlobal = true;
        vol.priority = 1f;
        vol.sharedProfile = profile;

        // Ensure the camera has post-processing enabled.
        var cam = Camera.main;
        string camMsg = "no Camera.main";
        if (cam != null)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            camMsg = $"post-processing enabled on {cam.name}";
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        return $"Bloom volume set up ({ProfilePath}); {camMsg}; scene saved.";
    }
}
