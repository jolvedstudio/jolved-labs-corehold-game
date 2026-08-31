using Corehold.Data;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// The rules waves are synthesized from (Part C): a roster plus an intensity
    /// curve, instead of hand-authored tables. The synthesizer composes each
    /// wave by spending a THREAT BUDGET on roster enemies — an enemy's cost is
    /// its bounty, the price the designer already put on it — under structure
    /// rules learned from the shipped tables (light openers, air held back,
    /// boss finale, staggered multi-group waves).
    ///
    /// Everything here is a knob, not a table: two campaigns sharing a roster
    /// but differing in curve produce different waves, and the same recipe +
    /// the same seed reproduces the same waves exactly (the determinism rule).
    /// Every synthesized set is re-certified by the balance model against the
    /// actual generated map (--waves), so variability never outruns the gates.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Wave Recipe (editor)", fileName = "Waves_")]
    public class WaveRecipe : ScriptableObject
    {
        [Header("Roster")]
        [Tooltip("The enemies waves may draw from. Every id must have a balance_model.py ENEMIES row " +
                 "(the Character Forge transcript prints missing rows); synthesis refuses otherwise — " +
                 "an unmodeled enemy would make the certification a lie.")]
        public EnemyDefinition[] roster;

        [Header("Shape")]
        [Tooltip("Waves per level.")]
        [Range(3, 20)] public int waveCount = 10;

        [Tooltip("Threat budget of wave 1, in bounty points. The shipped wave 1 is 5 scuttlers ≈ 40.")]
        public int budgetBase = 40;

        [Tooltip("Budget added per wave, as a fraction of budgetBase (0.55 ≈ the shipped ramp: " +
                 "wave 10 carries ~6x wave 1).")]
        [Range(0.1f, 1.5f)] public float budgetGrowthPerWave = 0.55f;

        [Tooltip("Per-STAGE escalation for campaigns: stage 2 gets (1+x), stage 3 (1+2x)… of every " +
                 "wave's budget. The carry verify still certifies each stage.")]
        [Range(0f, 0.5f)] public float escalationPerStage = 0.08f;

        [Header("Structure")]
        [Tooltip("First wave that may contain air units (the shipped tables hold air until wave 3).")]
        [Range(1, 10)] public int airFromWave = 3;

        [Tooltip("Reserve the final wave for a boss (heaviest roster enemy) plus a light escort, " +
                 "shipped-style. Needs a roster enemy at or above Boss Hp Min.")]
        public bool bossFinale = true;

        [Tooltip("An enemy at or above this HP counts as a boss (and never appears in normal waves).")]
        public float bossHpMin = 1500f;

        [Tooltip("Enemies at or below this HP count as LIGHT — wave 1-2 material, escorts, swarms.")]
        public float lightHpMax = 120f;

        [Header("Mutators")]
        [Tooltip("The mutator pool every eligible wave of this level gets. Leave empty for a level " +
                 "with no mutators at all.\n\n" +
                 "This is stamped onto the synthesized waves as their DRAW pool, not resolved here: " +
                 "the wave rolls at runtime, so the same level plays differently each run while the " +
                 "gate certifies the worst member. A pool member that could not matter on a given " +
                 "wave is dropped from that wave — an air-speed mutator on a wave with no air is a " +
                 "banner promising something that will not happen.")]
        public WaveMutatorDefinition[] mutatorPool;

        [Tooltip("From this wave on, waves carry the pool. Earlier waves are always plain — the " +
                 "opening waves are where a player learns the map, and a rule they cannot yet read " +
                 "is noise.")]
        [Range(2, 12)] public int mutatorsFromWave = 5;

        [Tooltip("How many 'nothing drawn' slots sit in each wave's hat. 2 alongside a 2-member pool " +
                 "makes a plain wave the even-money outcome. 0 means every eligible wave carries one.")]
        [Min(0)] public int poolNothingWeight = 2;
    }
}
