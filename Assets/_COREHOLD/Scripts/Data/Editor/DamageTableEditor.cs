using UnityEditor;
using UnityEngine;

namespace Corehold.Data.EditorTools
{
    /// <summary>
    /// Renders <see cref="DamageTable"/> as a readable, labelled 3×3 grid in the
    /// Inspector (GDD §7.1) — rows are damage types, columns are armour types.
    /// </summary>
    [CustomEditor(typeof(DamageTable))]
    public class DamageTableEditor : Editor
    {
        static readonly string[] DamageLabels = { "Kinetic", "Energy", "Explosive" };
        static readonly string[] ArmourLabels = { "Unarmoured", "Plated", "Shielded" };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty rows = serializedObject.FindProperty("rows");
            if (rows == null || rows.arraySize != 3)
            {
                EditorGUILayout.HelpBox("Damage table must have exactly 3 rows.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.LabelField("Damage × Armour Multipliers", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            float labelW = 90f;
            float cellW = 80f;

            // Header row: armour type column labels.
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(GUIContent.none, GUILayout.Width(labelW));
                var headerStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
                for (int c = 0; c < 3; c++)
                    GUILayout.Label(ArmourLabels[c], headerStyle, GUILayout.Width(cellW));
            }

            // One line per damage type.
            string[] fieldNames = { "vsUnarmoured", "vsPlated", "vsShielded" };
            for (int r = 0; r < 3; r++)
            {
                SerializedProperty row = rows.GetArrayElementAtIndex(r);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(DamageLabels[r], EditorStyles.boldLabel, GUILayout.Width(labelW));
                    for (int c = 0; c < 3; c++)
                    {
                        SerializedProperty cell = row.FindPropertyRelative(fieldNames[c]);
                        cell.floatValue = EditorGUILayout.FloatField(cell.floatValue, GUILayout.Width(cellW));
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
