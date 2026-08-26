using System;
using System.Collections;
using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// The two status effects a unit can carry (R18). Applied via
    /// <see cref="Enemy.ApplyStatus"/>; refresh-not-stack per kind.
    /// </summary>
    public enum StatusKind
    {
        /// <summary>Drops the unit to the minDesiredSpeed crawl floor — never a hard halt.</summary>
        Stun,
        /// <summary>Scales speed by (1 − strength), e.g. strength 0.5 = half speed.</summary>
        Slow
    }

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

        // ----- Status effects (R18) -----

        /// <summary>Seconds between pooled status-VFX pulses while an effect runs.</summary>
        private const float StatusVfxPulseSeconds = 1.0f;

        /// <summary>One active status. Refresh-not-stack: at most one entry per kind.</summary>
        private struct ActiveStatus
        {
            public StatusKind kind;
            public float strength;    // Slow: fraction of speed removed. Stun: unused.
            public float endTime;     // Time.time at which the effect expires.
            public float nextPulseAt; // Time.time of the next status-VFX pulse.
        }

        private readonly List<ActiveStatus> _statuses = new List<ActiveStatus>(4);

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
        /// Wave this enemy was spawned for (1-based). Recorded so the chain cap can
        /// count DISTINCT waves on the field — "two waves in flight" is a statement
        /// about waves, and a live-enemy count cannot answer it.
        /// </summary>
        public int WaveNumber { get; private set; }

        /// <summary>Set the owning wave (called at spawn).</summary>
        public void SetWaveNumber(int value) => WaveNumber = Mathf.Max(1, value);

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
        /// Uses the assigned <see cref="hitPoint"/> transform when authored; otherwise
        /// falls back to the body's GEOMETRIC CENTRE (centre mass) rather than the
        /// transform root, which sits at the feet — so turrets aim at the middle of
        /// the unit, not the ground beneath it (GDD §12.5 "empty at centre mass").
        /// </summary>
        public Vector3 HitPoint => hitPoint != null
            ? hitPoint.position
            : Corehold.Systems.GeometricCenter.Of(gameObject);

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

        /// <summary>
        /// Multiplier on the DISTANCE a turret measures when acquiring this unit
        /// (R20 Blackout: 2 while unlit, so towers see it at half range). 1 = normal.
        /// Scales only the max-range comparison — min-range dead zones stay true.
        /// </summary>
        public float AcquisitionDistanceScale { get; private set; } = 1f;

        /// <summary>Set the acquisition-distance multiplier (stamped at spawn — R20).</summary>
        public void SetAcquisitionDistanceScale(float value) =>
            AcquisitionDistanceScale = Mathf.Max(0.01f, value);

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
            ClearStatuses();
            AcquisitionDistanceScale = 1f;
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

            // Warden bubble (roster): allies inside a live Warden's radius take
            // reduced damage — killing the Warden first is the counterplay.
            amount *= WardenAura.DamageMultiplierFor(this);

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

        // ================================================================================
        //  Status effects (R18)
        // ================================================================================

        /// <summary>True while a stun is active (the unit is at the crawl floor).</summary>
        public bool IsStunned => HasStatus(StatusKind.Stun);

        /// <summary>True if a status of the given kind is currently active.</summary>
        public bool HasStatus(StatusKind kind)
        {
            for (int i = 0; i < _statuses.Count; i++)
                if (_statuses[i].kind == kind)
                    return true;
            return false;
        }

        /// <summary>Stun for <paramref name="duration"/> seconds (crawl floor, not a halt).</summary>
        public void ApplyStun(float duration) => ApplyStatus(StatusKind.Stun, duration, 1f);

        /// <summary>Slow by <paramref name="strength"/> (0.5 = half speed) for the duration.</summary>
        public void ApplySlow(float duration, float strength) =>
            ApplyStatus(StatusKind.Slow, duration, strength);

        /// <summary>
        /// Apply a status effect. Refresh-not-stack: a second application of the
        /// same kind REPLACES the existing entry (duration restarts, strength is
        /// the new value) — two 50% slows never compound into 75%. Different kinds
        /// DO compose multiplicatively through the mover's status slot. Stun
        /// durations are shortened by <see cref="EnemyDefinition.stunResistance"/>
        /// (Colossus 0.25 → 25% shorter). The stored base speed is never touched —
        /// everything acts through <see cref="EnemyMover.StatusSpeedMultiplier"/>,
        /// whose DesiredSpeed floor guarantees the unit still crawls (liveness).
        /// </summary>
        public void ApplyStatus(StatusKind kind, float duration, float strength)
        {
            if (!IsAlive || duration <= 0f)
                return;

            if (kind == StatusKind.Stun && definition != null)
                duration *= 1f - Mathf.Clamp01(definition.stunResistance);
            if (duration <= 0f)
                return;

            float now = Time.time;
            var entry = new ActiveStatus
            {
                kind = kind,
                strength = Mathf.Clamp01(strength),
                endTime = now + duration,
                nextPulseAt = now + StatusVfxPulseSeconds
            };

            int existing = -1;
            for (int i = 0; i < _statuses.Count; i++)
                if (_statuses[i].kind == kind) { existing = i; break; }

            if (existing >= 0)
                _statuses[existing] = entry;
            else
                _statuses.Add(entry);

            PlayStatusVfx(kind);
            ApplyStatusSpeed();
        }

        private void Update()
        {
            if (_statuses.Count == 0)
                return;

            float now = Time.time;
            bool changed = false;
            for (int i = _statuses.Count - 1; i >= 0; i--)
            {
                var s = _statuses[i];
                if (now >= s.endTime)
                {
                    _statuses.RemoveAt(i);
                    changed = true;
                    continue;
                }
                // Re-fire the pooled one-shot while the status runs so a 3 s stun
                // reads as an ongoing state, without any looping-effect machinery.
                if (IsAlive && now >= s.nextPulseAt)
                {
                    s.nextPulseAt = now + StatusVfxPulseSeconds;
                    _statuses[i] = s;
                    PlayStatusVfx(s.kind);
                }
            }

            if (changed)
                ApplyStatusSpeed();
        }

        /// <summary>Recompute the mover's status slot from the active list.</summary>
        private void ApplyStatusSpeed()
        {
            float mult = 1f;
            for (int i = 0; i < _statuses.Count; i++)
            {
                var s = _statuses[i];
                mult *= s.kind == StatusKind.Stun ? 0f : 1f - s.strength;
            }

            if (_mover == null)
                _mover = GetComponent<EnemyMover>();
            if (_mover != null)
                _mover.StatusSpeedMultiplier = mult;
        }

        private void PlayStatusVfx(StatusKind kind)
        {
            var vfx = Corehold.Systems.VFXDirector.Instance;
            if (vfx == null)
                return;
            if (kind == StatusKind.Stun)
                vfx.PlayStun(HitPoint);
            else
                vfx.PlaySlow(HitPoint);
        }

        /// <summary>Drop all statuses and restore the mover's status slot to 1 (pool reuse).</summary>
        private void ClearStatuses()
        {
            _statuses.Clear();
            if (_mover != null)
                _mover.StatusSpeedMultiplier = 1f;
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

            // Everything between here and OnDied is COSMETIC or economic — VFX,
            // audio, the salvage payout and the HUD it drives. None of it may be
            // allowed to skip the bookkeeping below, and until this try/catch
            // existed it could: a MissingReferenceException thrown by the kill-
            // streak label left OnDied unraised, so the WaveManager never removed
            // this enemy from its live list. The corpse stayed on the books
            // forever — the wave could not complete and the chain lock never
            // released, over a dead text label. A subsystem that draws numbers
            // must not be able to strand a unit.
            try
            {
                DieEffects();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }

            // Bookkeeping. Raised even if the effects above threw, because THIS
            // is what tells the WaveManager the unit is gone (GDD §8.1) and what
            // starts the death animation (GDD §6.3).
            try
            {
                OnDied?.Invoke(this);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e, this);
            }

            if (deathAnimDuration > 0f && isActiveAndEnabled)
                StartCoroutine(DeactivateAfterDeath());
            else
                gameObject.SetActive(false);
        }

        /// <summary>The death's cosmetics and payout — best-effort, see <see cref="Die"/>.</summary>
        private void DieEffects()
        {

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
            // kill-streak path (R2) so rapid kills escalate the payout. A Salvage
            // Rig (roster) covering the DEATH POSITION boosts it — rigs reward
            // killzones, non-stacking like every aura.
            if (GameManager.Instance != null && bounty > 0)
            {
                int payout = Mathf.RoundToInt(
                    bounty * Corehold.Towers.SalvageRig.BountyMultiplierAt(HitPoint));
                GameManager.Instance.AddKillSalvage(payout, HitPoint);
            }
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
