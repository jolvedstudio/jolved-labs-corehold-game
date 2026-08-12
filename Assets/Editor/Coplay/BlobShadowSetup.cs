using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 support: creates a shared soft radial blob-shadow texture and a single
    /// unlit transparent material, then attaches a "BlobShadow" quad to every enemy and
    /// turret prefab. One shared material across all of them means the blob shadows batch
    /// into effectively a single draw call (SRP Batcher / dynamic batching for the quads).
    /// </summary>
    public static class BlobShadowSetup
    {
        const string ArtDir = "Assets/_COREHOLD/Art";
        const string TexPath = ArtDir + "/Textures/BlobShadow.png";
        const string MatPath = ArtDir + "/Materials/Mat_BlobShadow.mat";

        const string EnemyDir = "Assets/_COREHOLD/Prefabs/Enemies";
        const string TowerDir = "Assets/_COREHOLD/Prefabs/Towers";

        [MenuItem("Tools/COREHOLD/Setup Blob Shadows")]
        public static string Run()
        {
            var log = new System.Text.StringBuilder();

            var tex = CreateBlobTexture(log);
            var mat = CreateBlobMaterial(tex, log);

            int count = 0;
            // Enemy blob: radius ~ half the footprint, low to ground.
            foreach (var path in PrefabsIn(EnemyDir))
                count += AttachBlob(path, mat, EnemyBlobSize(path), log) ? 1 : 0;

            foreach (var path in PrefabsIn(TowerDir))
                count += AttachBlob(path, mat, 4.0f, log) ? 1 : 0;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine($"Done. Blob shadows attached/updated on {count} prefabs.");
            Debug.Log("[COREHOLD] " + log);
            return log.ToString();
        }

        static IEnumerable<string> PrefabsIn(string dir)
        {
            if (!AssetDatabase.IsValidFolder(dir)) return Enumerable.Empty<string>();
            return AssetDatabase.FindAssets("t:Prefab", new[] { dir })
                .Select(AssetDatabase.GUIDToAssetPath)
                // Only top-level prefabs in the folder, not CombinedMeshes assets.
                .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == dir)
                .Distinct();
        }

        static float EnemyBlobSize(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            switch (name)
            {
                case "Scuttler": return 4.5f;
                case "Strider": return 3.5f;
                case "Lancer": return 4.0f;
                case "Wasp": return 3.0f;
                case "Roller": return 4.0f;
                case "Breaker": return 6.0f;
                default: return 4.0f;
            }
        }

        static Texture2D CreateBlobTexture(System.Text.StringBuilder log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TexPath));
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            float maxR = c;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - c) / maxR;
                    float dy = (y - c) / maxR;
                    float r = Mathf.Sqrt(dx * dx + dy * dy); // 0 centre .. 1 edge
                    // Soft falloff: opaque core, feathered edge, transparent past r=1.
                    float a = Mathf.Clamp01(1f - r);
                    a = a * a * (3f - 2f * a); // smoothstep for a soft gradient
                    byte alpha = (byte)Mathf.RoundToInt(a * 200f); // max ~0.78 opacity
                    px[y * size + x] = new Color32(0, 0, 0, alpha);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(TexPath, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceUpdate);

            var imp = (TextureImporter)AssetImporter.GetAtPath(TexPath);
            imp.textureType = TextureImporterType.Default;
            imp.alphaSource = TextureImporterAlphaSource.FromInput;
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.mipmapEnabled = true;
            imp.maxTextureSize = 128;
            imp.SaveAndReimport();

            log.AppendLine($"Blob texture written: {TexPath}");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
        }

        static Material CreateBlobMaterial(Texture2D tex, System.Text.StringBuilder log)
        {
            // Prefer URP unlit; fall back to particles/unlit which is guaranteed present.
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MatPath);
            }
            else
            {
                mat.shader = shader;
            }

            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 1f));
            // Alpha blend, no depth write, render as transparent, unlit, no shadows.
            mat.SetFloat("_Surface", 1f);   // Transparent
            mat.SetFloat("_Blend", 0f);     // Alpha
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Cull", 2f);      // Back
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

            EditorUtility.SetDirty(mat);
            log.AppendLine($"Blob material ready: {MatPath} (shader {shader.name})");
            return mat;
        }

        static bool AttachBlob(string prefabPath, Material mat, float diameter, System.Text.StringBuilder log)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var existing = root.transform.Find("BlobShadow");
                GameObject blob;
                if (existing != null)
                {
                    blob = existing.gameObject;
                }
                else
                {
                    blob = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(blob.GetComponent<Collider>());
                    blob.name = "BlobShadow";
                    blob.transform.SetParent(root.transform, false);
                }

                // Lie flat on the ground, just above y=0 in prefab local space.
                blob.transform.localPosition = new Vector3(0f, 0.05f, 0f);
                blob.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                blob.transform.localScale = new Vector3(diameter, diameter, 1f);

                var mr = blob.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                mr.allowOcclusionWhenDynamic = false;

                // Keep the quad flat on the ground beneath the owner at runtime, so it
                // works for both walking units and air units flying at 4 m altitude.
                var comp = blob.GetComponent<Corehold.Systems.BlobShadow>();
                if (comp == null) comp = blob.AddComponent<Corehold.Systems.BlobShadow>();
                comp.SetDiameter(diameter);
                comp.SetGroundY(0.05f);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                log.AppendLine($"  BlobShadow on {Path.GetFileName(prefabPath)} (Ø{diameter})");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
