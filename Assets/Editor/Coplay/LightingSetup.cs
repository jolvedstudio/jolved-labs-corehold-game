using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 32 lighting:
    ///  - Mark every environment object Static (Contribute GI + Batching + Occluder/Occludee etc.)
    ///  - Disable directional-light shadows entirely (on the light and in the URP asset(s)).
    ///  - Ensure a LightProbeGroup exists over the playfield so the units get baked lighting.
    ///  - Ensure a ReflectionProbe exists (baked) for the units.
    ///  - Configure Lighting Settings for a baked GI workflow.
    /// The actual bake is kicked off by BakeLighting() (separate call so it can run async).
    /// </summary>
    public static class LightingSetup
    {
        // Roots whose entire subtree is static environment. Route/Spawner/Hardpoint empties
        // and the units are NOT static.
        static readonly string[] StaticRoots =
        {
            "RefineryLevel/Structures",
            "RefineryLevel/Core_Blockout",
            "RefineryLevel/Narrative",
        };

        [MenuItem("Tools/COREHOLD/Setup Lighting (no realtime shadows)")]
        public static string Run()
        {
            var sb = new StringBuilder();

            int staticCount = MarkEnvironmentStatic(sb);
            DisableDirectionalShadows(sb);
            EnsureLightProbeGroup(sb);
            EnsureReflectionProbe(sb);
            ConfigureLightingSettings(sb);

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            sb.Insert(0, $"LIGHTING SETUP (Ticket 32) — {staticCount} objects marked static\n");
            Debug.Log("[COREHOLD] " + sb);
            return sb.ToString();
        }

        static int MarkEnvironmentStatic(StringBuilder sb)
        {
            // Also mark the Floor and any RefineryLevel renderers that are geometry.
            var flags = StaticEditorFlags.ContributeGI
                      | StaticEditorFlags.BatchingStatic
                      | StaticEditorFlags.OccluderStatic
                      | StaticEditorFlags.OccludeeStatic
                      | StaticEditorFlags.ReflectionProbeStatic;

            var toMark = new HashSet<Transform>();

            // Explicit Floor object.
            var floor = GameObject.Find("Floor");
            if (floor != null) toMark.Add(floor.transform);

            foreach (var rootPath in StaticRoots)
            {
                var root = GameObject.Find(rootPath);
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    toMark.Add(t);
            }

            int count = 0;
            foreach (var t in toMark)
            {
                // Skip lights and cameras nested in the environment (rare, but safe).
                if (t.GetComponent<Light>() != null || t.GetComponent<Camera>() != null)
                    continue;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
                count++;
            }

            sb.AppendLine($"  Marked {count} environment transforms static (ContributeGI+Batching+Occlusion+ReflectionProbe).");
            return count;
        }

        static void DisableDirectionalShadows(StringBuilder sb)
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    light.shadows = LightShadows.None;
                    // Keep it real-time-mixed off; environment is baked, this light stays for
                    // direct baked lighting + real-time direct on units (no shadows).
                    EditorUtility.SetDirty(light);
                    sb.AppendLine($"  Directional light '{light.name}': shadows = None.");
                }
            }

            // Also turn Main Light shadows off in every URP asset in the project so no
            // main-light shadow pass is ever submitted.
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (rp == null) continue;
                bool changed = SetPrivateBool(rp, "m_MainLightShadowsSupported", false)
                             | SetPrivateBool(rp, "m_AdditionalLightShadowsSupported", false)
                             | SetPrivateBool(rp, "m_SoftShadowsSupported", false);
                if (changed)
                {
                    EditorUtility.SetDirty(rp);
                    sb.AppendLine($"  URP asset '{System.IO.Path.GetFileName(path)}': main/additional/soft shadows disabled.");
                }
            }
            AssetDatabase.SaveAssets();
        }

        static bool SetPrivateBool(object obj, string field, bool value)
        {
            var f = obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null || f.FieldType != typeof(bool)) return false;
            if ((bool)f.GetValue(obj) == value) return false;
            f.SetValue(obj, value);
            return true;
        }

        static void EnsureLightProbeGroup(StringBuilder sb)
        {
            var existing = Object.FindFirstObjectByType<LightProbeGroup>();
            GameObject go;
            if (existing != null) { go = existing.gameObject; }
            else
            {
                go = new GameObject("LightProbeGroup");
                existing = go.AddComponent<LightProbeGroup>();
            }
            go.transform.position = Vector3.zero;

            // Grid of probes over the playfield (130 x 75) at two heights so walking and
            // flying (4 m) units both get lit. Keep the count modest.
            var probes = new List<Vector3>();
            float[] xs = { -60f, -40f, -20f, 0f, 20f, 40f, 60f };
            float[] zs = { -35f, -17.5f, 0f, 17.5f, 35f };
            float[] ys = { 1.0f, 5.0f };
            foreach (float y in ys)
                foreach (float x in xs)
                    foreach (float z in zs)
                        probes.Add(new Vector3(x, y, z));

            existing.probePositions = probes.ToArray();
            EditorUtility.SetDirty(existing);
            sb.AppendLine($"  LightProbeGroup: {probes.Count} probes over the playfield (2 heights).");
        }

        static void EnsureReflectionProbe(StringBuilder sb)
        {
            var existing = Object.FindFirstObjectByType<ReflectionProbe>();
            GameObject go;
            if (existing != null) { go = existing.gameObject; }
            else
            {
                go = new GameObject("ReflectionProbe");
                existing = go.AddComponent<ReflectionProbe>();
            }
            go.transform.position = new Vector3(0f, 15f, 0f);
            existing.mode = ReflectionProbeMode.Baked;
            existing.size = new Vector3(140f, 60f, 85f);
            existing.center = Vector3.zero;
            existing.resolution = 128;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ReflectionProbeStatic);
            EditorUtility.SetDirty(existing);
            sb.AppendLine("  ReflectionProbe: baked, covers the playfield.");
        }

        static void ConfigureLightingSettings(StringBuilder sb)
        {
            LightingSettings settings = null;
            try { settings = Lightmapping.lightingSettings; }
            catch { settings = null; }

            const string settingsPath = "Assets/_COREHOLD/Settings/Corehold_Baked.lighting";
            if (settings == null)
                settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(settingsPath);
            if (settings == null)
            {
                settings = new LightingSettings { name = "Corehold_Baked" };
                System.IO.Directory.CreateDirectory("Assets/_COREHOLD/Settings");
                AssetDatabase.CreateAsset(settings, settingsPath);
            }
            Lightmapping.lightingSettings = settings;
            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.autoGenerate = false;
            settings.lightmapMaxSize = 1024;
            settings.lightmapResolution = 12f; // texels/unit — coarse, big map
            settings.directionalityMode = LightmapsMode.NonDirectional;
            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly;

            // Ambient / environment.
            sb.AppendLine("  LightingSettings: Baked GI on, Realtime GI off, Auto off, non-directional, 1024 maps.");
        }

        [MenuItem("Tools/COREHOLD/Bake Lighting")]
        public static string BakeLighting()
        {
            Lightmapping.Bake();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(scene);
            var msg = "Lighting bake completed and scene saved.";
            Debug.Log("[COREHOLD] " + msg);
            return msg;
        }
    }
}
