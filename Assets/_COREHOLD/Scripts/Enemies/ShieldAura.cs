using Corehold.Data;
using Corehold.VFX;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// The visible shield read for Shielded enemies (VFX plan Tier 1 — the single
    /// highest-value item: the counter pillar must be legible BEFORE the first wrong
    /// shot, not only as an after-the-fact ripple).
    ///
    /// This is the ENEMY-SEMANTICS layer: it decides WHEN the shell shows (a live
    /// Shielded enemy) and in WHICH colour (blue, matching
    /// <see cref="Corehold.Systems.OverlayManager"/>'s Shielded pip so the bubble and
    /// the HP-pip reinforce one identity). All the rendering, per-frame bounds sizing
    /// and material sharing live in the reusable <see cref="ShieldShell"/>.
    ///
    /// Lifecycle: attach on spawn, detach on death only — there is no shield-break
    /// mechanic (armour type is static per enemy), so the shell simply shows while the
    /// unit is a live Shielded enemy and hides otherwise.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShieldAura : MonoBehaviour
    {
        // Matches OverlayManager.Shielded so the bubble and the armour pip read as one
        // identity. Kept DISTINCT from the tower "empowered" colour so blue always
        // means "Shielded enemy" (VFX colour-language rule, review item E).
        private static readonly Color ShieldColor = new Color(0.35f, 0.7f, 1f, 1f);

        private Enemy _enemy;
        private ShieldShell _shell;

        /// <summary>
        /// Ensure a ShieldAura exists on the enemy and reflects its current armour
        /// type. Safe to call repeatedly (spawn, Configure, SetArmourType) — it adds
        /// the component once and just refreshes visibility thereafter.
        /// </summary>
        public static void Refresh(Enemy enemy)
        {
            if (enemy == null)
                return;
            var aura = enemy.GetComponent<ShieldAura>();
            if (aura == null)
                aura = enemy.gameObject.AddComponent<ShieldAura>();
            aura._enemy = enemy;
            aura.Apply(enemy.ArmourType == ArmourType.Shielded);
        }

        /// <summary>Hide the shell (called when the unit dies or leaves play).</summary>
        public static void Hide(Enemy enemy)
        {
            if (enemy == null)
                return;
            var aura = enemy.GetComponent<ShieldAura>();
            if (aura != null)
                aura.Apply(false);
        }

        private void Apply(bool show)
        {
            if (show)
            {
                if (_shell == null)
                {
                    _shell = GetComponent<ShieldShell>();
                    if (_shell == null)
                        _shell = gameObject.AddComponent<ShieldShell>();
                    _shell.FallbackRadius = _enemy != null ? _enemy.BodyRadius : 0.6f;
                }
                _shell.Show(ShieldColor);
            }
            else if (_shell != null)
            {
                _shell.Hide();
            }
        }
    }
}
