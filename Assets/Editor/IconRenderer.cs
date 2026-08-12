using System.Collections.Generic;
using System.IO;
using Corehold.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace Corehold.EditorTools
{
    /// <summary>
    /// Renders a 256x256 transparent icon for every TowerDefinition and
    /// EnemyDefinition from its associated prefab, writes the PNG to
    /// Assets/_COREHOLD/Art/Icons/, imports it as a Sprite (2D and UI),
    /// and assigns it back to the definition's <c>icon</c> field.
    /// GDD §9.5 · Ticket 33.
    /// </summary>
    public static class IconRenderer
    {
        private const int IconSize = 256;
        private const string IconDir = "Assets/_COREHOLD/Art/Icons";

        // 3/4 view direction the camera looks *from* (normalized in code).
        private static readonly Vector3 ViewDir = new Vector3(1f, 0.6f, -1f);

        [MenuItem("Tools/COREHOLD/Render Icons")]
        public static void RenderIcons()
        {
            Directory.CreateDirectory(IconDir);

            var jobs = CollectJobs(out var fallbackPrefab);
            if (jobs.Count == 0)
            {
                Debug.LogWarning("[IconRenderer] No TowerDefinition or EnemyDefinition assets found.");
                return;
            }

            // Temporary scene so nothing pollutes the working scene.
            var prevScene = SceneManager.GetActiveScene();
            var prevScenePath = prevScene.path;
            var tempScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(tempScene);

            // Temporary camera + light.
            var camGo = new GameObject("__IconCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent
            cam.orthographic = false;
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            var lightGo = new GameObject("__IconLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(35f, -140f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f, 1f);

            var rt = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 8
            };
            cam.targetTexture = rt;

            int rendered = 0;
            var pngPaths = new List<string>();

            try
            {
                foreach (var job in jobs)
                {
                    var source = job.prefab != null ? job.prefab : fallbackPrefab;
                    if (source == null)
                    {
                        Debug.LogWarning($"[IconRenderer] '{job.name}' has no prefab and no fallback exists. Skipping.");
                        continue;
                    }

                    var path = RenderOne(cam, rt, source, job.name);
                    if (!string.IsNullOrEmpty(path))
                    {
                        pngPaths.Add(path);
                        rendered++;
                    }
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                EditorSceneManager.CloseScene(tempScene, true);
                if (!string.IsNullOrEmpty(prevScenePath))
                    SceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(prevScenePath));
            }

            AssetDatabase.Refresh();

            // Import every generated PNG as a Sprite (2D and UI).
            foreach (var p in pngPaths)
                ConfigureAsSprite(p);
            AssetDatabase.Refresh();

            // Assign icons back to their definitions.
            AssignIcons(jobs);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[IconRenderer] Rendered {rendered} icon(s) to {IconDir} and assigned them to definitions.");
        }

        private struct IconJob
        {
            public string name;         // File name (without extension) e.g. "Tower_Autocannon"
            public GameObject prefab;   // Source prefab to render (may be null)
            public Object definition;   // The definition asset to assign the icon to
            public bool isTower;
        }

        private static List<IconJob> CollectJobs(out GameObject fallbackPrefab)
        {
            var jobs = new List<IconJob>();
            fallbackPrefab = null;

            // Towers.
            foreach (var guid in AssetDatabase.FindAssets("t:TowerDefinition"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(assetPath);
                if (def == null) continue;
                jobs.Add(new IconJob
                {
                    name = def.name,
                    prefab = def.basePrefab,
                    definition = def,
                    isTower = true
                });
            }

            // Enemies.
            foreach (var guid in AssetDatabase.FindAssets("t:EnemyDefinition"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(assetPath);
                if (def == null) continue;
                jobs.Add(new IconJob
                {
                    name = def.name,
                    prefab = def.prefab,
                    definition = def,
                    isTower = false
                });

                // Use the heaviest available enemy prefab as a fallback for any
                // definition missing a prefab (e.g. the gated Colossus, §4.5).
                if (def.prefab != null && fallbackPrefab == null)
                    fallbackPrefab = def.prefab;
            }

            // Prefer a Strider (heavy spider mech) as fallback if present.
            var striderGuids = AssetDatabase.FindAssets("Strider t:Prefab");
            foreach (var g in striderGuids)
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g));
                if (p != null) { fallbackPrefab = p; break; }
            }

            return jobs;
        }

        private static string RenderOne(Camera cam, RenderTexture rt, GameObject prefab, string iconName)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
            {
                Debug.LogWarning($"[IconRenderer] Could not instantiate prefab for '{iconName}'.");
                return null;
            }

            try
            {
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                // Disable animators so we get a clean bind pose.
                foreach (var anim in instance.GetComponentsInChildren<Animator>(true))
                    anim.enabled = false;

                // Hide the blob shadow and any flat ground decals so the icon shows
                // JUST the tower/enemy (Ticket e). These flat ground quads clutter the
                // icon and inflate its bounds, shrinking the actual model.
                HideNonModelRenderers(instance);

                if (!TryGetRenderBounds(instance, out var bounds))
                {
                    Debug.LogWarning($"[IconRenderer] No renderers found for '{iconName}'.");
                    return null;
                }

                // Frame the object with the camera at a 3/4 angle.
                FrameCamera(cam, bounds);

                // URP writes opaque (alpha=1) into the render target, so a single
                // pass produces a solid square background. Instead render the object
                // twice — once over black, once over white — and reconstruct true
                // coverage alpha per pixel:  on black  p = c*a ; on white p = c*a +
                // (1-a).  Hence  a = 1 - (white - black)  and  c = black / a.
                var onBlack = RenderToPixels(cam, rt, Color.black);
                var onWhite = RenderToPixels(cam, rt, Color.white);

                var outPixels = new Color32[onBlack.Length];
                for (int i = 0; i < onBlack.Length; i++)
                {
                    Color b = onBlack[i];
                    Color w = onWhite[i];
                    // Average the three channels of (white-black) for a stable alpha.
                    float diff = ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f;
                    float a = Mathf.Clamp01(1f - diff);
                    Color rgb = a > 0.001f ? new Color(b.r / a, b.g / a, b.b / a, 1f) : Color.clear;
                    rgb.a = a;
                    outPixels[i] = rgb;
                }

                var tex = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
                tex.SetPixels32(outPixels);
                tex.Apply();

                var png = tex.EncodeToPNG();
                Object.DestroyImmediate(tex);

                var filePath = $"{IconDir}/{iconName}.png";
                File.WriteAllBytes(filePath, png);
                return filePath;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Renders the camera with the given solid background colour and returns the
        /// pixels. Used to reconstruct true alpha from a black + white pair because
        /// URP always writes opaque alpha into the target.
        /// </summary>
        private static Color[] RenderToPixels(Camera cam, RenderTexture rt, Color bg)
        {
            cam.backgroundColor = new Color(bg.r, bg.g, bg.b, 1f);
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(bg.r, bg.g, bg.b, 1f));
            cam.Render();
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            var pixels = tex.GetPixels();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        /// <summary>
        /// Disables renderers that are not part of the model itself — the blob
        /// shadow, and any flat ground decal named like a shadow/marker/decal — so
        /// the rendered icon frames JUST the tower or enemy (Ticket e).
        /// </summary>
        private static void HideNonModelRenderers(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                // Particle/trail renderers are never part of the static silhouette.
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer)
                {
                    r.enabled = false;
                    continue;
                }

                // Blob shadows carry the BlobShadow component; also catch anything
                // named as a shadow, ground decal or marker.
                bool isShadow = r.GetComponent("BlobShadow") != null;
                string n = r.gameObject.name.ToLowerInvariant();
                if (isShadow || n.Contains("shadow") || n.Contains("decal") ||
                    n.Contains("marker") || n.Contains("blob") || n.Contains("range"))
                {
                    r.enabled = false;
                }
            }
        }

        private static bool TryGetRenderBounds(GameObject go, out Bounds bounds)
        {
            bounds = new Bounds();
            var renderers = go.GetComponentsInChildren<Renderer>(false);
            bool has = false;
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!r.enabled) continue; // skip renderers we hid (blob shadow etc.)
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return has;
        }

        private static void FrameCamera(Camera cam, Bounds bounds)
        {
            var dir = ViewDir.normalized;
            float radius = bounds.extents.magnitude;
            if (radius < 0.001f) radius = 1f;

            // Distance so the bounding sphere fits vertical FOV with margin.
            float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float distance = radius / Mathf.Sin(fovRad * 0.5f);
            distance *= 0.92f; // tight margin so the tower fills the icon (Ticket e)

            cam.transform.position = bounds.center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation((bounds.center - cam.transform.position).normalized, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.01f, distance - radius * 2f);
            cam.farClipPlane = distance + radius * 4f;
        }

        private static void ConfigureAsSprite(string pngPath)
        {
            var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer == null) return;

            importer.textureType = TextureImporterType.Sprite; // Sprite (2D and UI)
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }

        private static void AssignIcons(List<IconJob> jobs)
        {
            foreach (var job in jobs)
            {
                var spritePath = $"{IconDir}/{job.name}.png";
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite == null)
                {
                    Debug.LogWarning($"[IconRenderer] Missing sprite for '{job.name}' at {spritePath}.");
                    continue;
                }

                if (job.isTower && job.definition is TowerDefinition tower)
                {
                    tower.icon = sprite;
                    EditorUtility.SetDirty(tower);
                }
                else if (!job.isTower && job.definition is EnemyDefinition enemy)
                {
                    enemy.icon = sprite;
                    EditorUtility.SetDirty(enemy);
                }
            }
        }
    }
}
