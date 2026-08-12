namespace Corehold.Towers
{
    /// <summary>
    /// The current external buffs applied to a <see cref="Tower"/> (GDD §7.3).
    /// A plain value struct — one instance lives on each Tower and is replaced
    /// wholesale when a <see cref="SupportAura"/> refresh runs (never mutated
    /// per frame).
    ///
    /// Every field is an additive fraction of the base tier value: 0.15 means
    /// +15%. <see cref="None"/> is the identity (all zero = ×1.0). The Tower's
    /// EffectiveRange / EffectiveFireRate / EffectiveDamage properties multiply
    /// their base tier number by (1 + bonus), so these are the single source of
    /// truth for buffs and there is no cached effective value to go stale.
    ///
    /// Auras do not stack (GDD §7.3): when a tower sits inside two relays it takes
    /// the STRONGEST single relay's bonus, not the sum. <see cref="StrongerThan"/>
    /// expresses that comparison per field, so the winning modifier can be built
    /// field-by-field across every relay covering the tower.
    /// </summary>
    public struct TowerModifiers
    {
        /// <summary>Additive fire-rate bonus (0.15 = +15% shots per second).</summary>
        public float fireRateBonus;

        /// <summary>Additive range bonus (0.10 = +10% metres).</summary>
        public float rangeBonus;

        /// <summary>Additive damage bonus (0.10 = +10% damage per shot).</summary>
        public float damageBonus;

        /// <summary>The identity modifier: no buff on any axis (all ×1.0).</summary>
        public static TowerModifiers None => default;

        /// <summary>True when every bonus is zero — the tower is unbuffed.</summary>
        public bool IsNone => fireRateBonus == 0f && rangeBonus == 0f && damageBonus == 0f;

        public TowerModifiers(float fireRateBonus, float rangeBonus, float damageBonus)
        {
            this.fireRateBonus = fireRateBonus;
            this.rangeBonus = rangeBonus;
            this.damageBonus = damageBonus;
        }

        /// <summary>
        /// Fold another relay's bonuses in under the non-stacking rule (GDD §7.3):
        /// each field keeps the larger of the two values, so the result carries the
        /// strongest single relay's contribution per axis rather than their sum.
        /// Applying this across every relay that covers a tower yields the strongest
        /// bonus without ever adding two auras together.
        /// </summary>
        public TowerModifiers TakeStrongerPerAxis(TowerModifiers other)
        {
            return new TowerModifiers(
                fireRateBonus > other.fireRateBonus ? fireRateBonus : other.fireRateBonus,
                rangeBonus > other.rangeBonus ? rangeBonus : other.rangeBonus,
                damageBonus > other.damageBonus ? damageBonus : other.damageBonus);
        }

        public bool Equals(TowerModifiers other) =>
            fireRateBonus == other.fireRateBonus &&
            rangeBonus == other.rangeBonus &&
            damageBonus == other.damageBonus;

        public override bool Equals(object obj) => obj is TowerModifiers m && Equals(m);

        public override int GetHashCode() =>
            fireRateBonus.GetHashCode() ^ (rangeBonus.GetHashCode() << 2) ^ (damageBonus.GetHashCode() >> 2);

        public override string ToString() =>
            $"(+{fireRateBonus:P0} rate, +{rangeBonus:P0} range, +{damageBonus:P0} dmg)";
    }
}
