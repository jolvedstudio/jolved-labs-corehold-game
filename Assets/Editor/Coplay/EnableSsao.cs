using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// One-shot project setup (M-d): add URP's Screen Space Ambient Occlusion
/// renderer feature to the active Universal Renderer, so terrain folds, prop
/// bases and pad sockets get contact shading in every scene at once.
///
/// This edits a PROJECT asset (the renderer data), not a scene — which is why
/// it is a menu tool and not a generation stage: a stage must never mutate
/// project-wide rendering state as a side effect of building one map.
///
/// The SSAO feature type is not public API, so the tool goes through
/// serialized properties and reflection, and on ANY miss it prints the exact
/// manual path instead of half-editing the asset. Idempotent: run twice, get
/// one feature.
/// </summary>
public static class EnableSsao
{
    [MenuItem("Tools/COREHOLD/Scene Setup/Enable SSAO (URP Renderer)", false, 48)]
    public static void Run()
    {
        const string manual =
            "Manual path: select the Universal Renderer Data asset (Project Settings → Graphics → " +
            "default render pipeline asset → Renderer List), press 'Add Renderer Feature' → " +
            "'Screen Space Ambient Occlusion', and set Source to 'Depth' (the terrain shader has " +
            "no DepthNormals pass).";

        try
        {
            var rp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (rp == null)
            {
                Debug.LogWarning("[M-d] No UniversalRenderPipelineAsset is active. " + manual);
                return;
            }

            // The renderer list and default index are serialized-private.
            var rpSo = new SerializedObject(rp);
            SerializedProperty list = rpSo.FindProperty("m_RendererDataList");
            SerializedProperty defaultIndex = rpSo.FindProperty("m_DefaultRendererIndex");
            if (list == null || list.arraySize == 0)
            {
                Debug.LogWarning("[M-d] Could not read the renderer list off the URP asset. " + manual);
                return;
            }
            int idx = defaultIndex != null
                ? Mathf.Clamp(defaultIndex.intValue, 0, list.arraySize - 1) : 0;
            var rendererData = list.GetArrayElementAtIndex(idx).objectReferenceValue as ScriptableObject;
            if (rendererData == null)
            {
                Debug.LogWarning("[M-d] The default renderer entry is empty. " + manual);
                return;
            }

            // The feature type is internal — resolve it by name from URP's runtime assembly.
            Type ssaoType = typeof(UniversalRenderPipelineAsset).Assembly
                .GetTypes().FirstOrDefault(t => t.Name == "ScreenSpaceAmbientOcclusion");
            if (ssaoType == null)
            {
                Debug.LogWarning("[M-d] This URP version exposes no ScreenSpaceAmbientOcclusion type. " + manual);
                return;
            }

            var dataSo = new SerializedObject(rendererData);
            SerializedProperty features = dataSo.FindProperty("m_RendererFeatures");
            SerializedProperty featureMap = dataSo.FindProperty("m_RendererFeatureMap");
            if (features == null)
            {
                Debug.LogWarning("[M-d] Renderer data has no m_RendererFeatures list. " + manual);
                return;
            }

            for (int i = 0; i < features.arraySize; i++)
            {
                var existing = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (existing != null && existing.GetType() == ssaoType)
                {
                    Debug.Log("[M-d] SSAO renderer feature already present — nothing to do.");
                    return;
                }
            }

            var feature = (ScriptableObject)ScriptableObject.CreateInstance(ssaoType);
            feature.name = "ScreenSpaceAmbientOcclusion";
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            AssetDatabase.SaveAssets();

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            // Newer URP keeps a parallel local-id map; keep it in step when present.
            if (featureMap != null &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
            {
                featureMap.arraySize++;
                featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
            }

            dataSo.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();

            Debug.Log("[M-d] SSAO renderer feature added to the default Universal Renderer. " +
                      "In its inspector set Source to 'Depth' (the terrain shader has no DepthNormals " +
                      "pass); defaults are otherwise sensible. WebGL2 supports it — profile before " +
                      "shipping to low-end targets.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[M-d] SSAO auto-setup failed ({e.GetType().Name}: {e.Message}). " + manual);
        }
    }
}
