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
    /// THE MUTATOR MODEL IS ONE LIST. The wave draws ONE member of
    /// <see cref="poolMutators"/> each run, or nothing, weighted by
    /// <see cref="poolNothingWeight"/>. That is the whole of it.
    ///
    /// "Always carries X" is not a second concept — it is a pool of one with a
    /// nothing-weight of zero. Spelling it that way costs a designer nothing and
    /// saves the asset from showing two mutator lists whose relationship has to
    /// be explained.
    ///
    /// The one shape this cannot express is a GUARANTEED rule stacked under a
    /// DRAWN one on the same wave. That is deliberate: a wave that is always
    /// dark and sometimes stormy is better authored as one mutator asset that
    /// does both, since the model prices the composed vector either way.
    ///
    /// Everything else about a mutator — its words, its weather, its numbers —
    /// lives on the mutator asset, so a wave says only WHICH rules it can carry
    /// and how often.
    ///
    /// This replaced an enum of four hardcoded mutators sitting alongside two
    /// asset lists. Three authoring routes for one concept meant every consumer
    /// carried de-duplication logic — the HUD, the effect fold, the exporter and
    /// the weather all had to answer "is this asset standing in for that flag?"
    /// — and the asset showed a designer four mutator fields with no stated
    /// relationship between any of them.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Wave Definition", fileName = "Wave_")]
    public class WaveDefinition : ScriptableObject
    {
        [Header("Who attacks")]
        [Tooltip("The groups that make up this wave.")]
        public SpawnGroup[] groups;

        [Tooltip("Salvage awarded when the wave is cleared.")]
        public int clearBonus;

        // The renames below carry FormerlySerializedAs so a wave already
        // authored against the old names keeps its pool. Without it Unity would
        // read a field that no longer exists, write one that was never set, and
        // silently empty every pool in the project — a data loss with no error
        // and no diff worth reading.
        [Header("Mutators — the wave draws one")]
        [Tooltip("The mutators this wave may carry. It draws ONE of them each run, or nothing.\n\n" +
                 "This is the variability lever: the same wave plays differently across runs and " +
                 "across retries, while staying inside a band the gate has certified. For a wave " +
                 "that ALWAYS carries one rule, put that one mutator here and set the nothing-weight " +
                 "below to 0.\n\n" +
                 "The balance model evaluates the wave once per member and gates on the WORST, so a " +
                 "run can never be harder than what was certified — which is what keeps a level " +
                 "learnable while its shape changes. Keep pools NARROW: pool width is the variance, " +
                 "and a wide pool means a level tuned for a worst case it rarely draws.")]
        [FormerlySerializedAs("mutatorPool")]
        public WaveMutatorDefinition[] poolMutators;

        [Tooltip("How many 'nothing drawn' slots sit in the pool's hat. 1 alongside a 3-member pool " +
                 "makes a plain wave a 1-in-4 outcome. 0 means the wave ALWAYS carries one of the " +
                 "pool's mutators — with a single-member pool, that is how you author 'this wave is " +
                 "the storm wave'.")]
        [FormerlySerializedAs("mutatorPoolNoneWeight")]
        [Min(0)] public int poolNothingWeight = 1;

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
