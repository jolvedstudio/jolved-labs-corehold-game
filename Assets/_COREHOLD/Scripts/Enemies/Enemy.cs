using System;
using System.Collections;
using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Runtime state for a single enemy unit: health and what happens when it
    /// dies or leaks (reaches the Core). Carries no collider and no Rigidbody —
    /// targeting is done through a live-enemy registry, not physics (GDD §7.4).
    ///
    /// Implements the Colossus enrage (GDD §6.2): once health drops below
    /// <see cref="EnemyDefinition.enrageAtHealthFraction"/>, movement speed is
    /// multiplied by <see cref="EnemyDefinition.enrageSpeedMultiplier"/> and the
    /// emissive shifts from orange to white. Fires once.
    /// </summary>
    [DisallowMultipleComponent]
    public class Enemy : MonoBehaviour
    {
        /// <summary>
        /// Registry of every enemy currently alive and active in the scene.
        /// Towers iterate this instead of using physics queries (GDD §7.4).
        /// Populated in OnEnable, removed in OnDisable.
        /// </summary>
        public static readonly List<Enemy> Live = new List<Enemy>();

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [Header("Health")]
        [Tooltip("Maximum health. Base value before any wave scalar is applied.")]
        [SerializeField] private float maxHealth = 100f;

        [Header("Targeting")]
        [Tooltip("Empty at centre mass — the point turrets aim at and measure range to (GDD §12.5). Falls back to the transform if unset.")]
        [SerializeField] private Transform hitPoint;

        [Header("Economy")]
        [Tooltip("Salvage awarded to the player when this enemy is killed (GDD §7.3).")]
        [SerializeField] private int bounty = 8;

        [Header("Core")]
        [Tooltip("Integrity removed from the Core when this enemy leaks (reaches the Core).")]
        [SerializeField] private float leakDamage = 1f;

        [Header("Death")]
        [Tooltip("Seconds to keep the enemy alive after death so its death animation can play before it is pooled (GDD §6.3). Set from the death clip length by the animation setup.")]
        [SerializeField] private float deathAnimDuration = 0.6f;

        [Header("Classification")]
        [Tooltip("Air units ignore the ground route and can only be hit by turrets whose canTargetAir is true (GDD §6.1, §7.2).")]
        [SerializeField] private bool isAir;

        [Tooltip("Armour type — determines the damage multiplier taken from each damage type (GDD §7.1). Set at spawn from the EnemyDefinition.")]
        [SerializeField] private ArmourType armourType = ArmourType.Unarmoured;

        [Header("Enrage (GDD §6.2)")]
        [Tooltip("Optional definition. Drives the Colossus enrage (health fraction, speed multiplier). Assign at spawn via Configure().")]
        [SerializeField] private EnemyDefinition definition;

        [Tooltip("Renderers whose emissive shifts orange to white on enrage. Auto-found in children if left empty.")]
        [SerializeField] private Renderer[] enrageRenderers;

        [Tooltip("Emissive colour before enrage (orange).")]
        [ColorUsage(false, true)]
        [SerializeField] private Color enrageEmissiveFrom = new Color(1f, 0.35f, 0f, 1f) * 2f;

        [Tooltip("Emissive colour after enrage (white).")]
        [ColorUsage(false, true)]
        [SerializeField] private Color enrageEmissiveTo = new Color(1f, 1f, 1f, 1f) * 3f;

        private EnemyMover _mover;
        private MaterialPropertyBlock _emissiveBlock;
        private bool _enraged;

        /// <summary>Current runtime health. Set to maxHealth on enable/spawn.</summary>
        public float CurrentHealth { get; private set; }

        /// <summary>Maximum health for this enemy.</summary>
        public float MaxHealth => maxHealth;

        /// <summary>Integrity removed from the Core when this enemy leaks.</summary>
        public float LeakDamage => leakDamage;

        /// <summary>Salvage awarded to the player when this enemy is killed.</summary>
        public int Bounty => bounty;

        /// <summary>
        /// Set the kill bounty (called at spawn, after the difficulty economy
        /// multiplier has been applied — GDD §8.2).
        /// </summary>
        public void SetBounty(int value) => bounty = Mathf.Max(0, value);

        /// <summary>Set the Core-integrity leak damage (called at spawn from the definition).</summary>
        public void SetLeakDamage(float value) => leakDamage = Mathf.Max(0f, value);

        /// <summary>
        /// True if this enemy flies (GDD §6.1). Ground-only turrets skip air units.
        /// Set from the EnemyDefinition at spawn via <see cref="SetIsAir"/>.
        /// </summary>
        public bool IsAir => isAir;

        /// <summary>Set whether this enemy is an air unit (called at spawn from its definition).</summary>
        public void SetIsAir(bool value) => isAir = value;

        /// <summary>
        /// Armour type used to look up the damage multiplier each hit takes (GDD §7.1).
        /// Set from the EnemyDefinition at spawn via <see cref="SetArmourType"/>.
        /// </summary>
        public ArmourType ArmourType => armourType;

        /// <summary>Set the armour type (called at spawn from the enemy's definition).</summary>
        public void SetArmourType(ArmourType value) => armourType = value;

        /// <summary>True once this enemy has enraged (GDD §6.2).</summary>
        public bool IsEnraged => _enraged;

        /// <summary>
        /// The definition backing this enemy (GDD §12.2), assigned at spawn via
        /// <see cref="Configure"/>. Drives the per-enemy fire/death sounds (GDD §10)
        /// and the Colossus enrage. May be null on hand-placed test enemies.
        /// </summary>
        public EnemyDefinition Definition => definition;

        /// <summary>
        /// True for the Colossus (GDD §6.2): the only unit with an enrage threshold.
        /// Used to gate the heavy-footfall camera shake (GDD §3.3, §11.5) so ordinary
        /// swarm units never shake the camera as they walk.
        /// </summary>
        public bool IsColossus => definition != null && definition.enrageAtHealthFraction > 0f;

        /// <summary>
        /// World-space point turrets aim at and measure range to (GDD §7.2, §12.5).
        /// Uses the assigned <see cref="hitPoint"/> transform, or this transform if unset.
        /// </summary>
        public Vector3 HitPoint => hitPoint != null ? hitPoint.position : transform.position;

        /// <summary>The transform used as the aim/range target, or this transform if unset.</summary>
        public Transform HitPointTransform => hitPoint != null ? hitPoint : transform;

        /// <summary>
        /// This enemy's mover, cached in Awake. Exposed so scheduling / targeting /
        /// projectile code can read progress and velocity without a per-frame
        /// GetComponent (avoids O(n²) lookups in the traffic manager).
        /// </summary>
        public EnemyMover Mover => _mover;

        /// <summary>Physical radius (m) of this unit, read from its mover (falls back to 0.6).</summary>
        public float BodyRadius => _mover != null ? _mover.BodyRadius : 0.6f;

        /// <summary>True while the enemy is alive and active.</summary>
        public bool IsAlive { get; private set; }

        /// <summary>Raised when this enemy's health reaches zero and it dies.</summary>
        public event Action<Enemy> OnDied;

        /// <summary>Raised when this enemy reaches the Core (leaks).</summary>
        public event Action<Enemy> OnLeaked;

        private void Awake()
        {
            _mover = GetComponent<EnemyMover>();
            ResetHealth();

            // Self-contained, always-visible health bar (billboarded, follows and
            // dies with this enemy). One per instance.
            if (GetComponentInChildren<Corehold.UI.WorldHealthBar>() == null)
            {
                Corehold.UI.WorldHealthBar.Attach(
                    gameObject,
                    () => maxHealth > 0f ? Mathf.Clamp01(CurrentHealth / maxHealth) : 0f,
                    3.6f, 1.8f, 0.26f,
                    new Color(0.4f, 0.95f, 0.45f, 1f));
            }
        }

        private void OnEnable()
        {
            if (_mover == null)
                _mover = GetComponent<EnemyMover>();
            ResetHealth();
            ResetEnrage();
            // Re-enable renderers hidden by the death sequence when reused from pool.
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                r.enabled = true;
            if (!Live.Contains(this))
                Live.Add(this);
        }

        private void OnDisable()
        {
            Live.Remove(this);
        }

        /// <summary>Restore health to full and mark alive. Call when spawning from a pool.</summary>
        public void ResetHealth()
        {
            CurrentHealth = maxHealth;
            IsAlive = true;
        }

        /// <summary>Set the maximum health (e.g. after applying a wave scalar) and refill.</summary>
        public void SetMaxHealth(float value)
        {
            maxHealth = Mathf.Max(1f, value);
            ResetHealth();
        }

        /// <summary>
        /// Assign the definition at spawn. Copies the armour type, air flag and
        /// base health across, and arms the Colossus enrage (GDD §6.2, §12.2).
        /// </summary>
        public void Configure(EnemyDefinition def)
        {
            definition = def;
            if (def == null)
                return;

            armourType = def.armourType;
            isAir = def.isAir;
            if (def.baseHealth > 0f)
                SetMaxHealth(def.baseHealth);

            ResetEnrage();
        }

        /// <summary>Apply damage. When health reaches zero the enemy dies and raises OnDied.</summary>
        public void TakeDamage(float amount)
        {
            if (!IsAlive || amount <= 0f)
                return;

            CurrentHealth -= amount;

            // Colossus enrage (GDD §6.2): below the health fraction, boost speed
            // and shift the emissive orange -> white. Fires once.
            if (!_enraged && definition != null && definition.enrageAtHealthFraction > 0f &&
                CurrentHealth > 0f && CurrentHealth <= maxHealth * definition.enrageAtHealthFraction)
            {
                Enrage();
            }

            if (CurrentHealth <= 0f)
            {
                CurrentHealth = 0f;
                Die();
            }
        }

        /// <summary>
        /// Trigger the enrage state (GDD §6.2): ×enrageSpeedMultiplier movement and
        /// a white emissive. The two design lines live here.
        /// </summary>
        private void Enrage()
        {
            _enraged = true;

            if (_mover == null)
                _mover = GetComponent<EnemyMover>();
            if (_mover != null && definition.enrageSpeedMultiplier > 0f)
                _mover.SpeedMultiplier = definition.enrageSpeedMultiplier;

            SetEmissive(enrageEmissiveTo);
        }

        /// <summary>Clear enrage state on (re)spawn and restore the base emissive.</summary>
        private void ResetEnrage()
        {
            _enraged = false;

            if (_mover == null)
                _mover = GetComponent<EnemyMover>();
            if (_mover != null)
                _mover.SpeedMultiplier = 1f;

            // Only touch emissive on units that actually enrage (the Colossus),
            // so ordinary enemies keep their authored materials untouched.
            if (definition != null && definition.enrageAtHealthFraction > 0f)
                SetEmissive(enrageEmissiveFrom);
        }

        private void SetEmissive(Color emission)
        {
            var renderers = enrageRenderers;
            if (renderers == null || renderers.Length == 0)
            {
                renderers = GetComponentsInChildren<Renderer>(true);
                enrageRenderers = renderers;
            }
            if (renderers == null || renderers.Length == 0)
                return;

            if (_emissiveBlock == null)
                _emissiveBlock = new MaterialPropertyBlock();

            foreach (var r in renderers)
            {
                if (r == null)
                    continue;
                r.GetPropertyBlock(_emissiveBlock);
                _emissiveBlock.SetColor(EmissionColorId, emission);
                r.SetPropertyBlock(_emissiveBlock);
            }
        }

        /// <summary>Called by EnemyMover when the enemy passes the last waypoint.</summary>
        public void ReachCore()
        {
            if (!IsAlive)
                return;

            IsAlive = false;
            Debug.Log($"[Corehold] {name} reached the Core (leak {leakDamage}).");

            // Core hit flash where the leaker reached the Core (GDD §11), pooled.
            if (Corehold.Systems.VFXDirector.Instance != null)
                Corehold.Systems.VFXDirector.Instance.PlayCoreHit(HitPoint);

            // Core alarm on a leak (GDD §10). Voice-stolen / collapsed in the director.
            if (Corehold.Systems.AudioDirector.Instance != null)
                Corehold.Systems.AudioDirector.Instance.PlayCoreAlarm();

            // Camera shake on Core hits only, low intensity, gated by a 1.5 s
            // cooldown so a twelve-Scuttler breach is one nudge, not a seizure
            // (GDD §3.3). The shaker refuses the request while on cooldown.
            if (Corehold.Systems.CameraShake.Instance != null)
                Corehold.Systems.CameraShake.Instance.ShakeCoreHit();

            OnLeaked?.Invoke(this);

            // For now, reaching the core just deactivates the GameObject.
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Watchdog cull (navigation liveness net, GDD redesign §Gap 4). Removes a
        /// unit that failed to make progress WITHOUT damaging the Core and WITHOUT
        /// paying bounty — a movement regression must cost the player nothing but a
        /// log line. Raises OnLeaked so the WaveManager decrements its live count and
        /// the wave can still complete.
        /// </summary>
        public void CullSilently()
        {
            if (!IsAlive)
                return;

            IsAlive = false;
            Debug.LogWarning($"[Corehold] Watchdog culled '{name}': no path progress in the stall window. " +
                             "This should never happen if navigation is healthy — investigate RouteTraffic.");

            OnLeaked?.Invoke(this);
            gameObject.SetActive(false);
        }

        private void Die()
        {
            IsAlive = false;

            // Death burst + a visible explosion (GDD §11), pooled through the
            // VFXDirector. The explosion guarantees a clearly readable death even for
            // enemies whose animator has no Die clip (e.g. the drone). Hide the body
            // so the explosion reads as the destruction rather than the mesh popping.
            if (Corehold.Systems.VFXDirector.Instance != null)
            {
                Corehold.Systems.VFXDirector.Instance.PlayEnemyDeath(HitPoint);
                Corehold.Systems.VFXDirector.Instance.PlayExplosion(
                    HitPoint, Corehold.Systems.VFXDirector.LargeSplashThreshold + 1f);
            }
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            // Death SFX (GDD §10). Uses this enemy's authored death clip when its
            // definition provides one, otherwise the shared EnemyDeath one-shot.
            // Collapsed within 50 ms so a wiped swarm is one louder burst rather
            // than a wall of identical deaths.
            if (Corehold.Systems.AudioDirector.Instance != null)
                Corehold.Systems.AudioDirector.Instance.PlayEnemyDeath(definition);

            // Award salvage bounty to the player (GDD §7.3), routed through the
            // kill-streak path (R2) so rapid kills escalate the payout.
            if (GameManager.Instance != null && bounty > 0)
                GameManager.Instance.AddKillSalvage(bounty, HitPoint);

            // Raise OnDied immediately so EnemyAnimatorBridge can fire the Die
            // trigger and start the death animation (GDD §6.3). The GameObject is
            // then deactivated after the death clip has had time to play.
            OnDied?.Invoke(this);

            if (deathAnimDuration > 0f && isActiveAndEnabled)
                StartCoroutine(DeactivateAfterDeath());
            else
                gameObject.SetActive(false);
        }

        /// <summary>
        /// Wait for the death animation to play, then deactivate so the pool can
        /// reclaim the instance (GDD §6.3). No Destroy — pooling only (GDD §11).
        /// </summary>
        private IEnumerator DeactivateAfterDeath()
        {
            yield return new WaitForSeconds(deathAnimDuration);
            gameObject.SetActive(false);
        }

        /// <summary>Death display duration in seconds (set from the death clip length).</summary>
        public float DeathAnimDuration
        {
            get => deathAnimDuration;
            set => deathAnimDuration = Mathf.Max(0f, value);
        }
    }
}
