using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    public static class ToggleRotatePanel
    {
        public static string Show()
        {
            var p = SceneLookup.Find("Canvas_RotatePrompt");
            if (p == null) return "no canvas";
            var panel = p.transform.Find("Panel");
            if (panel == null) return "no panel";
            panel.gameObject.SetActive(true);
            return "panel shown";
        }

        public static string Hide()
        {
            var p = SceneLookup.Find("Canvas_RotatePrompt");
            if (p == null) return "no canvas";
            var panel = p.transform.Find("Panel");
            if (panel == null) return "no panel";
            panel.gameObject.SetActive(false);
            return "panel hidden";
        }
    }
}
