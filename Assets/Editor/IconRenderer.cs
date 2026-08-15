using System.Collections.Generic;
using System.IO;
using Corehold.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace Corehold.EditorTools
{
    /// <summary>
    /// Bakes a 256×256 transparent icon for every TowerDefinition and
    /// EnemyDefinition from its prefab, writes the PNG to
    /// Assets/_COREHOLD/Art/Icons/, imports it as a Sprite (2D and UI), and
    /// assigns it back to the definition's <c>icon</c> field. GDD §9.5 · Ticket 33.
    ///
    /// <b>This is the one icon baker.</b> A new unit needs nothing but its
    /// definition and prefab: re-run the menu item and it is drawn in the same
    /// style as everything else. That only holds while there is a single
    /// implementation — two bakers writing this folder is how the towers ended up
    /// on white discs while the enemies were not.
    ///
    /// Three things decide whether an icon reads at HUD size (24 px in the wave
    /// preview, 58 px in the build bar), and all three are handled here:
    ///
    ///   • <b>Isolation.</b> The bake scene opens ADDITIVE, so the level being
    ///     worked on is still loaded and still at the origin. A prefab spawned at
    ///     <see cref="Vector3.zero"/> is photographed standing in the middle of the
    ///     battlefield, which is exactly what the old icons show: an opaque grey
    ///     floor behind the unit, with scene decals leaking in. Everything is
    ///     staged at <see cref="StageOrigin"/> instead, far from any scene content,
    ///     and post-processing is off so the level's Global Volume cannot tonemap
    ///     or bloom the cut-out.
    ///   • <b>Silhouette.</b> A fill light opposite the key lifts the shadow side,
    ///     and flat FX/decal quads are dropped, so the shape reads rather than
    ///     dissolving into its own dark side.
    ///   • <b>Edge.</b> A dark halo is composited behind the cut-out. At 24 px this
    ///     is what separates a grey mech from a dark panel; without it the icon
    ///     smears into the HUD.
    ///
    /// Judge the result from the CONTACT SHEET the bake writes next to the icons —
    /// every unit at 58 and 24 px over both the HUD panel and the battlefield —
    /// not from the 256 px files, which flatter everything.
    /// </summary>
    public static class IconRenderer
    {
        private const int IconSize = 256;
        private const string IconDir = "Assets/_COREHOLD/Art/Icons";
        private const string ContactSheetPath = IconDir + "/_ContactSheet.png";

        // 3/4 view direction the camera looks *from* (normalized in code).
        private static readonly Vector3 ViewDir = new Vector3(1f, 0.6f, -1f);

        /// <summary>
        /// Where units are staged for the bake. High above the world: the bake
        /// scene is additive, so the level is still loaded, and the ONLY reliable
        /// way to keep it out of frame is to shoot somewhere it is not.
        /// </summary>
        private static readonly Vector3 StageOrigin = new Vector3(0f, 10000f, 0f);

        /// <summary>Halo radius in icon pixels — ~0.7 px once scaled to the 24 px preview. [TUNE]</summary>
        private const int OutlineRadius = 7;

        /// <summary>Halo colour and strength. Near-black reads on the HUD panel and on rock. [TUNE]</summary>
        private static readonly Color OutlineColor = new Color(0.02f, 0.04f, 0.06f, 1f);
        private const float OutlineAlpha = 0.92f;

        [MenuItem("Tools/COREHOLD/Art/Render Icons", false, 81)]
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
            cam.useOcclusionCulling = false;

            // The level's Global Volume is global — it would tonemap and bloom the
            // icons, and the two-pass alpha reconstruction below assumes the black
            // and white backgrounds come back as black and white.
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;

            var lightGo = new GameObject("__IconLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(35f, -140f, 0f);

            // Fill from the opposite azimuth. Without it the shadow side goes to
            // ambient grey and the silhouette loses its far edge — survivable at
            // 256 px, fatal at 24.
            var fillGo = new GameObject("__IconFill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fill.color = new Color(0.8f, 0.88f, 1f);
            fill.transform.rotation = Quaternion.Euler(20f, 40f, 0f);

            // Directional light ignores position, so staging far away does NOT
            // escape the level's sun — it would light the units too, and icons
            // would come out different depending on which scene happened to be
            // open. Mute every light but ours for the bake.
            var muted = new List<Light>();
            foreach (Light other in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (other == null || !other.enabled || other == light || other == fill)
                    continue;
                other.enabled = false;
                muted.Add(other);
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f, 1f);
            RenderSettings.fog = false;

            var rt = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 8
            };
            cam.targetTexture = rt;

            int rendered = 0;
            var pngPaths = new List<string>();
            var sheet = new List<(string name, Color[] pixels)>();
            var skipped = new List<string>();

            try
            {
                foreach (var job in jobs)
                {
                    var source = job.prefab;
                    if (source == null)
                    {
                        // A tower must never ship a stand-in icon of an enemy
                        // mech — a missing basePrefab is a wiring bug to surface,
                        // not to paper over with the fallback.
                        if (job.isTower)
                        {
                            Debug.LogWarning($"[IconRenderer] Tower '{job.name}' has no basePrefab on its " +
                                             "TowerDefinition (or the reference is broken). Assign the prefab and re-run.");
                            skipped.Add(job.name);
                            continue;
                        }

                        source = fallbackPrefab;
                        if (source == null)
                        {
                            Debug.LogWarning($"[IconRenderer] '{job.name}' has no prefab and no fallback exists. Skipping.");
                            skipped.Add(job.name);
                            continue;
                        }
                        Debug.LogWarning($"[IconRenderer] '{job.name}' has no prefab — using the fallback mech " +
                                         "as a placeholder, so its row on the contact sheet is not this unit's real look.");
                    }

                    var path = RenderOne(cam, rt, source, job.name, out Color[] pixels);
                    if (!string.IsNullOrEmpty(path))
                    {
                        pngPaths.Add(path);
                        sheet.Add((job.name, pixels));
                        rendered++;
                    }
                    else
                    {
                        skipped.Add(job.name);
                    }
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(fillGo);
                foreach (Light l in muted)
                    if (l != null) l.enabled = true;
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

            string sheetNote = WriteContactSheet(sheet);
            AssetDatabase.Refresh();

            // A unit that silently drops off the sheet is how stale icons linger:
            // the loop keeps going, the old PNG stays on disk, and nothing looks
            // wrong until the build bar does. Name every casualty in one place.
            string skipNote = skipped.Count == 0
                ? ""
                : $"\nSKIPPED {skipped.Count} of {jobs.Count}: {string.Join(", ", skipped)} — reasons in the warnings/errors above; " +
                  "their PNGs on disk (if any) are stale, from an older bake.";
            Debug.Log($"[IconRenderer] Rendered {rendered} of {jobs.Count} icon(s) to {IconDir} and assigned them to definitions." +
                      skipNote + "\n" + sheetNote);
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

        private static string RenderOne(Camera cam, RenderTexture rt, GameObject prefab, string iconName,
                                        out Color[] iconPixels)
        {
            iconPixels = null;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
            {
                Debug.LogWarning($"[IconRenderer] Could not instantiate prefab for '{iconName}' — the prefab asset " +
                                 $"'{prefab.name}' failed to spawn (broken or partially imported asset?).");
                return null;
            }

            try
            {
                instance.transform.position = StageOrigin;
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
                    // Every renderer matched the decal/aura filters (or the GO
                    // carrying the model is inactive). A cluttered icon beats a
                    // missing one: undo the cosmetic filtering and try again —
                    // effects and the blob shadow stay hidden either way.
                    RestoreForRescue(instance);
                    if (!TryGetRenderBounds(instance, out bounds))
                    {
                        Debug.LogError($"[IconRenderer] '{iconName}' has no renderable geometry at all. " +
                                       DescribeChildren(instance) +
                                       "\nIf the model child shows 'Missing Prefab' in the hierarchy, or is marked " +
                                       "NO MESH above, its source geometry (often a Vendor kit piece) is broken or " +
                                       "absent on this machine — re-link it in the prefab and re-run.");
                        return null;
                    }
                    Debug.LogWarning($"[IconRenderer] '{iconName}': every renderer matched the shadow/decal/aura name " +
                                     "filters, so it was rendered unfiltered instead. If the icon shows stray flat " +
                                     "quads, rename the model parts so they stop matching. " + DescribeChildren(instance));
                }

                // A dangling mesh reference still has its MeshRenderer, but the
                // bounds collapse to a point — the bake would frame 1 m of nothing
                // and write a blank PNG that LOOKS like a successful run.
                if (bounds.size.magnitude < 0.001f)
                {
                    Debug.LogError($"[IconRenderer] '{iconName}' has renderers but zero-size bounds — its meshes are " +
                                   "missing (broken mesh references, often a kit piece absent on this machine). " +
                                   DescribeChildren(instance));
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

                var outPixels = new Color[onBlack.Length];
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

                ApplyOutline(outPixels, IconSize);
                iconPixels = outPixels;

                var tex = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
                tex.SetPixels(outPixels);
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
                    n.Contains("marker") || n.Contains("blob") || n.Contains("range") ||
                    n.Contains("aura") || n.Contains("glow") || n.Contains("telegraph"))
                {
                    r.enabled = false;
                    continue;
                }

                // Anything a name list misses, geometry catches: a renderer with no
                // meaningful height is a flat quad lying on the ground — a decal by
                // construction, whatever it is called. These are the cyan and green
                // slabs fanning out behind the old enemy icons.
                Vector3 s = r.bounds.size;
                float widest = Mathf.Max(s.x, s.z);
                if (widest > 0.05f && s.y < widest * 0.04f)
                    r.enabled = false;
            }
        }

        /// <summary>
        /// Undoes the cosmetic filtering for a unit whose EVERY renderer matched
        /// it — a support tower named "CryoGlow_Rings" must not lose its whole
        /// icon to a halo filter. Effects and the blob shadow are never
        /// silhouette, so they stay hidden. Inactive GameObjects are left alone:
        /// an authored-off child (LOD variant, alt state) is a choice, not an
        /// accident, and DescribeChildren reports it instead.
        /// </summary>
        private static void RestoreForRescue(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (r.GetComponent("BlobShadow") != null) continue;
                r.enabled = true;
            }
        }

        /// <summary>
        /// One line per child naming what the bake can see — renderer type,
        /// filtered/inactive state, missing meshes — so a failed unit's console
        /// entry IS the diagnosis and nobody has to bisect the prefab by hand.
        /// </summary>
        private static string DescribeChildren(GameObject root)
        {
            var parts = new List<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                var r = t.GetComponent<Renderer>();
                string what;
                if (r == null)
                {
                    what = "no renderer";
                }
                else
                {
                    what = r.GetType().Name;
                    if (!r.enabled) what += ", hidden by filter";
                    var mf = t.GetComponent<MeshFilter>();
                    if (r is MeshRenderer && (mf == null || mf.sharedMesh == null)) what += ", NO MESH";
                }
                if (!t.gameObject.activeInHierarchy) what += ", INACTIVE";
                parts.Add($"{t.name} ({what})");
            }
            return "Children: " + (parts.Count == 0 ? "none — the prefab root is empty" : string.Join("; ", parts));
        }

        // ------------------------------------------------------------ edge halo

        /// <summary>
        /// Composites a dark halo behind the cut-out, in place.
        ///
        /// The icon is drawn over whatever the HUD puts behind it — a dark panel in
        /// the build bar, open battlefield in the wave preview — and a mid-grey mech
        /// has poor contrast against both. Dilating the coverage mask and filling
        /// the ring with near-black gives every icon its own edge, which is the
        /// single biggest legibility win at 24 px.
        /// </summary>
        private static void ApplyOutline(Color[] px, int size)
        {
            var alpha = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
                alpha[i] = px[i].a;

            float[] halo = MaxFilter(alpha, size, OutlineRadius);
            halo = BoxBlur(halo, size, Mathf.Max(1, OutlineRadius / 2));

            for (int i = 0; i < px.Length; i++)
            {
                float src = px[i].a;
                float outA = Mathf.Clamp01(Mathf.Max(src, halo[i] * OutlineAlpha));
                if (outA <= 0.002f)
                {
                    px[i] = Color.clear;
                    continue;
                }

                // How much of this pixel is model rather than halo.
                float w = Mathf.Clamp01(src / outA);
                Color rgb = Color.Lerp(OutlineColor, new Color(px[i].r, px[i].g, px[i].b, 1f), w);
                rgb.a = outA;
                px[i] = rgb;
            }
        }

        /// <summary>Separable max filter — a square dilation, smoothed afterwards.</summary>
        private static float[] MaxFilter(float[] src, int size, int r)
        {
            var tmp = new float[src.Length];
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float m = 0f;
                    int x0 = Mathf.Max(0, x - r), x1 = Mathf.Min(size - 1, x + r);
                    for (int i = x0; i <= x1; i++)
                        if (src[row + i] > m) m = src[row + i];
                    tmp[row + x] = m;
                }
            }

            var dst = new float[src.Length];
            for (int y = 0; y < size; y++)
            {
                int y0 = Mathf.Max(0, y - r), y1 = Mathf.Min(size - 1, y + r);
                for (int x = 0; x < size; x++)
                {
                    float m = 0f;
                    for (int j = y0; j <= y1; j++)
                        if (tmp[j * size + x] > m) m = tmp[j * size + x];
                    dst[y * size + x] = m;
                }
            }
            return dst;
        }

        /// <summary>Separable box blur, so the dilated square reads as a soft halo.</summary>
        private static float[] BoxBlur(float[] src, int size, int r)
        {
            var tmp = new float[src.Length];
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    int x0 = Mathf.Max(0, x - r), x1 = Mathf.Min(size - 1, x + r);
                    for (int i = x0; i <= x1; i++) sum += src[row + i];
                    tmp[row + x] = sum / (x1 - x0 + 1);
                }
            }

            var dst = new float[src.Length];
            for (int y = 0; y < size; y++)
            {
                int y0 = Mathf.Max(0, y - r), y1 = Mathf.Min(size - 1, y + r);
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    for (int j = y0; j <= y1; j++) sum += tmp[j * size + x];
                    dst[y * size + x] = sum / (y1 - y0 + 1);
                }
            }
            return dst;
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

        // -------------------------------------------------------- contact sheet

        /// <summary>HUD sizes an icon actually has to survive: build bar, wave preview.</summary>
        private static readonly int[] SheetSizes = { 58, 24 };

        /// <summary>The two things icons are seen against: the HUD panel, and open ground.</summary>
        private static readonly Color PanelBg = new Color(0.06f, 0.09f, 0.12f, 1f);
        private static readonly Color FieldBg = new Color(0.42f, 0.44f, 0.46f, 1f);

        /// <summary>
        /// Writes every baked icon at its real HUD sizes over both backgrounds, one
        /// row per unit, alphabetical (the order is logged since the sheet has no
        /// labels). A 256 px PNG makes any icon look fine; this is where you find
        /// out whether the wave preview reads.
        /// </summary>
        private static string WriteContactSheet(List<(string name, Color[] pixels)> icons)
        {
            if (icons.Count == 0)
                return "[IconRenderer] no icons to sheet.";

            icons.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            const int pad = 8;
            const int big = 96;
            int rowH = big + pad;
            int half = pad + big + pad;
            foreach (int s in SheetSizes) half += s + pad;
            int width = half * 2;
            int height = rowH * icons.Count + pad;

            var sheet = new Color[width * height];
            for (int i = 0; i < sheet.Length; i++)
                sheet[i] = (i % width) < half ? PanelBg : FieldBg;

            for (int r = 0; r < icons.Count; r++)
            {
                int top = pad + r * rowH;
                for (int side = 0; side < 2; side++)
                {
                    int x = side * half + pad;
                    Blit(sheet, width, height, Downscale(icons[r].pixels, IconSize, big), big, x, top);
                    x += big + pad;
                    foreach (int s in SheetSizes)
                    {
                        // Vertically centre the small copies against the big one.
                        Blit(sheet, width, height, Downscale(icons[r].pixels, IconSize, s), s,
                             x, top + (big - s) / 2);
                        x += s + pad;
                    }
                }
            }

            var tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.SetPixels(sheet);
            tex.Apply();
            File.WriteAllBytes(ContactSheetPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.Refresh();
            var importer = AssetImporter.GetAtPath(ContactSheetPath) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;   // a check artifact, not a sprite
                importer.SaveAndReimport();
            }

            var names = new List<string>();
            foreach (var i in icons) names.Add(i.name);
            return $"Contact sheet: {ContactSheetPath} — each row is one unit at 96 / 58 / 24 px, " +
                   $"HUD-panel dark on the left, battlefield grey on the right. Rows top to bottom:\n  " +
                   string.Join(", ", names);
        }

        /// <summary>Box-downsample with premultiplied alpha, so edges do not fringe dark.</summary>
        private static Color[] Downscale(Color[] src, int srcSize, int dstSize)
        {
            var dst = new Color[dstSize * dstSize];
            float step = srcSize / (float)dstSize;

            for (int y = 0; y < dstSize; y++)
            {
                int y0 = Mathf.FloorToInt(y * step), y1 = Mathf.Min(srcSize, Mathf.FloorToInt((y + 1) * step));
                if (y1 <= y0) y1 = y0 + 1;
                for (int x = 0; x < dstSize; x++)
                {
                    int x0 = Mathf.FloorToInt(x * step), x1 = Mathf.Min(srcSize, Mathf.FloorToInt((x + 1) * step));
                    if (x1 <= x0) x1 = x0 + 1;

                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    int n = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            Color c = src[sy * srcSize + sx];
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                            n++;
                        }
                    }

                    if (n == 0 || a <= 0.0001f) { dst[y * dstSize + x] = Color.clear; continue; }
                    dst[y * dstSize + x] = new Color(r / a, g / a, b / a, a / n);
                }
            }
            return dst;
        }

        /// <summary>Alpha-composites a square tile into the sheet at (x, top), top-down.</summary>
        private static void Blit(Color[] sheet, int sheetW, int sheetH, Color[] tile, int tileSize, int x, int top)
        {
            for (int ty = 0; ty < tileSize; ty++)
            {
                // Pixel arrays run bottom-up, the layout above is top-down, so tile
                // row ty sits (tileSize-1-ty) rows below the tile's top edge.
                int sy = sheetH - 1 - (top + tileSize - 1 - ty);
                if (sy < 0 || sy >= sheetH) continue;
                for (int tx = 0; tx < tileSize; tx++)
                {
                    int sx = x + tx;
                    if (sx < 0 || sx >= sheetW) continue;
                    Color c = tile[ty * tileSize + tx];
                    Color bg = sheet[sy * sheetW + sx];
                    sheet[sy * sheetW + sx] = Color.Lerp(bg, new Color(c.r, c.g, c.b, 1f), c.a);
                }
            }
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
