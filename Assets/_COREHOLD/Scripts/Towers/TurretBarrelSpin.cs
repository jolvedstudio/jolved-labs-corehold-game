using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Adds a firing "kick" (recoil) to a turret's barrel: when the weapon fires the
    /// barrel snaps back along its local -Z and springs smoothly forward to rest.
    /// It ONLY moves while firing — at rest there is no motion, so the animation
    /// never plays when the turret is inactive.
    ///
    /// If no barrel transform is assigned it auto-resolves to the TurretAim pitch
    /// pivot (the part that elevates), so it works on the existing prefabs without
    /// manual wiring. <see cref="TowerWeapon"/> calls <see cref="NotifyFired"/> on
    /// every shot.
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretBarrelSpin : MonoBehaviour
    {
        [Tooltip("Transform that kicks back on fire. If null, resolves to the TurretAim pitch pivot.")]
        [SerializeField] private Transform barrel;

        [Tooltip("How far (metres) the barrel kicks back on each shot.")]
        [SerializeField] private float recoilDistance = 0.18f;

        [Tooltip("How quickly the barrel springs back to rest (higher = snappier).")]
        [SerializeField] private float recoverSpeed = 12f;

        private Vector3 _restLocalPos;
        private float _recoil;      // current recoil offset magnitude along -Z
        private bool _resolved;

        public Transform Barrel { get => barrel; set { barrel = value; _resolved = false; } }

        private void OnEnable()
        {
            Resolve();
        }

        private void Resolve()
        {
            if (barrel == null)
            {
                var aim = GetComponent<TurretAim>();
                if (aim != null)
                    barrel = aim.PitchPivot != null ? aim.PitchPivot : aim.YawPivot;
            }
            if (barrel != null)
                _restLocalPos = barrel.localPosition;
            _resolved = barrel != null;
        }

        /// <summary>Call on every shot to kick the barrel back.</summary>
        public void NotifyFired()
        {
            if (!_resolved)
                Resolve();
            _recoil = recoilDistance;
        }

        private void Update()
        {
            if (!_resolved || barrel == null)
                return;

            // No firing => no motion (barrel sits exactly at rest).
            if (_recoil <= 0.0001f)
            {
                if (barrel.localPosition != _restLocalPos)
                    barrel.localPosition = _restLocalPos;
                return;
            }

            _recoil = Mathf.MoveTowards(_recoil, 0f, recoverSpeed * recoilDistance * Time.deltaTime);
            barrel.localPosition = _restLocalPos + Vector3.back * _recoil;
        }
    }
}
