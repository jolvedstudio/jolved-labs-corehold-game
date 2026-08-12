using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public static class SwitchToWebGL
{
    public static string Execute()
    {
        var current = EditorUserBuildSettings.activeBuildTarget;
        if (current == BuildTarget.WebGL)
            return "Already on WebGL build target.";

        bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.WebGL, BuildTarget.WebGL);
        return $"SwitchActiveBuildTarget to WebGL requested (from {current}). Returned={ok}. Reimport may be in progress.";
    }
}
