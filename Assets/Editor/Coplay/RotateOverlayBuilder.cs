using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Corehold.UI;

namespace CoreholdEditor
{
    /// <summary>
    /// Builds the portrait "rotate your device" overlay in the active scene (GDD §5.1).
    /// A dedicated Canvas with a very high sorting order, a full-screen dark panel, a
    /// generated phone icon, and a TextMeshPro prompt. Driven by RotateDeviceOverlay,
    /// which shows the panel only when the viewport is portrait.
    /// </summary>
    public static class RotateOverlayBuilder
    {
        const string IconPath = "Assets/_COREHOLD/Art/Textures/RotateDeviceIcon.png";

        [MenuItem("Tools/COREHOLD/Scene Setup/Build Rotate-Device Overlay", false, 48)]
        public static string Run()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            // Remove any prior overlay so this is idempotent.
            var prior = GameObject.Find("Canvas_RotatePrompt");
            if (prior != null) Object.DestroyImmediate(prior);

            var iconSprite = CreateIconSprite();

            // --- Canvas ---
            var canvasGo = new GameObject("Canvas_RotatePrompt",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(RotateDeviceOverlay));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000; // draws above every other UI

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920); // portrait reference
            scaler.matchWidthOrHeight = 0.5f;

            // --- Panel (the thing that toggles) ---
            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
            var bg = panelGo.GetComponent<Image>();
            bg.color = new Color(0.03f, 0.04f, 0.06f, 0.98f);

            // --- Icon ---
            var iconGo = new GameObject("RotateIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconGo.transform.SetParent(panelGo.transform, false);
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.anchoredPosition = new Vector2(0f, 120f);
            iconRt.sizeDelta = new Vector2(320f, 320f);
            var iconImg = iconGo.GetComponent<Image>();
            iconImg.sprite = iconSprite;
            iconImg.color = new Color(0.55f, 0.85f, 1f, 1f); // cyan faction colour
            iconImg.preserveAspect = true;

            // --- Text ---
            var textGo = new GameObject("Prompt", typeof(RectTransform), typeof(CanvasRenderer));
            textGo.transform.SetParent(panelGo.transform, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = "Please rotate your device\nCOREHOLD is landscape only";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 48f;
            tmp.color = new Color(0.9f, 0.95f, 1f, 1f);
            tmp.enableWordWrapping = true;
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = textRt.anchorMax = new Vector2(0.5f, 0.5f);
            textRt.pivot = new Vector2(0.5f, 0.5f);
            textRt.anchoredPosition = new Vector2(0f, -180f);
            textRt.sizeDelta = new Vector2(900f, 300f);

            // Wire the controller and start hidden.
            var overlay = canvasGo.GetComponent<RotateDeviceOverlay>();
            var so = new SerializedObject(overlay);
            so.FindProperty("panel").objectReferenceValue = panelGo;
            so.ApplyModifiedPropertiesWithoutUndo();
            panelGo.SetActive(false);

            EnsureEventSystem();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var msg = "Rotate-device overlay built: Canvas_RotatePrompt (sortingOrder 32000), panel hidden until portrait.";
            Debug.Log("[COREHOLD] " + msg);
            return msg;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            }
        }

        static Sprite CreateIconSprite()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IconPath));
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var px = new Color32[S * S];
            var clear = new Color32(255, 255, 255, 0);
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            float cx = S * 0.5f, cy = S * 0.5f;

            // Faint portrait phone + bright landscape phone frame + rotation arrows.
            FillRoundedRect(px, S, cx, cy, 44f, 74f, 16f, new Color32(255, 255, 255, 70));
            FillRoundedRect(px, S, cx, cy, 74f, 44f, 16f, new Color32(255, 255, 255, 255));
            FillRoundedRect(px, S, cx, cy, 64f, 34f, 10f, clear);
            DrawArc(px, S, cx, cy, 104f, 20f, 150f, 5f);
            DrawArc(px, S, cx, cy, 104f, 200f, 330f, 5f);

            tex.SetPixels32(px);
            tex.Apply();
            Debug.Log($"[COREHOLD] Rotate icon corner alpha={px[0].a} (expect 0), centre alpha={px[(S / 2) * S + S / 2].a}");

            File.WriteAllBytes(IconPath, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceUpdate);

            var imp = (TextureImporter)AssetImporter.GetAtPath(IconPath);
            imp.textureType = TextureImporterType.Sprite;
            imp.alphaSource = TextureImporterAlphaSource.FromInput;
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = false;
            imp.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
        }

        static void FillRoundedRect(Color32[] px, int S, float cx, float cy,
                                    float hw, float hh, float r, Color32 col)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - hw));
            int x1 = Mathf.Min(S - 1, Mathf.CeilToInt(cx + hw));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - hh));
            int y1 = Mathf.Min(S - 1, Mathf.CeilToInt(cy + hh));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = RoundedRectSDF(x - cx, y - cy, hw, hh, r);
                    if (d <= 0f) px[y * S + x] = col;
                }
        }

        static float RoundedRectSDF(float px, float py, float hw, float hh, float r)
        {
            float qx = Mathf.Abs(px) - (hw - r);
            float qy = Mathf.Abs(py) - (hh - r);
            float ax = Mathf.Max(qx, 0f), ay = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        static void DrawArc(Color32[] px, int S, float cx, float cy, float radius,
                            float startDeg, float endDeg, float thickness)
        {
            for (float a = startDeg; a <= endDeg; a += 0.5f)
            {
                float rad = a * Mathf.Deg2Rad;
                float x = cx + Mathf.Cos(rad) * radius;
                float y = cy + Mathf.Sin(rad) * radius;
                Stamp(px, S, x, y, thickness);
            }
            // simple arrowhead at the end
            float er = endDeg * Mathf.Deg2Rad;
            float ex = cx + Mathf.Cos(er) * radius;
            float ey = cy + Mathf.Sin(er) * radius;
            for (int k = 0; k < 14; k++)
            {
                Stamp(px, S, ex - k, ey + k, thickness * 0.7f);
                Stamp(px, S, ex + k, ey + k, thickness * 0.7f);
            }
        }

        static void Stamp(Color32[] px, int S, float x, float y, float radius)
        {
            int r = Mathf.CeilToInt(radius);
            for (int j = -r; j <= r; j++)
                for (int i = -r; i <= r; i++)
                {
                    int xi = Mathf.RoundToInt(x) + i;
                    int yi = Mathf.RoundToInt(y) + j;
                    if (xi < 0 || yi < 0 || xi >= S || yi >= S) continue;
                    if (i * i + j * j <= radius * radius)
                        px[yi * S + xi] = new Color32(255, 255, 255, 255);
                }
        }
    }
}
