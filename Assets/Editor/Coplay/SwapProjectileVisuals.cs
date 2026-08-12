using UnityEditor;
using UnityEngine;

/// <summary>
/// Replaces the placeholder primitive-sphere visuals on the two COREHOLD
/// projectile prefabs (FX_Missile, FX_MortarShell) with the sci-fi vendor meshes
/// from the Mech_Constructor_Turrets pack.
///
/// The vendor art is added as a CHILD of the existing projectile root and the
/// root's own primitive MeshRenderer/MeshFilter are removed. This preserves the
/// root GameObject (its fileID/GUID and its Corehold.Towers.Projectile component
/// with arcApex/poolPrewarm), so every TowerDefinition that already references
/// these prefabs stays wired — only the look changes.
///
/// Run once from Tools/COREHOLD. Safe to re-run: it rebuilds the "Model" child.
/// </summary>
public static class SwapProjectileVisuals
{
    private const string MissilePrefab = "Assets/_COREHOLD/Prefabs/Projectiles/FX_Missile.prefab";
    private const string MortarPrefab = "Assets/_COREHOLD/Prefabs/Projectiles/FX_MortarShell.prefab";

    private const string RocketVendor = "Assets/Vendor/Mech_Constructor_Turrets/Prefabs/Weapons/Projectile_Rocket_Lvl1.prefab";
    private const string ShellVendor = "Assets/Vendor/Mech_Constructor_Turrets/Prefabs/Weapons/Projectile_Shell_Mortar.prefab";

    [MenuItem("Tools/COREHOLD/Swap Projectile Visuals")]
    public static void Run()
    {
        // Missile: rocket points +Z, so no extra rotation. Scale it down so a
        // ~1.1 m rocket reads at projectile size in the diorama.
        SwapVisual(MissilePrefab, RocketVendor, Vector3.zero, 0.6f);

        // Mortar shell: roughly spherical; keep upright, modest scale.
        SwapVisual(MortarPrefab, ShellVendor, Vector3.zero, 0.7f);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] Swapped projectile visuals: FX_Missile -> Rocket_Lvl1, FX_MortarShell -> Shell_Mortar.");
    }

    private static void SwapVisual(string projectilePath, string vendorPath, Vector3 localEuler, float scale)
    {
        var vendor = AssetDatabase.LoadAssetAtPath<GameObject>(vendorPath);
        if (vendor == null)
        {
            Debug.LogError($"[COREHOLD] Vendor prefab not found: {vendorPath}");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(projectilePath);
        if (root == null)
        {
            Debug.LogError($"[COREHOLD] Projectile prefab not found: {projectilePath}");
            return;
        }

        try
        {
            // Remove the placeholder primitive renderer/filter on the root.
            var mr = root.GetComponent<MeshRenderer>();
            if (mr != null) Object.DestroyImmediate(mr, true);
            var mf = root.GetComponent<MeshFilter>();
            if (mf != null) Object.DestroyImmediate(mf, true);

            // Reset the root scale to 1: the placeholder scaled the primitive; the
            // vendor art carries its own scale on the Model child instead.
            root.transform.localScale = Vector3.one;

            // Rebuild the Model child from the vendor prefab.
            var existing = root.transform.Find("Model");
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject, true);

            var model = (GameObject)PrefabUtility.InstantiatePrefab(vendor);
            // Break the link so the projectile prefab owns the art (self-contained).
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localEulerAngles = localEuler;
            model.transform.localScale = Vector3.one * scale;

            // Projectiles never cast/receive shadows — keep the layer cheap.
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            PrefabUtility.SaveAsPrefabAsset(root, projectilePath);
            Debug.Log($"[COREHOLD] Updated {projectilePath} with vendor model {vendor.name}.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
