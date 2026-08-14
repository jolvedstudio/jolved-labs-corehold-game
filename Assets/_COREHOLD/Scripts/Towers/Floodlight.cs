using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// The Floodlight's lit-area registry (R24). A sixth buildable that projects a
    /// circle of light; anything standing inside any floodlight's circle is LIT,
    /// which is what the Blackout mutator (R20) checks at turret acquisition — a
    /// lit unit is seen at full range, an unlit one counts distance double.
    ///
    /// Follows the <see cref="SupportAura"/> pattern exactly: a component on the
    /// tower prefab, a static registry populated in OnEnable/OnDisable, and no
    /// per-frame recompute — <see cref="IsLit"/> is a query over the registry,
    /// called only for units that actually carry a Blackout stamp. The light
    /// radius is the tier's <c>auraRadius</c> (12 m shipped), read live like the
    /// relay reads its aura, so a future tiered floodlight needs no code change.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Tower))]
    public class Floodlight : MonoBehaviour
    {
        /// <summary>Registry of every active floodlight (mirrors <see cref="SupportAura.Relays"/>).</summary>
        public static readonly List<Floodlight> Lights = new List<Floodlight>();

        private Tower _tower;

        /// <summary>Lit-circle radius in metres, sourced from the current tier's auraRadius.</summary>
        public float LightRadius =>
            _tower != null && _tower.HasTier ? _tower.CurrentTier.auraRadius : 0f;

        private void Awake()
        {
            _tower = GetComponent<Tower>();
        }

        private void OnEnable()
        {
            if (!Lights.Contains(this))
                Lights.Add(this);
        }

        private void OnDisable()
        {
            // Selling deactivates the tower, which lands here — the lit area dies
            // with the light, no recompute pass needed.
            Lights.Remove(this);
        }

        /// <summary>
        /// True when <paramref name="worldPos"/> lies inside any floodlight's lit
        /// circle. Planar XZ, so a Wasp above a floodlight is lit too — the beam
        /// is a column, same convention as the Strike Wing burst (R19).
        /// </summary>
        public static bool IsLit(Vector3 worldPos)
        {
            for (int i = 0; i < Lights.Count; i++)
            {
                Floodlight f = Lights[i];
                if (f == null)
                    continue;

                float r = f.LightRadius;
                if (r <= 0f)
                    continue;

                Vector3 d = f.transform.position - worldPos;
                d.y = 0f;
                if (d.sqrMagnitude <= r * r)
                    return true;
            }
            return false;
        }

        private void OnDrawGizmosSelected()
        {
            float radius = LightRadius;
            if (radius <= 0f && _tower == null)
            {
                var t = GetComponent<Tower>();
                radius = t != null && t.HasTier ? t.CurrentTier.auraRadius : 0f;
            }
            if (radius <= 0f)
                return;

            Gizmos.color = new Color(1f, 0.9f, 0.5f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
