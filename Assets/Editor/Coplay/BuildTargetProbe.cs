using UnityEditor;
using UnityEngine;

public static class BuildTargetProbe
{
    public static string Execute()
    {
        var group = EditorUserBuildSettings.selectedBuildTargetGroup;
        var target = EditorUserBuildSettings.activeBuildTarget;
        return $"activeBuildTarget={target}\nselectedBuildTargetGroup={group}\nactiveTextureCompression(WebGL uses DXT/S3TC by default)";
    }
}
