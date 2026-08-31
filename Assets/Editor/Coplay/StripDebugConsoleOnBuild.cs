using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Strip the DebugConsole object from RELEASE builds.
///
/// <see cref="Corehold.Systems.DebugConsole"/> is compiled only under
/// UNITY_EDITOR || DEVELOPMENT_BUILD, but the scene generator bakes its
/// GameObject into every gameplay scene (SceneSkeleton runs in the editor,
/// where the class exists). A release build then ships the object with a
/// dangling reference — the browser console's "referenced script on this
/// Behaviour (Game Object 'DebugConsole') is missing!" noise. Deleting the
/// object at build time fixes the noise without touching scenes or keeping
/// debug UI alive in release; development builds keep it, class and all.
/// </summary>
public class StripDebugConsoleOnBuild : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        // report is null when the editor loads a scene in play mode — the class
        // exists there, leave the console alone.
        if (report == null)
            return;
        if ((report.summary.options & UnityEditor.BuildOptions.Development) != 0)
            return;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            // The skeleton adopts roots into container objects, so search deep.
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "DebugConsole")
                {
                    Object.DestroyImmediate(t.gameObject);
                    break;
                }
            }
        }
    }
}
