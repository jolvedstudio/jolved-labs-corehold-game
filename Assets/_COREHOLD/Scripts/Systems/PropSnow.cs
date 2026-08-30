using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// The props' half of the weather surface response — snow, wet, and the
    /// wind vegetation stands in.
    ///
    /// THE PROBLEM THIS SOLVES. Terrain whitens inside COREHOLD/Terrain Lit,
    /// where the surface normal decides what accumulates. The first version of
    /// this class tried to give props the same treatment by tinting _BaseColor
    /// through a MaterialPropertyBlock, and it could never have worked:
    /// _BaseColor MULTIPLIES the base map, so pushing it toward white leaves a
    /// textured rock exactly as brown as it started, and a vendor shader that
    /// names its colour anything else ignores the write entirely. The result
    /// on screen was white ground under untouched brown rocks — which reads as
    /// a bug, not as weather.
    ///
    /// WHAT IT DOES INSTEAD. While weather asks for it, each prop renderer is
    /// swapped onto a SHARED variant material built once per source material
    /// and wearing COREHOLD/Prop Lit. That shader does the same normal-based
    /// accumulation the terrain does, reads the weather channels from GLOBALS
    /// (one write per channel for the whole field, versus a property block per
    /// renderer), and — because a swap buys a vertex stage — leans and sways
    /// vegetation in the scene's wind.
    ///
    /// Shared, not per-instance: one variant serves every renderer using that
    /// source material, so the SRP batcher keeps batching and nothing leaks
    /// per prop. Sway is baked into the VARIANT rather than set per renderer
    /// for the same reason — a property block on 500 props would cost more
    /// than the entire weather system.
    ///
    /// WHAT IT COSTS. The vendor shader's own features (normal maps, bespoke
    /// stylisation) for as long as weather is up. At 130-150 m that is a trade
    /// worth making, and <see cref="SkipShaders"/> keeps out the materials
    /// where it is not — anything running its own vertex animation would lose
    /// more than it gains.
    ///
    /// Play mode only. Swapping shared materials in the editor would dirty the
    /// scene; edit-mode previews show terrain weather and leave props alone.
    /// </summary>
    public static class PropSnow
    {
        // ---- [TUNE] ----------------------------------------------------------

        /// <summary>Metres of lateral travel per metre of height, at full wind,
        /// for a prop classed as vegetation. A 4 m tree tips about half a metre
        /// — plainly moving, still standing where the occlusion test put it.</summary>
        private const float SwayPerMetre = 0.11f;

        /// <summary>Wind speed at which sway reaches full amplitude.</summary>
        private const float SwayFullWind = 12f;

        /// <summary>Name fragments that mark a prop as FLEXIBLE. Everything
        /// else is rock: it stands still and only takes snow. Matched against
        /// the prop's own object name, which carries the source prefab's, so
        /// this reads the art rather than needing a field authored per entry.
        /// Cactus is deliberately absent — it is a stiff thing and swaying it
        /// looks wrong.</summary>
        private static readonly string[] FlexibleTokens =
        {
            "tree", "pine", "fir", "palm", "shrub", "bush", "scrub", "grass",
            "fern", "plant", "weed", "flower", "sapling", "reed", "foliage",
            "leaf", "leaves", "branch"
        };

        /// <summary>Shaders never swapped: anything doing its own vertex
        /// animation would lose more than snow buys. Matched case-insensitively
        /// as a substring of the shader name.</summary>
        private static readonly string[] SkipShaders =
        {
            "grass", "wind", "foliage", "water", "vegetation"
        };

        // ---- globals the prop shader reads -----------------------------------

        private static readonly int PropSnowId = Shader.PropertyToID("_CoreholdPropSnow");
        private static readonly int PropWetId = Shader.PropertyToID("_CoreholdPropWet");
        private static readonly int PropSnowColorId = Shader.PropertyToID("_CoreholdPropSnowColor");
        private static readonly int WindId = Shader.PropertyToID("_CoreholdWind");

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int SwayScaleId = Shader.PropertyToID("_SwayScale");

        // ---- state -----------------------------------------------------------

        private static Shader _propShader;
        private static bool _shaderMissing;

        /// <summary>Source material → its swapped variant, one per (material,
        /// flexible) pair. Two dictionaries rather than a tuple key so the
        /// sway class lives in the variant and never in a property block.</summary>
        private static readonly Dictionary<Material, Material> _rigidVariants =
            new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Material> _flexVariants =
            new Dictionary<Material, Material>();

        /// <summary>Renderers currently swapped, with the exact material array
        /// each wore before — restored verbatim, so a prop that was authored
        /// with three materials gets its three back in order.</summary>
        private static readonly List<Renderer> _swapped = new List<Renderer>();
        private static readonly List<Material[]> _originals = new List<Material[]>();

        private static bool _active;

        // ---- API -------------------------------------------------------------

        /// <param name="snow">0-1 accumulation toward <paramref name="snowColor"/>.</param>
        /// <param name="wet">0-1 darkening; composes under the snow.</param>
        /// <param name="snowColor">The colour the film lies as.</param>
        /// <param name="windDirection">Horizontal wind; normalized here.</param>
        /// <param name="windStrength">Wind speed in m/s, gust envelope included.</param>
        /// <param name="sway">0-1 designer scale on vegetation motion.</param>
        public static void Apply(float snow, float wet, Color snowColor,
                                 Vector3 windDirection, float windStrength, float sway)
        {
            if (!Application.isPlaying)
                return;

            snow = Mathf.Clamp01(snow);
            wet = Mathf.Clamp01(wet);
            sway = Mathf.Clamp01(sway);

            float amplitude = SwayAmplitude(windStrength, sway);
            bool wants = snow > 0.001f || wet > 0.001f || amplitude > 0.0001f;

            if (!wants)
            {
                Restore();
                return;
            }

            PushWind(windDirection, amplitude);
            Shader.SetGlobalFloat(PropSnowId, snow);
            Shader.SetGlobalFloat(PropWetId, wet);
            Shader.SetGlobalColor(PropSnowColorId, snowColor);

            if (!_active)
                Swap();
        }

        /// <summary>
        /// Metres of lateral travel per metre of height, for a given wind and
        /// designer scale. One formula, shared by the surface push and the gust
        /// tick, so a gust cannot accidentally use a different curve from the
        /// steady wind and make the trees jump when the tick order changes.
        /// </summary>
        public static float SwayAmplitude(float windStrength, float sway)
        {
            return Mathf.Clamp01(sway) *
                   Mathf.Clamp01(Mathf.Max(0f, windStrength) / SwayFullWind) *
                   SwayPerMetre;
        }

        /// <summary>
        /// Write the wind globals alone. Separate from <see cref="Apply"/>
        /// because gusts keep moving after the surface ramp has settled, and
        /// the applier's throttled tick stops pushing surfaces once every
        /// channel has reached its target.
        /// </summary>
        public static void PushWind(Vector3 direction, float amplitude)
        {
            Vector3 d = new Vector3(direction.x, 0f, direction.z);
            d = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
            Shader.SetGlobalVector(WindId, new Vector4(d.x, d.y, d.z, amplitude));
        }

        /// <summary>Put every swapped renderer back on its authored materials
        /// and drop the variants. Called when weather clears, and safe to call
        /// when nothing is swapped.</summary>
        public static void Restore()
        {
            if (!_active)
                return;

            for (int i = 0; i < _swapped.Count; i++)
            {
                Renderer r = _swapped[i];
                if (r != null)
                    r.sharedMaterials = _originals[i];
            }
            _swapped.Clear();
            _originals.Clear();

            foreach (Material m in _rigidVariants.Values)
                if (m != null) Object.Destroy(m);
            foreach (Material m in _flexVariants.Values)
                if (m != null) Object.Destroy(m);
            _rigidVariants.Clear();
            _flexVariants.Clear();

            Shader.SetGlobalFloat(PropSnowId, 0f);
            Shader.SetGlobalFloat(PropWetId, 0f);
            Shader.SetGlobalVector(WindId, Vector4.zero);
            _active = false;
        }

        // ---- the swap --------------------------------------------------------

        private static void Swap()
        {
            if (_shaderMissing)
                return;
            if (_propShader == null)
            {
                _propShader = Shader.Find("COREHOLD/Prop Lit");
                if (_propShader == null)
                {
                    // Never spam: one line, then this path is inert for the run.
                    Debug.LogWarning("[PropSnow] COREHOLD/Prop Lit not found — props will " +
                                     "not take snow or wind. Is the shader in the build?");
                    _shaderMissing = true;
                    return;
                }
            }

            // Dressing is static during play, so this runs ONCE per weather
            // onset rather than every tick — the cost the old cached-renderer
            // list existed to avoid, avoided the same way.
            foreach (PlacedProp prop in Object.FindObjectsByType<PlacedProp>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (prop == null)
                    continue;
                bool flexible = IsFlexible(prop.name);

                foreach (Renderer r in prop.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r is ParticleSystemRenderer)
                        continue;

                    Material[] originals = r.sharedMaterials;
                    if (originals == null || originals.Length == 0)
                        continue;

                    var swapped = new Material[originals.Length];
                    bool any = false;
                    for (int i = 0; i < originals.Length; i++)
                    {
                        Material variant = VariantFor(originals[i], flexible);
                        swapped[i] = variant != null ? variant : originals[i];
                        any |= variant != null;
                    }
                    if (!any)
                        continue;

                    _swapped.Add(r);
                    _originals.Add(originals);
                    r.sharedMaterials = swapped;
                }
            }
            _active = true;
        }

        /// <summary>The shared variant for one source material, built on first
        /// need. Null means "leave this one alone".</summary>
        private static Material VariantFor(Material source, bool flexible)
        {
            if (source == null || source.shader == null)
                return null;

            string shaderName = source.shader.name;
            if (shaderName == "COREHOLD/Prop Lit")
                return null;                      // already ours
            for (int i = 0; i < SkipShaders.Length; i++)
                if (shaderName.IndexOf(SkipShaders[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;

            Dictionary<Material, Material> table = flexible ? _flexVariants : _rigidVariants;
            if (table.TryGetValue(source, out Material existing) && existing != null)
                return existing;

            var variant = new Material(_propShader)
            {
                name = source.name + (flexible ? " (snow+sway)" : " (snow)"),
                hideFlags = HideFlags.HideAndDontSave,
            };

            // Carry across what the look actually depends on. Base map and
            // colour under either URP or legacy naming, so a vendor material
            // on a Standard-derived shader still arrives with its texture.
            Texture map = source.HasProperty(BaseMapId) ? source.GetTexture(BaseMapId)
                        : source.HasProperty(MainTexId) ? source.GetTexture(MainTexId)
                        : null;
            if (map != null)
            {
                variant.SetTexture(BaseMapId, map);
                variant.SetTextureScale(BaseMapId,
                    source.HasProperty(BaseMapId) ? source.GetTextureScale(BaseMapId)
                                                  : source.GetTextureScale(MainTexId));
                variant.SetTextureOffset(BaseMapId,
                    source.HasProperty(BaseMapId) ? source.GetTextureOffset(BaseMapId)
                                                  : source.GetTextureOffset(MainTexId));
            }

            Color tint = source.HasProperty(BaseColorId) ? source.GetColor(BaseColorId)
                       : source.HasProperty(ColorId) ? source.GetColor(ColorId)
                       : Color.white;
            variant.SetColor(BaseColorId, tint);

            // Foliage is cut out, not opaque; without this a tree arrives as a
            // box of leaf-textured cards.
            if (source.HasProperty(CutoffId) && IsAlphaClipped(source))
            {
                variant.SetFloat(CutoffId, source.GetFloat(CutoffId));
                variant.EnableKeyword("_ALPHATEST_ON");
            }

            variant.SetFloat(SwayScaleId, flexible ? 1f : 0f);
            table[source] = variant;
            return variant;
        }

        /// <summary>URP marks clipping with _AlphaClip; older shaders signal it
        /// with the AlphaTest render queue. Accept either.</summary>
        private static bool IsAlphaClipped(Material m)
        {
            if (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f)
                return true;
            return m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest &&
                   m.renderQueue < (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private static bool IsFlexible(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i < FlexibleTokens.Length; i++)
                if (name.IndexOf(FlexibleTokens[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
