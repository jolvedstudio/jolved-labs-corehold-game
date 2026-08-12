using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Towers;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket (f) — hardpoints must glow / show an aura. Adds a soft radial glow
    /// halo above every TowerHardpoint pad marker and wires it as the pad's
    /// auraRenderer so it breathes (alpha + scale) while empty and fades out when
    /// occupied. Uses a procedurally generated radial-gradient sprite so the glow is
    /// soft-edged rather than a hard disc. Idempotent — reuses an existing 'PadAura'
    /// child if present.
    /// </summary>
    public static class HardpointAura
    {
        const string ArtDir = "Assets/_COREHOLD/Art";
        const string TexDir = ArtDir + "/Textures";
        const string MatDir = ArtDir + "/Materials";
        const string GlowTexPath = TexDir + "/PadGlow_Radial.png";
        const int TexSize = 256;

        public static string Run()
        {
            var sb = new StringBuilder();
            Directory.CreateDirectory(TexDir);
            Directory.CreateDirectory(MatDir);

            EnsureGlowTexture(sb);
            var glowSprite = AssetDatabase.LoadAssetAtPath<Texture2D>(GlowTexPath);

            foreach (var pad in Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            {
                Color c = ColorForPad(pad.name);
                var mat = GetOrCreateGlowMat(c, glowSprite);

                var aura = pad.transform.Find("PadAura");
                if (aura == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    go.name = "PadAura";
                    var col = go.GetComponent<Collider>();
                    if (col != null) Object.DestroyImmediate(col);
                    go.transform.SetParent(pad.transform, false);
                    // Lay the quad flat, just above the pad marker (marker sits at y=0.12).
                    go.transform.localPosition = new Vector3(0f, 0.14f, 0f);
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    go.transform.localScale = new Vector3(7f, 7f, 1f); // wide soft halo
                    aura = go.transform;
                    sb.AppendLine($"{pad.name}: created PadAura");
                }

                var mr = aura.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                }

                pad.SetAuraColor(c);
                pad.SetAuraRenderer(mr);
                EditorUtility.SetDirty(pad);
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("[COREHOLD] HardpointAura:\n" + sb);
            return sb.ToString();
        }

        static void EnsureGlowTexture(StringBuilder sb)
        {
            if (File.Exists(GlowTexPath))
                return;

            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
            float half = TexSize * 0.5f;
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy); // 0 centre .. 1 edge
                    // Soft radial falloff: bright core, smooth fade to nothing.
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a; // ease so the halo is soft, not linear
                    // Premultiply RGB by the falloff so this reads correctly under
                    // ADDITIVE blending: the corners are black and add nothing to the
                    // framebuffer, so no square edge ever shows (Ticket f fix).
                    tex.SetPixel(x, y, new Color(a, a, a, a));
                }
            }
            tex.Apply();
            File.WriteAllBytes(GlowTexPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(GlowTexPath);

            var importer = AssetImporter.GetAtPath(GlowTexPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
            sb.AppendLine("Generated radial glow texture.");
        }

        static Color ColorForPad(string name)
        {
            if (name.Contains("Premium")) return new Color(0f, 0.8f, 1f);      // cyan
            if (name.Contains("Standard")) return new Color(0.2f, 1f, 0.5f);   // green
            if (name.Contains("Rear")) return new Color(1f, 0.7f, 0.15f);      // amber
            if (name.Contains("Overwatch")) return new Color(1f, 0.3f, 0.9f);  // magenta
            return new Color(0f, 0.8f, 1f);
        }

        static Material GetOrCreateGlowMat(Color c, Texture2D glowTex)
        {
            string hex = ColorUtility.ToHtmlStringRGB(c);
            string path = $"{MatDir}/Mat_PadAura_{hex}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            // Dedicated additive unlit shader — guaranteed One/One blending that
            // URP's Unlit ShaderGUI can never re-validate back to alpha-blend (which
            // was what caused the square edge). The texture is premultiplied so the
            // transparent corners add nothing to the framebuffer.
            var shader = Shader.Find("COREHOLD/PadAuraAdditive");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { name = $"Mat_PadAura_{hex}" };

            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", glowTex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", glowTex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
