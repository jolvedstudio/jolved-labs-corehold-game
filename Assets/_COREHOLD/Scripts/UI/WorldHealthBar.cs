using UnityEngine;

namespace Corehold.UI
{
    /// <summary>
    /// A self-contained world-space health bar that lives ON the entity it describes
    /// (an enemy or a turret). It builds its own back + fill quads once, billboards
    /// them to the camera every frame, and reads a health fraction from a supplied
    /// delegate. Because it is a child of its owner it moves, disables and is
    /// destroyed with the owner automatically — no central registry, no dictionary
    /// keyed on Unity objects, so there is nothing to leave behind or null-key.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldHealthBar : MonoBehaviour
    {
        private System.Func<float> _fraction;   // 0..1 health fraction
        private Vector3 _localOffset = Vector3.up * 2.2f;
        private float _width = 1.6f;
        private float _height = 0.18f;

        // World-space clearance kept between the top of the owner's mesh and the
        // bottom of the bar so it never sits inside the hull.
        private float _clearanceAbove = 0.9f;
        // When true, measure the owner's renderer bounds each frame and place the
        // bar just above the highest point (so large models like the Strider don't
        // encase the bar). The supplied heightAbove is used as a minimum fallback.
        private bool _autoHeight = true;
        private float _minHeightAbove = 2.2f;
        private Renderer[] _ownerRenderers;

        private Transform _root;
        /// <summary>The generated bar sub-tree (quads), or null before Build(). Exposed so
        /// other cosmetics (e.g. ShieldAura's bounds measurement) can exclude the bar
        /// without walking up to this component, which lives on the owner root.</summary>
        public Transform BarRoot => _root;
        private Transform _fill;
        private MeshRenderer _backRenderer;
        private MeshRenderer _fillRenderer;
        private MaterialPropertyBlock _block;
        private Camera _cam;

        private static Mesh _quad;
        private static Material _sharedMat;

        private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorIdLegacy = Shader.PropertyToID("_Color");

        // The BACK stays dark deliberately — it is the contrast plate the fill
        // reads against. The FILL colours are bright at both ends and lerp
        // through amber (never a muddy dark midpoint), so the level is legible
        // at any fraction on any ground.
        private static readonly Color BackColor = new Color(0.03f, 0.04f, 0.06f, 0.9f);
        private static readonly Color FullColor = new Color(0.30f, 1f, 0.45f, 1f);
        private static readonly Color MidColor = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color LowColor = new Color(1f, 0.36f, 0.22f, 1f);

        /// <summary>Create and attach a health bar to <paramref name="owner"/>.</summary>
        public static WorldHealthBar Attach(GameObject owner, System.Func<float> fraction,
            float heightAbove, float width, float height, Color? fullColorOverride = null)
        {
            var bar = owner.AddComponent<WorldHealthBar>();
            bar._fraction = fraction;
            bar._localOffset = Vector3.up * heightAbove;
            bar._minHeightAbove = heightAbove;
            bar._width = width;
            bar._height = height;
            bar._fullColor = fullColorOverride ?? FullColor;
            bar.Build();
            return bar;
        }

        private Color _fullColor = FullColor;

        private void Build()
        {
            _block = new MaterialPropertyBlock();
            EnsureShared();

            // Cache the owner's renderers so we can measure the top of the hull and
            // keep the bar above it. Captured before the bar quads exist so we never
            // measure our own geometry.
            _ownerRenderers = GetComponentsInChildren<Renderer>(true);

            var rootGo = new GameObject("HealthBar");
            _root = rootGo.transform;
            _root.SetParent(transform, false);

            _backRenderer = MakeQuad("Back", _root, Vector3.zero, new Vector3(_width, _height, 1f));
            var fillTr = MakeQuad("Fill", _root, new Vector3(0f, 0f, -0.01f), new Vector3(_width, _height * 0.75f, 1f));
            _fill = fillTr.transform;
            _fillRenderer = fillTr;

            SetColor(_backRenderer, BackColor);
        }

        private MeshRenderer MakeQuad(string name, Transform parent, Vector3 localPos, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            // Instance the material so each quad holds its own colour and draws on
            // top of world geometry (ZTest Always) — otherwise the bar is occluded
            // by the turret/enemy mesh and appears invisible.
            r.material = new Material(_sharedMat);
            if (r.material.HasProperty("_ZTest"))
                r.material.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            r.material.renderQueue = 5000;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return r;
        }

        private void LateUpdate()
        {
            if (_root == null || _fraction == null)
                return;
            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null)
                return;

            // Position above the owner, billboard to the camera. When auto-height is
            // on, place the bar just above the highest point of the owner's mesh so
            // large models (e.g. the Strider) never encase it.
            _root.position = transform.position + ComputeOffset();
            _root.rotation = Quaternion.LookRotation(_cam.transform.forward, _cam.transform.up);

            float frac = Mathf.Clamp01(_fraction());

            // Fill shrinks from the left.
            var s = _fill.localScale;
            s.x = Mathf.Max(0.0001f, _width * frac);
            _fill.localScale = s;
            _fill.localPosition = new Vector3(-_width * (1f - frac) * 0.5f, 0f, -0.01f);

            // Two-segment ramp keeps every intermediate colour BRIGHT: green→
            // amber→red, instead of a straight lerp whose midpoint goes muddy.
            Color fillColor = frac >= 0.5f
                ? Color.Lerp(MidColor, _fullColor, (frac - 0.5f) * 2f)
                : Color.Lerp(LowColor, MidColor, frac * 2f);
            SetColor(_fillRenderer, fillColor);
        }

        /// <summary>
        /// World-space vertical offset from the owner's origin to the bar. When
        /// auto-height is enabled, measures the highest point of the owner's mesh
        /// (ignoring the bar's own quads) and adds a clearance margin so the bar
        /// always floats above the hull. Falls back to the supplied minimum height.
        /// </summary>
        private Vector3 ComputeOffset()
        {
            float heightAbove = _minHeightAbove;

            if (_autoHeight && _ownerRenderers != null)
            {
                bool hasBounds = false;
                float maxY = float.NegativeInfinity;
                for (int i = 0; i < _ownerRenderers.Length; i++)
                {
                    var r = _ownerRenderers[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                        continue;
                    // Skip the bar's own quad renderers.
                    if (r == _backRenderer || r == _fillRenderer)
                        continue;
                    var b = r.bounds;
                    if (b.size == Vector3.zero)
                        continue;
                    hasBounds = true;
                    if (b.max.y > maxY)
                        maxY = b.max.y;
                }

                if (hasBounds)
                {
                    // Convert the world top into an offset above the owner's origin,
                    // add clearance and half the bar height so the whole bar clears.
                    float top = maxY - transform.position.y + _clearanceAbove + _height * 0.5f;
                    heightAbove = Mathf.Max(_minHeightAbove, top);
                }
            }

            return Vector3.up * heightAbove;
        }

        private void SetColor(MeshRenderer r, Color c)
        {
            if (r == null) return;
            // Each quad gets its own material instance so setting .color is reliable
            // across shaders (property blocks are finicky on URP transparent).
            if (r.material != null)
            {
                r.material.color = c;
                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
            }
        }

        private static void EnsureShared()
        {
            if (_quad == null)
            {
                _quad = new Mesh { name = "HealthBarQuad" };
                _quad.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                };
                _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                _quad.RecalculateBounds();
            }

            if (_sharedMat == null)
            {
                // URP/Unlit is unlit (always-readable) AND exposes _ZTest so the bar
                // can draw on top of world geometry. Configured transparent so the
                // per-instance colour + alpha applies.
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                _sharedMat = new Material(shader) { name = "HealthBarMat" };
                if (_sharedMat.HasProperty("_Surface")) _sharedMat.SetFloat("_Surface", 1f); // transparent
                _sharedMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _sharedMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (_sharedMat.HasProperty("_ZWrite")) _sharedMat.SetFloat("_ZWrite", 0f);
                _sharedMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _sharedMat.renderQueue = 5000; // draw after everything
            }
        }
    }
}
