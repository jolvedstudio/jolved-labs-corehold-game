using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// The composed effect of every mutator in force on one wave.
    ///
    /// Multipliers compose by MULTIPLICATION and switches by OR, which is the
    /// only rule that makes "any two mutators can share a wave" true without a
    /// table of special cases: order stops mattering, identity is 1, and two
    /// mutators that both slow the ground compound instead of one silently
    /// winning. It is also exactly what the balance model does with the same
    /// numbers, which is what keeps the two implementations from drifting.
    /// </summary>
    public struct MutatorEffects
    {
        public float airSpeed;
        public float groundSpeed;
        public float health;
        public float bounty;
        public float turretRange;
        public float spawnGap;
        public bool singleApproach;

        /// <summary>The no-mutator wave: every multiplier 1, every switch off.</summary>
        public static MutatorEffects Identity => new MutatorEffects
        {
            airSpeed = 1f,
            groundSpeed = 1f,
            health = 1f,
            bounty = 1f,
            turretRange = 1f,
            spawnGap = 1f,
            singleApproach = false,
        };

        public void Fold(MutatorEffects other)
        {
            airSpeed *= other.airSpeed;
            groundSpeed *= other.groundSpeed;
            health *= other.health;
            bounty *= other.bounty;
            turretRange *= other.turretRange;
            spawnGap *= other.spawnGap;
            singleApproach |= other.singleApproach;
        }

        /// <summary>True when this wave is mechanically a plain wave. Lets the
        /// spawn path skip the whole mutator lane on the common case.</summary>
        public bool IsIdentity =>
            !singleApproach &&
            Mathf.Approximately(airSpeed, 1f) && Mathf.Approximately(groundSpeed, 1f) &&
            Mathf.Approximately(health, 1f) && Mathf.Approximately(bounty, 1f) &&
            Mathf.Approximately(turretRange, 1f) && Mathf.Approximately(spawnGap, 1f);
    }

    /// <summary>
    /// One authored wave mutator: a named rule a wave can carry, with its
    /// player-facing words, its weather, and its effect on play.
    ///
    /// THE ONE PLACE A MUTATOR IS DEFINED. A wave names which mutators it can
    /// carry; everything about what one IS lives here. That was not always
    /// true — the original four were enum flags whose numbers sat on the
    /// WaveManager, whose weather sat on the WeatherApplier, and whose banner
    /// words sat in a switch in the HUD, so "what does Storm do?" had four
    /// answers in four files and adding a fifth mutator meant editing all of
    /// them. Now it is one asset, and a new mutator is a new asset.
    ///
    /// WHY AN ASSET AND NOT MORE CODE:
    ///
    ///   • The effect list below is CLOSED. Each field maps 1:1 onto a term the
    ///     balance model already computes, so a mutator built here is a mutator
    ///     the model can see. Inventing a NEW KIND of effect is still a code
    ///     change plus a model term plus a gate run — deliberately, because a
    ///     free-form effect field would let a designer author something the
    ///     model cannot price, and the gate would be certifying a fiction.
    ///   • The MAGNITUDES are yours. The exporter writes each mutator's actual
    ///     numbers into the wave table and the model composes them generically,
    ///     so a mutator tuned here is priced with the value you typed, not with
    ///     a constant compiled into the model.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Wave Mutator", fileName = "Mutator_")]
    public class WaveMutatorDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable lowercase id. This is what the exporter writes into the wave table and what " +
                 "the balance model reports against, so RENAMING IT INVALIDATES saved tables and gate " +
                 "runs. Keep it short and mechanical: 'storm', 'hailfall', 'surge'.")]
        public string id = "new_mutator";

        [Tooltip("The banner title, in PLAIN WORDS — this is what the player reads at wave start. " +
                 "The mutator's own name, not an in-fiction one: the narrative bible's names belong " +
                 "in briefing prose, where there is room to explain them.")]
        public string title = "NEW RULE";

        [Tooltip("One line under the title saying what it DOES, in the words a player would use. " +
                 "'Air units move faster', not 'airSpeed x1.3'.")]
        public string clause = "Something is different about this wave";

        [Header("Presentation")]
        [Tooltip("Weather layer stacked over the level's preset while a wave carrying this mutator " +
                 "runs, and removed when it clears. Presentation only — nothing here changes play. " +
                 "Leave empty and the wave looks like whatever the level already looks like.")]
        public WeatherPreset weatherLayer;

        // ------------------------------------------------------------ effects
        //
        // CLOSED LIST. Every field below is a term the balance model computes.
        // Adding a field here without adding the matching term to the model is
        // how the gate starts lying, so the two move together or not at all.

        [Header("Effects — speed")]
        [Tooltip("Multiplier on the speed of AIR units this wave. Above 1 is faster: they cross " +
                 "sooner AND spend less time under every tower covering the corridor, so this is " +
                 "sharper than it looks. Storm ships at 1.3.")]
        [Range(0.25f, 3f)] public float airSpeedMultiplier = 1f;

        [Tooltip("Multiplier on the speed of GROUND units this wave. Below 1 is a slow, grinding " +
                 "push — more time under fire, but also more time leaking damage into the pads.")]
        [Range(0.25f, 3f)] public float groundSpeedMultiplier = 1f;

        [Header("Effects — durability and payout")]
        [Tooltip("Multiplier on enemy MAX HEALTH this wave, applied over the wave scalar and the " +
                 "difficulty multiplier. Overcharge ships at 1.3.")]
        [Range(0.25f, 4f)] public float healthMultiplier = 1f;

        [Tooltip("Multiplier on the SALVAGE each kill pays. The counterweight to a harder rule: a " +
                 "mutator that only takes is a punishment, one that pays reads as a bargain. " +
                 "Overcharge ships at 1.5.")]
        [Range(0.25f, 4f)] public float bountyMultiplier = 1f;

        [Header("Effects — the defence")]
        [Tooltip("Multiplier on every turret's EFFECTIVE RANGE this wave. Below 1 shortens it — " +
                 "Blackout ships at 0.5, where turrets see half as far until a Floodlight lights " +
                 "the units. This is the harshest term in the list: range is area, so 0.5 range is " +
                 "a quarter of the ground covered.")]
        [Range(0.25f, 2f)] public float turretRangeMultiplier = 1f;

        [Header("Effects — shape of the attack")]
        [Tooltip("Every ground group funnels onto ONE approach instead of spreading across the " +
                 "map's spawners. Concentrates the wave — brutal against a spread defence, feeble " +
                 "against a stacked one. This is Convoy.")]
        public bool singleApproach;

        [Tooltip("Multiplier on the gap between spawns within every group. Below 1 compresses the " +
                 "wave into a shorter, denser push; above 1 stretches it into a trickle. Changes " +
                 "wave DURATION, which is what the model prices each pad's firing budget against.")]
        [Range(0.2f, 3f)] public float spawnGapMultiplier = 1f;

        /// <summary>This asset's effects as a foldable vector.</summary>
        public MutatorEffects Effects => new MutatorEffects
        {
            airSpeed = Mathf.Max(0.01f, airSpeedMultiplier),
            groundSpeed = Mathf.Max(0.01f, groundSpeedMultiplier),
            health = Mathf.Max(0.01f, healthMultiplier),
            bounty = Mathf.Max(0.01f, bountyMultiplier),
            turretRange = Mathf.Max(0.01f, turretRangeMultiplier),
            spawnGap = Mathf.Max(0.01f, spawnGapMultiplier),
            singleApproach = singleApproach,
        };

        /// <summary>The id, normalized the way the exporter and the model read
        /// it. Empty ids fall back to the asset name so a half-authored asset
        /// still exports something traceable rather than an empty string.</summary>
        public string ResolvedId =>
            string.IsNullOrWhiteSpace(id) ? name.ToLowerInvariant() : id.Trim().ToLowerInvariant();
    }
}
