using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Towers;

namespace CoplayEditor
{
    /// <summary>
    /// Tidies the two support towers: their bodies shipped with a stray TurretAim +
    /// TowerWeapon (harmless for a pure aura, but the weapon had no definition). This
    /// links the definition on the TowerWeapon so the tower reports consistent
    /// tier/range data, matching the Floodlight reference support tower.
    /// </summary>
    public static class PatchSupportTowerDefs
    {
        public static string Execute()
        {
            var log = new System.Text.StringBuilder();
            Patch("Tower_CryoNode", log);
            Patch("Tower_SalvageRig", log);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return log.ToString();
        }

        private static void Patch(string name, System.Text.StringBuilder log)
        {
            string prefabPath = $"Assets/_COREHOLD/Prefabs/Towers/{name}.prefab";
            string defPath = $"Assets/_COREHOLD/Data/Towers/{name}.asset";
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(defPath);
            if (def == null) { log.AppendLine($"ERROR: {defPath} missing."); return; }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var weapon = root.GetComponent<TowerWeapon>();
                if (weapon != null)
                {
                    var so = new SerializedObject(weapon);
                    so.FindProperty("definition").objectReferenceValue = def;
                    so.FindProperty("tierIndex").intValue = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                log.AppendLine($"{name}: linked TowerWeapon definition.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
