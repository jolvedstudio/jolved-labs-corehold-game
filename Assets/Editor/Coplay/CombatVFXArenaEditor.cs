using System.Collections.Generic;
using System.Text;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Authoring surface for the combat testbed. The play-mode arena is a VIEWER
/// (it spawns disconnected clones), so tuning that must persist is done here, in
/// EDIT mode, on CONNECTED prefab instances:
///
///   1. "Spawn Editable Instances" lays out one connected prefab instance of every
///      tower and every enemy. Select any of them and tweak in the Inspector, or
///      tweak the VFXDirector in the scene.
///   2. When happy, "Apply Overrides → Prefabs &amp; VFX Config":
///        • applies every instance's overrides back to its source prefab
///          (PrefabUtility.ApplyPrefabInstance), and
///        • writes the scene VFXDirector's wiring into the shared
///          VFXDirectorConfig asset the Level Generator reads.
///      From then on every newly generated level picks the changes up.
///   3. "Clear Editable Instances" removes the authoring row.
///
/// Tower/enemy STATS live in their ScriptableObject definitions (Data/Towers,
/// Data/Enemies, DamageTable) — those are edited directly and are already global,
/// so they need no apply step; this tool covers prefab structure/visuals and the
/// VFX wiring, which previously required editing code.
/// </summary>
[CustomEditor(typeof(CombatVFXArena))]
public class CombatVFXArenaEditor : Editor
{
    private const string AuthoringRootName = "AuthoringInstances (edit-mode)";
    private const float Spacing = 6f;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var arena = (CombatVFXArena)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Authoring (edit mode)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Play mode is a VIEWER. To make tuning persist, spawn editable instances " +
            "here, tweak them (and/or the VFXDirector), then Apply. Stats live in the " +
            "Data/ definition assets and are already global — edit those directly.",
            MessageType.Info);

        if (Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Exit play mode to author and apply.", MessageType.Warning);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Spawn Editable Instances"))
                SpawnEditableInstances(arena);
            if (GUILayout.Button("Clear Editable Instances"))
                ClearEditableInstances();
        }

        EditorGUILayout.Space(2);
        GUI.backgroundColor = new Color(0.5f, 0.9f, 0.5f);
        if (GUILayout.Button("Apply Overrides → Prefabs & VFX Config", GUILayout.Height(30)))
            ApplyAll(arena);
        GUI.backgroundColor = Color.white;
    }

    // ---------------------------------------------------------------- spawn

    private static Transform GetAuthoringRoot(bool create)
    {
        var existing = GameObject.Find(AuthoringRootName);
        if (existing != null)
            return existing.transform;
        if (!create)
            return null;
        var go = new GameObject(AuthoringRootName);
        Undo.RegisterCreatedObjectUndo(go, "Create Authoring Root");
        return go.transform;
    }

    private void SpawnEditableInstances(CombatVFXArena arena)
    {
        ClearEditableInstances();
        Transform root = GetAuthoringRoot(true);

        float x = 0f;
        const float step = 6f;

        // Towers on one row (z = -4), enemies on another (z = +4), connected to
        // their source prefabs so overrides can be applied back.
        x = 0f;
        if (arena.towerPrefabs != null)
            foreach (var prefab in arena.towerPrefabs)
            {
                if (prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
                inst.transform.position = new Vector3(x, 0f, -4f);
                Undo.RegisterCreatedObjectUndo(inst, "Spawn Tower Instance");
                x += step;
            }

        x = 0f;
        if (arena.enemies != null)
            foreach (var e in arena.enemies)
            {
                if (e.prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(e.prefab, root);
                bool isAir = e.definition != null && e.definition.isAir;
                inst.transform.position = new Vector3(x, isAir ? 4f : 0f, 4f);
                Undo.RegisterCreatedObjectUndo(inst, "Spawn Enemy Instance");
                x += step;
            }

        EditorSceneManager.MarkSceneDirty(arena.gameObject.scene);
        Selection.activeTransform = root;
        Debug.Log("[Arena] Spawned editable (connected) prefab instances under " +
                  $"'{AuthoringRootName}'. Tweak them, then Apply.");
    }

    private static void ClearEditableInstances()
    {
        var root = GetAuthoringRoot(false);
        if (root != null)
            Undo.DestroyObjectImmediate(root.gameObject);
    }

    // ---------------------------------------------------------------- apply

    private void ApplyAll(CombatVFXArena arena)
    {
        var log = new StringBuilder();
        int applied = ApplyPrefabOverrides(log);
        bool vfx = WriteVfxConfig(arena, log);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(arena.gameObject.scene);

        string summary = $"[Arena] Apply complete: {applied} prefab instance(s) applied, " +
                         $"VFX config {(vfx ? "updated" : "unchanged")}.\n" + log;
        Debug.Log(summary);
        EditorUtility.DisplayDialog("Apply to Prefabs & VFX Config",
            $"{applied} prefab(s) applied.\nVFX config {(vfx ? "updated" : "not written")}.\n\n" +
            "Newly generated levels will pick these up.", "OK");
    }

    private int ApplyPrefabOverrides(StringBuilder log)
    {
        var root = GetAuthoringRoot(false);
        if (root == null)
        {
            log.AppendLine("• No authoring instances found — nothing to apply to prefabs.");
            return 0;
        }

        int count = 0;
        // Snapshot children first (applying can reorder).
        var children = new List<Transform>();
        foreach (Transform c in root)
            children.Add(c);

        foreach (var child in children)
        {
            var go = child.gameObject;
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(go))
                continue;

            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            string prefabPath = src != null ? AssetDatabase.GetAssetPath(src) : "(unknown)";
            try
            {
                PrefabUtility.ApplyPrefabInstance(go, InteractionMode.UserAction);
                log.AppendLine($"• Applied '{go.name}' → {prefabPath}");
                count++;
            }
            catch (System.Exception ex)
            {
                log.AppendLine($"• FAILED '{go.name}' → {prefabPath}: {ex.Message}");
            }
        }
        return count;
    }

    private bool WriteVfxConfig(CombatVFXArena arena, StringBuilder log)
    {
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
        {
            log.AppendLine("• No VFXDirector in scene — VFX config not written.");
            return false;
        }

        VFXDirectorConfig config = VFXConfigIO.WriteFromDirector(director, log);
        return config != null;
    }
}
