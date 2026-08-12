using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Enemies;
using Corehold.Data;

/// <summary>
/// Creates a new air-drone enemy (item g) from the RipVertices Sci-Fi Drone URP
/// prefab: builds a Drone.prefab wired with the Corehold Enemy stack (Enemy,
/// EnemyMover set to air, EnemyWeapon), and a matching Enemy_Drone EnemyDefinition
/// with isAir = true so it flies straight to the Core like the Wasp.
/// </summary>
public static class CreateDroneEnemy
{
    private const string DroneSource = "Assets/Vendor/RipVerticesStudio/Sci-Fi Drone Stylized Enemy Unit/URP/Sci-Fi_Enemy_Orange_URP.prefab";
    private const string DronePrefabPath = "Assets/_COREHOLD/Prefabs/Enemies/Drone.prefab";
    private const string DroneDefPath = "Assets/_COREHOLD/Data/Enemies/Enemy_Drone.asset";

    public static string Execute()
    {
        var sb = new StringBuilder();

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(DroneSource);
        if (source == null)
            return $"Source drone prefab not found at {DroneSource}";

        // Instantiate the source model, add the Corehold enemy stack, then save as
        // a new prefab under the Corehold Enemies folder.
        var root = (GameObject)PrefabUtility.InstantiatePrefab(source);
        root.name = "Drone";

        // Unpack so the new prefab is self-contained and can hold added components
        // without being a nested variant of the vendor prefab.
        PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        // Enemy core.
        var enemy = root.GetComponent<Enemy>();
        if (enemy == null) enemy = root.AddComponent<Enemy>();
        var enemySo = new SerializedObject(enemy);
        enemySo.FindProperty("maxHealth").floatValue = 60f;
        enemySo.FindProperty("bounty").intValue = 12;
        enemySo.FindProperty("leakDamage").floatValue = 2f;
        enemySo.FindProperty("isAir").boolValue = true;
        enemySo.FindProperty("armourType").enumValueIndex = 0; // Unarmoured
        enemySo.ApplyModifiedPropertiesWithoutUndo();

        // Mover (air).
        var mover = root.GetComponent<EnemyMover>();
        if (mover == null) mover = root.AddComponent<EnemyMover>();
        var moverSo = new SerializedObject(mover);
        moverSo.FindProperty("moveSpeed").floatValue = 8f;
        moverSo.ApplyModifiedPropertiesWithoutUndo();

        // Return fire.
        var weapon = root.GetComponent<EnemyWeapon>();
        if (weapon == null) weapon = root.AddComponent<EnemyWeapon>();
        var weaponSo = new SerializedObject(weapon);
        var weaponArr = weaponSo.FindProperty("weapons");
        weaponArr.arraySize = 1;
        var weaponEl = weaponArr.GetArrayElementAtIndex(0);
        weaponEl.FindPropertyRelative("range").floatValue = 14f;
        weaponEl.FindPropertyRelative("fireRate").floatValue = 0.9f;
        weaponEl.FindPropertyRelative("damage").floatValue = 6f;
        weaponEl.FindPropertyRelative("tracerColor").colorValue = new Color(4f, 1.2f, 0.3f, 1f);
        weaponSo.ApplyModifiedPropertiesWithoutUndo();

        // Save the new prefab.
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, DronePrefabPath);
        Object.DestroyImmediate(root);
        if (savedPrefab == null)
            return sb.AppendLine("Failed to save Drone.prefab").ToString();
        sb.AppendLine($"Saved {DronePrefabPath}");

        // Create the EnemyDefinition.
        EnemyDefinition def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(DroneDefPath);
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<EnemyDefinition>();
            AssetDatabase.CreateAsset(def, DroneDefPath);
        }
        var defSo = new SerializedObject(def);
        defSo.FindProperty("id").stringValue = "drone";
        defSo.FindProperty("displayName").stringValue = "Drone";
        defSo.FindProperty("prefab").objectReferenceValue = savedPrefab;
        defSo.FindProperty("baseHealth").floatValue = 60f;
        defSo.FindProperty("armourType").enumValueIndex = 0;
        defSo.FindProperty("moveSpeed").floatValue = 8f;
        defSo.FindProperty("bounty").intValue = 12;
        defSo.FindProperty("leakDamage").intValue = 2;
        defSo.FindProperty("isAir").boolValue = true;
        defSo.FindProperty("flightAltitude").floatValue = 4f;
        defSo.FindProperty("animatorClipSpeedRef").floatValue = 1f;
        defSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(def);
        sb.AppendLine($"Saved {DroneDefPath}");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return sb.ToString();
    }
}
