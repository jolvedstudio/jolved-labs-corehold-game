using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Corehold.Enemies;

namespace CoplayEditor
{
    /// <summary>
    /// Adds the TrackScroll tread-roll component to the Shrike tank and wires its
    /// two track renderers, so the treads visibly roll at travel speed. Uses a
    /// per-renderer MaterialPropertyBlock UV scroll (no rig, no shared-material edit).
    /// </summary>
    public static class AddShrikeTrackScroll
    {
        private const string ShrikePath = "Assets/_COREHOLD/Prefabs/Enemies/Enemy_Shrike.prefab";

        public static string Execute()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(ShrikePath);
            try
            {
                var tracks = new List<Renderer>();
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.name.IndexOf("Track", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        tracks.Add(r);

                var scroll = root.GetComponent<TrackScroll>();
                if (scroll == null) scroll = root.AddComponent<TrackScroll>();

                var so = new SerializedObject(scroll);
                var arr = so.FindProperty("trackRenderers");
                arr.arraySize = tracks.Count;
                for (int i = 0; i < tracks.Count; i++)
                    arr.GetArrayElementAtIndex(i).objectReferenceValue = tracks[i];
                so.FindProperty("metresPerLoop").floatValue = 1.2f;
                so.FindProperty("uvAxis").vector2Value = new Vector2(0f, 1f);
                so.FindProperty("fallbackSpeed").floatValue = 4.5f;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, ShrikePath);
                return $"Shrike: added TrackScroll wired to {tracks.Count} track renderer(s).";
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
