using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authors the four roster-expansion TowerDefinitions (approved 10-slot
/// roster): Railgun, Cryo Node, Flak Array, Salvage Rig. CODE + DATA only —
/// the chassis prefabs are HUMAN-authored in Unity/CoPlay and wired via
/// WireTowerPrefabs (each definition ships with basePrefab null until then;
/// the build menu shows those entries as WIP and keeps them un-buildable).
///
/// Prefab contracts (root components, mirroring the shipped towers):
///   Railgun     — Tower + TowerWeapon + TowerTargeting + TurretAim. The tier
///                 mounts carry `pierce = true`; nothing else special.
///   Cryo Node   — Tower (+ required chain) + CryoField. No weapons; radius
///                 rides the tier's auraRadius (the Floodlight trick).
///   Flak Array  — standard combat chassis; definition sets targetAirOnly.
///   Salvage Rig — Tower (+ chain) + SalvageRig. No weapons; auraRadius again.
///
/// Idempotent; safe to re-run (existing definitions are updated in place).
/// After prefabs exist: run WireTowerPrefabs, then Build Real UI, then the
/// icon renderer.
/// </summary>
public static class SetupRosterTowers
{
    private const string DataDir = "Assets/_COREHOLD/Data/Towers";

    [MenuItem("Tools/COREHOLD/Scene Setup/Roster Towers", false, 51)]
    public static void Setup()
    {
        var log = new StringBuilder();

        Author(log, "Tower_Railgun", def =>
        {
            def.id = "railgun";
            def.displayName = "Railgun";
            def.description = "Very long range. Slow, heavy bolts that pierce every enemy along the line.";
            def.damageType = DamageType.Kinetic;
            def.canTargetAir = true;
            def.targetAirOnly = false;
            def.tiers = new[]
            {
                RailTier(cost: 180, range: 26f, damage: 60f, fireRate: 0.5f),
                RailTier(cost: 220, range: 28f, damage: 95f, fireRate: 0.55f),
                RailTier(cost: 320, range: 30f, damage: 150f, fireRate: 0.6f),
            };
        });

        Author(log, "Tower_CryoNode", def =>
        {
            def.id = "cryonode";
            def.displayName = "Cryo Node";
            def.description = "No damage. Chills every enemy in its field — they crawl, your turrets feast.";
            def.damageType = DamageType.Energy;   // unused: it never fires
            def.canTargetAir = true;
            def.targetAirOnly = false;
            def.tiers = new[]
            {
                SupportTier(cost: 110, auraRadius: 9f),
                SupportTier(cost: 150, auraRadius: 11f),
                SupportTier(cost: 210, auraRadius: 13f),
            };
        });

        Author(log, "Tower_FlakArray", def =>
        {
            def.id = "flakarray";
            def.displayName = "Flak Array";
            def.description = "Air only. Shreds Wasps and Shrikes; blind to everything on the ground.";
            def.damageType = DamageType.Kinetic;
            def.canTargetAir = true;
            def.targetAirOnly = true;
            def.tiers = new[]
            {
                FlakTier(cost: 130, range: 16f, damage: 12f, fireRate: 3.5f),
                FlakTier(cost: 170, range: 18f, damage: 18f, fireRate: 4.0f),
                FlakTier(cost: 240, range: 20f, damage: 26f, fireRate: 4.5f),
            };
        });

        Author(log, "Tower_SalvageRig", def =>
        {
            def.id = "salvagerig";
            def.displayName = "Salvage Rig";
            def.description = "No damage. Kills inside its radius pay bonus salvage — reward your killzone.";
            def.damageType = DamageType.Kinetic;  // unused: it never fires
            def.canTargetAir = false;
            def.targetAirOnly = false;
            def.tiers = new[]
            {
                SupportTier(cost: 120, auraRadius: 10f),
                SupportTier(cost: 160, auraRadius: 12f),
                SupportTier(cost: 220, auraRadius: 14f),
            };
        });

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] SetupRosterTowers:\n" + log +
                  "\nAuthor the four chassis prefabs, then run WireTowerPrefabs → Build Real UI → Render Icons.");
    }

    // ------------------------------------------------------------ tier shapes

    private static TowerTier RailTier(int cost, float range, float damage, float fireRate) => new TowerTier
    {
        cost = cost,
        range = range,
        minRange = 0f,
        weapons = new[]
        {
            new TowerWeaponMount
            {
                damage = damage,
                fireRate = fireRate,
                muzzleIndex = -1,
                pierce = true,
                chainFalloff = 0f,
                tracerColor = new Color(3.2f, 1.4f, 4.5f, 1f),   // violet rail bolt, HDR
            },
        },
    };

    private static TowerTier FlakTier(int cost, float range, float damage, float fireRate) => new TowerTier
    {
        cost = cost,
        range = range,
        minRange = 0f,
        weapons = new[]
        {
            new TowerWeaponMount
            {
                damage = damage,
                fireRate = fireRate,
                muzzleIndex = -1,
                chainFalloff = 0f,
                tracerColor = new Color(3.5f, 3.0f, 1.0f, 1f),   // pale flak burst, HDR
            },
        },
    };

    // Support tiers: no weapons, no range — the aura radius is the payload
    // (CryoField / SalvageRig read it live; IsSupportRelay classifies for free
    // with zero aura bonuses so the buff pass grants nothing).
    private static TowerTier SupportTier(int cost, float auraRadius) => new TowerTier
    {
        cost = cost,
        range = 0f,
        minRange = 0f,
        weapons = new TowerWeaponMount[0],
        auraRadius = auraRadius,
        auraFireRateBonus = 0f,
        auraRangeBonus = 0f,
        auraDamageBonus = 0f,
    };

    // ------------------------------------------------------------ plumbing

    private static void Author(StringBuilder log, string assetName,
        System.Action<TowerDefinition> fill)
    {
        string path = $"{DataDir}/{assetName}.asset";
        var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(path);
        bool created = false;
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<TowerDefinition>();
            AssetDatabase.CreateAsset(def, path);
            created = true;
        }

        GameObject keepPrefab = def.basePrefab;   // human-authored — never clobbered
        fill(def);
        def.basePrefab = keepPrefab;

        EditorUtility.SetDirty(def);
        log.AppendLine($"[{(created ? "created" : "updated")}] {path}" +
                       (def.basePrefab == null ? " (prefab pending — entry shows WIP)" : ""));
    }
}
