using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CoreholdEditor
{
    public static class IconVisualCheck
    {
        const string ContainerName = "__IconVisualCheck";

        public static string Build()
        {
            Cleanup();
            var canvas = new GameObject(ContainerName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 999;

            // Magenta backdrop to make any opaque square background obvious.
            var bg = new GameObject("BG", typeof(Image));
            bg.transform.SetParent(canvas.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(1f, 0f, 1f, 1f);

            string[] icons = {
                "Tower_Autocannon", "Tower_MissileBattery", "Tower_ArcNode",
                "Tower_SiegeMortar", "Tower_ScanRelay"
            };
            float x = -520f;
            foreach (var name in icons)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"Assets/_COREHOLD/Art/Icons/{name}.png");
                var go = new GameObject(name, typeof(Image));
                go.transform.SetParent(canvas.transform, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(220, 220);
                rt.anchoredPosition = new Vector2(x, 0);
                var img = go.GetComponent<Image>();
                img.sprite = sprite;
                img.preserveAspect = true;
                x += 260f;
            }
            Canvas.ForceUpdateCanvases();
            return "Built icon visual check canvas.";
        }

        public static string Cleanup()
        {
            var go = SceneLookup.Find(ContainerName);
            if (go != null) Object.DestroyImmediate(go);
            return "Cleaned up.";
        }
    }
}
