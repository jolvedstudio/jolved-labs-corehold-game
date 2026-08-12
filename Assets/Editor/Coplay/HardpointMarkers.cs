using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Towers;

namespace CoreholdEditor
{
    /// <summary>
    /// The hardpoint 'Pad' children had no renderer, so build pads were invisible and
    /// the player had no idea where to build. This adds a visible flat emissive disc
    /// marker to every TowerHardpoint, tinted by pad class, and wires it as the pad's
    /// rimRenderer so it pulses while empty and darkens when occupied (GDD §5.3).
    /// Idempotent — reuses an existing 'PadMarker' child if present.
    /// </summary>
    public static class HardpointMarkers
    {
        const string MatDir = "Assets/_COREHOLD/Art/Materials";

        public static string Run()
        {
            var sb = new StringBuilder();
            Directory.CreateDirectory(MatDir);

            foreach (var pad in Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            {
                Color c = ColorForPad(pad.name);
                var mat = GetOrCreateMat(c);

                var marker = pad.transform.Find("PadMarker");
                if (marker == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    go.name = "PadMarker";
                    // Remove the primitive's collider — the pad's own trigger handles taps.
                    var col = go.GetComponent<Collider>();
                    if (col != null) Object.DestroyImmediate(col);
                    go.transform.SetParent(pad.transform, false);
                    go.transform.localPosition = new Vector3(0f, 0.12f, 0f);
                    go.transform.localScale = new Vector3(2.6f, 0.05f, 2.6f); // flat disc ~2.6m, readable from the play camera
                    marker = go.transform;
                    sb.AppendLine($"{pad.name}: created PadMarker");
                }

                var mr = marker.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                // Wire the rim renderer via the public setter (edit-mode safe).
                pad.SetRimRenderer(mr);
                EditorUtility.SetDirty(pad);
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("[COREHOLD] HardpointMarkers:\n" + sb);
            return sb.ToString();
        }

        static Color ColorForPad(string name)
        {
            if (name.Contains("Premium")) return new Color(0f, 0.8f, 1f);      // cyan
            if (name.Contains("Standard")) return new Color(0.2f, 1f, 0.5f);   // green
            if (name.Contains("Rear")) return new Color(1f, 0.7f, 0.15f);      // amber
            if (name.Contains("Overwatch")) return new Color(1f, 0.3f, 0.9f);  // magenta
            return new Color(0f, 0.8f, 1f);
        }

        static Material GetOrCreateMat(Color c)
        {
            string hex = ColorUtility.ToHtmlStringRGB(c);
            string path = $"{MatDir}/Mat_Pad_{hex}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader) { name = $"Mat_Pad_{hex}" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            // Enable emission so the TowerHardpoint pulse (which drives _EmissionColor) shows.
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", c);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
