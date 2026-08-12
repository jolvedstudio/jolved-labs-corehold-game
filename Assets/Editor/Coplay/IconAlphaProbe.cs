using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    public static class IconAlphaProbe
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            string[] paths = {
                "Assets/_COREHOLD/Art/Icons/Tower_Autocannon.png",
                "Assets/_COREHOLD/Art/Textures/PadGlow_Radial.png",
            };
            foreach (var p in paths)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (tex == null) { sb.AppendLine($"{p}: NOT FOUND"); continue; }
                // Ensure readable
                var imp = AssetImporter.GetAtPath(p) as TextureImporter;
                bool wasReadable = imp != null && imp.isReadable;
                if (imp != null && !imp.isReadable) { imp.isReadable = true; imp.SaveAndReimport(); tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p); }
                int w = tex.width, h = tex.height;
                Color corner = tex.GetPixel(2, 2);
                Color center = tex.GetPixel(w/2, h/2);
                sb.AppendLine($"{p}: {w}x{h} corner a={corner.a:0.00} rgb=({corner.r:0.00},{corner.g:0.00},{corner.b:0.00}) center a={center.a:0.00}");
                if (imp != null && !wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            }
            Debug.Log("[COREHOLD] IconAlphaProbe:\n" + sb);
            return sb.ToString();
        }
    }
}
