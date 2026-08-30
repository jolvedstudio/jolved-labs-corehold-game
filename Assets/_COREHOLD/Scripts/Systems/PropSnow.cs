using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// The props' half of the weather surface response (snow and wet).
    ///
    /// The terrain gets its whitening inside COREHOLD/Terrain Lit, where the
    /// surface normal decides what accumulates. Dressing props cannot: they
    /// wear arbitrary vendor materials on arbitrary shaders, and there is no
    /// one shader to edit. Without them, a snow preset produces white ground
    /// under untouched brown rocks — which reads as a bug, not as weather.
    ///
    /// So props are handled the only way that works across unknown shaders:
    /// a per-renderer MaterialPropertyBlock tinting _BaseColor / _Color. That
    /// is a tint, not a normal-aware accumulation — a prop's tops and sides
    /// whiten together. At 130-150 m that difference is invisible, while the
    /// difference between "props match the weather" and "props ignore it" is
    /// the whole read.
    ///
    /// Every prop the generator placed carries <see cref="PlacedProp"/>, so
    /// this finds exactly the dressing and never the units, the pads or the
    /// Core — snow on the ground and on the rocks, not on the turrets a player
    /// is tracking. Authored (non-generated) dressing carries the marker too
    /// once a scene has been through adapt.
    ///
    /// Blocks are restored to the authored look at snow/wet 0, so clearing
    /// weather genuinely clears.
    /// </summary>
    public static class PropSnow
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static MaterialPropertyBlock _block;

        /// <summary>Renderers touched last time, so a return to clear weather
        /// can restore precisely those and nothing else.</summary>
        private static readonly List<Renderer> _tinted = new List<Renderer>();

        /// <param name="snow">0-1 whitening toward <paramref name="snowColor"/>.</param>
        /// <param name="wet">0-1 darkening; composes under the snow.</param>
        public static void Apply(float snow, float wet, Color snowColor)
        {
            snow = Mathf.Clamp01(snow);
            wet = Mathf.Clamp01(wet);
            _block ??= new MaterialPropertyBlock();

            // Nothing to do, and nothing left over: clear the previous pass.
            if (snow <= 0.001f && wet <= 0.001f)
            {
                Restore();
                return;
            }

            // Wet darkens, snow whitens over it — the same order the terrain
            // shader uses, so ground and props agree.
            Color tint = Color.Lerp(Color.white, new Color(0.62f, 0.64f, 0.68f), wet);
            tint = Color.Lerp(tint, snowColor, snow);

            // The renderer list is CACHED: the progressive ramp calls this every
            // throttled tick for ~10 s, and re-finding a few hundred PlacedProps
            // each time would be the exact per-frame cost the weather system
            // promises not to have. Dressing is static during play; a scene
            // change empties the cache via the destroyed-renderer check below.
            if (_tinted.Count == 0 || _tinted[0] == null)
            {
                _tinted.Clear();
                foreach (PlacedProp prop in Object.FindObjectsByType<PlacedProp>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    foreach (Renderer r in prop.GetComponentsInChildren<Renderer>(false))
                        if (r != null && !(r is ParticleSystemRenderer))
                            _tinted.Add(r);
            }

            foreach (Renderer r in _tinted)
            {
                if (r == null)
                    continue;
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, tint);
                _block.SetColor(ColorId, tint);
                r.SetPropertyBlock(_block);
            }
        }

        /// <summary>Return every renderer this touched to its authored colour.</summary>
        private static void Restore()
        {
            if (_tinted.Count == 0)
                return;
            _block ??= new MaterialPropertyBlock();
            foreach (Renderer r in _tinted)
            {
                if (r == null)
                    continue;
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, Color.white);
                _block.SetColor(ColorId, Color.white);
                r.SetPropertyBlock(_block);
            }
            _tinted.Clear();
        }
    }
}
