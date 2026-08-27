using System;
using System.Collections.Generic;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The runtime identity and tier state of a single placed turret (GDD §12.4).
    /// It is the coordination point between the definition, the weapon and the
    /// support-aura system.
    ///
    /// Effective stats are exposed as COMPUTED PROPERTIES, never cached fields
    /// (GDD §7.3): <see cref="EffectiveRange"/>, <see cref="EffectiveFireRate"/> and
    /// <see cref="EffectiveDamage"/> read the base tier number and multiply it by
    /// the <see cref="CurrentModifiers"/> struct on every access. Recomputing three
    /// floats is deliberately cheaper than the class of bugs where a relay is sold
    /// and a stale cached buff survives on a tower it no longer covers.
    ///
    /// Buffs arrive only through <see cref="SetModifiers"/>, which a
    /// <see cref="SupportAura"/> calls on build, upgrade and sell — the three
    /// discrete events, never per frame. When the modifier actually changes, the
    /// tower pushes its new effective range down to <see cref="TowerTargeting"/> so
    /// acquisition stays consistent; the weapon reads effective damage and fire rate
    /// live, so those need no push.
    ///
    /// Every live tower registers in <see cref="Live"/> so <see cref="SupportAura"/>
    /// can find its neighbours without physics queries, mirroring the enemy registry
    /// pattern (GDD §7.4).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TowerWeapon))]
    public class Tower : MonoBehaviour
    {
        /// <summary>
        /// Registry of every tower currently placed and active in the scene.
        /// <see cref="SupportAura"/> iterates this to find towers in its radius.
        /// Populated in OnEnable, removed in OnDisable.
        /// </summary>
        public static readonly List<Tower> Live = new List<Tower>();

        /// <summary>
        /// Raised whenever ANY tower is built, upgraded or sold (GDD §7.3). Every
        /// <see cref="SupportAura"/> listens and recomputes the towers it covers.
        /// This is the ONLY trigger for an aura refresh — there is no per-frame path.
        /// The argument is the tower that changed (may be null on a sell, since the
        /// selling tower is already gone).
        /// </summary>
        public static event Action<Tower> OnRosterChanged;

        [Header("Definition (GDD §7.3)")]
        [Tooltip("The turret this instance represents. Drives all base tuning.")]
        [SerializeField] private TowerDefinition definition;

        [Tooltip("Zero-based tier index (0 = tier 1, 2 = tier 3).")]
        [SerializeField] private int tierIndex;

        private TowerWeapon _weapon;
        private TowerTargeting _targeting;

        // The current external buff. Replaced wholesale by SetModifiers; never
        // mutated per frame. Default = TowerModifiers.None (unbuffed).
        private TowerModifiers _modifiers = TowerModifiers.None;

        /// <summary>The definition backing this tower.</summary>
        public TowerDefinition Definition => definition;

        /// <summary>Current zero-based tier index.</summary>
        public int TierIndex => tierIndex;

        /// <summary>True when a definition with a valid tier is assigned.</summary>
        public bool HasTier =>
            definition != null &&
            definition.tiers != null &&
            tierIndex >= 0 &&
            tierIndex < definition.tiers.Length;

        /// <summary>The active tier data (GDD §7.3). Only valid when <see cref="HasTier"/>.</summary>
        public TowerTier CurrentTier => definition.tiers[tierIndex];

        /// <summary>
        /// The buff currently applied to this tower. Read-only to everyone but
        /// <see cref="SetModifiers"/>; represents the STRONGEST single relay under
        /// the non-stacking rule, not a sum (GDD §7.3).
        /// </summary>
        public TowerModifiers CurrentModifiers => _modifiers;

        /// <summary>The damage type this tower deals (GDD §7.1).</summary>
        public DamageType DamageType => definition != null ? definition.damageType : DamageType.Kinetic;

        /// <summary>
        /// True when this tower is a support relay — it grants an aura and deals no
        /// damage (GDD §7.2). Detected from the tier having a positive aura radius.
        /// </summary>
        public bool IsSupportRelay => HasTier && CurrentTier.auraRadius > 0f;

        // -- Base (unbuffed) tier stats ------------------------------------------------

        /// <summary>Base range from the current tier, before any aura (metres).</summary>
        public float BaseRange => HasTier ? CurrentTier.range : 0f;

        /// <summary>
        /// Base fire rate from the current tier, before any aura (shots/s). Summed
        /// across every mounted weapon so multi-weapon turrets report their true rate.
        /// </summary>
        public float BaseFireRate => HasTier ? CurrentTier.TotalFireRate : 0f;

        /// <summary>
        /// Base damage per volley from the current tier, before any aura. Summed
        /// across every mounted weapon so multi-weapon turrets report their true output.
        /// </summary>
        public float BaseDamage => HasTier ? CurrentTier.TotalDamagePerVolley : 0f;

        // -- Effective (buffed) stats — COMPUTED, never cached (GDD §7.3) --------------

        /// <summary>Base range × (1 + aura range bonus) × the level's certified
        /// range tuning. Recomputed on every read.</summary>
        public float EffectiveRange => BaseRange * (1f + _modifiers.rangeBonus) * TowerTuning.RangeMult;

        /// <summary>Base fire rate × (1 + aura fire-rate bonus). Recomputed on every read.</summary>
        public float EffectiveFireRate => BaseFireRate * (1f + _modifiers.fireRateBonus);

        /// <summary>
        /// Additive damage-bonus fraction from terrain high ground (M-b), read
        /// from the hosting pad on EVERY access — the same no-cache doctrine as
        /// the aura stats, so a relocated turret (M-c) can never carry its old
        /// pad's bonus. 0 off-pad (mid-transit, with weapons offline) and on
        /// every flat map.
        /// </summary>
        public float HighGroundBonus
        {
            get
            {
                var pad = GetComponentInParent<TowerHardpoint>();
                return pad != null ? pad.HighGroundBonus : 0f;
            }
        }

        /// <summary>Base damage × (1 + aura bonus + high ground) × veterancy (R21/M-b)
        /// × the level's certified damage tuning. Recomputed on every read.</summary>
        public float EffectiveDamage =>
            BaseDamage * (1f + _modifiers.damageBonus + HighGroundBonus) * VeterancyDamageMultiplier
            * TowerTuning.DamageMult;

        /// <summary>
        /// True damage-per-second including aura buffs, terrain high ground and
        /// veterancy, summed per mount so a multi-weapon turret is not
        /// overstated. Recomputed on every read.
        /// </summary>
        public float EffectiveDps => HasTier
            ? CurrentTier.TotalDps * (1f + _modifiers.damageBonus + HighGroundBonus)
              * (1f + _modifiers.fireRateBonus) * VeterancyDamageMultiplier
              * TowerTuning.DamageMult
            : 0f;

        // ----- Veterancy (R21) -----

        // [TUNE] Career kills required for ranks 1..3, and damage added per rank.
        private static readonly int[] VeterancyKillThresholds = { 25, 75, 150 };
        private const float VeterancyDamagePerRank = 0.04f;

        /// <summary>Career kills credited to this instance (R21). Reset by <see cref="Build"/>.</summary>
        public int KillCount { get; private set; }

        /// <summary>
        /// Veterancy rank 0–3 (R21). Rank survives upgrades (same instance, same
        /// career) and dies with a sell — <see cref="Build"/> resets it, so a
        /// rebuilt pad starts green. Stored per-instance, never in the definition.
        /// </summary>
        public int VeterancyRank { get; private set; }

        /// <summary>Damage multiplier from rank (R21): 1 + 0.04 × rank.</summary>
        public float VeterancyDamageMultiplier => 1f + VeterancyDamagePerRank * VeterancyRank;

        /// <summary>
        /// Credit one kill (called by the weapon / projectile that landed the
        /// killing hit — R21). Rank-ups fire here; the OverlayManager draws the
        /// chevrons by polling <see cref="VeterancyRank"/>.
        /// </summary>
        public void RegisterKill()
        {
            KillCount++;

            int rank = VeterancyRank;
            while (rank < VeterancyKillThresholds.Length && KillCount >= VeterancyKillThresholds[rank])
                rank++;
            if (rank == VeterancyRank)
                return;

            VeterancyRank = rank;

            // Rank-up beat: the pooled build puff over the turret head (GDD §11 —
            // no new effect slot for a mechanic-first ticket) plus a log line.
            if (Corehold.Systems.VFXDirector.Instance != null)
                Corehold.Systems.VFXDirector.Instance.PlayBuildPuff(transform.position + Vector3.up * 2f);
            Debug.Log($"[Corehold] {name} reached veterancy rank {VeterancyRank} " +
                      $"({KillCount} kills, +{VeterancyDamagePerRank * VeterancyRank:P0} damage).");
        }

        private void Awake()
        {
            _weapon = GetComponent<TowerWeapon>();
            _targeting = GetComponent<TowerTargeting>();
            SyncWeaponTier();
        }

        private void OnEnable()
        {
            if (!Live.Contains(this))
                Live.Add(this);
        }

        private void OnDisable()
        {
            Live.Remove(this);
        }

        private void OnValidate()
        {
            if (definition != null && definition.tiers != null)
                tierIndex = Mathf.Clamp(tierIndex, 0, Mathf.Max(0, definition.tiers.Length - 1));
        }

        /// <summary>
        /// Place this tower: assign its definition at a starting tier, register it,
        /// and announce the roster change so every aura recomputes (GDD §7.3, build
        /// event). Call this from the build flow rather than setting fields directly.
        /// </summary>
        public void Build(TowerDefinition def, int tier = 0)
        {
            definition = def;
            tierIndex = ClampTier(tier);
            SyncWeaponTier();

            // Ensure the turret has health so enemies can fire back and destroy it.
            var towerHealth = GetComponent<TowerHealth>();
            if (towerHealth == null)
                towerHealth = gameObject.AddComponent<TowerHealth>();

            // Attach a self-contained world-space health bar (billboarded, always on).
            if (GetComponentInChildren<Corehold.UI.WorldHealthBar>() == null)
            {
                var h = towerHealth;
                Corehold.UI.WorldHealthBar.Attach(
                    gameObject,
                    () => h != null ? h.HealthFraction : 0f,
                    6.8f, 2.4f, 0.34f,
                    new Color(0.35f, 0.8f, 1f, 1f));
            }

            // Ensure this tower is in the registry even if OnEnable has not run yet
            // (e.g. built the same frame it is created, or driven from edit mode).
            if (!Live.Contains(this))
                Live.Add(this);

            // A freshly built tower starts unbuffed; the aura pass below will apply
            // any coverage it deserves.
            _modifiers = TowerModifiers.None;

            // A build is a NEW purchase: veterancy never carries over (R21 —
            // selling forfeits rank, and this reset also covers any future
            // instance pooling). Upgrades go through SetTier and keep the career.
            KillCount = 0;
            VeterancyRank = 0;

            ApplyEffectiveRange();

            // Build-placement puff at the pad (GDD §11), pooled through the director.
            if (Corehold.Systems.VFXDirector.Instance != null)
                Corehold.Systems.VFXDirector.Instance.PlayBuildPuff(transform.position);

            // Build SFX (GDD §10).
            if (Corehold.Systems.AudioDirector.Instance != null)
                Corehold.Systems.AudioDirector.Instance.PlayBuild();

            NotifyRosterChanged();
        }

        /// <summary>
        /// Upgrade to the next tier (or a specific tier). Re-syncs the weapon and
        /// fires the roster-changed event so auras recompute — including this tower's
        /// own aura if it is a relay whose radius grew (GDD §7.3, upgrade event).
        /// </summary>
        public void Upgrade()
        {
            SetTier(tierIndex + 1);
        }

        /// <summary>Set an explicit tier and announce the change (GDD §7.3, upgrade event).</summary>
        public void SetTier(int tier)
        {
            if (!HasDefinition)
                return;

            tierIndex = ClampTier(tier);
            SyncWeaponTier();
            ApplyEffectiveRange();

            NotifyRosterChanged();
        }

        /// <summary>
        /// Sell this tower: deregister, disable, then announce the roster change so
        /// every remaining aura recomputes and any neighbour it was buffing is
        /// restored (GDD §7.3, sell event). The event fires AFTER removal from the
        /// registry so no aura re-adds a buff from the tower being sold.
        /// </summary>
        public void Sell()
        {
            Live.Remove(this);
            gameObject.SetActive(false);

            // Recompute first so any tower this one was buffing is restored to the
            // strongest REMAINING relay (or unbuffed), then announce to other
            // listeners. Passing null: the selling tower is already gone.
            SupportAura.RecomputeAll();
            OnRosterChanged?.Invoke(null);
        }

        /// <summary>
        /// Replace the current buff (GDD §7.3). Called by <see cref="SupportAura"/>
        /// during a refresh with the STRONGEST single relay's bonus (non-stacking).
        /// If the modifier is unchanged this is a no-op — no range push, no work —
        /// which keeps a refresh over many towers cheap. When it does change, the
        /// new effective range is pushed to targeting; damage and fire rate are read
        /// live by the weapon and need no push.
        /// </summary>
        public void SetModifiers(TowerModifiers modifiers)
        {
            if (_modifiers.Equals(modifiers))
                return;

            _modifiers = modifiers;
            ApplyEffectiveRange();
        }

        /// <summary>Clear any buff back to the identity (used when no relay covers this tower).</summary>
        public void ClearModifiers() => SetModifiers(TowerModifiers.None);

        private bool HasDefinition =>
            definition != null && definition.tiers != null && definition.tiers.Length > 0;

        private int ClampTier(int tier)
        {
            if (!HasDefinition)
                return 0;
            return Mathf.Clamp(tier, 0, definition.tiers.Length - 1);
        }

        private void SyncWeaponTier()
        {
            if (_weapon == null)
                _weapon = GetComponent<TowerWeapon>();
            if (_targeting == null)
                _targeting = GetComponent<TowerTargeting>();

            if (_weapon != null)
            {
                // TowerWeapon owns the definition + tier for its own firing logic and
                // pushes base range into targeting via ApplyTier. Keep it in step.
                _weapon.Configure(definition, tierIndex);
            }

            ApplyEffectiveRange();
        }

        /// <summary>
        /// Push the current effective range into <see cref="TowerTargeting"/> so a
        /// range buff actually widens acquisition. Called whenever the tier or the
        /// modifier changes — the two discrete moments the effective range can move,
        /// never per frame.
        /// </summary>
        private void ApplyEffectiveRange()
        {
            if (_targeting == null)
                _targeting = GetComponent<TowerTargeting>();
            if (_targeting != null && HasTier)
                _targeting.SetEffectiveRange(EffectiveRange);
        }

        private void NotifyRosterChanged()
        {
            // Recompute every aura buff exactly once for this discrete event
            // (build / upgrade / sell), before broadcasting to any other listeners
            // (UI, audio) so they observe already-buffed effective stats. This is the
            // single, non-per-frame recompute trigger the GDD §7.3 mandates.
            SupportAura.RecomputeAll();
            OnRosterChanged?.Invoke(this);
        }
    }
}
