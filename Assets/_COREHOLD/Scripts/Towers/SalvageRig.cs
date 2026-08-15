using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The Salvage Rig (roster expansion): a support tower that boosts the
    /// bounty of every kill landed inside its radius. Registry + static query,
    /// the SupportAura/Floodlight pattern; radius rides the tier's auraRadius.
    ///
    /// NON-STACKING like every aura in this game: overlapping rigs pay the
    /// STRONGEST single bonus, never the sum. The hook lives in
    /// <see cref="Corehold.Enemies.Enemy"/>'s death payout, keyed on where the
    /// enemy DIED — a rig rewards killzones, not tower positions.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Tower))]
    public class SalvageRig : MonoBehaviour
    {
        /// <summary>Registry of active rigs (mirrors SupportAura.Relays).</summary>
        public static readonly List<SalvageRig> Rigs = new List<SalvageRig>();

        [Tooltip("[TUNE] Bounty bonus per tier index (0.15 = +15% salvage on kills in radius).")]
        [SerializeField] private float[] bountyBonusPerTier = { 0.15f, 0.20f, 0.25f };

        private Tower _tower;

        private float Radius =>
            _tower != null && _tower.HasTier ? _tower.CurrentTier.auraRadius : 0f;

        private float Bonus
        {
            get
            {
                if (_tower == null || bountyBonusPerTier == null || bountyBonusPerTier.Length == 0)
                    return 0.15f;
                int i = Mathf.Clamp(_tower.TierIndex, 0, bountyBonusPerTier.Length - 1);
                return bountyBonusPerTier[i];
            }
        }

        private void Awake()
        {
            _tower = GetComponent<Tower>();
        }

        private void OnEnable()
        {
            if (!Rigs.Contains(this))
                Rigs.Add(this);
        }

        private void OnDisable()
        {
            Rigs.Remove(this);
        }

        /// <summary>
        /// Bounty multiplier for a kill at <paramref name="worldPos"/>: 1 when no
        /// rig covers it, else 1 + the strongest single rig's bonus (planar XZ).
        /// </summary>
        public static float BountyMultiplierAt(Vector3 worldPos)
        {
            float best = 0f;
            for (int i = 0; i < Rigs.Count; i++)
            {
                SalvageRig rig = Rigs[i];
                if (rig == null)
                    continue;
                float r = rig.Radius;
                if (r <= 0f)
                    continue;
                Vector3 d = rig.transform.position - worldPos;
                d.y = 0f;
                if (d.sqrMagnitude > r * r)
                    continue;
                if (rig.Bonus > best)
                    best = rig.Bonus;
            }
            return 1f + best;
        }

        private void OnDrawGizmosSelected()
        {
            var t = _tower != null ? _tower : GetComponent<Tower>();
            float r = t != null && t.HasTier ? t.CurrentTier.auraRadius : 0f;
            if (r <= 0f)
                return;
            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, r);
        }
    }
}
