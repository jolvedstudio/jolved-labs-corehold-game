using UnityEditor;
using UnityEngine;

public static class FrameBattlefield
{
    public static string Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        if (sv == null) return "No SceneView";

        // Overlook the whole battlefield: pivot near the Core, pull the camera back
        // and up so both turret pads and the incoming west/north routes are visible.
        var core = GameObject.Find("RefineryLevel/Core_Blockout/Core_Target");
        Vector3 pivot = core != null ? core.transform.position : Vector3.zero;

        sv.pivot = pivot;
        sv.rotation = Quaternion.Euler(35f, 135f, 0f);
        sv.size = 40f;
        sv.Repaint();
        return $"Framed battlefield around {pivot}";
    }
}
