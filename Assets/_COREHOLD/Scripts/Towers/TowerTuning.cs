using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Per-level turret tuning, applied at level load from the
    /// LevelDefinition's certified multipliers (written by the generator's
    /// adopt flow, editable by hand). Scales every turret's damage and range
    /// for THIS level only — the Tower_*.asset tiers stay untouched, so one
    /// level's certification can never rewrite another level's turrets.
    ///
    /// Statics survive scene loads, so WaveManager.ResolveRules calls
    /// <see cref="Apply"/> on every level (falling back to 1 when the level
    /// authored none) rather than trusting a previous scene's values.
    /// The balance model receives the same multipliers per run
    /// (--tower-dps-mult / --tower-range-mult), which is what keeps
    /// "certified" true for a tuned level.
    /// </summary>
    public static class TowerTuning
    {
        /// <summary>Multiplier on every turret's damage this level (1 = authored).</summary>
        public static float DamageMult { get; private set; } = 1f;

        /// <summary>Multiplier on every turret's range this level (1 = authored).</summary>
        public static float RangeMult { get; private set; } = 1f;

        /// <summary>Set both multipliers; non-positive values mean "authored" (1).</summary>
        public static void Apply(float damage, float range)
        {
            DamageMult = damage > 0f ? damage : 1f;
            RangeMult = range > 0f ? range : 1f;

            if (!Mathf.Approximately(DamageMult, 1f) || !Mathf.Approximately(RangeMult, 1f))
                Debug.Log($"[TowerTuning] level multipliers active: damage ×{DamageMult:0.##}, " +
                          $"range ×{RangeMult:0.##} (certified tuning from the LevelDefinition)");
        }
    }
}
