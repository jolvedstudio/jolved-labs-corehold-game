using Corehold.Core;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// Movable turrets (M-c). Two modes by game state, one flow for the player:
    /// open a turret's panel, press MOVE, tap a free pad.
    ///
    ///   • BUILD phase — the move is instant and FREE, and tier/veterancy/
    ///     invested value all carry. This deliberately waives the 60% sell tax:
    ///     newcomers' biggest early frustration is "I built wrong and now I'm
    ///     punished", and the R21 veterancy investment made rebuilding even
    ///     more punishing than the refund suggests.
    ///   • WAVE phase — the turret WALKS: it lifts off, travels pad-to-pad with
    ///     its weapons offline, and plants at the destination. The downtime IS
    ///     the price, which keeps the balance model a lower bound — a mid-wave
    ///     move can only trade DPS away, never mint it, so certified margins
    ///     stay honest without a model term.
    ///
    /// Pads stay the geometry the gates certified: turrets move BETWEEN pads,
    /// never to free ground — free-form placement would dissolve the coverage
    /// gates and the model's whole geometric basis.
    /// </summary>
    public static class TurretRelocation
    {
        /// <summary>The pad whose occupant is awaiting a destination tap, if any.</summary>
        public static TowerHardpoint Source { get; private set; }

        public static bool Pending => Source != null && Source.IsOccupied;

        /// <summary>[TUNE] Walk speed for mid-wave transits, metres/second.</summary>
        public const float TransitSpeed = 5.5f;

        /// <summary>[TUNE] Hop height of the transit arc, metres.</summary>
        public const float TransitArc = 1.6f;

        public static void Begin(TowerHardpoint pad)
        {
            Source = pad != null && pad.IsOccupied ? pad : null;
        }

        public static void Cancel() => Source = null;

        /// <summary>
        /// Complete the pending move onto <paramref name="dest"/>. Instant in
        /// Build, a transit in Wave. Returns false when nothing was pending or
        /// the destination cannot take a turret.
        /// </summary>
        public static bool TryCompleteAt(TowerHardpoint dest)
        {
            if (!Pending || dest == null || dest == Source || dest.IsOccupied || dest.IsReserved)
                return false;

            var src = Source;
            Source = null;

            if (!src.DetachForRelocation(out Tower tower, out int invested))
                return false;

            var gm = GameManager.Instance;
            bool instant = gm == null || gm.State != GameState.Wave;
            if (instant)
            {
                dest.ReceiveRelocated(tower, invested);
                return true;
            }

            dest.SetReserved(true);
            var transit = tower.gameObject.AddComponent<TurretTransit>();
            transit.Launch(tower, dest, invested);
            return true;
        }
    }

    /// <summary>
    /// A turret in flight between pads: weapons offline, an arced hover from
    /// mount to mount, then plant-and-resume. Added at launch, removes itself
    /// on arrival. If the destination is lost mid-flight (shouldn't happen —
    /// it is reserved) the turret plants wherever it is and self-sells safe.
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretTransit : MonoBehaviour
    {
        private Tower _tower;
        private TowerHardpoint _dest;
        private int _invested;
        private Vector3 _from;
        private float _t;
        private float _duration;

        private Behaviour[] _suspended;

        public void Launch(Tower tower, TowerHardpoint dest, int invested)
        {
            _tower = tower;
            _dest = dest;
            _invested = invested;

            var t = tower.transform;
            t.SetParent(null, true);
            _from = t.position;
            float dist = Vector3.Distance(_from, dest.transform.position);
            _duration = Mathf.Max(0.5f, dist / TurretRelocation.TransitSpeed);
            _t = 0f;

            // Weapons offline for the whole flight — the transit's honesty.
            _suspended = new Behaviour[]
            {
                tower.GetComponent<TowerWeapon>(),
                tower.GetComponent<TowerTargeting>(),
                tower.GetComponent<TurretAim>(),
            };
            foreach (var b in _suspended)
                if (b != null) b.enabled = false;
        }

        private void Update()
        {
            if (_dest == null)
            {
                Finish();
                return;
            }

            _t += Time.deltaTime / _duration;
            float k = Mathf.Clamp01(_t);
            Vector3 to = _dest.transform.position;
            Vector3 pos = Vector3.Lerp(_from, to, k);
            pos.y += Mathf.Sin(k * Mathf.PI) * TurretRelocation.TransitArc;
            transform.position = pos;

            // Lean into the travel direction so the hop reads as motion, not a slide.
            Vector3 dir = to - _from; dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(dir.normalized) * Quaternion.Euler(6f, 0f, 0f),
                    Time.deltaTime * 4f);

            if (k >= 1f)
                Finish();
        }

        private void Finish()
        {
            foreach (var b in _suspended)
                if (b != null) b.enabled = true;

            if (_dest != null)
            {
                if (_tower != null)
                    _dest.ReceiveRelocated(_tower, _invested);
                else
                    _dest.SetReserved(false); // turret died in flight — free the pad
            }

            Destroy(this);
        }
    }
}
