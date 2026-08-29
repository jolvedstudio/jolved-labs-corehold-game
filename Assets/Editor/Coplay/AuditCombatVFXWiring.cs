using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Audits whether every TOWER and ENEMY prefab is actually wired to produce the
/// three combat VFX classes — muzzle flash, tracer, and impact — through the
/// VFXDirector. This inspects the authored DATA (muzzle points, weapon mounts,
/// tracer colours, projectile prefabs), not just the code path, so it catches
/// units that would silently no-op (e.g. a hitscan mount with no muzzle point, or
/// an enemy weapon with no muzzle).
///
/// Rules encoded (mirrors TowerWeapon / Projectile / EnemyWeapon):
///  - Muzzle flash: PlayMuzzle needs a TowerDefinition (for the damage type) and a
///    muzzle transform; with no muzzle point it spawns at the tower origin (base),
///    which reads wrong. So a combat tower SHOULD have >=1 muzzle point.
///  - Tracer: only hitscan / chain / pierce mounts draw a tracer. Projectile mounts
///    (Missile / Mortar) have visible travel instead — NOT a defect.
///  - Impact: hitscan / chain / single-target projectile play an impact; splash
///    projectiles play a sized explosion instead — NOT a defect.
/// </summary>
public static class AuditCombatVFXWiring
{
    private const string TowerDir = "Assets/_COREHOLD/Prefabs/Towers";
    private const string EnemyDir = "Assets/_COREHOLD/Prefabs/Enemies";

    [MenuItem("Tools/COREHOLD/Validate/Audit Combat VFX Wiring", false, 29)]
    public static void RunMenu() => Debug.Log(Execute());

    public static string Execute()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== COREHOLD Combat VFX Wiring Audit ===");

        // --- VFXDirector prefab coverage (are the effect slots filled?) ---
        sb.AppendLine("\n--- VFXDirector effect slots (active scene) ---");
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
        {
            sb.AppendLine("WARNING: no VFXDirector in the active scene.");
        }
        else
        {
            var so = new SerializedObject(director);
            var effects = so.FindProperty("effects");
            var filled = new HashSet<int>();
            for (int i = 0; i < effects.arraySize; i++)
            {
                var el = effects.GetArrayElementAtIndex(i);
                int id = el.FindPropertyRelative("id").enumValueIndex;
                bool hasPrefab = el.FindPropertyRelative("prefab").objectReferenceValue != null;
                if (hasPrefab) filled.Add(id);
            }
            foreach (var name in System.Enum.GetNames(typeof(VFXDirector.Effect)))
            {
                int val = (int)System.Enum.Parse(typeof(VFXDirector.Effect), name);
                sb.AppendLine($"  {(filled.Contains(val) ? "OK " : "MISSING ")} {name}");
            }
            var coreMat = so.FindProperty("tracerCoreMaterial").objectReferenceValue;
            var haloMat = so.FindProperty("tracerHaloMaterial").objectReferenceValue;
            sb.AppendLine($"  Tracer core material: {(coreMat != null ? coreMat.name : "(built at runtime)")}");
            sb.AppendLine($"  Tracer halo material: {(haloMat != null ? haloMat.name : "(built at runtime)")}");
        }

        // --- Towers ---
        sb.AppendLine("\n--- Towers ---");
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { TowerDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            AuditTower(go, sb);
        }

        // --- Enemies ---
        sb.AppendLine("\n--- Enemies ---");
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { EnemyDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            AuditEnemy(go, sb);
        }

        return sb.ToString();
    }

    private static void AuditTower(GameObject go, StringBuilder sb)
    {
        var weapon = go.GetComponent<TowerWeapon>();
        if (weapon == null)
        {
            sb.AppendLine($"[{go.name}] no TowerWeapon (support/other) — skipped.");
            return;
        }

        var so = new SerializedObject(weapon);
        var def = so.FindProperty("definition").objectReferenceValue as TowerDefinition;
        var muzzlePoints = so.FindProperty("muzzlePoints");
        int muzzleCount = muzzlePoints != null ? muzzlePoints.arraySize : 0;
        // Legacy single muzzle counts too.
        bool legacyMuzzle = so.FindProperty("muzzlePoint").objectReferenceValue != null;
        int effMuzzles = muzzleCount > 0 ? muzzleCount : (legacyMuzzle ? 1 : 0);

        var issues = new List<string>();
        if (def == null)
            issues.Add("NO definition (muzzle flash damage type unresolved → PlayMuzzle no-ops if null)");

        // Inspect each tier's weapons for the firing behaviour + tracer wiring.
        bool anyCombat = false;
        bool anyHitscanLike = false;
        bool anyProjectile = false;
        if (def != null && def.tiers != null)
        {
            for (int t = 0; t < def.tiers.Length; t++)
            {
                var weapons = def.tiers[t].Weapons;
                for (int w = 0; w < weapons.Length; w++)
                {
                    var m = weapons[w];
                    if (m.fireRate <= 0f) continue; // non-combat mount (Scan Relay etc.)
                    anyCombat = true;

                    bool isProjectile = m.projectilePrefab != null;
                    bool isChain = m.chainTargets > 1;
                    bool isPierce = m.pierce;
                    bool hitscanLike = !isProjectile; // chain/pierce/plain all hitscan-draw a tracer

                    if (isProjectile) anyProjectile = true;
                    if (hitscanLike) anyHitscanLike = true;

                    if (isProjectile && m.projectilePrefab == null)
                        issues.Add($"tier{t} wpn{w}: projectile mount with null prefab");

                    // Tracer colour: mount alpha 0 falls back to the component default,
                    // which is authored — so not an error, just note if relying on it.
                    if (hitscanLike && m.tracerColor.a <= 0f)
                        issues.Add($"tier{t} wpn{w}: hitscan tracerColor alpha 0 (uses component fallback)");
                }
            }
        }

        // Muzzle flash needs a muzzle point for a combat tower.
        if (anyCombat && effMuzzles == 0)
            issues.Add("combat tower with NO muzzle points (muzzle flash spawns at tower origin, not the barrel)");

        string kind = anyProjectile && anyHitscanLike ? "hitscan+projectile"
            : anyProjectile ? "projectile (tracer by design N/A)"
            : anyHitscanLike ? "hitscan/chain/pierce"
            : "non-combat";

        string status = issues.Count == 0 ? "OK" : "ISSUES";
        sb.AppendLine($"[{go.name}] {status} — kind={kind}, muzzlePoints={effMuzzles}, def={(def != null ? def.name : "NULL")}");
        foreach (var i in issues)
            sb.AppendLine($"    - {i}");
    }

    private static void AuditEnemy(GameObject go, StringBuilder sb)
    {
        var weapon = go.GetComponent<EnemyWeapon>();
        if (weapon == null)
        {
            sb.AppendLine($"[{go.name}] no EnemyWeapon — does NOT shoot back (no muzzle/tracer/impact expected).");
            return;
        }

        var so = new SerializedObject(weapon);
        var weapons = so.FindProperty("weapons");
        var issues = new List<string>();
        int combatMounts = 0;

        int n = weapons != null ? weapons.arraySize : 0;
        if (n == 0)
        {
            // Legacy single-weapon fields migrate to weapons[0] at runtime.
            float fr = so.FindProperty("fireRate").floatValue;
            float dmg = so.FindProperty("damage").floatValue;
            var legacyMuzzle = so.FindProperty("muzzle").objectReferenceValue;
            var legacyColor = so.FindProperty("tracerColor").colorValue;
            if (fr > 0f && dmg > 0f)
            {
                combatMounts++;
                if (legacyMuzzle == null)
                    issues.Add("legacy weapon: no muzzle (tracer/impact origin = transform +1m up)");
                if (legacyColor.a <= 0f)
                    issues.Add("legacy weapon: tracerColor alpha 0");
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                var el = weapons.GetArrayElementAtIndex(i);
                float fr = el.FindPropertyRelative("fireRate").floatValue;
                float dmg = el.FindPropertyRelative("damage").floatValue;
                var muzzle = el.FindPropertyRelative("muzzle").objectReferenceValue;
                var muzzles = el.FindPropertyRelative("muzzles");
                var color = el.FindPropertyRelative("tracerColor").colorValue;
                if (fr <= 0f || dmg <= 0f) continue;
                combatMounts++;

                bool hasMuzzle = muzzle != null || (muzzles != null && muzzles.arraySize > 0);
                if (!hasMuzzle)
                    issues.Add($"wpn{i}: no muzzle (tracer/impact origin = transform +1m up)");
                if (color.a <= 0f)
                    issues.Add($"wpn{i}: tracerColor alpha 0 (invisible tracer!)");
            }
        }

        string status = issues.Count == 0 ? "OK" : "ISSUES";
        sb.AppendLine($"[{go.name}] {status} — combat mounts={combatMounts}");
        foreach (var i in issues)
            sb.AppendLine($"    - {i}");
    }
}
