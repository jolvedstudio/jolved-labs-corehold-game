using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoplayEditor
{
    /// <summary>
    /// Ticket 37 wiring: attach the CameraShake to the Main Camera and the
    /// CoreDamageState to the Firadzo Shield Generator (Core), wiring the dome
    /// renderers and the two darkening segments. Idempotent — safe to re-run.
    /// </summary>
    public static class Ticket37Setup
    {
        private const string HeadPath = "RefineryLevel/Core_Blockout/Core_ShieldGenerator/Shield_generator_2_head";
        private const string CorePath = "RefineryLevel/Core_Blockout/Core_ShieldGenerator";
        private const string SegAPath = "RefineryLevel/Core_Blockout/Core_ShieldGenerator/Shield_generator_2_base/Shield_generator2_base_extra_module_1";
        private const string SegBPath = "RefineryLevel/Core_Blockout/Core_ShieldGenerator/Shield_generator_2_base/Shield_generator2_base_extra_module_2";

        public static string Execute()
        {
            var scene = SceneManager.GetActiveScene();
            var log = new System.Text.StringBuilder();

            // ---- Camera shake ----
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = SceneLookup.Find("Main Camera");
                if (camGo != null) cam = camGo.GetComponent<Camera>();
            }
            if (cam != null)
            {
                var shake = cam.GetComponent<CameraShake>();
                if (shake == null)
                {
                    shake = Undo.AddComponent<CameraShake>(cam.gameObject);
                    log.AppendLine($"Added CameraShake to {cam.name}.");
                }
                else
                {
                    log.AppendLine("CameraShake already present on Main Camera.");
                }
                EditorUtility.SetDirty(cam.gameObject);
            }
            else
            {
                log.AppendLine("WARNING: Main Camera not found — CameraShake NOT added.");
            }

            // ---- Core damage state ----
            var core = SceneLookup.Find(CorePath);
            var head = SceneLookup.Find(HeadPath);
            if (core == null)
            {
                log.AppendLine("WARNING: Core_ShieldGenerator not found — CoreDamageState NOT added.");
            }
            else
            {
                var cds = core.GetComponent<CoreDamageState>();
                if (cds == null)
                {
                    cds = Undo.AddComponent<CoreDamageState>(core);
                    log.AppendLine("Added CoreDamageState to Core_ShieldGenerator.");
                }
                else
                {
                    log.AppendLine("CoreDamageState already present on Core.");
                }

                var so = new SerializedObject(cds);

                // Dome root + renderers (the head/dome drives the cyan→amber emissive + flicker).
                if (head != null)
                {
                    so.FindProperty("domeRoot").objectReferenceValue = head.transform;
                    var domeRenderers = head.GetComponentsInChildren<Renderer>(true);
                    SetRendererArray(so.FindProperty("domeRenderers"), domeRenderers);
                    EnableEmission(domeRenderers);
                    log.AppendLine($"Dome root = head, {domeRenderers.Length} dome renderer(s).");
                }

                // Two darkening segments — the base extra modules go dark at 66% / 33%.
                var segA = SceneLookup.Find(SegAPath);
                var segB = SceneLookup.Find(SegBPath);
                SetSingleRenderer(so.FindProperty("segment0Renderers"), segA, "segment0 (66%)", log);
                SetSingleRenderer(so.FindProperty("segment1Renderers"), segB, "segment1 (33%)", log);
                if (segA != null) EnableEmission(new[] { segA.GetComponent<Renderer>() });
                if (segB != null) EnableEmission(new[] { segB.GetComponent<Renderer>() });

                // Segment anchors: place them at the segment mesh centres so sparks land there.
                var anchors = so.FindProperty("segmentAnchors");
                anchors.arraySize = 2;
                anchors.GetArrayElementAtIndex(0).objectReferenceValue = SegmentAnchor(core.transform, segA, 0);
                anchors.GetArrayElementAtIndex(1).objectReferenceValue = SegmentAnchor(core.transform, segB, 1);

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(core);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            log.AppendLine("Scene saved.");
            return log.ToString();
        }

        private static Transform SegmentAnchor(Transform coreRoot, GameObject seg, int index)
        {
            string name = $"DomeSegmentAnchor_{index}";
            // Reuse an existing anchor if one was already created.
            var existing = coreRoot.Find(name);
            if (existing != null) return existing;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create dome segment anchor");
            go.transform.SetParent(coreRoot, true);
            if (seg != null)
            {
                var r = seg.GetComponent<Renderer>();
                go.transform.position = r != null ? r.bounds.center : seg.transform.position;
            }
            else
            {
                go.transform.position = coreRoot.position + new Vector3(index == 0 ? 0.9f : -0.9f, 0.7f, index == 0 ? 0.5f : -0.5f);
            }
            return go.transform;
        }

        private static void SetSingleRenderer(SerializedProperty prop, GameObject go, string label, System.Text.StringBuilder log)
        {
            var r = go != null ? go.GetComponent<Renderer>() : null;
            if (r != null)
            {
                prop.arraySize = 1;
                prop.GetArrayElementAtIndex(0).objectReferenceValue = r;
                log.AppendLine($"{label} renderer = {go.name}.");
            }
            else
            {
                prop.arraySize = 0;
                log.AppendLine($"WARNING: {label} renderer not found.");
            }
        }

        private static void SetRendererArray(SerializedProperty prop, Renderer[] renderers)
        {
            prop.arraySize = renderers.Length;
            for (int i = 0; i < renderers.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
        }

        /// <summary>
        /// Enable the emission keyword on the renderers' shared materials so a
        /// MaterialPropertyBlock override of _EmissionColor actually renders
        /// (URP/Lit ignores emissive when the keyword is off).
        /// </summary>
        private static void EnableEmission(Renderer[] renderers)
        {
            if (renderers == null) return;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (!m.HasProperty("_EmissionColor")) continue;
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    // Give it a low baseline so the cyan drive is visible from the start.
                    var c = m.GetColor("_EmissionColor");
                    if (c.maxColorComponent < 0.01f)
                        m.SetColor("_EmissionColor", new Color(0.15f, 0.9f, 1f, 1f));
                    EditorUtility.SetDirty(m);
                }
            }
        }
    }
}
