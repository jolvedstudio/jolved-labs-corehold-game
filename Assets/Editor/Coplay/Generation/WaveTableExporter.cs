using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Data;
using Corehold.Enemies;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports a level's ACTUAL wave tables + enemy stats as the balance model's
/// <c>--waves</c> JSON, so certification always describes the level being
/// built — never a static copy of it.
///
/// Why this exists: the model's embedded ENEMIES/WAVES literals are hand
/// mirrors, and hand mirrors drift — by the time this was written the live
/// Shrike had become a plated ground unit, the Wasp had been grounded, and
/// the shipped Wave_01 fielded Breakers while the model still certified
/// Scuttlers. Reading the WaveDefinition and EnemyDefinition assets the
/// LevelDefinition actually references (plus each enemy prefab's authored
/// EnemyWeapon mounts, for the tower-loss term) closes that gap at the only
/// place it can stay closed: every model run. Add a boss to wave 1 or triple
/// an enemy's health and the next verify/generate says so immediately.
///
/// The file is a temp file; the CALLER deletes it after the model run.
/// </summary>
public static class WaveTableExporter
{
    /// <summary>
    /// Write the model's waves JSON for <paramref name="level"/>'s wave assets.
    /// Returns the temp-file path, or null with an actionable
    /// <paramref name="error"/> (a null enemy slot, a missing id, two
    /// definitions sharing one id — the things that would make the
    /// certification a lie).
    /// </summary>
    public static string Export(LevelDefinition level, out string error)
    {
        error = null;
        if (level == null || level.waves == null || level.waves.Length == 0)
        {
            error = "the LevelDefinition has no waves to certify";
            return null;
        }

        // ---- collect every referenced enemy, refusing ambiguity ------------
        var byId = new Dictionary<string, EnemyDefinition>();
        for (int w = 0; w < level.waves.Length; w++)
        {
            WaveDefinition wave = level.waves[w];
            if (wave == null)
            {
                error = $"wave {w + 1} slot is empty on '{level.name}'";
                return null;
            }
            if (wave.groups == null || wave.groups.Length == 0)
            {
                error = $"'{wave.name}' (wave {w + 1}) has no spawn groups";
                return null;
            }
            for (int g = 0; g < wave.groups.Length; g++)
            {
                EnemyDefinition e = wave.groups[g].enemy;
                if (e == null)
                {
                    error = $"'{wave.name}' group {g + 1} references no enemy";
                    return null;
                }
                if (string.IsNullOrEmpty(e.id))
                {
                    error = $"enemy definition '{e.name}' has an empty id";
                    return null;
                }
                if (byId.TryGetValue(e.id, out EnemyDefinition seen) && seen != e)
                {
                    error = $"two enemy definitions share id '{e.id}': " +
                            $"'{seen.name}' and '{e.name}' — certification would be ambiguous";
                    return null;
                }
                byId[e.id] = e;
            }
        }

        var inv = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.####", inv);

        // ---- enemies block: the stats the assets actually carry -------------
        var sb = new StringBuilder();
        sb.Append("{\"enemies\":{");
        bool firstE = true;
        foreach (var kv in byId)
        {
            EnemyDefinition e = kv.Value;
            if (!firstE) sb.Append(',');
            firstE = false;
            sb.Append($"\"{kv.Key}\":{{\"hp\":{F(e.baseHealth)},\"armour\":{(int)e.armourType}," +
                      $"\"speed\":{F(e.moveSpeed)},\"bounty\":{e.bounty},\"leak\":{e.leakDamage}," +
                      $"\"air\":{(e.isAir ? "true" : "false")}");
            if (e.isAir)
                sb.Append($",\"altitude\":{F(e.flightAltitude)}");
            if (e.hasSecondPhase && e.secondPhaseSpeed > 0f)
                sb.Append($",\"phase_at\":{F(e.phaseChangeAtPathFraction)}," +
                          $"\"phase_speed\":{F(e.secondPhaseSpeed)}");
            if (e.enrageAtHealthFraction > 0f && e.enrageSpeedMultiplier > 0f)
                sb.Append($",\"enrage_mult\":{F(e.enrageSpeedMultiplier)}");
            ReadGuns(e, out float gdps, out float grange);
            if (gdps > 0f && grange > 0f)
                sb.Append($",\"gdps\":{F(gdps)},\"grange\":{F(grange)}");
            sb.Append('}');
        }
        sb.Append("},\"towers\":{");

        // ---- towers block: the DEFENDER side of live certification ----------
        // Every TowerDefinition in the project, read straight off the assets
        // (per-tier dps from the authored mounts, ranges, costs, auras, chassis
        // HP) — so a turret buff moves the verdict the same run it is
        // authored, exactly like an enemy edit does. Extra ids the sim never
        // places are harmless rows.
        //
        // UTILITY towers are skipped, not exported: the Cryo Node, Floodlight
        // and Salvage Rig legitimately author range 0 (their reach is an aura
        // or a light, not a gun), which the model's combat schema cannot
        // express — and a row it cannot express must not brick certification
        // of a map that never fields it. Skips are recorded in the JSON as
        // towers_skipped so a debugging eye can see the decision.
        string towersError = null;
        var towersSkipped = new List<string>();
        bool firstT = true;
        foreach (TowerDefinition tower in AllTowerDefinitions(ref towersError))
        {
            if (tower.tiers.Any(t => t.cost <= 0 || t.range <= 0f))
            {
                towersSkipped.Add(tower.id);
                continue;
            }
            if (!firstT) sb.Append(',');
            firstT = false;
            sb.Append($"\"{tower.id}\":{{\"type\":{(int)tower.damageType}," +
                      $"\"air\":{(tower.canTargetAir ? "true" : "false")}");
            if (tower.targetAirOnly)
                sb.Append(",\"air_only\":true");
            sb.Append($",\"hp\":{F(TowerHp(tower))},\"tiers\":[");
            for (int t = 0; t < tower.tiers.Length; t++)
            {
                TowerTier tier = tower.tiers[t];
                if (t > 0) sb.Append(',');
                sb.Append($"{{\"cost\":{tier.cost},\"range\":{F(tier.range)}," +
                          $"\"min_range\":{F(tier.minRange)},\"dps\":{F(tier.TotalDps)}");
                // Authored shield barrier (absorbs before hp; the model's
                // tower-loss term mirrors TowerHealth's rules).
                if (tier.shieldHitPoints > 0f)
                    sb.Append($",\"shield\":{F(tier.shieldHitPoints)}," +
                              $"\"regen\":{F(tier.shieldRegenPerSec)}," +
                              $"\"delay\":{F(tier.shieldRegenDelay)}");
                int chain = 0;
                float falloff = 0f, splash = 0f;
                foreach (TowerWeaponMount m in tier.Weapons)
                {
                    if (m.chainTargets > chain) { chain = m.chainTargets; falloff = m.chainFalloff; }
                    splash = Mathf.Max(splash, m.splashRadius);
                }
                if (chain > 0)
                    sb.Append($",\"chain\":{chain},\"falloff\":{F(falloff)}");
                if (splash > 0f)
                    sb.Append($",\"splash\":{F(splash)}");
                if (tier.auraRadius > 0f)
                    sb.Append($",\"aura_radius\":{F(tier.auraRadius)}," +
                              $"\"aura_fire\":{F(tier.auraFireRateBonus)}," +
                              $"\"aura_range\":{F(tier.auraRangeBonus)}," +
                              $"\"aura_dmg\":{F(tier.auraDamageBonus)}");
                sb.Append('}');
            }
            sb.Append("]}");
        }
        if (towersError != null)
        {
            error = towersError;
            return null;
        }
        sb.Append('}');
        if (towersSkipped.Count > 0)
            sb.Append(",\"towers_skipped\":[" +
                      string.Join(",", towersSkipped.Select(t => $"\"{t}\"")) + "]");

        sb.Append(",\"waves\":[");

        // ---- waves: exactly what WaveManager will run -----------------------
        for (int w = 0; w < level.waves.Length; w++)
        {
            WaveDefinition wave = level.waves[w];
            if (w > 0) sb.Append(',');
            sb.Append($"{{\"clear\":{wave.clearBonus}");
            string muts = MutatorNames(wave);
            if (muts.Length > 0)
                sb.Append($",\"mutators\":[{muts}]");
            sb.Append(",\"groups\":[");
            for (int g = 0; g < wave.groups.Length; g++)
            {
                SpawnGroup grp = wave.groups[g];
                if (g > 0) sb.Append(',');
                sb.Append($"{{\"enemy\":\"{grp.enemy.id}\",\"count\":{grp.count}," +
                          $"\"gap\":{F(grp.spawnGap)},\"offset\":{F(grp.startOffset)}," +
                          $"\"spawner\":{grp.spawnerIndex}}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");

        string path = Path.Combine(Path.GetTempPath(), $"corehold_livewaves_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// Sum the enemy prefab's authored EnemyWeapon mounts into the model's
    /// return-fire pair: gdps = Σ damage × fireRate over active mounts,
    /// grange = the longest active mount. Mirrors the runtime rules — a mount
    /// with non-positive damage or fire rate never fires (TickWeapon), and an
    /// empty array falls back to the legacy single-weapon fields
    /// (MigrateLegacy). No prefab or no weapon = unarmed.
    /// </summary>
    private static void ReadGuns(EnemyDefinition def, out float gdps, out float grange)
    {
        gdps = 0f;
        grange = 0f;
        if (def.prefab == null)
            return;
        var weapon = def.prefab.GetComponentInChildren<EnemyWeapon>(true);
        if (weapon == null)
            return;

        var so = new SerializedObject(weapon);
        SerializedProperty mounts = so.FindProperty("weapons");
        if (mounts != null && mounts.isArray && mounts.arraySize > 0)
        {
            for (int i = 0; i < mounts.arraySize; i++)
            {
                SerializedProperty m = mounts.GetArrayElementAtIndex(i);
                float damage = m.FindPropertyRelative("damage").floatValue;
                float fireRate = m.FindPropertyRelative("fireRate").floatValue;
                float range = m.FindPropertyRelative("range").floatValue;
                if (damage <= 0f || fireRate <= 0f)
                    continue;
                gdps += damage * fireRate;
                grange = Mathf.Max(grange, range);
            }
            return;
        }

        float lDamage = so.FindProperty("damage").floatValue;
        float lRate = so.FindProperty("fireRate").floatValue;
        float lRange = so.FindProperty("range").floatValue;
        if (lDamage > 0f && lRate > 0f)
        {
            gdps = lDamage * lRate;
            grange = lRange;
        }
    }

    /// <summary>
    /// Every usable TowerDefinition in the project, deterministic order,
    /// refusing id ambiguity the same way enemies do. Definitions with no id
    /// or no tiers are skipped — the sim cannot place them anyway.
    /// </summary>
    private static IEnumerable<TowerDefinition> AllTowerDefinitions(ref string error)
    {
        var byId = new Dictionary<string, TowerDefinition>();
        var ordered = new List<TowerDefinition>();
        foreach (string guid in AssetDatabase.FindAssets("t:TowerDefinition")
                     .OrderBy(g => AssetDatabase.GUIDToAssetPath(g), System.StringComparer.Ordinal))
        {
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (def == null || string.IsNullOrEmpty(def.id) ||
                def.tiers == null || def.tiers.Length == 0)
                continue;
            if (byId.TryGetValue(def.id, out TowerDefinition seen) && seen != def)
            {
                error = $"two tower definitions share id '{def.id}': '{seen.name}' and " +
                        $"'{def.name}' — certification would be ambiguous";
                return System.Linq.Enumerable.Empty<TowerDefinition>();
            }
            if (!byId.ContainsKey(def.id))
            {
                byId[def.id] = def;
                ordered.Add(def);
            }
        }
        return ordered;
    }

    /// <summary>Chassis HP: the prefab's authored TowerHealth, or the runtime
    /// default Tower.Build falls back to when none is authored.</summary>
    private static float TowerHp(TowerDefinition def)
    {
        if (def.basePrefab != null)
        {
            var health = def.basePrefab.GetComponentInChildren<Corehold.Towers.TowerHealth>(true);
            if (health != null && health.MaxHealth > 0f)
                return health.MaxHealth;
        }
        return 220f;
    }

    /// <summary>
    /// The wave's mutators, as the balance model reads them.
    ///
    /// TWO SHAPES, on purpose. A legacy flag emits its BARE NAME exactly as it
    /// always has — the model knows those four and their constants, and keeping
    /// the form identical is what lets every table exported before R33 still
    /// parse and still produce the same numbers. An authored mutator emits an
    /// OBJECT carrying its own values, because the model cannot have a constant
    /// for an asset that did not exist when it was written; the numbers travel
    /// with the wave instead.
    ///
    /// Only non-default terms are written. A mutator that changes one thing
    /// should read as one line in the table, not as seven, or nobody will read
    /// the table.
    /// </summary>
    private static string MutatorNames(WaveDefinition wave)
    {
        var parts = new List<string>(6);

        WaveMutator flags = wave.mutators;
        if ((flags & WaveMutator.Storm) != 0) parts.Add("\"storm\"");
        if ((flags & WaveMutator.Convoy) != 0) parts.Add("\"convoy\"");
        if ((flags & WaveMutator.Overcharge) != 0) parts.Add("\"overcharge\"");
        if ((flags & WaveMutator.Blackout) != 0) parts.Add("\"blackout\"");

        if (wave.mutatorAssets != null)
        {
            var seen = new HashSet<string>();
            foreach (WaveMutatorDefinition d in wave.mutatorAssets)
            {
                // A bound asset whose flag is already ticked is the same
                // mutator said twice; the flag has already spoken for it.
                if (d == null || (d.legacyFlag != WaveMutator.None && (flags & d.legacyFlag) != 0))
                    continue;
                if (!seen.Add(d.ResolvedId))
                    continue;

                var fields = new List<string>(8) { $"\"id\":\"{Escape(d.ResolvedId)}\"" };
                AddTerm(fields, "air_speed", d.airSpeedMultiplier);
                AddTerm(fields, "ground_speed", d.groundSpeedMultiplier);
                AddTerm(fields, "hp", d.healthMultiplier);
                AddTerm(fields, "bounty", d.bountyMultiplier);
                AddTerm(fields, "range", d.turretRangeMultiplier);
                AddTerm(fields, "gap", d.spawnGapMultiplier);
                if (d.singleApproach)
                    fields.Add("\"convoy\":true");
                parts.Add("{" + string.Join(",", fields) + "}");
            }
        }

        return string.Join(",", parts);
    }

    /// <summary>Same format as the exporter's local F: invariant culture, four
    /// decimals, so a mutator term reads identically to every other number in
    /// the table and cannot arrive comma-separated on a European machine.</summary>
    private static string FS(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static void AddTerm(List<string> fields, string key, float value)
    {
        if (!Mathf.Approximately(value, 1f))
            fields.Add($"\"{key}\":{FS(value)}");
    }

    private static string Escape(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
