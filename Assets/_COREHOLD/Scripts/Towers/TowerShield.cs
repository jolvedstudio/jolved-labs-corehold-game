using Corehold.VFX;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The visible "empowered" read for towers (VFX plan Tier 1, extended to towers).
    ///
    /// This is the TOWER-SEMANTICS layer over the shared <see cref="ShieldShell"/>.
    /// A tower shows the shell while its authored SHIELD BARRIER holds charge (the
    /// per-tier shieldHitPoints on <see cref="Corehold.Data.TowerTier"/>). The bubble
    /// therefore means exactly "this turret's shield is up": it appears when the
    /// shield is charged and vanishes the instant a barrage depletes it, giving the
    /// player a true read on when the protection is spent.
    ///
    /// Colour is DISTINCT from the enemy Shielded blue (VFX colour-language rule,
    /// review item E): a warm amber-green so the bubble reads as "friendly buff",
    /// never as "shielded enemy". Same one-draw, no-particle, no-light shell.
    ///
    /// Driven from <see cref="TowerHealth"/> whenever the shield value changes
    /// (absorb, deplete, regen, tier re-configure), never per frame in this class.
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerShield : MonoBehaviour
    {
        // Amber-green "friendly barrier" tint. Kept clear of the enemy Shielded blue
        // (0.35, 0.7, 1) and of the armour-identity palette.
        private static readonly Color BarrierColor = new Color(0.5f, 1f, 0.55f, 1f);

        private ShieldShell _shell;

        /// <summary>
        /// Reflect a tower's shield state on its shell. Adds the components on first
        /// use. Shows the barrier shell while the shield holds charge, hides it once
        /// the shield is depleted or absent.
        /// </summary>
        public static void Refresh(Tower tower, bool shieldUp)
        {
            if (tower == null)
                return;
            var ts = tower.GetComponent<TowerShield>();
            if (ts == null)
            {
                if (!shieldUp)
                    return; // nothing to show and nothing built yet — skip allocation
                ts = tower.gameObject.AddComponent<TowerShield>();
            }
            ts.Apply(shieldUp);
        }

        private void Apply(bool shieldUp)
        {
            if (shieldUp)
            {
                if (_shell == null)
                {
                    _shell = GetComponent<ShieldShell>();
                    if (_shell == null)
                        _shell = gameObject.AddComponent<ShieldShell>();
                }
                _shell.Show(BarrierColor);
            }
            else if (_shell != null)
            {
                _shell.Hide();
            }
        }
    }
}
