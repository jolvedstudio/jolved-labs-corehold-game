using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// Wave synthesis (Part C): turns a <see cref="WaveRecipe"/> into per-stage
    /// <see cref="WaveDefinition"/> assets. Certification happens downstream:
    /// WaveTableExporter reads the created assets (waves AND enemy stats,
    /// guns included) into the model's --waves JSON, so there is exactly one
    /// producer of model input and no roster-vs-embedded-table refusal — a
    /// forge-built enemy certifies from its own asset like everything else.
    ///
    /// Deterministic by doctrine: every draw derives from
    /// Fnv1a(seed, "waves") → xorshift, so a stage's accepted seed reproduces
    /// its exact waves. Structure mirrors what made the shipped tables work —
    /// light single-group openers, air held back then escorted in, multi-group
    /// waves staggered across spawners, a boss finale with a light escort —
    /// while the SPENDING inside that structure is where the variability lives.
    /// </summary>
    public static class WaveSynthesizer
    {
        public class Result
        {
            public WaveDefinition[] waves;
            public string transcript;
        }

        // ---------------------------------------------------------- synthesize

        /// <summary>
        /// Synthesize one stage's waves into <paramref name="wavesFolder"/>.
        /// <paramref name="groundSpawners"/> is the generated map's ground route
        /// count (air is always spawner 2, the project-wide convention).
        /// </summary>
        public static Result Synthesize(WaveRecipe recipe, int stageIndex, int seed,
                                        int groundSpawners, string wavesFolder)
        {
            var log = new StringBuilder();
            var roster = (recipe.roster ?? new EnemyDefinition[0]).Where(e => e != null).ToList();
            if (roster.Count == 0)
            {
                log.AppendLine("Wave synthesis: the recipe has no roster.");
                return new Result { transcript = log.ToString() };
            }

            // ---- classify the roster (an enemy is priced by its bounty) ----
            var bosses = roster.Where(e => e.baseHealth >= recipe.bossHpMin && !e.isAir).ToList();
            var light = roster.Where(e => e.baseHealth <= recipe.lightHpMax && !e.isAir).ToList();
            var heavy = roster.Where(e => !e.isAir && e.baseHealth > recipe.lightHpMax &&
                                          e.baseHealth < recipe.bossHpMin).ToList();
            var air = roster.Where(e => e.isAir).ToList();
            if (light.Count == 0)
            {
                // Openers need SOMETHING cheap; the cheapest ground unit stands in.
                var cheapest = roster.Where(e => !e.isAir).OrderBy(e => e.bounty).FirstOrDefault();
                if (cheapest == null)
                {
                    log.AppendLine("Wave synthesis: the roster has no ground units at all.");
                    return new Result { transcript = log.ToString() };
                }
                light.Add(cheapest);
            }

            uint rng = GenerationPipeline.Fnv1a(seed, "waves");
            float escalation = 1f + recipe.escalationPerStage * Mathf.Max(0, stageIndex);
            int ground = Mathf.Max(1, groundSpawners);

            var defs = new List<WaveDefinition>();

            for (int w = 1; w <= recipe.waveCount; w++)
            {
                float budget = recipe.budgetBase * (1f + recipe.budgetGrowthPerWave * (w - 1)) * escalation;
                bool finale = recipe.bossFinale && w == recipe.waveCount && bosses.Count > 0;
                bool airAllowed = air.Count > 0 && w >= recipe.airFromWave;

                var groups = new List<SpawnGroup>();
                if (finale)
                {
                    var boss = bosses[Next(ref rng, bosses.Count)];
                    groups.Add(Group(boss, 1, 0f, 4f, 0));
                    budget = Mathf.Max(0, budget - boss.bounty);
                    // Escort: light pressure on the other lanes while the boss walks.
                    var escort = light[Next(ref rng, light.Count)];
                    int escortCount = Mathf.Clamp(Mathf.RoundToInt(budget * 0.6f / Mathf.Max(1, escort.bounty)), 3, 14);
                    groups.Add(Group(escort, escortCount, Gap(ref rng, 2.0f, 2.8f), 0f, ground > 1 ? 1 : 0));
                    if (airAllowed)
                    {
                        var flier = air[Next(ref rng, air.Count)];
                        int n = Mathf.Clamp(Mathf.RoundToInt(budget * 0.4f / Mathf.Max(1, flier.bounty)), 2, 8);
                        groups.Add(Group(flier, n, Gap(ref rng, 3.2f, 4.2f), 12f, 2));
                    }
                }
                else
                {
                    // Composition template: openers are one light group; from
                    // wave 3, two to three groups mixing classes, staggered.
                    int groupCount = w <= 2 ? 1 : 2 + Next(ref rng, Mathf.Min(2, ground));
                    var classes = PickClasses(ref rng, groupCount, w, airAllowed, light, heavy, air);

                    float[] shares = Shares(ref rng, classes.Count);
                    for (int gi = 0; gi < classes.Count; gi++)
                    {
                        var pool = classes[gi];
                        var pick = pool[Next(ref rng, pool.Count)];
                        int count = Mathf.Clamp(
                            Mathf.RoundToInt(budget * shares[gi] / Mathf.Max(1, pick.bounty)),
                            1, pick.isAir ? 8 : 14);
                        float gap = pick.isAir ? Gap(ref rng, 2.8f, 4.0f)
                                  : pick.baseHealth > recipe.lightHpMax ? Gap(ref rng, 3.0f, 4.4f)
                                  : Gap(ref rng, 1.8f, 2.6f);
                        float offset = gi == 0 ? 0f : 4f + 5f * (gi - 1) + Next(ref rng, 3);
                        int spawner = pick.isAir ? 2 : Next(ref rng, ground);
                        groups.Add(Group(pick, count, gap, offset, spawner));
                    }
                }

                // ---- the mutator pool (never on the finale — the boss IS the event) ----
                //
                // The pool is STAMPED, not resolved: the wave draws from it at
                // runtime, so the same synthesized level plays differently every
                // run, and the gate certifies its worst member rather than one
                // roll that happened at authoring time.
                bool hasAir = groups.Any(g => g.enemy.isAir);
                var wavePool = new List<WaveMutatorDefinition>();
                if (!finale && w >= recipe.mutatorsFromWave && recipe.mutatorPool != null)
                    foreach (WaveMutatorDefinition d in recipe.mutatorPool)
                    {
                        if (d == null || wavePool.Contains(d))
                            continue;
                        // A mutator that can only touch air is a promise this
                        // wave cannot keep: banner, weather, and nothing behind
                        // either of them.
                        if (!hasAir && OnlyAffectsAir(d))
                            continue;
                        wavePool.Add(d);
                    }

                int clear = 60 + 18 * w; // the shipped clear-bonus formula

                // ---- the runtime asset ----
                var def = ScriptableObject.CreateInstance<WaveDefinition>();
                def.name = $"Wave_{w:00}";
                def.groups = groups.ToArray();
                def.clearBonus = clear;
                def.poolMutators = wavePool.ToArray();
                def.poolNothingWeight = recipe.poolNothingWeight;
                AssetDatabase.CreateAsset(def, $"{wavesFolder}/Wave_{w:00}.asset");
                defs.Add(def);

                log.AppendLine($"  w{w,-2} budget {Mathf.RoundToInt(budget),4}  " +
                               string.Join(" + ", groups.Select(g => $"{g.count}×{g.enemy.id}@s{g.spawnerIndex}")) +
                               (wavePool.Count > 0
                                   ? $"  [pool: {string.Join("/", wavePool.Select(m => m.ResolvedId))}" +
                                     $" +{recipe.poolNothingWeight} none]"
                                   : ""));
            }

            AssetDatabase.SaveAssets();

            return new Result { waves = defs.ToArray(), transcript = log.ToString() };
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// True when a mutator's ONLY effect is on air units, so it would do
        /// nothing at all on a wave with no air in it.
        ///
        /// Read off the effects rather than off a name: a mutator authored next
        /// year gets the same treatment as Storm without anyone remembering to
        /// add it to a list here.
        /// </summary>
        private static bool OnlyAffectsAir(WaveMutatorDefinition d)
        {
            MutatorEffects e = d.Effects;
            if (Mathf.Approximately(e.airSpeed, 1f))
                return false;   // it does not touch air at all, so this is not the reason to drop it

            return !e.singleApproach &&
                   Mathf.Approximately(e.groundSpeed, 1f) &&
                   Mathf.Approximately(e.health, 1f) &&
                   Mathf.Approximately(e.bounty, 1f) &&
                   Mathf.Approximately(e.turretRange, 1f) &&
                   Mathf.Approximately(e.spawnGap, 1f);
        }

        private static SpawnGroup Group(EnemyDefinition e, int count, float gap, float offset, int spawner)
            => new SpawnGroup { enemy = e, count = count, spawnGap = gap, startOffset = offset, spawnerIndex = spawner };

        /// <summary>Class pools for a wave's groups: always leads light-ish, mixes
        /// heavy from wave 3, air last so its offset staggers in after ground.</summary>
        private static List<List<EnemyDefinition>> PickClasses(ref uint rng, int groupCount, int wave,
            bool airAllowed, List<EnemyDefinition> light, List<EnemyDefinition> heavy, List<EnemyDefinition> air)
        {
            var result = new List<List<EnemyDefinition>> { light };
            while (result.Count < groupCount)
            {
                bool wantAir = airAllowed && !result.Contains(air) && Next(ref rng, 3) == 0;
                if (wantAir) result.Add(air);
                else if (heavy.Count > 0 && wave >= 3 && Next(ref rng, 2) == 0) result.Add(heavy);
                else result.Add(light);
            }
            // Air group (if any) goes last so its start offset is the largest.
            return result.OrderBy(pool => pool == air ? 1 : 0).ToList();
        }

        /// <summary>Random budget shares summing to 1, none below 20%.</summary>
        private static float[] Shares(ref uint rng, int n)
        {
            var shares = new float[n];
            float sum = 0f;
            for (int i = 0; i < n; i++) { shares[i] = 0.2f + NextFloat(ref rng) * 0.8f; sum += shares[i]; }
            for (int i = 0; i < n; i++) shares[i] /= sum;
            return shares;
        }

        private static float Gap(ref uint rng, float min, float max) => min + NextFloat(ref rng) * (max - min);

        // xorshift32 — the project's draw primitive (same as the pipeline's).
        private static uint Step(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        private static int Next(ref uint s, int n) => n <= 0 ? 0 : (int)(Step(ref s) % (uint)n);
        private static float NextFloat(ref uint s) => (Step(ref s) & 0xFFFFFF) / (float)0x1000000;
    }
}
