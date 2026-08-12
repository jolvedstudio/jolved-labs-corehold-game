using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    public static class RawPngProbe
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            string[] paths = {
                "Assets/_COREHOLD/Art/Icons/Tower_Autocannon.png",
            };
            foreach (var p in paths)
            {
                if (!File.Exists(p)) { sb.AppendLine($"{p}: missing"); continue; }
                var bytes = File.ReadAllBytes(p);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(bytes);
                int w = tex.width, h = tex.height;
                Color c00 = tex.GetPixel(2, 2);
                Color cCenter = tex.GetPixel(w/2, h/2);
                Color cTop = tex.GetPixel(w/2, h-3);
                sb.AppendLine($"{p}: {w}x{h}");
                sb.AppendLine($"  corner a={c00.a:0.00} rgb=({c00.r:0.00},{c00.g:0.00},{c00.b:0.00})");
                sb.AppendLine($"  center a={cCenter.a:0.00} rgb=({cCenter.r:0.00},{cCenter.g:0.00},{cCenter.b:0.00})");
                sb.AppendLine($"  top    a={cTop.a:0.00}");
                Object.DestroyImmediate(tex);
            }
            Debug.Log("[COREHOLD] RawPngProbe:\n" + sb);
            return sb.ToString();
        }
    }
}
