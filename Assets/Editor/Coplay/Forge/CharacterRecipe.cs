using Corehold.Data;
using UnityEngine;

namespace CoreholdEditor.Forge
{
    public enum ForgeArchetype
    {
        Walker,        // ground enemy, Animator-driven (walk + die clips)
        Flier,         // air enemy — no animator required
        CombatTurret,  // human-authored chassis wired into a TowerDefinition
    }

    /// <summary>
    /// A character recipe (plan v2 §B.2): TEMPLATE DEFINITION + ASSEMBLY HINTS.
    /// The template definition is the single stat schema — the forge clones it
    /// and sets only identity, prefab and icon, so a new definition field never
    /// needs a matching recipe field (the v1 stat-block design died in review
    /// for exactly that drift).
    ///
    /// Editor assembly on purpose: recipes reference vendor prefabs and clips,
    /// which must never become reachable from shipped content.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Character Recipe (editor)", fileName = "Recipe_")]
    public class CharacterRecipe : ScriptableObject
    {
        [Header("What to build")]
        public ForgeArchetype archetype = ForgeArchetype.Walker;

        [Tooltip("The developer-supplied model — vendor or project prefab. NOTE: a prefab built from " +
                 "git-ignored vendor art carries GUID references that dangle on a fresh clone; the forge " +
                 "transcript lists every out-of-repo dependency so the exposure is visible per unit.")]
        public GameObject sourcePrefab;

        [Header("Identity")]
        [Tooltip("Stable id for code/model/save data, e.g. 'ripper'. Lowercase.")]
        public string id;
        public string displayName;

        [Header("Stats — template definition (cloned, then identity/prefab overwritten)")]
        [Tooltip("Enemy archetypes: the definition whose stats/audio/enrage block this unit starts from.")]
        public EnemyDefinition enemyTemplate;
        [Tooltip("CombatTurret: the definition whose tiers/damage profile this turret starts from.")]
        public TowerDefinition towerTemplate;

        [Tooltip("CombatTurret: build-menu position (see TowerDefinition.menuOrder). 0 = after everything.")]
        public int menuOrder;

        [Header("Rig hints (enemies)")]
        [Tooltip("Child names probed (deep) for weapon muzzles, in order. No match → a forward marker is generated (the Colossus fallback).")]
        public string[] muzzleMarkerNames = { "Barrel_End", "Muzzle", "Barrel_End_1" };

        [Tooltip("Walker: walk/locomotion clip. Both clips assigned → an Animator controller is built; " +
                 "neither → the unit ships animator-less (mover-driven, like the Shrike).")]
        public AnimationClip walkClip;
        public AnimationClip dieClip;

        [Header("Return fire (enemies; leave damage 0 for unarmed units)")]
        public float weaponRange = 14f;
        public float weaponFireRate = 0.9f;
        public float weaponDamage = 0f;
        public Color tracerColor = new Color(4f, 1.2f, 0.3f, 1f);

        [Header("Visuals")]
        public Material bodyMaterialOverride;
        public float scale = 1f;
        [Tooltip("Blob-shadow diameter in metres; 0 = auto from renderer bounds.")]
        public float shadowDiameter;

        [Header("Movement feel (enemies)")]
        [Tooltip("Mover turn rate °/s — massive units want ~90 (the Colossus slew fix), skirmishers 360.")]
        public float turnRate = 240f;
        [Tooltip("Physical body radius for car-following spacing; 0 = auto from renderer bounds.")]
        public float bodyRadius;

        [Header("Output")]
        [Tooltip("Defaults: Prefabs/Enemies or Prefabs/Towers, Data/Enemies or Data/Towers.")]
        public string outputPrefabFolder;
        public string outputDefinitionFolder;

        public bool IsEnemy => archetype != ForgeArchetype.CombatTurret;
    }
}
