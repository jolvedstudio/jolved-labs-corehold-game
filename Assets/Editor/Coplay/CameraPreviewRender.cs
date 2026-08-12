using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Renders the Main Camera to PNGs at 16:9, 16:10 and 20:9 so the fixed framing
    /// can be inspected. Also aligns the SceneView to the Main Camera for convenience.
    /// </summary>
    public static class CameraPreviewRender
    {
        [MenuItem("Tools/COREHOLD/Render Camera Preview")]
        public static string Run()
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return "No camera.";

            string dir = "Assets/_COREHOLD/Docs/FramingPreviews";
            Directory.CreateDirectory(dir);

            (string tag, int w, int h)[] shots =
            {
                ("16x9", 1600, 900),
                ("16x10", 1440, 900),
                ("20x9", 2000, 900),
            };

            var sb = new System.Text.StringBuilder();
            foreach (var (tag, w, h) in shots)
            {
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                rt.antiAliasing = 4;
                var prev = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = prev;

                string path = $"{dir}/Framing_{tag}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                sb.AppendLine("Wrote " + path);
            }

            // Align scene view camera to the game camera for a quick visual sanity check.
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                sv.AlignViewToObject(cam.transform);
                sv.Repaint();
            }

            AssetDatabase.Refresh();
            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }
    }
}
