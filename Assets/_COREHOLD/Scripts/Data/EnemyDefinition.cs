using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Static definition of an enemy unit (GDD §12.2, §6.1).
    /// Base stats are multiplied by the wave HP scalar at spawn.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Enemy Definition", fileName = "Enemy_")]
    public class EnemyDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable identifier used in code and save data.")]
        public string id;

        [Tooltip("Player-facing name.")]
        public string displayName;

        [Tooltip("Icon rendered from the prefab (GDD §9.5).")]
        public Sprite icon;

        [Tooltip("Enemy prefab spawned into the scene.")]
        public GameObject prefab;

        [Header("Combat profile")]
        [Tooltip("Base health before the wave scalar is applied.")]
        public float baseHealth;

        [Tooltip("Armour type — determines the damage multiplier taken (GDD §7.1).")]
        public ArmourType armourType;

        [Tooltip("Movement speed in metres per second.")]
        public float moveSpeed;

        [Tooltip("Salvage awarded on kill.")]
        public int bounty;

        [Tooltip("Core integrity removed when this unit leaks (reaches the Core).")]
        public int leakDamage;

        [Header("Audio (GDD §10)")]
        [Tooltip("Sound played each time one of this enemy's weapons fires (GDD §10). Leave null for a silent shot. At least one clip per enemy is recommended.")]
        public AudioClip fireSound;

        [Tooltip("Base linear volume (0..1) for the fire sound before group gains.")]
        [Range(0f, 1f)] public float fireVolume = 0.7f;

        [Tooltip("Random pitch spread applied each fire, e.g. 0.08 = ±8% (GDD §10).")]
        [Range(0f, 0.5f)] public float firePitchSpread = 0.08f;

        [Tooltip("Sound played when this enemy is destroyed (GDD §10). Leave null to use the shared EnemyDeath one-shot on the AudioDirector.")]
        public AudioClip deathSound;

        [Tooltip("Base linear volume (0..1) for the death sound before group gains.")]
        [Range(0f, 1f)] public float deathVolume = 0.8f;

        [Tooltip("Random pitch spread applied each death, e.g. 0.06 = ±6% (GDD §10).")]
        [Range(0f, 0.5f)] public float deathPitchSpread = 0.06f;

        [Header("Movement type")]
        [Tooltip("Air units ignore the ground route and fly straight to the Core.")]
        public bool isAir;

        [Tooltip("Fixed flight altitude in metres for air units (GDD §5.2).")]
        public float flightAltitude;

        [Tooltip("Implied ground speed of the locomotion clip, used to drive Animator.speed and avoid foot-sliding (GDD §6.3).")]
        public float animatorClipSpeedRef;

        [Header("Roller phase change (GDD §6.2)")]
        [Tooltip("Whether this unit changes phase partway along the route (the Roller).")]
        public bool hasSecondPhase;

        [Tooltip("Path fraction (0..1) at which the phase change occurs.")]
        public float phaseChangeAtPathFraction;

        [Tooltip("Movement speed after the phase change in m/s.")]
        public float secondPhaseSpeed;

        [Header("Colossus enrage (GDD §6.2)")]
        [Tooltip("Health fraction (0..1) below which the boss enrages. 0 = never.")]
        public float enrageAtHealthFraction;

        [Tooltip("Speed multiplier applied when enraged (e.g. 1.4 = +40%).")]
        public float enrageSpeedMultiplier;

        [Header("Status effects (R18)")]
        [Tooltip("Fraction (0..1) of any incoming STUN duration this unit shrugs off. 0.25 = stuns last 25% less (the Colossus). Slows are unaffected.")]
        [Range(0f, 1f)] public float stunResistance;
    }
}
