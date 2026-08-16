using System.Linq;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The one place the tower roster is enumerated (plan v2 §B4 / B0). Before
/// this, adding a turret meant editing a hardcoded name array in BuildRealUI —
/// the registry replaces that with discovery + a menuOrder field on the
/// definition, so "add a unit" is: create the definition, set menuOrder,
/// re-run Build Real UI. No list edits anywhere.
///
/// Ordering contract: (menuOrder, name). Ties resolve alphabetically, so two
/// definitions sharing an order value still sort deterministically.
/// </summary>
public static class RosterRegistry
{
    private const string TowersFolder = "Assets/_COREHOLD/Data/Towers";

    /// <summary>Every TowerDefinition, in build-menu order.</summary>
    public static TowerDefinition[] AllTowersOrdered()
    {
        return AssetDatabase.FindAssets("t:TowerDefinition", new[] { TowersFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<TowerDefinition>)
            .Where(d => d != null)
            .OrderBy(d => d.menuOrder)
            .ThenBy(d => d.name, System.StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// One-time migration: seed menuOrder from the retired BuildRealUI name
    /// array (10, 20, 30… — gaps left so inserts need no renumbering). Safe to
    /// re-run: definitions that already carry a non-zero order are left alone.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Scene Setup/Assign Tower Menu Order (defaults)", false, 40)]
    public static void AssignDefaults()
    {
        string[] legacyOrder = { "Autocannon", "MissileBattery", "ArcNode", "SiegeMortar", "ScanRelay",
                                 "Floodlight", "Railgun", "CryoNode", "FlakArray", "SalvageRig" };

        var defs = AllTowersOrdered();
        int assigned = 0;
        foreach (var def in defs)
        {
            if (def.menuOrder != 0) continue;
            int idx = System.Array.FindIndex(legacyOrder, n => def.name.Contains(n));
            def.menuOrder = idx >= 0 ? (idx + 1) * 10 : (legacyOrder.Length + 1) * 10;
            EditorUtility.SetDirty(def);
            assigned++;
        }
        AssetDatabase.SaveAssets();

        Debug.Log($"[RosterRegistry] Assigned menuOrder on {assigned} definition(s). Current order:\n  " +
                  string.Join("\n  ", AllTowersOrdered().Select(d => $"{d.menuOrder,4}  {d.name}")));
    }
}
