using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Caches the geometric centre of a unit's visible body so weapons aim at centre
    /// mass instead of the transform root (which usually sits at the feet).
    ///
    /// The centre is computed ONCE from the combined bounds of the body's
    /// <see cref="MeshRenderer"/> and <see cref="SkinnedMeshRenderer"/> components,
    /// excluding cosmetic renderers that would skew it (blob shadows, health bars,
    /// tracers/trails, particle effects). It is stored as a LOCAL offset from the
    /// transform, so <see cref="WorldCenter"/> stays correct as the unit moves or
    /// turns without recomputing every frame.
    ///
    /// Use <see cref="Of"/> to get the world centre of any GameObject — it adds and
    /// caches a provider on first use, so callers need no setup.
    /// </summary>
    [DisallowMultipleComponent]
    public class GeometricCenter : MonoBehaviour
    {
        private Vector3 _localOffset;
        private bool _computed;

        /// <summary>
        /// World-space geometric centre of <paramref name="go"/>'s visible body.
        /// Adds/caches a <see cref="GeometricCenter"/> on first use. Falls back to the
        /// transform position (plus 1 m up) if the object has no usable renderers.
        /// </summary>
        public static Vector3 Of(GameObject go)
        {
            if (go == null)
                return Vector3.zero;
            var gc = go.GetComponent<GeometricCenter>();
            if (gc == null)
                gc = go.AddComponent<GeometricCenter>();
            return gc.WorldCenter;
        }

        /// <summary>Current world-space centre, computing the cached offset on first access.</summary>
        public Vector3 WorldCenter
        {
            get
            {
                if (!_computed)
                    Compute();
                return transform.TransformPoint(_localOffset);
            }
        }

        /// <summary>Force a recompute (e.g. after the body's renderers change).</summary>
        public void Recompute() => Compute();

        private void Compute()
        {
            _computed = true;

            bool has = false;
            Bounds bounds = default;

            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !Included(r))
                    continue;

                if (!has)
                {
                    bounds = r.bounds;
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (has)
            {
                // Store as a local offset so it tracks the transform as it moves/turns.
                _localOffset = transform.InverseTransformPoint(bounds.center);
            }
            else
            {
                // No body renderers: aim roughly at mid-height above the root.
                _localOffset = new Vector3(0f, 1f, 0f);
            }
        }

        /// <summary>
        /// Only real body meshes contribute to centre mass. Cosmetic renderers
        /// (shadow decals, world-space health bars, line/trail tracers, particles)
        /// would drag the centre off the body, so they are excluded.
        /// </summary>
        private static bool Included(Renderer r)
        {
            if (r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer)
                return false;
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer))
                return false;

            string n = r.gameObject.name;
            if (n.IndexOf("Shadow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (n.IndexOf("HealthBar", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (n.IndexOf("Health", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // World-space health bar parts live under a "HealthBar" object; skip any
            // renderer parented beneath one.
            for (Transform t = r.transform; t != null; t = t.parent)
            {
                string pn = t.name;
                if (pn.IndexOf("HealthBar", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
        }
    }
}
