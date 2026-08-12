using System;
using Corehold.Core;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// A build pad on the Hardpoint layer (GDD §5.3, §12.3, Ticket 22).
    ///
    /// The pad is a fixed, designer-placed slot that holds at most one turret. The
    /// tap collider is a <b>1.5 m sphere</b> on a <b>1.0 m visual</b> — a fingertip
    /// is imprecise and a missed tap on a pad reads as a broken game, so the hit
    /// target is deliberately larger than the art (GDD §5.3).
    ///
    /// The pad drives the economy for the turret it hosts:
    ///   • <see cref="TryBuild"/>  spends the tier-1 cost and places the turret.
    ///   • <see cref="TryUpgrade"/> spends the next tier's cost and upgrades.
    ///   • <see cref="Sell"/>       refunds <b>60% of cumulative invested salvage</b>
    ///                              (GDD §7.3) and clears the pad.
    ///
    /// "Cumulative invested" is tracked here in <see cref="_invested"/> rather than
    /// re-derived from the tier table, because a definition's tier costs are the
    /// costs to REACH each tier, and summing them for the current tier is exactly
    /// the invested total — but tracking it explicitly keeps the refund correct
    /// even if a future feature (discounts, partial refunds mid-build) changes what
    /// was actually paid.
    ///
    /// Visual state (GDD §5.3): the emissive rim pulses gently while the pad is
    /// EMPTY (the primary build-phase call to action) and goes dark once OCCUPIED.
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerHardpoint : MonoBehaviour
    {
        [Header("Tap collider (GDD §5.3)")]
        [Tooltip("Radius of the tap sphere collider in metres. Deliberately larger " +
                 "than the 1.0 m visual so an imprecise fingertip still lands.")]
        [SerializeField] private float tapRadius = 1.5f;

        [Header("Occupancy")]
        [Tooltip("Parent transform the built turret is placed under. Defaults to this " +
                 "hardpoint's transform if left null.")]
        [SerializeField] private Transform turretMount;

        [Header("Emissive rim (GDD §5.3)")]
        [Tooltip("Renderer whose emissive rim pulses when empty and darkens when occupied. " +
                 "Optional — leave null on a pad with no rim mesh.")]
        [SerializeField] private Renderer rimRenderer;

        [Tooltip("Emissive colour of the rim when the pad is empty.")]
        [ColorUsage(false, true)]
        [SerializeField] private Color rimColor = new Color(0f, 0.8f, 1f, 1f);

        [Tooltip("Pulse speed of the empty-pad rim, in cycles per second.")]
        [SerializeField] private float pulseSpeed = 1.2f;

        [Tooltip("Minimum emissive intensity at the bottom of the pulse.")]
        [SerializeField] private float pulseMin = 0.3f;

        [Tooltip("Maximum emissive intensity at the top of the pulse.")]
        [SerializeField] private float pulseMax = 1.4f;

        [Header("Aura glow (GDD §5.3)")]
        [Tooltip("Soft glow halo renderer that surrounds the pad. Pulses (alpha + " +
                 "scale) while the pad is EMPTY to draw the eye, and fades out when " +
                 "OCCUPIED. Optional — leave null on a pad with no aura mesh.")]
        [SerializeField] private Renderer auraRenderer;

        [Tooltip("Base tint of the aura glow.")]
        [ColorUsage(true, true)]
        [SerializeField] private Color auraColor = new Color(0f, 0.8f, 1f, 1f);

        [Tooltip("Minimum aura alpha at the bottom of the pulse.")]
        [SerializeField] private float auraAlphaMin = 0.25f;

        [Tooltip("Maximum aura alpha at the top of the pulse.")]
        [SerializeField] private float auraAlphaMax = 0.75f;

        [Tooltip("How much the aura breathes in scale (0 = none, 0.15 = +/-15%).")]
        [SerializeField] private float auraScalePulse = 0.12f;

        /// <summary>Raised whenever this pad is built on, upgraded or sold. Argument is this pad.</summary>
        public event Action<TowerHardpoint> OnOccupancyChanged;

        private SphereCollider _collider;
        private Tower _occupant;

        // Cumulative salvage actually spent on the current occupant (build + all
        // upgrades). Reset to zero on sell. The 60% refund is computed from this.
        private int _invested;

        // Cached material-property block so the rim pulse never allocates or edits
        // the shared material (GDD §11 — no per-frame garbage).
        private MaterialPropertyBlock _rimBlock;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        // Aura glow state. Cached property block + baseline scale so the pulse never
        // allocates and always breathes around the authored size.
        private MaterialPropertyBlock _auraBlock;
        private Vector3 _auraBaseScale = Vector3.one;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");

        /// <summary>The turret currently on this pad, or null when empty.</summary>
        public Tower Occupant => _occupant;

        /// <summary>True when a turret occupies this pad.</summary>
        public bool IsOccupied => _occupant != null;

        /// <summary>Salvage sunk into the current occupant across build and upgrades.</summary>
        public int Invested => _invested;

        /// <summary>The salvage a sell would return right now: 60% of invested, floored.</summary>
        public int SellValue => Mathf.FloorToInt(_invested * SellRefundFraction);

        /// <summary>The refund fraction on sell (GDD §7.3).</summary>
        public const float SellRefundFraction = 0.60f;

        private void Awake()
        {
            EnsureCollider();
            if (turretMount == null)
                turretMount = transform;
            if (rimRenderer != null)
                _rimBlock = new MaterialPropertyBlock();
            if (auraRenderer != null)
            {
                _auraBlock = new MaterialPropertyBlock();
                _auraBaseScale = auraRenderer.transform.localScale;
            }
        }

        private void OnValidate()
        {
            // Keep the collider in step with the inspector radius in edit mode so a
            // pad's tap target is always correct without entering play.
            EnsureCollider();
        }

        private void Update()
        {
            // The only per-frame work: pulse the empty rim + aura glow. Occupied pads
            // do nothing (their rim/aura were darkened when the turret was placed).
            if (IsOccupied)
                return;

            // 0..1 breathing curve shared by the rim and the aura so they pulse in sync.
            float t = 0.5f * (1f + Mathf.Sin(Time.unscaledTime * pulseSpeed * Mathf.PI * 2f));

            if (rimRenderer != null && _rimBlock != null)
            {
                float intensity = Mathf.Lerp(pulseMin, pulseMax, t);
                SetRimEmission(rimColor * intensity);
            }

            if (auraRenderer != null && _auraBlock != null)
            {
                // Alpha breathes between min and max; the halo also breathes in scale
                // so the glow reads as a living aura rather than a static decal.
                float alpha = Mathf.Lerp(auraAlphaMin, auraAlphaMax, t);
                SetAuraColor(alpha);
                float s = 1f + auraScalePulse * (t - 0.5f) * 2f; // 1 +/- auraScalePulse
                auraRenderer.transform.localScale = _auraBaseScale * s;
            }
        }

        /// <summary>
        /// Build a turret on this pad from its definition (GDD §3.1). Spends the
        /// tier-1 cost through <see cref="GameManager.TrySpend"/> and instantiates the
        /// definition's base prefab. Returns false and changes nothing if the pad is
        /// occupied, the definition is invalid, or salvage is short.
        /// </summary>
        public bool TryBuild(TowerDefinition def)
        {
            if (IsOccupied)
                return false;
            if (def == null || def.tiers == null || def.tiers.Length == 0 || def.basePrefab == null)
                return false;

            int cost = def.tiers[0].cost;

            var gm = GameManager.Instance;
            if (gm == null)
                return false;
            if (!gm.TrySpend(cost))
                return false;

            // Instantiate the turret prefab under the pad's mount at the pad's pose.
            var go = Instantiate(def.basePrefab, turretMount.position, turretMount.rotation, turretMount);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var tower = go.GetComponent<Tower>();
            if (tower == null)
                tower = go.AddComponent<Tower>();

            tower.Build(def, 0);

            _occupant = tower;
            _invested = cost;

            SetRimDark();
            SetAuraDark();
            OnOccupancyChanged?.Invoke(this);
            return true;
        }

        /// <summary>
        /// Upgrade the occupant to its next tier (GDD §3.1, §7.3). Spends the next
        /// tier's cost. Returns false and changes nothing if the pad is empty, the
        /// occupant is already at max tier, or salvage is short.
        /// </summary>
        public bool TryUpgrade()
        {
            if (!IsOccupied)
                return false;

            var def = _occupant.Definition;
            if (def == null || def.tiers == null)
                return false;

            int nextTier = _occupant.TierIndex + 1;
            if (nextTier >= def.tiers.Length)
                return false; // already at max tier

            int cost = def.tiers[nextTier].cost;

            var gm = GameManager.Instance;
            if (gm == null)
                return false;
            if (!gm.TrySpend(cost))
                return false;

            _occupant.SetTier(nextTier);
            _invested += cost;

            OnOccupancyChanged?.Invoke(this);
            return true;
        }

        /// <summary>
        /// Sell the occupant, refunding 60% of cumulative invested salvage (GDD §7.3)
        /// with no cooldown, and clear the pad. Does nothing if empty.
        /// </summary>
        public void Sell()
        {
            if (!IsOccupied)
                return;

            int refund = SellValue;

            // Tear the turret down: Tower.Sell deregisters it and recomputes auras,
            // then we destroy the object so the pad is genuinely free.
            var tower = _occupant;
            _occupant = null;
            _invested = 0;

            tower.Sell();
            Destroy(tower.gameObject);

            var gm = GameManager.Instance;
            if (gm != null && refund > 0)
                gm.AddSalvage(refund);

            OnOccupancyChanged?.Invoke(this);
            // Rim resumes pulsing automatically in Update now that the pad is empty.
        }

        /// <summary>
        /// True when the current occupant can still be upgraded (not at max tier).
        /// Used by UI to grey out the upgrade action.
        /// </summary>
        public bool CanUpgrade =>
            IsOccupied &&
            _occupant.Definition != null &&
            _occupant.Definition.tiers != null &&
            _occupant.TierIndex + 1 < _occupant.Definition.tiers.Length;

        /// <summary>
        /// Called by <see cref="TowerHealth"/> when the occupant is destroyed by
        /// enemy fire. Clears the pad so it becomes buildable again and resumes the
        /// empty-pad rim pulse. The turret GameObject destroys itself separately.
        /// </summary>
        public void NotifyOccupantDestroyed()
        {
            if (_occupant == null)
                return;
            _occupant = null;
            _invested = 0;
            OnOccupancyChanged?.Invoke(this);
            // Rim resumes pulsing automatically in Update now that the pad is empty.
        }

        /// <summary>
        /// Ensure this pad has a turret mount transform (used by runtime bootstrap when
        /// the scene pad was authored without one). Only assigns if currently unset or
        /// pointing at the pad itself.
        /// </summary>
        public void EnsureMount(Transform mount)
        {
            if (mount == null)
                return;
            if (turretMount == null || turretMount == transform)
                turretMount = mount;
        }

        /// <summary>
        /// Assign the emissive rim renderer at runtime/edit time (used by scene setup
        /// tooling to wire a generated pad marker). Initialises the property block so
        /// the pulse works immediately.
        /// </summary>
        public void SetRimRenderer(Renderer r)
        {
            rimRenderer = r;
            if (rimRenderer != null && _rimBlock == null)
                _rimBlock = new MaterialPropertyBlock();
        }

        /// <summary>Cost to reach the next tier, or -1 when the pad is empty or maxed.</summary>
        public int NextUpgradeCost
        {
            get
            {
                if (!CanUpgrade)
                    return -1;
                return _occupant.Definition.tiers[_occupant.TierIndex + 1].cost;
            }
        }

        private void EnsureCollider()
        {
            _collider = GetComponent<SphereCollider>();
            if (_collider == null)
                _collider = gameObject.AddComponent<SphereCollider>();
            _collider.radius = tapRadius;
            _collider.isTrigger = true; // pads are tap targets, not physical blockers
        }

        private void SetRimDark()
        {
            SetRimEmission(Color.black);
        }

        private void SetRimEmission(Color emission)
        {
            if (rimRenderer == null || _rimBlock == null)
                return;
            rimRenderer.GetPropertyBlock(_rimBlock);
            _rimBlock.SetColor(EmissionColorId, emission);
            rimRenderer.SetPropertyBlock(_rimBlock);
        }

        /// <summary>
        /// Assign the aura glow renderer at runtime/edit time (used by scene setup
        /// tooling to wire a generated halo). Initialises the property block and
        /// caches the baseline scale so the breathing pulse works immediately.
        /// </summary>
        public void SetAuraRenderer(Renderer r)
        {
            auraRenderer = r;
            if (auraRenderer != null)
            {
                if (_auraBlock == null)
                    _auraBlock = new MaterialPropertyBlock();
                _auraBaseScale = auraRenderer.transform.localScale;
            }
        }

        /// <summary>Override the aura tint (used by scene tooling to tint per pad class).</summary>
        public void SetAuraColor(Color color)
        {
            auraColor = color;
        }

        // Applies the aura tint at the given alpha via a property block (writes both
        // _BaseColor/_Color for URP/legacy and _TintColor for additive-particle
        // shaders so the glow shows regardless of the material's shader).
        private void SetAuraColor(float alpha)
        {
            if (auraRenderer == null || _auraBlock == null)
                return;
            // Scale RGB by alpha too so an additive-blended glow visibly breathes
            // (additive ignores alpha), while alpha still drives alpha-blend glows.
            Color c = auraColor * alpha;
            c.a = alpha;
            auraRenderer.GetPropertyBlock(_auraBlock);
            _auraBlock.SetColor(BaseColorId, c);
            _auraBlock.SetColor(ColorId, c);
            _auraBlock.SetColor(EmissionColorId, auraColor * alpha);
            _auraBlock.SetColor(TintColorId, c);
            auraRenderer.SetPropertyBlock(_auraBlock);
        }

        private void SetAuraDark()
        {
            if (auraRenderer == null || _auraBlock == null)
                return;
            SetAuraColor(0f);
            auraRenderer.transform.localScale = _auraBaseScale;
        }
    }
}
