using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    public static class IconRawProbe
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            string p = "Assets/_COREHOLD/Art/Icons/Tower_Autocannon.png";
            var bytes = File.ReadAllBytes(p);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            int w = tex.width, h = tex.height;
            // Sample a column down the center and along bottom rows.
            // Row 0 = bottom of the PNG.
            int[] ys = { 5, 30, 60, 90, 120, 128, 160, 200, 240 };
            foreach (int y in ys)
            {
                Color c = tex.GetPixel(w / 2, y);
                sb.AppendLine($"center x, y={y}: a={c.a:0.00} rgb=({c.r:0.00},{c.g:0.00},{c.b:0.00})");
            }
            // Bottom-left corner region (should be transparent, outside disc)
            Color bl = tex.GetPixel(20, 20);
            sb.AppendLine($"bottom-left: a={bl.a:0.00} rgb=({bl.r:0.00},{bl.g:0.00},{bl.b:0.00})");
            Object.DestroyImmediate(tex);
            Debug.Log("[COREHOLD] IconRawProbe:\n" + sb);
            return sb.ToString();
        }
    }
}
