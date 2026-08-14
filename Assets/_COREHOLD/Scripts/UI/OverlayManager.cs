using System.Collections.Generic;
using Corehold.Core;
using Corehold.Enemies;
using TMPro;
using UnityEngine;

namespace Corehold.UI
{
    /// <summary>
    /// World-space enemy overlays — health bars and armour pips (GDD §9.4).
    ///
    /// Two hard requirements from the ticket, both met here:
    ///
    ///   • <b>The armour pip is separate and ALWAYS visible</b> — a small coloured
    ///     chevron above every enemy from the moment it spawns, because the counter
    ///     system is worthless if the player only learns an enemy is Plated after
    ///     shooting it. The health BAR, by contrast, only appears once an enemy has
    ///     been damaged and fades two seconds after the last hit.
    ///
    ///   • <b>One manager, one loop.</b> Bars and pips are world-space quads built
    ///     from <see cref="MeshRenderer"/>s sharing ONE material. A single
    ///     <see cref="LateUpdate"/> here iterates every live overlay and billboards
    ///     it — there is no per-overlay <c>LateUpdate</c>. Overlays are pooled and
    ///     reused as enemies spawn and die (no per-frame allocation, GDD §11).
    ///
    /// The manager tracks the live enemy set by diffing <see cref="Enemy.Live"/>
    /// each frame — cheap at ≤14 units and needs no spawn hook. Each tracked enemy
    /// gets one <see cref="Overlay"/> record (a pip quad, a bar-bg quad and a
    /// bar-fill quad), all children of a shared root so they batch together.
    /// </summary>
    [DisallowMultipleComponent]
    public class OverlayManager : MonoBehaviour
    {
        [Header("Placement")]
        [Tooltip("Metres above the enemy's HitPoint the overlay sits.")]
        [SerializeField] private float heightAboveHit = 2.2f;

        [Tooltip("World-space width of the health bar in metres.")]
        [SerializeField] private float barWidth = 1.6f;

        [Tooltip("World-space height of the health bar in metres.")]
        [SerializeField] private float barHeight = 0.18f;

        [Tooltip("World-space size of the armour pip chevron in metres.")]
        [SerializeField] private float pipSize = 0.32f;

        [Tooltip("Vertical gap between the pip and the bar, in metres.")]
        [SerializeField] private float pipGap = 0.28f;

        [Header("Shared material (GDD §9.4 — one material for all overlays)")]
        [Tooltip("Unlit, transparent, vertex-colour material shared by every quad. Built at runtime if null.")]
        [SerializeField] private Material sharedMaterial;

        [Header("Colours")]
        [SerializeField] private Color barBackColor = new Color(0.05f, 0.06f, 0.08f, 0.85f);
        [SerializeField] private Color barFillColor = new Color(0.3f, 0.9f, 0.4f, 1f);
        [SerializeField] private Color barFillLowColor = new Color(1f, 0.35f, 0.25f, 1f);

        [Header("Kill-streak combo labels (R2)")]
        [Tooltip("[TUNE] Seconds a combo label stays up (unscaled, like the HUD tweens).")]
        [SerializeField] private float comboLifetime = 0.9f;

        [Tooltip("[TUNE] Metres per second the combo label drifts upward while fading.")]
        [SerializeField] private float comboRiseSpeed = 1.4f;

        [Tooltip("[TUNE] World-space font size of the combo label (legibility bar: readable at 907×510).")]
        [SerializeField] private float comboFontSize = 5f;

        private Camera _cam;
        private Transform _root;
        private Mesh _quad;

        // Live overlays keyed by their enemy, plus a small free list for reuse.
        private readonly Dictionary<Enemy, Overlay> _active = new Dictionary<Enemy, Overlay>();
        private readonly Stack<Overlay> _pool = new Stack<Overlay>();
        private readonly List<Enemy> _toRemove = new List<Enemy>();

        // (Turret bars handled by WorldHealthBar attached to each turret.)

        private static readonly Color Unarmoured = new Color(0.75f, 0.78f, 0.8f, 1f);
        private static readonly Color Plated = new Color(0.95f, 0.78f, 0.25f, 1f);
        private static readonly Color Shielded = new Color(0.35f, 0.7f, 1f, 1f);

        private sealed class Overlay
        {
            public GameObject go;
            public Transform tr;
            public Transform pip;
            public Transform bar;      // bar background (parent of fill)
            public Transform fill;     // fill quad, scaled/offset by health fraction
            public MeshRenderer pipRenderer;
            public MeshRenderer barRenderer;
            public MeshRenderer fillRenderer;
            public MaterialPropertyBlock block;
            public float lastHealth;
            public float lastDamagedTime;
            public bool barShown;
        }

        private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorIdLegacy = Shader.PropertyToID("_Color");

        // Pooled world-space kill-streak combo labels (R2). Same doctrine as the
        // bars/pips: one manager, one LateUpdate, pooled, no per-label component.
        private sealed class ComboLabel
        {
            public GameObject go;
            public Transform tr;
            public TextMeshPro tmp;
            public float age;
        }

        private readonly List<ComboLabel> _comboActive = new List<ComboLabel>();
        private readonly Stack<ComboLabel> _comboPool = new Stack<ComboLabel>();
        private GameManager _gm;

        private void Awake()
        {
            _cam = Camera.main;
            _quad = BuildQuad();
            if (sharedMaterial == null)
                sharedMaterial = BuildSharedMaterial();

            var rootGo = new GameObject("Overlays");
            rootGo.transform.SetParent(transform, false);
            _root = rootGo.transform;
        }

        private void OnEnable()
        {
            _gm = GameManager.Instance;
            if (_gm != null)
                _gm.OnStreakChanged += HandleStreakChanged;
        }

        private void Start()
        {
            // Late-bind in case GameManager was not ready during OnEnable.
            if (_gm == null)
            {
                _gm = GameManager.Instance;
                if (_gm != null)
                    _gm.OnStreakChanged += HandleStreakChanged;
            }
        }

        private void OnDisable()
        {
            if (_gm != null)
                _gm.OnStreakChanged -= HandleStreakChanged;
        }

        private void LateUpdate()
        {
            if (_cam == null)
                _cam = Camera.main;

            // 1. Add overlays for newly-live enemies.
            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive)
                    continue;
                if (!_active.ContainsKey(e))
                    Acquire(e);
            }

            // 2. Update every active overlay, and mark dead/removed for reclaim.
            _toRemove.Clear();
            Quaternion face = _cam != null
                ? Quaternion.LookRotation(_cam.transform.forward, _cam.transform.up)
                : Quaternion.identity;

            foreach (var kv in _active)
            {
                Enemy e = kv.Key;
                Overlay o = kv.Value;

                if (e == null || !e.IsAlive || !e.isActiveAndEnabled)
                {
                    _toRemove.Add(e);
                    continue;
                }

                // Billboard: single point where every overlay is oriented (one loop).
                Vector3 pos = e.HitPoint + Vector3.up * heightAboveHit;
                o.tr.SetPositionAndRotation(pos, face);

                UpdateHealth(e, o);
            }

            // 3. Reclaim overlays whose enemy is gone.
            for (int i = 0; i < _toRemove.Count; i++)
                Release(_toRemove[i]);

            // 4. Tick the combo labels (R2): rise, fade, billboard — unscaled so
            // the 2× toggle does not distort the readout (GDD §9.6).
            for (int i = _comboActive.Count - 1; i >= 0; i--)
            {
                ComboLabel label = _comboActive[i];
                if (!Usable(label))
                {
                    // Destroyed externally mid-flight — drop it, never pool it.
                    // Whole-label test for the same reason as the spawn path: a
                    // label with a live GameObject and a dead Transform would be
                    // pooled here and then throw on reuse.
                    _comboActive.RemoveAt(i);
                    continue;
                }
                label.age += Time.unscaledDeltaTime;
                if (label.age >= comboLifetime)
                {
                    label.go.SetActive(false);
                    _comboPool.Push(label);
                    _comboActive.RemoveAt(i);
                    continue;
                }
                label.tr.position += Vector3.up * (comboRiseSpeed * Time.unscaledDeltaTime);
                label.tr.rotation = face;
                float alpha = 1f - Mathf.Clamp01(label.age / comboLifetime);
                Color c = label.tmp.color;
                c.a = alpha;
                label.tmp.color = c;
            }

            // Turret health bars are handled by a self-contained WorldHealthBar
            // component attached in Tower.Build — not tracked here.
        }

        private void UpdateHealth(Enemy e, Overlay o)
        {
            float cur = e.CurrentHealth;
            float max = Mathf.Max(1f, e.MaxHealth);
            float frac = Mathf.Clamp01(cur / max);

            // Detect damage since last frame → show the bar and reset the timer.
            if (cur < o.lastHealth - 0.001f)
            {
                o.lastDamagedTime = Time.time;
                o.barShown = true;
            }
            o.lastHealth = cur;

            // The actual health bar is now drawn by the self-contained
            // WorldHealthBar component on each enemy, so the OverlayManager only
            // keeps the always-visible armour pip. Disable its bar quad entirely.
            //
            // OPEN (GDD §9.4): the "bar stays up N s after last damage, then
            // fades" rule went with the bar — WorldHealthBar draws always-on and
            // implements no fade. The barHideDelay/barFadeDuration fields that
            // used to sit here were dead (this path is hard-disabled) and were
            // removed; when the rule gets built, it belongs in WorldHealthBar.
            float barAlpha = 0f;
            bool barVisible = false;
            if (o.bar.gameObject.activeSelf != barVisible)
                o.bar.gameObject.SetActive(barVisible);

            if (barVisible)
            {
                // Fill scales from the left: shrink X and shift so it anchors left.
                var fillScale = o.fill.localScale;
                fillScale.x = Mathf.Max(0.0001f, frac);
                o.fill.localScale = fillScale;
                o.fill.localPosition = new Vector3(-(1f - frac) * 0.5f, 0f, -0.001f);

                Color back = barBackColor; back.a *= barAlpha;
                Color fillCol = Color.Lerp(barFillLowColor, barFillColor, frac); fillCol.a *= barAlpha;
                SetColor(o.barRenderer, o.block, back);
                SetColor(o.fillRenderer, o.block, fillCol);
            }

            // The PIP is always visible — set once on acquire; nothing to do here.
        }

        private void Acquire(Enemy e)
        {
            Overlay o = _pool.Count > 0 ? _pool.Pop() : BuildOverlay();
            o.go.SetActive(true);
            o.lastHealth = e.CurrentHealth;
            o.lastDamagedTime = -999f;
            o.barShown = false;
            o.bar.gameObject.SetActive(false);

            // Pip colour from armour type — set once, always visible (GDD §9.4).
            SetColor(o.pipRenderer, o.block, ArmourColor(e.ArmourType));

            _active[e] = o;
        }

        private void Release(Enemy e)
        {
            // NOTE: 'e' may be a destroyed Unity object that compares == null but is
            // NOT a real null reference. Passing it to Dictionary.TryGetValue throws
            // ArgumentNullException, so we must NEVER do that. Instead we find the
            // matching entry (including destroyed keys) by scanning the small map.
            Enemy matchKey = null;
            bool found = false;
            Overlay o = null;
            foreach (var kv in _active)
            {
                // ReferenceEquals for a live match; fake-null (destroyed) keys match e==null.
                if (ReferenceEquals(kv.Key, e) || (kv.Key == null))
                {
                    matchKey = kv.Key;
                    o = kv.Value;
                    found = true;
                    break;
                }
            }

            if (found)
            {
                _active.Remove(matchKey);
                if (o != null)
                {
                    o.go.SetActive(false);
                    _pool.Push(o);
                }
            }
        }

        // ----- Kill-streak combo labels (R2) -----

        private void HandleStreakChanged(int streak, int bonus, Vector3 worldPos)
        {
            // Guard against the destroyed-but-event-subscribed edge (scene
            // teardown frame) — same discipline as Release() above.
            if (this == null || _root == null)
                return;

            // Pooled labels can be destroyed out from under us (scene reloads,
            // hierarchy edits during play). A destroyed GameObject compares ==
            // null while the C# wrapper survives in the pool — discard those and
            // rebuild instead of touching a dead Transform.
            //
            // ALL THREE references are checked, not just the GameObject. Checking
            // only `go` is what shipped, and the field still threw
            // MissingReferenceException on `tr` during heavy chaining — a label
            // whose GameObject answered "alive" while its Transform was gone. I
            // could not reproduce which reference dies first, and that is exactly
            // the reason to validate the whole label rather than the one field I
            // happened to guess: a partially-dead label is not reusable, and the
            // cost of finding out is an exception thrown from inside Enemy.Die().
            ComboLabel label = null;
            while (_comboPool.Count > 0)
            {
                label = _comboPool.Pop();
                if (Usable(label))
                    break;
                label = null;
            }
            if (label == null)
                label = BuildComboLabel();
            if (!Usable(label))
                return;                 // rebuild itself failed — drop the label, never the kill

            label.age = 0f;
            label.go.SetActive(true);
            label.tr.position = worldPos + Vector3.up * (heightAboveHit + pipGap + 0.6f);

            var theme = UITheme.Instance;
            // Cyan → amber as the streak deepens, so a long streak reads hotter.
            Color cool = theme != null ? theme.cyan : Color.cyan;
            Color hot = theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f);
            label.tmp.color = Color.Lerp(cool, hot, Mathf.Clamp01((streak - 2) / 6f));
            label.tmp.text = bonus > 0 ? $"×{streak}  +{bonus}" : $"×{streak}";

            _comboActive.Add(label);
        }

        /// <summary>
        /// Every reference a combo label needs, alive. Unity's <c>==</c> reports
        /// destroyed objects as null, so this is a liveness test rather than a
        /// null test — and it covers the Transform and the text component, not
        /// just the GameObject that owns them.
        /// </summary>
        private static bool Usable(ComboLabel label) =>
            label != null && label.go != null && label.tr != null && label.tmp != null;

        private ComboLabel BuildComboLabel()
        {
            var label = new ComboLabel();
            label.go = new GameObject("ComboLabel");
            label.go.transform.SetParent(_root, false);
            label.tr = label.go.transform;

            label.tmp = label.go.AddComponent<TextMeshPro>();
            label.tmp.fontSize = comboFontSize;
            label.tmp.alignment = TextAlignmentOptions.Center;
            label.tmp.textWrappingMode = TextWrappingModes.NoWrap;
            var theme = UITheme.Instance;
            if (theme != null && theme.font != null)
                label.tmp.font = theme.font;
            label.tmp.fontStyle = FontStyles.Bold;
            label.tmp.sortingOrder = 100; // above the pips/bars

            var mr = label.go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            return label;
        }

        private static Color ArmourColor(Data.ArmourType armour)
        {
            switch (armour)
            {
                case Data.ArmourType.Plated: return Plated;
                case Data.ArmourType.Shielded: return Shielded;
                default: return Unarmoured;
            }
        }

        private Overlay BuildOverlay()
        {
            var o = new Overlay();
            o.go = new GameObject("Overlay");
            o.go.transform.SetParent(_root, false);
            o.tr = o.go.transform;
            o.block = new MaterialPropertyBlock();

            // Armour pip (always on).
            o.pip = MakeQuad("Pip", o.tr, new Vector3(0f, barHeight * 0.5f + pipGap, 0f), new Vector3(pipSize, pipSize, 1f), out o.pipRenderer);

            // Bar background (parent of fill), initially hidden.
            o.bar = MakeQuad("Bar", o.tr, Vector3.zero, new Vector3(barWidth, barHeight, 1f), out o.barRenderer);
            o.fill = MakeQuad("Fill", o.bar, Vector3.zero, new Vector3(1f, 0.72f, 1f), out o.fillRenderer);
            o.bar.gameObject.SetActive(false);

            return o;
        }

        private Transform MakeQuad(string name, Transform parent, Vector3 localPos, Vector3 localScale, out MeshRenderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _quad;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = sharedMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go.transform;
        }

        private void SetColor(MeshRenderer r, MaterialPropertyBlock block, Color c)
        {
            if (r == null)
                return;
            r.GetPropertyBlock(block);
            block.SetColor(ColorId, c);
            block.SetColor(ColorIdLegacy, c);
            r.SetPropertyBlock(block);
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "OverlayQuad" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        private static Material BuildSharedMaterial()
        {
            // Prefer URP unlit; fall back to the built-in unlit colour shader.
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader) { name = "OverlaySharedMat" };
            mat.enableInstancing = true;

            // Transparent, always-on-top-ish, no depth write so overlays never clip
            // into geometry oddly.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 50;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return mat;
        }
    }
}
