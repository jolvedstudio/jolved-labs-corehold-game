using System;
using System.Linq;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="ArtTarget"/> with one job beyond the default: the
/// theme name is a PICKLIST of the EnvPacks that actually exist, not a typed
/// string. The coupling between a target and its pack is that exact string,
/// and a typo produced the least helpful failure available — a validate-gate
/// refusal one full pipeline run later. A dropdown makes the invalid state
/// unpickable; a value that matches no pack (deleted pack, imported JSON for a
/// pack not yet created) is shown loudly instead of silently kept.
///
/// The data stays a plain string underneath, on purpose: ReadingImporter
/// writes it from JSON, and a reading may legitimately name a pack that will
/// be created a minute later.
/// </summary>
[CustomEditor(typeof(ArtTarget))]
public class ArtTargetEditor : Editor
{
    private string[] _themes;

    private void OnEnable()
    {
        _themes = LoadThemes();
    }

    private static string[] LoadThemes()
    {
        return AssetDatabase.FindAssets("t:EnvPack")
            .Select(g => AssetDatabase.LoadAssetAtPath<EnvPack>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(p => p != null && !string.IsNullOrEmpty(p.themeName))
            .Select(p => p.themeName)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty prop = serializedObject.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (prop.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(prop);
            }
            else if (prop.name == "themeName")
            {
                DrawThemePicklist(prop);
            }
            else
            {
                EditorGUILayout.PropertyField(prop, true);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawThemePicklist(SerializedProperty prop)
    {
        if (_themes == null || _themes.Length == 0)
        {
            EditorGUILayout.PropertyField(prop);
            EditorGUILayout.HelpBox(
                "No EnvPack with a themeName exists yet — create one " +
                "(Create → COREHOLD → Env Pack) and set its themeName; this becomes a picklist.",
                MessageType.Warning);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            int idx = Array.IndexOf(_themes, prop.stringValue);
            if (idx < 0)
            {
                // Current value matches no pack: keep it visible and loud, and
                // only overwrite when the user picks a real theme.
                string current = string.IsNullOrEmpty(prop.stringValue)
                    ? "(pick an EnvPack theme)"
                    : $"⚠ '{prop.stringValue}' — no such pack";
                string[] options = new[] { current }.Concat(_themes).ToArray();
                int pick = EditorGUILayout.Popup("Theme Name", 0, options);
                if (pick > 0)
                    prop.stringValue = _themes[pick - 1];
            }
            else
            {
                int pick = EditorGUILayout.Popup("Theme Name", idx, _themes);
                if (pick != idx)
                    prop.stringValue = _themes[pick];
            }

            if (GUILayout.Button("↻", GUILayout.Width(26)))
                _themes = LoadThemes();
        }
    }
}
