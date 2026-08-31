using System;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Health for a placed turret so enemies can fire back and destroy it. When
    /// health reaches zero the turret is destroyed and its hardpoint is freed so the
    /// player can rebuild. Added automatically by <see cref="Tower.Build"/> if the
    /// prefab does not already carry one.
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerHealth : MonoBehaviour
    {
        [Tooltip("Maximum (and starting) health of the turret.")]
        [SerializeField] private float maxHealth = 220f;

        private float _current;
        private bool _dead;

        // Resolved lazily, and ONLY while a debug immortality rule is active —
        // the shipped path never touches it.
        private Tower _tower;

        // --- Shield (damage-absorbing barrier, authored per tier on TowerTier) ------
        // A shield soaks hits BEFORE health; overflow from a hit that empties it
        // carries through to health. It regenerates at _shieldRegenPerSec once the
        // turret has gone _shieldRegenDelay seconds without being hit.
        private float _shieldMax;
        private float _shieldCurrent;
        private float _shieldRegenPerSec;
        private float _shieldRegenDelay;
        private float _lastHitTime;

        /// <summary>Current health.</summary>
        public float CurrentHealth => _current;

        /// <summary>Maximum health.</summary>
        public float MaxHealth => maxHealth;

        /// <summary>Fraction of health remaining, 0..1.</summary>
        public float HealthFraction => maxHealth > 0f ? Mathf.Clamp01(_current / maxHealth) : 0f;

        /// <summary>Max shield points (0 = this turret has no shield).</summary>
        public float ShieldMax => _shieldMax;

        /// <summary>Current shield points.</summary>
        public float ShieldCurrent => _shieldCurrent;

        /// <summary>True when this turret has a shield and it currently holds charge.</summary>
        public bool ShieldActive => _shieldMax > 0f && _shieldCurrent > 0f;

        /// <summary>Fraction of shield remaining, 0..1 (0 when no shield).</summary>
        public float ShieldFraction => _shieldMax > 0f ? Mathf.Clamp01(_shieldCurrent / _shieldMax) : 0f;

        /// <summary>Raised when health changes. Args: current, max.</summary>
        public event Action<float, float> OnHealthChanged;

        /// <summary>Raised when the shield value changes. Args: current, max.</summary>
        public event Action<float, float> OnShieldChanged;

        /// <summary>Raised once when the turret is destroyed.</summary>
        public event Action<TowerHealth> OnDestroyedByDamage;

        private void OnEnable()
        {
            _current = maxHealth;
            _dead = false;
            _shieldCurrent = _shieldMax;
            _lastHitTime = -9999f;
        }

        /// <summary>
        /// Configure the shield from the current tier's authored values (called by
        /// <see cref="Tower.Build"/> and <see cref="Tower.SetTier"/>). Refills the
        /// shield to the new maximum so an upgrade to a shielded tier reads as fully
        /// charged. A zero maximum simply means "no shield".
        /// </summary>
        public void ConfigureShield(float shieldMax, float regenPerSec, float regenDelay)
        {
            _shieldMax = Mathf.Max(0f, shieldMax);
            _shieldRegenPerSec = Mathf.Max(0f, regenPerSec);
            _shieldRegenDelay = Mathf.Max(0f, regenDelay);
            _shieldCurrent = _shieldMax;
            NotifyShieldChanged();
        }

        private void Update()
        {
            if (_dead || _shieldMax <= 0f || _shieldRegenPerSec <= 0f)
                return;
            if (_shieldCurrent >= _shieldMax)
                return;
            if (Time.time - _lastHitTime < _shieldRegenDelay)
                return;

            _shieldCurrent = Mathf.Min(_shieldMax, _shieldCurrent + _shieldRegenPerSec * Time.deltaTime);
            NotifyShieldChanged();
        }

        /// <summary>Set the max health and refill (e.g. scaled per tier).</summary>
        public void SetMaxHealth(float value)
        {
            maxHealth = Mathf.Max(1f, value);
            _current = maxHealth;
            OnHealthChanged?.Invoke(_current, maxHealth);
        }

        /// <summary>Restore to full health (e.g. after an upgrade).</summary>
        public void Heal()
        {
            _current = maxHealth;
            _dead = false;
            OnHealthChanged?.Invoke(_current, maxHealth);
        }

        /// <summary>Apply damage. Destroys the turret when health reaches zero.</summary>
        public void TakeDamage(float amount)
        {
            if (_dead || amount <= 0f)
                return;

            // Debug immortality by tower TYPE (testing aid). The shot was still
            // fired, aimed and landed — only the health subtraction is skipped,
            // so enemy behaviour and DPS-on-target stay exactly as they are in a
            // real run. One bool test when no rule is active.
            if (TowerImmortality.Any)
            {
                if (_tower == null)
                    _tower = GetComponent<Tower>();
                if (_tower != null && TowerImmortality.IsImmortal(_tower.Definition))
                    return;
            }

            _lastHitTime = Time.time;

            // Shield absorbs first (GDD §7). A hit that empties the shield lets the
            // OVERFLOW carry through to health, so a big hit is not fully wasted on a
            // sliver of shield.
            if (_shieldCurrent > 0f)
            {
                float absorbed = Mathf.Min(_shieldCurrent, amount);
                _shieldCurrent -= absorbed;
                amount -= absorbed;
                NotifyShieldChanged();
                if (amount <= 0f)
                    return;
            }

            _current -= amount;
            if (_current < 0f)
                _current = 0f;

            OnHealthChanged?.Invoke(_current, maxHealth);

            if (_current <= 0f)
                Die();
        }

        private void NotifyShieldChanged()
        {
            OnShieldChanged?.Invoke(_shieldCurrent, _shieldMax);
            // Drive the visible shell: show while the barrier holds charge, hide when
            // it is depleted or absent (VFX Tier 1 — tower shield read).
            if (_tower == null)
                _tower = GetComponent<Tower>();
            if (_tower != null)
                TowerShield.Refresh(_tower, ShieldActive);
        }

        private void Die()
        {
            if (_dead)
                return;
            _dead = true;

            OnDestroyedByDamage?.Invoke(this);
            StartCoroutine(DieSequence());
        }

        /// <summary>
        /// Plays a visible destruction explosion (staged blasts + shake), hides the
        /// turret body, then frees the pad and removes the turret. The turret clearly
        /// "explodes before it dies" rather than blinking out.
        /// </summary>
        private System.Collections.IEnumerator DieSequence()
        {
            Vector3 p = transform.position;
            var vfx = Corehold.Systems.VFXDirector.Instance;

            if (vfx != null)
            {
                vfx.PlayExplosion(p + Vector3.up * 1.0f, Corehold.Systems.VFXDirector.LargeSplashThreshold + 2f);
                vfx.PlayExplosion(p + Vector3.up * 2.5f, Corehold.Systems.VFXDirector.LargeSplashThreshold + 2f);
            }
            if (Corehold.Systems.CameraShake.Instance != null)
                Corehold.Systems.CameraShake.Instance.ShakeFootfall();

            // Hide visuals immediately so it reads as destroyed while the FX plays.
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            yield return new WaitForSeconds(0.15f);
            if (vfx != null)
                vfx.PlayExplosion(p + Vector3.up * 1.8f, Corehold.Systems.VFXDirector.LargeSplashThreshold + 3f);

            yield return new WaitForSeconds(0.2f);

            var pad = GetOwningHardpoint();
            var tower = GetComponent<Tower>();
            if (tower != null)
                tower.Sell(); // deregisters from Tower.Live and recomputes auras

            if (pad != null)
                pad.NotifyOccupantDestroyed();

            Destroy(gameObject);
        }

        private TowerHardpoint GetOwningHardpoint()
        {
            return GetComponentInParent<TowerHardpoint>();
        }
    }
}
