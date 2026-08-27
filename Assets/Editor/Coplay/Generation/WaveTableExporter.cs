using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        sb.Append("},\"waves\":[");

        // ---- waves: exactly what WaveManager will run -----------------------
        for (int w = 0; w < level.waves.Length; w++)
        {
            WaveDefinition wave = level.waves[w];
            if (w > 0) sb.Append(',');
            sb.Append($"{{\"clear\":{wave.clearBonus}");
            string muts = MutatorNames(wave.mutators);
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

    private static string MutatorNames(WaveMutator flags)
    {
        if (flags == WaveMutator.None)
            return "";
        var names = new List<string>(4);
        if ((flags & WaveMutator.Storm) != 0) names.Add("\"storm\"");
        if ((flags & WaveMutator.Convoy) != 0) names.Add("\"convoy\"");
        if ((flags & WaveMutator.Overcharge) != 0) names.Add("\"overcharge\"");
        if ((flags & WaveMutator.Blackout) != 0) names.Add("\"blackout\"");
        return string.Join(",", names);
    }
}
