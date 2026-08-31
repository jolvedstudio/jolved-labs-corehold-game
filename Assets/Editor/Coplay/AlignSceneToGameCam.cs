using UnityEditor;
using UnityEngine;

/// <summary>
/// Aligns the SceneView camera to the game's Main Camera and turns ON the Scene
/// view's image-effects (post-processing) toggle, so a SceneView capture shows the
/// same bloom/tonemapping the player sees. Run in play mode.
/// </summary>
public static class AlignSceneToGameCam
{
    public static string Execute()
    {
        var cam = Camera.main;
        if (cam == null)
            return "ERROR: no Main Camera.";

        var sv = SceneView.lastActiveSceneView;
        if (sv == null)
            return "ERROR: no active SceneView.";

        // Show URP post-processing in the scene view.
        sv.sceneViewState.showImageEffects = true;
        sv.sceneViewState.showSkybox = true;
        sv.sceneViewState.showFog = true;

        // Match the game camera pose. SceneView.LookAt places the pivot in front of
        // the camera; use the camera's transform for an exact match.
        sv.orthographic = false;
        sv.cameraSettings.fieldOfView = cam.fieldOfView;
        var t = cam.transform;
        // Pivot a bit in front of the camera along its forward.
        float dist = 20f;
        sv.LookAtDirect(t.position + t.forward * dist, t.rotation, dist);
        sv.Repaint();

        return $"SceneView aligned to Main Camera (fov {cam.fieldOfView}), image effects ON.";
    }
}
