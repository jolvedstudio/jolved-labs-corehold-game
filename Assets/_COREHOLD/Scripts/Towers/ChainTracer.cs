using Corehold.Systems;
using UnityEngine;

namespace Corehold.Towers
{
    /// <summary>
    /// A single pooled lightning tracer segment for the Arc Node's chain (GDD §7.2).
    /// Each jump in a chain (muzzle→first target, target→target, …) draws one of
    /// these between two world points and then fades out over exactly two frames
    /// before returning itself to the pool.
    ///
    /// The two-frame life is deliberate: an arc reads as an instantaneous flash,
    /// not a persistent beam. Frame 0 draws at full alpha, frame 1 draws at half
    /// alpha, and on frame 2 the segment releases. Because it is frame-based
    /// rather than time-based it survives at any framerate and never lingers.
    ///
    /// Pooled through <see cref="CoreholdPool{T}"/> — nothing here calls
    /// Instantiate or Destroy during a wave (GDD §11).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LineRenderer))]
    public class ChainTracer : MonoBehaviour
    {
        // Frames the segment stays visible before releasing. Two frames = one full
        // flash then one half-alpha fade, matching the ticket's "two-frame fade".
        private const int LifeFrames = 2;

        private LineRenderer _line;
        private CoreholdPool<ChainTracer> _owningPool;
        private int _framesLeft;
        private Gradient _gradient;
        private GradientColorKey[] _colorKeys;
        private GradientAlphaKey[] _alphaKeys;
        private float _baseAlpha = 1f;

        // ----- Pooling -----

        private static CoreholdPool<ChainTracer> _pool;
        private static Transform _poolRoot;
        private static ChainTracer _prefab;

        /// <summary>
        /// The LineRenderer prefab tracers are cloned from. Assign a prefab
        /// carrying a <see cref="ChainTracer"/> (e.g. FX_Beam with this component)
        /// once at boot. When null a plain runtime tracer is built on first use.
        /// </summary>
        public static ChainTracer SharedPrefab
        {
            get => _prefab;
            set => _prefab = value;
        }

        private static CoreholdPool<ChainTracer> Pool
        {
            get
            {
                if (_pool == null)
                {
                    var rootGo = new GameObject("Pool_ChainTracers");
                    _poolRoot = rootGo.transform;
                    ChainTracer prefab = _prefab != null ? _prefab : BuildRuntimePrefab();
                    _pool = new CoreholdPool<ChainTracer>(prefab, _poolRoot, 8);
                }
                return _pool;
            }
        }

        /// <summary>Clear the tracer pool. Call when tearing a level down.</summary>
        public static void ClearPool()
        {
            _pool = null;
            _poolRoot = null;
        }

        /// <summary>
        /// Draw one tracer segment between two world points at the given colour.
        /// The segment fades over two frames and returns itself to the pool.
        /// </summary>
        public static ChainTracer Draw(Vector3 from, Vector3 to, Color color)
        {
            ChainTracer t = Pool.Get();
            t._owningPool = Pool;
            t.Play(from, to, color);
            return t;
        }

        private static ChainTracer BuildRuntimePrefab()
        {
            var go = new GameObject("ChainTracer");
            go.SetActive(false);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.widthMultiplier = 0.12f;
            lr.numCapVertices = 2;
            lr.numCornerVertices = 2;
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            // A cheap additive-friendly material so the arc glows without extra assets.
            var mat = new Material(Shader.Find("Sprites/Default"));
            lr.material = mat;
            return go.AddComponent<ChainTracer>();
        }

        private void EnsureInit()
        {
            if (_line == null)
                _line = GetComponent<LineRenderer>();

            if (_gradient == null)
            {
                _colorKeys = new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f),
                };
                _alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                };
                _gradient = new Gradient();
            }
        }

        private void Play(Vector3 from, Vector3 to, Color color)
        {
            EnsureInit();

            transform.position = Vector3.zero;
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.SetPosition(0, from);
            _line.SetPosition(1, to);

            _baseAlpha = color.a <= 0f ? 1f : color.a;
            ApplyAlpha(color, _baseAlpha);

            _framesLeft = LifeFrames;
        }

        private void ApplyAlpha(Color color, float alpha)
        {
            _colorKeys[0].color = color;
            _colorKeys[1].color = color;
            _alphaKeys[0].alpha = alpha;
            _alphaKeys[1].alpha = alpha;
            _gradient.SetKeys(_colorKeys, _alphaKeys);
            _line.colorGradient = _gradient;
        }

        private void LateUpdate()
        {
            if (_framesLeft <= 0)
                return;

            _framesLeft--;

            // Two-frame fade: frame 1 at full alpha, frame 0 at half, then release.
            float alpha = _baseAlpha * (_framesLeft / (float)LifeFrames);
            Color c = _colorKeys[0].color;
            ApplyAlpha(c, alpha);

            if (_framesLeft <= 0)
                Release();
        }

        private void Release()
        {
            if (_owningPool != null)
                _owningPool.Release(this);
            else
                gameObject.SetActive(false);
        }
    }
}
