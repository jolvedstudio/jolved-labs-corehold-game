using System.Collections.Generic;
using System.IO;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket (e), take 2 — renders a clean icon for every TowerDefinition in a
    /// dedicated throwaway scene. Each icon shows JUST the tower, framed on a round
    /// white circle background (nothing else), 256x256, and is assigned back to the
    /// definition. Menu: Tools/COREHOLD/Art/Render Tower Icons (Circle).
    ///
    /// URP always writes opaque alpha into the target, so a single render pass yields
    /// a solid square. We render the tower over solid BLACK and solid WHITE, then
    /// reconstruct the tower's true coverage alpha per pixel:  on black  p = t*a ;
    /// on white  p = t*a + (1-a).  Hence  a = 1 - (white - black)  and  t = black/a.
    /// The cut-out tower is then composited over a procedurally drawn white disc.
    /// </summary>
    public static class TowerIconScene
    {
        const int IconSize = 256;
        const string IconDir = "Assets/_COREHOLD/Art/Icons";
        static readonly Vector3 ViewDir = new Vector3(1f, 0.55f, -1f);

        [MenuItem("Tools/COREHOLD/Art/Render Tower Icons (Circle)", false, 82)]
        public static string Run()
        {
            Directory.CreateDirectory(IconDir);
            var sb = new StringBuilder();

            var jobs = CollectTowers();
            if (jobs.Count == 0)
                return "No TowerDefinition assets found.";

            Color[] circleBg = BuildCircleBackground();

            var prevScenePath = SceneManager.GetActiveScene().path;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);

            var camGo = new GameObject("__IconCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.orthographic = false;
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5000f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            var lightGo = new GameObject("__IconLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.transform.rotation = Quaternion.Euler(38f, -140f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.66f, 1f);

            var rt = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            cam.targetTexture = rt;

            var pngPaths = new List<string>();
            try
            {
                foreach (var job in jobs)
                {
                    if (job.prefab == null) { sb.AppendLine($"{job.name}: no prefab"); continue; }
                    var path = RenderTower(cam, rt, job.prefab, job.name, circleBg);
                    if (!string.IsNullOrEmpty(path)) { pngPaths.Add(path); sb.AppendLine($"{job.name}: rendered"); }
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                EditorSceneManager.CloseScene(scene, true);
                if (!string.IsNullOrEmpty(prevScenePath))
                    SceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(prevScenePath));
            }

            AssetDatabase.Refresh();
            foreach (var p in pngPaths) ConfigureAsSprite(p);
            AssetDatabase.Refresh();
            AssignIcons(jobs);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[COREHOLD] TowerIconScene:\n" + sb);
            return sb.ToString();
        }

        struct Job { public string name; public GameObject prefab; public TowerDefinition def; }

        static List<Job> CollectTowers()
        {
            var jobs = new List<Job>();
            foreach (var guid in AssetDatabase.FindAssets("t:TowerDefinition"))
            {
                var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def == null) continue;
                jobs.Add(new Job { name = def.name, prefab = def.basePrefab, def = def });
            }
            return jobs;
        }

        static string RenderTower(Camera cam, RenderTexture rt, GameObject prefab, string iconName, Color[] circleBg)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return null;
            try
            {
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                foreach (var anim in instance.GetComponentsInChildren<Animator>(true))
                    anim.enabled = false;

                HideNonModelRenderers(instance);

                if (!TryGetRenderBounds(instance, out var bounds))
                    return null;

                FrameCamera(cam, bounds);

                var onBlack = RenderToPixels(cam, rt, Color.black);
                var onWhite = RenderToPixels(cam, rt, Color.white);

                var outPixels = new Color32[onBlack.Length];
                for (int i = 0; i < onBlack.Length; i++)
                {
                    Color b = onBlack[i];
                    Color w = onWhite[i];

                    // Coverage alpha from the black/white difference.
                    float diff = ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f;
                    float a = Mathf.Clamp01(1f - diff);

                    // Snap the near-empty background haze to fully transparent so the
                    // tower cut-out is clean and only the disc shows behind it.
                    if (a < 0.08f) a = 0f;

                    Color towerRgb = a > 0.001f
                        ? new Color(b.r / a, b.g / a, b.b / a, 1f)
                        : Color.clear;

                    // Composite the tower (foreground, straight alpha) over the white
                    // disc (background, straight alpha) using the standard OVER op.
                    Color bg = circleBg[i];
                    float outA = a + bg.a * (1f - a);
                    Color rgb;
                    if (outA > 0.001f)
                        rgb = (towerRgb * a + bg * bg.a * (1f - a)) / outA;
                    else
                        rgb = Color.clear;
                    rgb.a = outA;
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

        // Solid white disc: opaque inside the circle, transparent outside, 2px AA
        // edge. Row 0 = bottom (matches ReadPixels order).
        static Color[] BuildCircleBackground()
        {
            var px = new Color[IconSize * IconSize];
            float half = IconSize * 0.5f;
            float radius = half * 0.96f;
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((radius - d) / 2f);
                    px[y * IconSize + x] = new Color(1f, 1f, 1f, a);
                }
            }
            return px;
        }

        static Color[] RenderToPixels(Camera cam, RenderTexture rt, Color bg)
        {
            cam.backgroundColor = new Color(bg.r, bg.g, bg.b, 1f);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(bg.r, bg.g, bg.b, 1f));
            cam.Render();
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var pixels = tex.GetPixels();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        static void HideNonModelRenderers(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer)
                {
                    r.enabled = false;
                    continue;
                }
                bool isShadow = r.GetComponent("BlobShadow") != null;
                string n = r.gameObject.name.ToLowerInvariant();
                if (isShadow || n.Contains("shadow") || n.Contains("decal") ||
                    n.Contains("marker") || n.Contains("blob") || n.Contains("range") ||
                    n.Contains("ring") || n.Contains("radius") || n.Contains("aura"))
                {
                    r.enabled = false;
                    continue;
                }
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    string mn = mf.sharedMesh.name.ToLowerInvariant();
                    if (mn.Contains("quad") || mn.Contains("plane"))
                        r.enabled = false;
                }
            }
        }

        static bool TryGetRenderBounds(GameObject go, out Bounds bounds)
        {
            bounds = new Bounds();
            bool has = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!r.enabled) continue;
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return has;
        }

        static void FrameCamera(Camera cam, Bounds bounds)
        {
            var dir = ViewDir.normalized;
            float radius = bounds.extents.magnitude;
            if (radius < 0.001f) radius = 1f;
            float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float distance = radius / Mathf.Sin(fovRad * 0.5f);
            distance *= 1.02f; // fit inside the circle with a hair of margin
            cam.transform.position = bounds.center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation((bounds.center - cam.transform.position).normalized, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.01f, distance - radius * 2f);
            cam.farClipPlane = distance + radius * 4f;
        }

        static void ConfigureAsSprite(string pngPath)
        {
            var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }

        static void AssignIcons(List<Job> jobs)
        {
            foreach (var job in jobs)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{IconDir}/{job.name}.png");
                if (sprite == null || job.def == null) continue;
                job.def.icon = sprite;
                EditorUtility.SetDirty(job.def);
            }
        }
    }
}
