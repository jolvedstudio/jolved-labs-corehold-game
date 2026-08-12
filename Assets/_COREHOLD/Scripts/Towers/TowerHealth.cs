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

        /// <summary>Current health.</summary>
        public float CurrentHealth => _current;

        /// <summary>Maximum health.</summary>
        public float MaxHealth => maxHealth;

        /// <summary>Fraction of health remaining, 0..1.</summary>
        public float HealthFraction => maxHealth > 0f ? Mathf.Clamp01(_current / maxHealth) : 0f;

        /// <summary>Raised when health changes. Args: current, max.</summary>
        public event Action<float, float> OnHealthChanged;

        /// <summary>Raised once when the turret is destroyed.</summary>
        public event Action<TowerHealth> OnDestroyedByDamage;

        private void OnEnable()
        {
            _current = maxHealth;
            _dead = false;
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

            _current -= amount;
            if (_current < 0f)
                _current = 0f;

            OnHealthChanged?.Invoke(_current, maxHealth);

            if (_current <= 0f)
                Die();
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
