using System.Collections.Generic;
using System.IO;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

namespace Corehold.EditorTools
{
    /// <summary>
    /// One-shot editor generator (Ticket 27). Creates the seven EnemyDefinition
    /// assets from GDD §6.1 (linking the existing enemy prefabs) and the ten
    /// WaveDefinition assets Wave_01..Wave_10 from GDD §8.1, assigning
    /// spawnerIndex per GDD §12.2.
    /// </summary>
    public static class CoreholdWaveDataGenerator
    {
        private const string EnemyDir = "Assets/_COREHOLD/Data/Enemies";
        private const string WaveDir = "Assets/_COREHOLD/Data/Waves";
        private const string LevelDir = "Assets/_COREHOLD/Data/Levels";
        private const string PrefabDir = "Assets/_COREHOLD/Prefabs/Enemies";

        /// <summary>
        /// Menu wrapper — this tool predates the menu convention and was only
        /// reachable through CoPlay, while every re-run instruction assumed a
        /// click.
        ///
        /// NO MENU ITEM any more — this is a GDD transcription, and the live
        /// assets have moved past the GDD: Wave_01 gained a hand-authored boss
        /// group, air groups were retuned, and Enemy_Colossus.asset was replaced
        /// by the A/B variants. Re-running would silently revert all of that
        /// (the same failure mode that de-menued Build Refinery Delta). Waves
        /// are authored by the WaveRecipe/WaveSynthesizer now; this class stays
        /// as the historical bootstrap, callable deliberately, never by click.
        /// </summary>
        public static void Run() =>
            Debug.Log("[COREHOLD] Wave data generator:\n" + Execute());

        public static string Execute()
        {
            var log = new System.Text.StringBuilder();

            EnsureFolder(EnemyDir);
            EnsureFolder(WaveDir);
            EnsureFolder(LevelDir);

            // ----- Enemy definitions (GDD §6.1) -----
            var scuttler = CreateEnemy("Enemy_Scuttler", "scuttler", "Scuttler", "Scuttler",
                hp: 45f, armour: ArmourType.Unarmoured, speed: 7.5f, bounty: 8, leak: 1, air: false, log: log);

            var strider = CreateEnemy("Enemy_Strider", "strider", "Strider", "Strider",
                hp: 110f, armour: ArmourType.Plated, speed: 5.0f, bounty: 12, leak: 1, air: false, log: log);

            var lancer = CreateEnemy("Enemy_Lancer", "lancer", "Lancer", "Lancer",
                hp: 190f, armour: ArmourType.Shielded, speed: 4.6f, bounty: 18, leak: 2, air: false, log: log);

            var wasp = CreateEnemy("Enemy_Wasp", "wasp", "Wasp", "Wasp",
                hp: 70f, armour: ArmourType.Unarmoured, speed: 9.0f, bounty: 14, leak: 2, air: true, log: log,
                flightAltitude: 4f);

            var roller = CreateEnemy("Enemy_Roller", "roller", "Roller", "Roller",
                hp: 150f, armour: ArmourType.Unarmoured, speed: 11.0f, bounty: 20, leak: 2, air: false, log: log,
                configure: def =>
                {
                    // Roller two-phase (GDD §6.2): sprints 60% then unpacks to 4.6 m/s.
                    def.hasSecondPhase = true;
                    def.phaseChangeAtPathFraction = 0.6f;
                    def.secondPhaseSpeed = 4.6f;
                });

            var breaker = CreateEnemy("Enemy_Breaker", "breaker", "Breaker", "Breaker",
                hp: 420f, armour: ArmourType.Plated, speed: 3.75f, bounty: 35, leak: 3, air: false, log: log);

            // ----- Roster expansion (approved 10-slot roster) -----
            // Prefabs are HUMAN-authored in Unity/CoPlay; rows link them when they
            // exist (same conditional pattern as the Colossus below). Neither is
            // used by the shipped waves yet — they are content for future tables.
            CreateEnemy("Enemy_Shrike", "shrike", "Shrike", "Shrike",
                hp: 55f, armour: ArmourType.Unarmoured, speed: 12.0f, bounty: 16, leak: 2, air: true, log: log,
                flightAltitude: 5f);

            // Warden: the first enemy-side support unit — its prefab must carry a
            // WardenAura component (damage-reduction bubble for nearby allies).
            CreateEnemy("Enemy_Warden", "warden", "Warden", "Warden",
                hp: 520f, armour: ArmourType.Plated, speed: 3.4f, bounty: 45, leak: 3, air: false, log: log);

            // Colossus prefab is conditional (GDD §4.5): may not exist yet. Link if
            // present, otherwise leave the prefab field null to be assigned later.
            var colossus = CreateEnemy("Enemy_Colossus", "colossus", "Colossus", "Colossus",
                hp: 2800f, armour: ArmourType.Shielded, speed: 3.0f, bounty: 250, leak: 20, air: false, log: log,
                configure: def =>
                {
                    // Colossus enrage (GDD §6.2): +40% speed below 50% health.
                    def.enrageAtHealthFraction = 0.5f;
                    def.enrageSpeedMultiplier = 1.4f;
                    // R18: the boss shrugs off a quarter of any stun's duration.
                    def.stunResistance = 0.25f;
                });

            // ----- Wave definitions (GDD §8.1, spawnerIndex per §12.2) -----
            // spawnerIndex: 0 = west ground, 1 = north ground, 2 = air.
            // Air groups always use 2. In a wave with 2+ ground groups the first
            // uses 0 and the second uses 1; single-group ground waves use 0.
            // Wave 10's Scuttlers use 1.

            var waves = new WaveDefinition[10];

            // Wave 1: 5× Scuttler @2.6
            waves[0] = CreateWave("Wave_01", clearBonus: 78, log,
                G(scuttler, 5, 2.6f, 0f, 0));

            // Wave 2: 8× Scuttler @2.2
            waves[1] = CreateWave("Wave_02", clearBonus: 96, log,
                G(scuttler, 8, 2.2f, 0f, 0));

            // Wave 3: 5× Strider @3.4 · 1× Wasp +12
            waves[2] = CreateWave("Wave_03", clearBonus: 114, log,
                G(strider, 5, 3.4f, 0f, 0),
                G(wasp, 1, 0f, 12f, 2));

            // Wave 4: 8× Scuttler @1.9 · 4× Strider @3.6 +5
            //   two ground groups: first (Scuttler) = 0, second (Strider) = 1.
            waves[3] = CreateWave("Wave_04", clearBonus: 132, log,
                G(scuttler, 8, 1.9f, 0f, 0),
                G(strider, 4, 3.6f, 5f, 1));

            // Wave 5: 8× Wasp @2.8
            waves[4] = CreateWave("Wave_05", clearBonus: 150, log,
                G(wasp, 8, 2.8f, 0f, 2));

            // Wave 6: 8× Strider @3.2 · 5× Scuttler @2.2 +4 · 4× Wasp @4.0 +10
            //   two ground groups: first (Strider) = 0, second (Scuttler) = 1; air = 2.
            waves[5] = CreateWave("Wave_06", clearBonus: 168, log,
                G(strider, 8, 3.2f, 0f, 0),
                G(scuttler, 5, 2.2f, 4f, 1),
                G(wasp, 4, 4.0f, 10f, 2));

            // Wave 7: 5× Lancer @4.4 · 3× Roller @6.0 +5
            //   two ground groups: first (Lancer) = 0, second (Roller) = 1.
            waves[6] = CreateWave("Wave_07", clearBonus: 186, log,
                G(lancer, 5, 4.4f, 0f, 0),
                G(roller, 3, 6.0f, 5f, 1));

            // Wave 8: 5× Lancer @4.6 · 10× Scuttler @2.2 +4 · 5× Wasp @3.4 +12
            //   two ground groups: first (Lancer) = 0, second (Scuttler) = 1; air = 2.
            waves[7] = CreateWave("Wave_08", clearBonus: 204, log,
                G(lancer, 5, 4.6f, 0f, 0),
                G(scuttler, 10, 2.2f, 4f, 1),
                G(wasp, 5, 3.4f, 12f, 2));

            // Wave 9: 3× Breaker @8.0 · 8× Strider @3.0 +5 · 5× Wasp @3.4 +10
            //   two ground groups: first (Breaker) = 0, second (Strider) = 1; air = 2.
            waves[8] = CreateWave("Wave_09", clearBonus: 222, log,
                G(breaker, 3, 8.0f, 0f, 0),
                G(strider, 8, 3.0f, 5f, 1),
                G(wasp, 5, 3.4f, 10f, 2));

            // Wave 10: 1× Colossus +4 · 8× Scuttler @2.4 · 4× Wasp @3.6 +14
            //   Colossus takes the west approach (0); Scuttlers use 1 so the boss
            //   has west to itself (GDD §12.2); air = 2.
            waves[9] = CreateWave("Wave_10", clearBonus: 240, log,
                G(colossus, 1, 0f, 4f, 0),
                G(scuttler, 8, 2.4f, 0f, 1),
                G(wasp, 4, 3.6f, 14f, 2));

            // ----- Level definition (Normal-tier base; GDD §8.2, §8.4, §12.2) -----
            CreateLevel("Level_RefineryDelta", waves, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return log.ToString();
        }

        private static SpawnGroup G(EnemyDefinition enemy, int count, float gap, float offset, int spawnerIndex)
        {
            return new SpawnGroup
            {
                enemy = enemy,
                count = count,
                spawnGap = gap,
                startOffset = offset,
                spawnerIndex = spawnerIndex
            };
        }

        private static EnemyDefinition CreateEnemy(
            string assetName, string id, string displayName, string prefabName,
            float hp, ArmourType armour, float speed, int bounty, int leak, bool air,
            System.Text.StringBuilder log,
            float flightAltitude = 0f,
            System.Action<EnemyDefinition> configure = null)
        {
            string path = $"{EnemyDir}/{assetName}.asset";
            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(path);
            bool created = false;
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<EnemyDefinition>();
                AssetDatabase.CreateAsset(def, path);
                created = true;
            }

            def.id = id;
            def.displayName = displayName;
            def.baseHealth = hp;
            def.armourType = armour;
            def.moveSpeed = speed;
            def.bounty = bounty;
            def.leakDamage = leak;
            def.isAir = air;
            def.flightAltitude = flightAltitude;
            // A sensible default clip-speed ref so foot-slide correction is stable
            // until measured on day three (GDD §6.3); equals moveSpeed by default.
            if (def.animatorClipSpeedRef <= 0.0001f)
                def.animatorClipSpeedRef = speed;

            // Reset special-behaviour fields, then let the configurator set them.
            def.hasSecondPhase = false;
            def.phaseChangeAtPathFraction = 0f;
            def.secondPhaseSpeed = 0f;
            def.enrageAtHealthFraction = 0f;
            def.enrageSpeedMultiplier = 0f;
            def.stunResistance = 0f;
            configure?.Invoke(def);

            // Link the prefab if one exists.
            string prefabPath = $"{PrefabDir}/{prefabName}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null)
                def.prefab = prefab;
            else
                log.AppendLine($"  ! {assetName}: prefab '{prefabPath}' not found (left {(def.prefab != null ? "existing" : "null")}).");

            EditorUtility.SetDirty(def);
            log.AppendLine($"  {(created ? "created" : "updated")} {assetName} (prefab: {(def.prefab != null ? def.prefab.name : "NONE")})");
            return def;
        }

        private static WaveDefinition CreateWave(string assetName, int clearBonus, System.Text.StringBuilder log, params SpawnGroup[] groups)
        {
            string path = $"{WaveDir}/{assetName}.asset";
            var wave = AssetDatabase.LoadAssetAtPath<WaveDefinition>(path);
            bool created = false;
            if (wave == null)
            {
                wave = ScriptableObject.CreateInstance<WaveDefinition>();
                AssetDatabase.CreateAsset(wave, path);
                created = true;
            }

            wave.groups = groups;
            wave.clearBonus = clearBonus;

            int units = 0;
            foreach (var g in groups)
                units += g.count;

            EditorUtility.SetDirty(wave);
            log.AppendLine($"  {(created ? "created" : "updated")} {assetName} ({groups.Length} groups, {units} units, clearBonus {clearBonus})");
            return wave;
        }

        private static void CreateLevel(string assetName, WaveDefinition[] waves, System.Text.StringBuilder log)
        {
            string path = $"{LevelDir}/{assetName}.asset";
            var level = AssetDatabase.LoadAssetAtPath<LevelDefinition>(path);
            bool created = false;
            if (level == null)
            {
                level = ScriptableObject.CreateInstance<LevelDefinition>();
                AssetDatabase.CreateAsset(level, path);
                created = true;
            }

            level.waves = waves;
            level.startingSalvage = 300;      // Normal (GDD §8.2)
            level.coreIntegrity = 20;         // Normal (GDD §3.3)
            level.hpGrowthPerWave = 0.18f;    // GDD §8.2
            level.chainBonusPerLiveEnemy = 8; // GDD §8.4
            level.chainBonusCap = 80;         // GDD §8.4
            level.maxLiveEnemies = 14;        // GDD §8.1

            EditorUtility.SetDirty(level);
            log.AppendLine($"  {(created ? "created" : "updated")} {assetName} ({waves.Length} waves)");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
