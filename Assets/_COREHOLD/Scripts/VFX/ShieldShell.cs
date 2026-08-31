using System.Collections.Generic;
using UnityEngine;

namespace Corehold.VFX
{
    /// <summary>
    /// A reusable, actor-agnostic fresnel "shell" bubble that wraps any GameObject's
    /// rendered body (VFX plan Tier 1). It is the shared visual payload behind both the
    /// enemy Shielded read (<see cref="Corehold.Enemies.ShieldAura"/>) and the tower
    /// empowered read (<see cref="Corehold.Towers.TowerShield"/>); the MEANING and the
    /// COLOUR are decided by the caller, so the same one-draw shell serves different
    /// gameplay signals without duplicating the (fiddly) bounds/sizing code.
    ///
    /// Why this lives in its own component:
    ///   • <b>One transparent draw, no particles, no lights</b> — cheap on the WebGL,
    ///     fill-rate-bound target even with several concurrent shells.
    ///   • <b>Sized from ACTUAL renderer bounds every frame</b> (LateUpdate), because a
    ///     SkinnedMeshRenderer reports uninitialised bounds on the spawn frame — the
    ///     bug that once shrank the enemy shell to a tiny ball at runtime. Measuring in
    ///     LateUpdate also tracks animation-driven bounds for free.
    ///   • <b>Shared sphere mesh + per-colour shared materials</b> — no per-instance
    ///     allocation beyond the single child object.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShieldShell : MonoBehaviour
    {
        private const float RimPower = 2.5f;
        private const float Opacity = 0.6f;

        // Shell diameter as a multiple of the body's largest bounds dimension — a touch
        // larger than the hull so it visibly wraps the actor rather than clipping.
        private const float RadiusScale = 1.35f;

        private static Mesh _sharedMesh;
        // One shared material per colour so many shells of the same colour batch and
        // never allocate per instance.
        private static readonly Dictionary<Color, Material> _materialsByColor = new Dictionary<Color, Material>();

        private Transform _shell;
        private MeshRenderer _renderer;
        private bool _visible;

        // Body renderers cached once when the shell is built, so the per-frame resize
        // does not allocate. The shell's own renderer and the health-bar quads are
        // filtered out here (see CacheBodyRenderers), not re-tested each frame.
        private Renderer[] _bodyRenderers;

        /// <summary>Fallback radius (m) used only when the actor has no measurable renderers.</summary>
        public float FallbackRadius { get; set; } = 0.6f;

        /// <summary>Show the shell in the given colour, (re)building it on first use.</summary>
        public void Show(Color color)
        {
            if (_shell == null)
                BuildShell();

            _renderer.sharedMaterial = MaterialFor(color);
            _visible = true;
            _shell.gameObject.SetActive(true);
            UpdateShell();
        }

        /// <summary>Hide the shell (kept for cheap re-show; not destroyed).</summary>
        public void Hide()
        {
            _visible = false;
            if (_shell != null)
                _shell.gameObject.SetActive(false);
        }

        private void BuildShell()
        {
            EnsureSharedMesh();

            var go = new GameObject("ShieldShell");
            go.transform.SetParent(transform, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _sharedMesh;

            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _shell = go.transform;

            CacheBodyRenderers();
        }

        private void LateUpdate()
        {
            if (_visible && _shell != null)
                UpdateShell();
        }

        /// <summary>
        /// Size and centre the shell from the actor's ACTUAL rendered bounds. Wraps
        /// every visible body mesh and centres on their combined centre-mass so it reads
        /// as a bubble around the whole model, independent of any navigation radius.
        /// </summary>
        private void UpdateShell()
        {
            if (_shell == null)
                return;

            if (!TryGetBodyBounds(out Bounds b))
            {
                float r = FallbackRadius * RadiusScale;
                _shell.position = transform.position + Vector3.up * r;
                _shell.localScale = ScaleForWorldDiameter(r * 2f);
                return;
            }

            float diameter = Mathf.Max(b.size.x, b.size.y, b.size.z) * RadiusScale;
            _shell.position = b.center;
            _shell.localScale = ScaleForWorldDiameter(diameter);
        }

        private void CacheBodyRenderers()
        {
            // The health bar's quad sub-tree, if present. We exclude ONLY this sub-tree,
            // not "anything under a WorldHealthBar": that component lives on the actor
            // ROOT, so GetComponentInParent would match every body renderer and leave us
            // with nothing to measure (the bug that shrank the shell at runtime).
            var healthBar = GetComponentInChildren<Corehold.UI.WorldHealthBar>(true);
            Transform barRoot = healthBar != null ? healthBar.BarRoot : null;

            var all = GetComponentsInChildren<Renderer>(true);
            var list = new List<Renderer>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null)
                    continue;
                if (_shell != null && r.transform.IsChildOf(_shell))
                    continue;
                if (barRoot != null && r.transform.IsChildOf(barRoot))
                    continue;
                list.Add(r);
            }
            _bodyRenderers = list.ToArray();
        }

        /// <summary>
        /// Combined world-space bounds of the cached body renderers. Returns false only
        /// when there are no body renderers at all.
        /// </summary>
        private bool TryGetBodyBounds(out Bounds bounds)
        {
            bounds = default;
            if (_bodyRenderers == null || _bodyRenderers.Length == 0)
                CacheBodyRenderers();

            bool any = false;
            for (int i = 0; i < _bodyRenderers.Length; i++)
            {
                var r = _bodyRenderers[i];
                if (r == null)
                    continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>
        /// The shared mesh is a unit-diameter sphere. Convert a desired WORLD diameter
        /// into the localScale that produces it, cancelling out any parent scale so the
        /// shell is a true sphere of the intended size regardless of the actor's scale.
        /// </summary>
        private Vector3 ScaleForWorldDiameter(float worldDiameter)
        {
            Vector3 parent = transform.lossyScale;
            return new Vector3(
                worldDiameter / Mathf.Max(0.0001f, parent.x),
                worldDiameter / Mathf.Max(0.0001f, parent.y),
                worldDiameter / Mathf.Max(0.0001f, parent.z));
        }

        private static void EnsureSharedMesh()
        {
            if (_sharedMesh != null)
                return;
            // Borrow Unity's built-in sphere mesh (unit diameter). No asset to author.
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sharedMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Destroy(tmp); else DestroyImmediate(tmp);
        }

        private static Material MaterialFor(Color color)
        {
            if (_materialsByColor.TryGetValue(color, out var mat) && mat != null)
                return mat;

            // The fresnel shader is found BY NAME — no serialized asset references
            // it, every material here is created at runtime — so the shader file
            // lives under a Resources/ folder: that is the only thing making the
            // BUILD include it. When it lived outside Resources, players got the
            // fallback below on every shell (Shader.Find null in the build, fine
            // in the editor) — the "force field is an opaque sphere" bug.
            Shader shader = Shader.Find("COREHOLD/ShieldFresnel");
            if (shader != null)
            {
                mat = new Material(shader) { name = $"COREHOLD_ShieldFresnel ({color})" };
                mat.SetColor("_RimColor", color);
                mat.SetFloat("_RimPower", RimPower);
                mat.SetFloat("_Opacity", Opacity);
            }
            else
            {
                // Last-resort degrade: a see-through additive tint, never an opaque
                // shell. The FULL transparent surface state is set explicitly — a
                // material configured from code gets none of it from the shader GUI
                // (same lesson as the weather precipitation material).
                Shader unlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (unlit == null) unlit = Shader.Find("Sprites/Default");
                mat = new Material(unlit) { name = $"COREHOLD_ShieldFresnel-fallback ({color})" };
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                if (mat.HasProperty("_SrcBlend"))
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend"))
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                Color tint = color;
                tint.a = 0.28f;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
                else if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            }

            _materialsByColor[color] = mat;
            return mat;
        }
    }
}
