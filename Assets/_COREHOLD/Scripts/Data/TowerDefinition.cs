using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Static definition of a buildable turret (GDD §12.2, §7.2).
    /// Holds identity, the damage type it deals, and its three upgrade tiers.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Tower Definition", fileName = "Tower_")]
    public class TowerDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable identifier used in code and save data.")]
        public string id;

        [Tooltip("Player-facing name shown in the build menu.")]
        public string displayName;

        [Tooltip("Build-menu position — lower comes first (B0 roster registry). Definitions sort by " +
                 "(menuOrder, name), so adding a turret to the game is: create the definition, set " +
                 "menuOrder, re-run Build Real UI. Leave gaps (10, 20, 30…) so inserts need no renumbering.")]
        public int menuOrder;

        [Tooltip("Icon rendered from the prefab (GDD §9.5). Null until Ticket 33 generates it.")]
        public Sprite icon;

        [Tooltip("Player-facing description shown in the tower panel.")]
        [TextArea] public string description;

        [Header("Combat profile")]
        [Tooltip("Damage type this turret deals. Support relays deal none.")]
        public DamageType damageType;

        [Tooltip("Whether this turret can target air units.")]
        public bool canTargetAir;

        [Tooltip("Flak Array (roster): this turret targets ONLY air units — ground contacts are skipped entirely. Requires canTargetAir.")]
        public bool targetAirOnly;

        [Header("Prefabs & tiers")]
        [Tooltip("Base chassis prefab placed on a hardpoint.")]
        public GameObject basePrefab;

        [Tooltip("The three upgrade tiers (GDD §7.3).")]
        public TowerTier[] tiers = new TowerTier[3];
    }
}
