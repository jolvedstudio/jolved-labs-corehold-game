using UnityEngine;
using UnityEngine.Serialization;

namespace Corehold.Data
{
    /// <summary>
    /// One group of identical enemies spawned within a wave (GDD §12.2, §8.1).
    /// </summary>
    [System.Serializable]
    public struct SpawnGroup
    {
        [Tooltip("The enemy type to spawn.")]
        public EnemyDefinition enemy;

        [Tooltip("Number of units to spawn.")]
        public int count;

        [Tooltip("Seconds between successive spawns in this group.")]
        public float spawnGap;

        [Tooltip("Seconds after the wave begins before this group starts spawning.")]
        public float startOffset;

        [Tooltip("Spawner: 0 = west ground entrance, 1 = north ground entrance, 2 = air corridor (GDD §12.2).")]
        public int spawnerIndex;
    }

    /// <summary>
    /// A single wave: who attacks, what it pays, and which mutators it can carry
    /// (GDD §12.2, §8.1).
    ///
    /// THE MUTATOR MODEL IS TWO LISTS, and that is the whole of it:
    ///
    ///   • <see cref="fixedMutators"/> — always on. Every run of this wave
    ///     carries all of them.
    ///   • <see cref="poolMutators"/> — the wave draws ONE of these (or nothing,
    ///     weighted by <see cref="poolNothingWeight"/>) fresh each run.
    ///
    /// A mutator in both lists is one mutator, not two. Everything else about a
    /// mutator — its words, its weather, its numbers — lives on the mutator
    /// asset, so a wave says only WHICH rules apply and how reliably.
    ///
    /// This replaced an enum of four hardcoded mutators that sat alongside the
    /// asset lists. Two authoring routes for one concept meant every consumer
    /// carried de-duplication logic — the HUD, the effect fold, the exporter and
    /// the weather all had to answer "is this asset standing in for that flag?"
    /// — and the wave asset showed a designer three mutator fields with no
    /// stated relationship. One route, two lists, no cross-checks.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Wave Definition", fileName = "Wave_")]
    public class WaveDefinition : ScriptableObject
    {
        [Header("Who attacks")]
        [Tooltip("The groups that make up this wave.")]
        public SpawnGroup[] groups;

        [Tooltip("Salvage awarded when the wave is cleared.")]
        public int clearBonus;

        // The three renames below carry FormerlySerializedAs so that every wave
        // asset already authored keeps its mutators through the rename. Without
        // it Unity would read a field that no longer exists, write a field that
        // was never set, and silently empty every mutator list in the project —
        // a data loss with no error and no diff worth reading.
        [Header("Mutators — always on")]
        [Tooltip("Mutators EVERY run of this wave carries. Use these to author a wave that is " +
                 "specifically the storm wave, or the blackout wave. Leave empty for a plain wave.\n\n" +
                 "The balance model prices these directly: they are part of what the wave IS.")]
        [FormerlySerializedAs("mutatorAssets")]
        public WaveMutatorDefinition[] fixedMutators;

        [Header("Mutators — draw one")]
        [Tooltip("The wave draws ONE of these each run, on top of anything fixed above. This is the " +
                 "variability lever: the same wave plays differently across runs and across retries, " +
                 "while staying inside a band the gate has certified.\n\n" +
                 "The balance model evaluates the wave once per pool member and gates on the WORST, " +
                 "so a run can never be harder than what was certified — which is what keeps a level " +
                 "learnable while its shape changes. Keep pools NARROW: pool width is the variance, " +
                 "and a wide pool means a level tuned for a worst case it rarely draws.")]
        [FormerlySerializedAs("mutatorPool")]
        public WaveMutatorDefinition[] poolMutators;

        [Tooltip("How many 'nothing drawn' slots sit in the pool's hat. 1 alongside a 3-member pool " +
                 "makes a plain wave a 1-in-4 outcome. 0 means the wave ALWAYS carries one of the " +
                 "pool's mutators.")]
        [FormerlySerializedAs("mutatorPoolNoneWeight")]
        [Min(0)] public int poolNothingWeight = 1;

        /// <summary>
        /// Every mutator this wave could POSSIBLY carry, fixed and pooled, with
        /// no duplicates. What the gate has to price and what a designer has to
        /// be able to see in one glance.
        /// </summary>
        public System.Collections.Generic.List<WaveMutatorDefinition> AllPossibleMutators()
        {
            var all = new System.Collections.Generic.List<WaveMutatorDefinition>(4);
            if (fixedMutators != null)
                foreach (WaveMutatorDefinition d in fixedMutators)
                    if (d != null && !all.Contains(d)) all.Add(d);
            if (poolMutators != null)
                foreach (WaveMutatorDefinition d in poolMutators)
                    if (d != null && !all.Contains(d)) all.Add(d);
            return all;
        }

        /// <summary>The pool's distinct, non-null members — what a draw actually
        /// picks between. Authoring slips (an empty element, the same mutator
        /// twice) would otherwise silently reweight the hat.</summary>
        public System.Collections.Generic.List<WaveMutatorDefinition> DrawablePool()
        {
            var pool = new System.Collections.Generic.List<WaveMutatorDefinition>(4);
            if (poolMutators != null)
                foreach (WaveMutatorDefinition d in poolMutators)
                    if (d != null && !pool.Contains(d)) pool.Add(d);
            return pool;
        }
    }
}
