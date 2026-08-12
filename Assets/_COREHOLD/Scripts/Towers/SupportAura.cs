using System.Collections.Generic;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The Scan Relay's support aura (GDD §7.3). A relay grants nearby towers a
    /// fire-rate bonus and a range bonus, and — at tier 3 — a damage bonus, to every
    /// tower whose position falls within <see cref="AuraRadius"/> metres.
    ///
    /// Two rules are load-bearing and non-negotiable:
    ///
    /// 1. AURAS DO NOT STACK. A tower inside two relays takes the STRONGEST single
    ///    relay's bonus per axis, not the sum. Without this, clustering relays is a
    ///    degenerate dominant strategy. This is enforced by folding each relay's
    ///    bonuses into a per-tower modifier with
    ///    <see cref="TowerModifiers.TakeStrongerPerAxis"/>, which keeps the larger
    ///    value on every axis rather than adding.
    ///
    /// 2. BONUSES ARE RECALCULATED ONLY ON BUILD, UPGRADE AND SELL — NEVER PER FRAME.
    ///    There is no Update here. The recompute runs exactly when
    ///    <see cref="Tower.OnRosterChanged"/> fires, which <see cref="Tower.Build"/>,
    ///    <see cref="Tower.SetTier"/>/<see cref="Tower.Upgrade"/> and
    ///    <see cref="Tower.Sell"/> raise. Recomputing three floats per tower on those
    ///    three discrete events is cheap and, crucially, cannot leave a stale buff on
    ///    a tower after a relay is sold.
    ///
    /// The pass is GLOBAL and static: any roster change recomputes EVERY non-relay
    /// tower from EVERY live relay found in <see cref="Tower.Live"/>. That single
    /// source of truth is what guarantees restoration — when a relay is sold it is
    /// already out of the tower registry, so the towers it used to cover recompute to
    /// the strongest of the REMAINING relays (or to <see cref="TowerModifiers.None"/>
    /// if none reach them), and the buff is removed rather than lingering.
    ///
    /// The recompute is driven from <see cref="Tower"/> on its build/upgrade/sell
    /// path — one pass per event — so there is no per-instance event subscription and
    /// no per-frame work here.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Tower))]
    public class SupportAura : MonoBehaviour
    {
        /// <summary>
        /// Registry of every active relay aura in the scene. The recompute pass
        /// iterates this to find which relays cover each tower.
        /// </summary>
        public static readonly List<SupportAura> Relays = new List<SupportAura>();

        private Tower _tower;

        /// <summary>The relay's aura radius in metres, sourced from its current tier (GDD §7.3).</summary>
        public float AuraRadius => _tower != null && _tower.HasTier ? _tower.CurrentTier.auraRadius : 0f;

        /// <summary>The bonuses this relay grants at its current tier (GDD §7.3).</summary>
        public TowerModifiers Bonuses
        {
            get
            {
                if (_tower == null || !_tower.HasTier)
                    return TowerModifiers.None;

                TowerTier tier = _tower.CurrentTier;
                return new TowerModifiers(
                    tier.auraFireRateBonus,
                    tier.auraRangeBonus,
                    tier.auraDamageBonus);
            }
        }

        private void Awake()
        {
            _tower = GetComponent<Tower>();
        }

        private void OnEnable()
        {
            if (!Relays.Contains(this))
                Relays.Add(this);

            // A relay only ever grants a buff; it never receives one.
            if (_tower != null)
                _tower.ClearModifiers();

            // Recompute so a relay directly enabled at runtime (SetActive) immediately
            // buffs its neighbours. The build/upgrade/sell events are driven from Tower
            // itself (single pass per event), so there is deliberately no per-instance
            // event subscription here — and never an Update (GDD §7.3).
            RecomputeAll();
        }

        private void OnDisable()
        {
            Relays.Remove(this);

            // This relay is going away (e.g. directly disabled). Recompute so towers
            // it covered fall back to the strongest remaining relay, or to no buff.
            RecomputeAll();
        }

        /// <summary>
        /// Recompute the buff on every non-relay tower from every live relay under
        /// the non-stacking rule (GDD §7.3). Static so a single pass handles the whole
        /// roster regardless of which relay's event triggered it, which is what makes
        /// selling a relay correctly RESTORE the towers it was buffing.
        /// </summary>
        public static void RecomputeAll()
        {
            var towers = Tower.Live;
            for (int t = 0; t < towers.Count; t++)
            {
                Tower tower = towers[t];
                if (tower == null || !tower.HasTier)
                    continue;

                // Relays do not buff themselves or each other; they only grant.
                if (tower.IsSupportRelay)
                {
                    tower.ClearModifiers();
                    continue;
                }

                // Fold in every relay that covers this tower, keeping the strongest
                // value per axis. Non-stacking: this is a max, never a sum.
                //
                // Relays are found by scanning the tower registry for towers that
                // are relays, rather than a parallel relay list — one source of
                // truth means a relay can never be missing from or stale in a second
                // registry, and the sell-then-restore path stays correct.
                TowerModifiers best = TowerModifiers.None;
                Vector3 towerPos = tower.transform.position;

                for (int r = 0; r < towers.Count; r++)
                {
                    Tower relayTower = towers[r];
                    if (relayTower == null || relayTower == tower || !relayTower.IsSupportRelay)
                        continue;

                    TowerTier relayTier = relayTower.CurrentTier;
                    float radius = relayTier.auraRadius;
                    if (radius <= 0f)
                        continue;

                    float distSqr = (relayTower.transform.position - towerPos).sqrMagnitude;
                    if (distSqr > radius * radius)
                        continue;

                    var relayBonuses = new TowerModifiers(
                        relayTier.auraFireRateBonus,
                        relayTier.auraRangeBonus,
                        relayTier.auraDamageBonus);
                    best = best.TakeStrongerPerAxis(relayBonuses);
                }

                // SetModifiers is a no-op when unchanged, so this pass stays cheap
                // even when nothing moved.
                tower.SetModifiers(best);
            }
        }

        private void OnDrawGizmosSelected()
        {
            float radius = AuraRadius;
            if (radius <= 0f)
                return;

            Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
