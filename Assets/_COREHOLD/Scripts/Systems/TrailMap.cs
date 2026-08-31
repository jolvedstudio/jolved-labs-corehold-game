using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Enemy trails in the snow: ground units carve visible tracks through the
    /// weather film as they advance (GDD-adjacent polish; proposed alongside
    /// the surface-response work).
    ///
    /// The classic technique minus the parts WebGL cannot afford. No
    /// tessellation, no displacement — at 130-150 m DEFORMATION is invisible
    /// but DARKENING reads perfectly — and no compute: one persistent little
    /// render texture over the field, stamped with soft dots where enemies
    /// walk, sampled by the terrain shader to punch the tracks through the
    /// snow film to the ground colour beneath.
    ///
    /// The whole cost ledger, because performance is the first-cut contract:
    ///   • one 512² R8 render texture (~0.26 MB), persistent;
    ///   • ≤ maxLive tiny additive quads per frame (usually far fewer — one
    ///     stamp per ~1.2 m walked per ground unit);
    ///   • one fullscreen-on-512 multiply quad every 0.5 s (the melt);
    ///   • one extra texture sample in the terrain fragment shader.
    ///
    /// The map self-clears when the film goes: tracks in snow that has melted
    /// are tracks in nothing, and the NEXT snowfall must start unmarked.
    ///
    /// Lives on the WeatherApplier's own GameObject, created lazily by
    /// <see cref="Push"/> — no scene, prefab or skeleton registration needed,
    /// which is what lets existing generated scenes gain trails on pull.
    /// </summary>
    public class TrailMap : MonoBehaviour
    {
        // ---- [TUNE] ----------------------------------------------------------

        /// <summary>Texture resolution. 512 over the 200 m area = 2.56 px/m —
        /// a 0.9 m stamp is ~2.3 px of soft dot, which overlapping stamps every
        /// ~1.2 m of walking turn into a continuous track.</summary>
        private const int Resolution = 512;

        /// <summary>World metres the map covers, centred on the origin. Wide
        /// enough for every route, spawn apron and fold on the standard field.</summary>
        private const float AreaSize = 200f;

        /// <summary>Stamp radius in metres, and how hard one footprint marks.
        /// Repeat passage deepens the track (additive, clamps at full carve).
        ///
        /// Sized against the ROADWAY, not against a footprint: a lane band is
        /// ~4 m across, and a track narrower than about a third of it reads as
        /// noise at 130-150 m rather than as something an army did. One pass of
        /// one unit should already be legible, so the first stamp lands most of
        /// the carve and repeat passage saturates it.</summary>
        private const float StampRadius = 1.35f;
        private const float StampAlpha = 0.7f;

        /// <summary>Seconds between melt passes. The melt RATE comes from the
        /// weather preset; this is only how often it is applied.</summary>
        private const float MeltTickSeconds = 0.5f;

        /// <summary>The film below which trails neither stamp nor show: tracks
        /// need something to be tracks IN.</summary>
        private const float FilmFloor = 0.15f;
        private const float FilmFull = 0.4f;

        /// <summary>How dark a fully carved track goes, multiplied over the
        /// ground the film was carved off. Removing the film is not enough to
        /// SEE a track: on the pale ground this project's themes mostly use,
        /// snow and sand sit at nearly the same luminance. Tracks read dark in
        /// life because the surface is packed and shadowed, so the shader says
        /// that outright and the effect no longer depends on the ground beneath
        /// happening to be darker than the snow on top of it.</summary>
        private static readonly Color TrailDarken = new Color(0.62f, 0.64f, 0.70f, 1f);

        private static readonly int TrailMapId = Shader.PropertyToID("_CoreholdTrailMap");
        private static readonly int TrailAreaId = Shader.PropertyToID("_CoreholdTrailArea");
        private static readonly int TrailStrengthId = Shader.PropertyToID("_CoreholdTrailStrength");
        private static readonly int TrailDarkenId = Shader.PropertyToID("_TrailDarken");

        private static TrailMap _i;

        private RenderTexture _rt;
        private Material _stampMat;
        private Material _fadeMat;
        private Texture2D _dot;
        private readonly List<Vector2> _queue = new List<Vector2>();
        private float _meltSeconds = 45f;
        private float _nextMelt;
        private bool _active;
        private bool _rtDirty;

        // ------------------------------------------------------------------ API

        /// <summary>
        /// Called by the WeatherApplier every surface tick with the CURRENT
        /// (ramped) snow film and the preset's trail parameters. Creates the
        /// instance on first need; play mode only — trails are made by moving
        /// enemies, and edit mode has none.
        /// </summary>
        public static void Push(GameObject host, float snowFilm, float strength, float meltSeconds)
        {
            if (!Application.isPlaying)
                return;
            if (_i == null)
            {
                if (host == null || strength <= 0.001f)
                    return;   // never spawn infrastructure for a disabled feature
                _i = host.GetComponent<TrailMap>();
                if (_i == null)
                    _i = host.AddComponent<TrailMap>();
            }
            _i.Configure(snowFilm, strength, meltSeconds);
        }

        /// <summary>
        /// A ground unit stamping its position. Static and guarded so the call
        /// from EnemyMover costs two field reads when trails are off — the
        /// mover must never pay for weather it is not standing in.
        /// </summary>
        public static void Stamp(Vector3 worldPos)
        {
            if (_i == null || !_i._active)
                return;
            _i._queue.Add(new Vector2(worldPos.x, worldPos.z));
        }

        // ------------------------------------------------------------- lifecycle

        private void Configure(float snowFilm, float strength, float meltSeconds)
        {
            _meltSeconds = Mathf.Max(2f, meltSeconds);

            // Trails need film to be trails in; fade in with it.
            float film = Mathf.InverseLerp(FilmFloor, FilmFull, snowFilm);
            float effective = Mathf.Clamp01(strength) * Mathf.SmoothStep(0f, 1f, film);

            bool wasActive = _active;
            _active = effective > 0.001f;

            if (_active && _rt == null)
                CreateResources();

            // Film gone entirely: the canvas melted. Clear so the next
            // snowfall starts unmarked instead of inheriting ghost tracks.
            if (wasActive && !_active && _rt != null)
                ClearRt();

            Shader.SetGlobalFloat(TrailStrengthId, _rt != null ? effective : 0f);
        }

        private void CreateResources()
        {
            var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                ? RenderTextureFormat.R8
                : RenderTextureFormat.ARGB32;
            _rt = new RenderTexture(Resolution, Resolution, 0, format)
            {
                name = "Corehold_TrailMap",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _rt.Create();
            ClearRt();

            // Same shader family and configuration doctrine as the
            // precipitation sheet: URP particle unlit, blend state set in code.
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            _dot = BuildDot();

            _stampMat = new Material(shader) { name = "TrailStamp (generated)", hideFlags = HideFlags.HideAndDontSave };
            ConfigureBlend(_stampMat,
                UnityEngine.Rendering.BlendMode.SrcAlpha, UnityEngine.Rendering.BlendMode.One);
            if (_stampMat.HasProperty("_BaseMap")) _stampMat.SetTexture("_BaseMap", _dot);
            if (_stampMat.HasProperty("_MainTex")) _stampMat.SetTexture("_MainTex", _dot);

            // The melt: dst *= colour. SrcFactor Zero kills the source term,
            // DstFactor SrcColor leaves dst × srcColour — one quad per tick,
            // no ping-pong buffer.
            _fadeMat = new Material(shader) { name = "TrailFade (generated)", hideFlags = HideFlags.HideAndDontSave };
            ConfigureBlend(_fadeMat,
                UnityEngine.Rendering.BlendMode.Zero, UnityEngine.Rendering.BlendMode.SrcColor);

            Shader.SetGlobalTexture(TrailMapId, _rt);
            Shader.SetGlobalVector(TrailDarkenId,
                new Vector4(TrailDarken.r, TrailDarken.g, TrailDarken.b, 1f));
            float half = AreaSize * 0.5f;
            Shader.SetGlobalVector(TrailAreaId,
                new Vector4(-half, -half, 1f / AreaSize, 1f / AreaSize));
        }

        private static void ConfigureBlend(Material m,
            UnityEngine.Rendering.BlendMode src, UnityEngine.Rendering.BlendMode dst)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)src);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)dst);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private void ClearRt()
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            _queue.Clear();
        }

        private void OnDestroy()
        {
            // The globals outlive the scene unless zeroed — a menu scene after
            // a snow level must not sample a released texture.
            Shader.SetGlobalFloat(TrailStrengthId, 0f);
            if (_rt != null) _rt.Release();
            if (_stampMat != null) Destroy(_stampMat);
            if (_fadeMat != null) Destroy(_fadeMat);
            if (_dot != null) Destroy(_dot);
            if (_i == this) _i = null;
        }

        // ------------------------------------------------------------- the work

        private void LateUpdate()
        {
            if (_rt == null)
                return;
            bool melt = _active && Time.time >= _nextMelt;
            if (_queue.Count == 0 && !melt)
                return;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.PushMatrix();
            GL.LoadOrtho();   // 0..1 across the map

            if (melt)
            {
                _nextMelt = Time.time + MeltTickSeconds;
                float keep = Mathf.Clamp01(1f - MeltTickSeconds / _meltSeconds);
                _fadeMat.color = new Color(keep, keep, keep, 1f);
                if (_fadeMat.HasProperty("_BaseColor"))
                    _fadeMat.SetColor("_BaseColor", new Color(keep, keep, keep, 1f));
                _fadeMat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(1f, 0f, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(0f, 1f, 0f);
                GL.End();
            }

            if (_queue.Count > 0)
            {
                Color c = new Color(1f, 1f, 1f, StampAlpha);
                _stampMat.color = c;
                if (_stampMat.HasProperty("_BaseColor"))
                    _stampMat.SetColor("_BaseColor", c);
                _stampMat.SetPass(0);
                float half = AreaSize * 0.5f;
                float r = StampRadius / AreaSize;   // world → 0..1
                GL.Begin(GL.QUADS);
                foreach (Vector2 p in _queue)
                {
                    float u = (p.x + half) / AreaSize;
                    float v = (p.y + half) / AreaSize;
                    if (u < -0.01f || u > 1.01f || v < -0.01f || v > 1.01f)
                        continue;
                    GL.TexCoord2(0f, 0f); GL.Vertex3(u - r, v - r, 0f);
                    GL.TexCoord2(1f, 0f); GL.Vertex3(u + r, v - r, 0f);
                    GL.TexCoord2(1f, 1f); GL.Vertex3(u + r, v + r, 0f);
                    GL.TexCoord2(0f, 1f); GL.Vertex3(u - r, v + r, 0f);
                }
                GL.End();
                _queue.Clear();
            }

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>A 16×16 soft dot — squared falloff, like the weather mote,
        /// and generated for the same reason: it can never be a missing asset.</summary>
        private static Texture2D BuildDot()
        {
            const int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "TrailDot (generated)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[size * size];
            const float centre = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / centre, dy = (y - centre) / centre;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    a *= a;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
