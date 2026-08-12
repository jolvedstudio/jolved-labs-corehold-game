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

        [Tooltip("Icon rendered from the prefab (GDD §9.5). Null until Ticket 33 generates it.")]
        public Sprite icon;

        [Tooltip("Player-facing description shown in the tower panel.")]
        [TextArea] public string description;

        [Header("Combat profile")]
        [Tooltip("Damage type this turret deals. Support relays deal none.")]
        public DamageType damageType;

        [Tooltip("Whether this turret can target air units.")]
        public bool canTargetAir;

        [Header("Prefabs & tiers")]
        [Tooltip("Base chassis prefab placed on a hardpoint.")]
        public GameObject basePrefab;

        [Tooltip("The three upgrade tiers (GDD §7.3).")]
        public TowerTier[] tiers = new TowerTier[3];
    }
}
