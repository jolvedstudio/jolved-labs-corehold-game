using System.Collections.Generic;
using CartoonFX;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Central spawner for every one-shot visual effect in the game (GDD §11).
    ///
    /// Twelve pooled effects — three muzzle flashes (one per damage type), an impact
    /// spark, two explosion sizes for splash weapons, an enemy death burst, a Core
    /// hit flash, a build-placement puff, the two status pulses (stun crackle / slow
    /// chill, R18) and the Strike Wing EM burst (R19) — plus a pooled hitscan tracer
    /// for the Autocannon and Arc Node. Missile and Mortar have visible travel time
    /// and need no tracer.
    ///
    /// The director is provider-agnostic: it pools ANY prefab whose hierarchy
    /// contains at least one Shuriken <see cref="ParticleSystem"/> — Cartoon FX
    /// Remaster, Epic Toon FX, or any other Shuriken-based pack all work the same
    /// way. The whole prefab root is cloned (not a single component), so multi-system
    /// effect hierarchies are reproduced in full.
    ///
    /// Every effect is pooled through <see cref="CoreholdPool{T}"/>; nothing on a
    /// gameplay path calls Instantiate or Destroy during a wave (GDD §11). A
    /// <see cref="PooledEffect"/> watcher returns each instance to its pool once all
    /// of its particle systems have finished — so pool counts stay stable and the
    /// effect layer allocates nothing after warm-up (the ticket's done-condition).
    /// When a prefab happens to carry a Cartoon FX <see cref="CFXR_Effect"/>, its
    /// clear-behaviour is neutralised so pooling owns the lifetime; prefabs without
    /// one are driven purely by their particle systems.
    ///
    /// CFXR's global animated-light switch is turned OFF at boot
    /// (<see cref="CFXR_Effect.GlobalDisableLights"/> = true): no CFXR effect spawns a
    /// light, per the ticket. Camera shake is left to the effect prefabs / §3.3.
    ///
    /// Access is via the <see cref="Instance"/> singleton. All the public Play*
    /// methods no-op safely when the matching prefab is unassigned, so gameplay
    /// never breaks if art has not been wired yet.
    /// </summary>
    [DisallowMultipleComponent]
    public class VFXDirector : MonoBehaviour
    {
        /// <summary>The nine pooled one-shot effects (GDD §11).</summary>
        public enum Effect
        {
            /// <summary>Muzzle flash for a Kinetic weapon (Autocannon).</summary>
            MuzzleKinetic,
            /// <summary>Muzzle flash for an Energy weapon (Arc Node).</summary>
            MuzzleEnergy,
            /// <summary>Muzzle flash for an Explosive weapon (Missile / Mortar).</summary>
            MuzzleExplosive,
            /// <summary>A small spark where a hitscan or projectile strikes a unit.</summary>
            ImpactSpark,
            /// <summary>Smaller splash explosion (Missile Battery).</summary>
            ExplosionSmall,
            /// <summary>Larger splash explosion (Siege Mortar / tier-3 splash).</summary>
            ExplosionLarge,
            /// <summary>Burst played when an enemy dies.</summary>
            EnemyDeath,
            /// <summary>Flash on the Core when it takes a leak hit.</summary>
            CoreHit,
            /// <summary>Puff played when a turret is built on a hardpoint.</summary>
            BuildPuff,
            /// <summary>Crackle pulsed (~1/s) over a stunned unit (R18).</summary>
            Stun,
            /// <summary>Chill glow pulsed (~1/s) over a slowed unit (R18).</summary>
            Slow,
            /// <summary>Strike Wing EM burst at the telegraphed point (R19).</summary>
            StrikeWingBurst,
            /// <summary>Amplified impact burst for a super-effective (countered) hit (R22 — counter readability).</summary>
            ImpactStrong,
            /// <summary>Muted deflection spark for a resisted (mismatched) hit (R22 — counter readability).</summary>
            ImpactWeak,
            /// <summary>Energy ripple when a shot strikes a Shielded enemy (R22 — counter readability).</summary>
            ShieldHit,
            /// <summary>Splash explosion tinted for a Kinetic weapon (VFX color-language rule). Optional; falls back to the size-based explosion when unassigned.</summary>
            ExplosionKinetic,
            /// <summary>Splash explosion tinted for an Energy weapon (VFX color-language rule). Optional; falls back to the size-based explosion when unassigned.</summary>
            ExplosionEnergy,
            /// <summary>Splash explosion tinted for an Explosive weapon (VFX color-language rule). Optional; falls back to the size-based explosion when unassigned.</summary>
            ExplosionExplosive,
            /// <summary>Per-unit materialisation flash at the spawn point (VFX plan Tier 1 — the per-spawn option of the portal item, which works with staggered group spawns). Optional; unassigned = no flash.</summary>
            SpawnFlash,
            /// <summary>One-shot portal/gate effect at each spawner a wave uses, played once at wave start. Optional; unassigned = no portal.</summary>
            SpawnPortal,
            /// <summary>Target marker at the Strike Wing's committed strike point, layered over the telegraph ring (a FRIENDLY marker — never a danger warning, per the VFX review). Optional; unassigned = ring only.</summary>
            StrikeMarker
        }

        [System.Serializable]
        private struct EffectEntry
        {
            [Tooltip("Which logical effect this prefab fulfils (GDD §11).")]
            public Effect id;

            [Tooltip("Any Shuriken-based effect prefab (Cartoon FX, Epic Toon FX, etc.). It must contain at least one ParticleSystem in its hierarchy. If it carries a CFXR_Effect its clear-behaviour is neutralised so it can be pooled.")]
            public GameObject prefab;

            [Tooltip("How many copies to prewarm so the first play never allocates.")]
            public int prewarm;
        }

        [Header("Effects — THIS scene's list is what plays (pools rebuild from it every run)")]
        [Tooltip("The runtime source of truth for THIS scene: pools are built from this list in Awake, every run. The VFXDirectorConfig ASSET is only a design-time template that tools stamp into scenes (Tools → COREHOLD → VFX → Apply VFX Config To Open Scene(s)) — it is never read at runtime. To apply changes made DURING play: right-click this component's header → 'Rebuild Pools From Inspector'.")]
        [SerializeField]
        private EffectEntry[] effects =
        {
            new EffectEntry { id = Effect.MuzzleKinetic,   prewarm = 4 },
            new EffectEntry { id = Effect.MuzzleEnergy,     prewarm = 4 },
            new EffectEntry { id = Effect.MuzzleExplosive,  prewarm = 4 },
            new EffectEntry { id = Effect.ImpactSpark,      prewarm = 8 },
            new EffectEntry { id = Effect.ExplosionSmall,   prewarm = 4 },
            new EffectEntry { id = Effect.ExplosionLarge,   prewarm = 4 },
            new EffectEntry { id = Effect.EnemyDeath,       prewarm = 6 },
            new EffectEntry { id = Effect.CoreHit,          prewarm = 2 },
            new EffectEntry { id = Effect.BuildPuff,        prewarm = 2 },
            new EffectEntry { id = Effect.Stun,             prewarm = 6 },
            new EffectEntry { id = Effect.Slow,             prewarm = 6 },
            new EffectEntry { id = Effect.StrikeWingBurst,  prewarm = 2 },
            new EffectEntry { id = Effect.ImpactStrong,     prewarm = 8 },
            new EffectEntry { id = Effect.ImpactWeak,       prewarm = 8 },
            new EffectEntry { id = Effect.ShieldHit,        prewarm = 6 },
            new EffectEntry { id = Effect.ExplosionKinetic,   prewarm = 4 },
            new EffectEntry { id = Effect.ExplosionEnergy,    prewarm = 4 },
            new EffectEntry { id = Effect.ExplosionExplosive, prewarm = 4 },
            new EffectEntry { id = Effect.SpawnFlash,         prewarm = 6 },
            new EffectEntry { id = Effect.SpawnPortal,        prewarm = 2 },
            new EffectEntry { id = Effect.StrikeMarker,       prewarm = 1 },
        };

        /// <summary>
        /// Which side fired a tracer. Each faction has its OWN width / glow / colour
        /// so tower fire and enemy fire read as a distinct visual language (GDD §11):
        /// cool blue friendly bolts vs. hot red hostile bolts, tuned independently.
        /// </summary>
        public enum TracerFaction { Friendly, Hostile }

        [Header("Hitscan tracer (GDD §11) — Autocannon + Arc Node only")]
        [Tooltip("ADDITIVE material for the tracer's white-hot CORE line (the thin inner streak that Bloom smears into glow). Uses the Corehold/VFXTracer shader with SrcAlpha+One blending. Built at runtime from that shader if left null.")]
        [SerializeField] private Material tracerCoreMaterial;

        [Tooltip("ALPHA-BLEND material for the tracer's coloured HALO line (the wide outer streak that carries the saturated faction hue). Uses the Corehold/VFXTracer shader with SrcAlpha+OneMinusSrcAlpha blending so the hue is PRESERVED over the bright desert instead of washing to white like additive does. Built at runtime from that shader if left null.")]
        [SerializeField] private Material tracerHaloMaterial;

        [Min(1f)]
        [Tooltip("How much wider the coloured halo line is than the core line. The core carries the glow; the halo carries the hue.")]
        [SerializeField] private float tracerHaloWidthScale = 3f;

        [Min(0f)]
        [Tooltip("HDR brightness of the white-hot core. Keep moderate (~1.5) so Bloom lights it up without ACES/Neutral tonemapping desaturating the surrounding halo hue.")]
        [SerializeField] private float tracerCoreGlow = 1.6f;

        [Tooltip("Copies of the tracer prewarmed into its pool (shared by both factions).")]
        [SerializeField] private int tracerPrewarm = 8;

        [Header("Diagnostics")]
        [Tooltip("Log ONE line the first time each effect slot plays: how many particles it actually spawned, " +
                 "how many renderers are enabled, its world scale, where it landed, whether that point is on " +
                 "screen, and which shader(s) it draws with. This is the only way to answer \"the effect did " +
                 "not appear\" in a BUILD, where there is no inspector to look at. Silent after each slot's " +
                 "first play (at most one line per slot, per run).")]
        [SerializeField] private bool logFirstPlayPerEffect = true;

        private readonly HashSet<Effect> _diagnosed = new HashSet<Effect>();

        [Header("Friendly tracer (tower fire)")]
        [Tooltip("Friendly (tower) tracer line width in metres.")]
        [SerializeField] private float friendlyTracerWidth = 0.08f;

        [Min(0f)]
        [Tooltip("Brightness multiplier applied to the friendly tracer's HDR colour. 1 = colour unchanged; higher blooms more. Keep moderate so the hue survives ACES tonemapping.")]
        [SerializeField] private float friendlyTracerGlow = 1f;

        [ColorUsage(true, true)]
        [Tooltip("Friendly (tower) tracer colour. Cool blue faction identity. This is the HALO hue — keep it moderately bright (one dominant channel) so alpha-blending preserves the blue instead of washing to white.")]
        [SerializeField] private Color friendlyTracerColor = new Color(0.15f, 0.55f, 1.8f, 1f);

        [Header("Hostile tracer (enemy fire)")]
        [Tooltip("Hostile (enemy) tracer line width in metres.")]
        [SerializeField] private float hostileTracerWidth = 0.08f;

        [Min(0f)]
        [Tooltip("Brightness multiplier applied to the hostile tracer's HDR colour. 1 = colour unchanged; higher blooms more. Keep moderate so the hue survives ACES tonemapping.")]
        [SerializeField] private float hostileTracerGlow = 1f;

        [ColorUsage(true, true)]
        [Tooltip("Hostile (enemy) tracer colour. Hot red faction identity. This is the HALO hue — keep it moderately bright (one dominant channel) so alpha-blending preserves the red over the bright desert instead of washing to white.")]
        [SerializeField] private Color hostileTracerColor = new Color(1.8f, 0.12f, 0.06f, 1f);

        // ----- Singleton -----

        private static VFXDirector _instance;

        /// <summary>The active director, if one exists in the scene.</summary>
        public static VFXDirector Instance => _instance;

        // ----- Pools -----

        // One pool per assigned prefab, keyed by the logical Effect id. Pools clone
        // the whole prefab ROOT (a Transform) rather than a single component, so any
        // Shuriken-based effect — however many particle systems its hierarchy has —
        // is reproduced in full.
        private readonly Dictionary<Effect, CoreholdPool<Transform>> _pools =
            new Dictionary<Effect, CoreholdPool<Transform>>();

        private Transform _poolRoot;
        private Transform _tracerRoot;
        private CoreholdPool<VfxTracer> _tracerPool;
        private VfxTracer _tracerPrefab;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                if (_instance.gameObject.scene == gameObject.scene)
                {
                    // Two directors in ONE scene is a setup error; the first wins.
                    Debug.LogWarning("[VFXDirector] Duplicate director in this scene — destroying the extra.", this);
                    Destroy(this);
                    return;
                }
                // A leftover from ANOTHER scene (a persistent object). The current
                // scene's own inspector setup must rule — replace the old director.
                Debug.LogWarning(
                    $"[VFXDirector] Replacing leftover director from scene '{_instance.gameObject.scene.name}' — " +
                    "this scene's own setup takes over.", this);
                Destroy(_instance.gameObject);
            }
            _instance = this;

            // The ticket is explicit: no effect spawns a light. Turn CFXR's global
            // animated-light switch off for the whole run (GDD §11).
            CFXR_Effect.GlobalDisableLights = true;

            BuildPools();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        // ================================================================================
        //  Pool construction
        // ================================================================================

        private void BuildPools()
        {
            if (_poolRoot == null)
            {
                var go = new GameObject("Pool_VFX");
                go.transform.SetParent(transform, false);
                _poolRoot = go.transform;
            }

            if (effects != null)
            {
                // LAST entry per id wins. The inspector's + button DUPLICATES an
                // element, so "swap a slot's prefab" is naturally authored as a
                // new row below the old one — and the previous first-wins skip
                // silently ignored it, which read as "the scene's own setup does
                // not apply". An override is called out loud instead.
                var byId = new Dictionary<Effect, EffectEntry>();
                foreach (var entry in effects)
                {
                    if (entry.prefab == null)
                        continue;
                    if (byId.TryGetValue(entry.id, out var prev) && prev.prefab != entry.prefab)
                        Debug.LogWarning(
                            $"[VFXDirector] Duplicate '{entry.id}' slot: using the LAST entry " +
                            $"('{entry.prefab.name}'), ignoring '{prev.prefab.name}' — remove the stale row.",
                            this);
                    byId[entry.id] = entry;
                }

                foreach (var entry in byId.Values)
                {
                    Transform prefabRoot = PreparePrefab(entry.prefab);
                    if (prefabRoot == null)
                    {
                        Debug.LogWarning(
                            $"[VFXDirector] Effect '{entry.id}' prefab '{entry.prefab.name}' has no " +
                            "ParticleSystem in its hierarchy — no pool was built. Assign a Shuriken-based " +
                            "effect prefab.", entry.prefab);
                        continue;
                    }

                    var parentGo = new GameObject($"Pool_{entry.id}");
                    parentGo.transform.SetParent(_poolRoot, false);

                    int prewarm = Mathf.Max(0, entry.prewarm);
                    var pool = new CoreholdPool<Transform>(prefabRoot, parentGo.transform, prewarm);
                    _pools[entry.id] = pool;
                }
            }

            // Provability: every run states whose setup built the pools, so "which
            // list is live" is never a matter of inference again.
            Debug.Log($"[VFXDirector] Pools built from scene '{gameObject.scene.name}' inspector setup: " +
                      $"{_pools.Count} slot(s) live — {string.Join(", ", _pools.Keys)}.", this);

            // A director with ZERO usable slots plays NOTHING — no explosions, no
            // impacts, no portals — while code-generated tracers still work, which
            // reads as "VFX broken in the build". Loud, with the fix, because this
            // is exactly what a stale scene baked before the config was localized
            // looks like (the browser console shows this line on WebGL).
            if (_pools.Count == 0)
                Debug.LogError($"[VFXDirector] Scene '{gameObject.scene.name}' has NO usable effect slots — " +
                               "explosions/impacts/portals will not appear. Open the scene, run " +
                               "Tools → COREHOLD → VFX → Apply VFX Config To Open Scenes, save, and " +
                               "regenerate/rebuild.", this);

            BuildTracerPool();
        }

        /// <summary>
        /// Validate that the prefab is a usable Shuriken-based effect and return the
        /// root <see cref="Transform"/> to clone into the pool. A prefab qualifies if
        /// its hierarchy contains at least one <see cref="ParticleSystem"/> — that
        /// covers Cartoon FX, Epic Toon FX and any other Shuriken pack. Returns null
        /// (and the caller logs) when no particle system is present.
        ///
        /// Cloning the whole prefab root (rather than one component) preserves
        /// multi-system hierarchies, which most non-trivial effects rely on.
        /// </summary>
        private static Transform PreparePrefab(GameObject prefab)
        {
            if (prefab == null)
                return null;
            if (prefab.GetComponentInChildren<ParticleSystem>(true) == null)
                return null;

            // VFX Graph rides compute shaders; WebGL has none. A pooled prefab
            // carrying a VisualEffect plays its Shuriken parts and silently
            // renders NOTHING from the graph in the browser ("Hidden/VFX/..."
            // shader errors in the console) — say so where the pool is built.
            if (prefab.GetComponentInChildren<UnityEngine.VFX.VisualEffect>(true) != null)
                Debug.LogWarning($"[VFXDirector] Pooled effect '{prefab.name}' contains a VFX GRAPH — it will " +
                                 "render nothing on WebGL (no compute shaders). Replace it with a Shuriken effect; " +
                                 "the WebGL Shader Audit flags these as errors.", prefab);

            return prefab.transform;
        }

        /// <summary>
        /// Disable every real-time <see cref="Light"/> in a pooled effect instance's
        /// hierarchy. The ticket forbids VFX-spawned lights (GDD §11) — CFXR honours
        /// this through its GlobalDisableLights switch, but the provider-agnostic pool
        /// clones prefabs from any pack (e.g. Epic Toon FX) whose effects commonly embed
        /// point Lights. On the WebGL, fill-rate-bound target a handful of concurrent
        /// per-pixel lights is a real cost, so they are switched off once when the
        /// instance is first played.
        /// </summary>
        private static void DisableEmbeddedLights(Transform root)
        {
            var lights = root.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
                lights[i].enabled = false;
        }

        /// <summary>
        /// Play-mode iteration: re-read THIS component's inspector list and rebuild
        /// the effect pools from it (right-click the component header → this).
        /// Old pool parents are deliberately kept so in-flight instances release
        /// cleanly; all NEW plays come from the rebuilt pools.
        /// </summary>
        [ContextMenu("Rebuild Pools From Inspector")]
        private void RebuildPoolsFromInspector()
        {
            _pools.Clear();
            BuildPools();
        }

        private void BuildTracerPool()
        {
            if (_tracerPool != null)
                return;

            EnsureTracerMaterials();

            var rootGo = new GameObject("Pool_Tracers");
            rootGo.transform.SetParent(transform, false);
            _tracerRoot = rootGo.transform;

            _tracerPrefab = BuildTracerPrefab();
            _tracerPool = new CoreholdPool<VfxTracer>(_tracerPrefab, _tracerRoot, Mathf.Max(0, tracerPrewarm));
        }

        /// <summary>
        /// Build the two shared tracer materials from the purpose-built
        /// <c>Corehold/VFXTracer</c> shader when they were not authored as assets.
        ///
        /// Two blend models on ONE shader:
        ///   • CORE — ADDITIVE (SrcAlpha, One): the thin white-hot inner streak.
        ///     Additive is exactly what a glowing hot line should do; Bloom smears
        ///     its brightness into the halo of light.
        ///   • HALO — ALPHA-BLEND (SrcAlpha, OneMinusSrcAlpha): the wide coloured
        ///     outer streak. Alpha-blend REPLACES the background toward the tracer's
        ///     hue instead of adding to it, so a saturated red/blue stays red/blue
        ///     over the bright desert instead of washing to white the way a single
        ///     additive line does.
        ///
        /// We build both from the SAME shader (which exposes _SrcBlend/_DstBlend as
        /// real material properties) so the blend state is reliable — unlike the old
        /// approach of SetFloat-ing _Surface/_Blend on a URP shader, which does not
        /// actually change the render state at runtime.
        /// </summary>
        private void EnsureTracerMaterials()
        {
            Shader shader = Shader.Find("Corehold/VFXTracer");

            if (tracerCoreMaterial == null)
            {
                Shader s = shader != null ? shader : Shader.Find("Sprites/Default");
                tracerCoreMaterial = new Material(s) { name = "VFX_Tracer_Core_Additive (shared)" };
                if (tracerCoreMaterial.HasProperty("_SrcBlend")) tracerCoreMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (tracerCoreMaterial.HasProperty("_DstBlend")) tracerCoreMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                tracerCoreMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            if (tracerHaloMaterial == null)
            {
                Shader s = shader != null ? shader : Shader.Find("Sprites/Default");
                tracerHaloMaterial = new Material(s) { name = "VFX_Tracer_Halo_AlphaBlend (shared)" };
                if (tracerHaloMaterial.HasProperty("_SrcBlend")) tracerHaloMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (tracerHaloMaterial.HasProperty("_DstBlend")) tracerHaloMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                // Draw the halo just BEFORE the additive core so the bright core
                // sits visually on top of the coloured halo.
                tracerHaloMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 1;
            }
        }

        private VfxTracer BuildTracerPrefab()
        {
            var go = new GameObject("VfxTracer");
            go.transform.SetParent(_tracerRoot, false);
            go.SetActive(false);

            // Wide coloured HALO line (child, drawn first / underneath).
            var haloGo = new GameObject("Halo");
            haloGo.transform.SetParent(go.transform, false);
            LineRenderer halo = ConfigureTracerLine(haloGo, tracerHaloMaterial, friendlyTracerWidth * tracerHaloWidthScale);

            // Thin white-hot CORE line (child, drawn second / on top).
            var coreGo = new GameObject("Core");
            coreGo.transform.SetParent(go.transform, false);
            LineRenderer core = ConfigureTracerLine(coreGo, tracerCoreMaterial, friendlyTracerWidth);

            var tracer = go.AddComponent<VfxTracer>();
            tracer.Configure(core, halo, tracerHaloWidthScale, tracerCoreGlow);
            return tracer;
        }

        private static LineRenderer ConfigureTracerLine(GameObject go, Material material, float width)
        {
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.widthMultiplier = width; // per-shot Play() overrides this
            lr.numCapVertices = 2;
            lr.numCornerVertices = 0;
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            lr.sharedMaterial = material;
            return lr;
        }

        // ================================================================================
        //  Public play API
        // ================================================================================

        /// <summary>
        /// Play a one-shot effect at a world position (default orientation). Safe to
        /// call when the effect's prefab is unassigned — it simply no-ops. Returns
        /// the pooled instance, or null when nothing was spawned.
        /// </summary>
        public ParticleSystem Play(Effect effect, Vector3 position)
        {
            return Play(effect, position, Quaternion.identity, 1f);
        }

        /// <summary>Play a one-shot effect at a position facing a direction.</summary>
        public ParticleSystem Play(Effect effect, Vector3 position, Vector3 forward)
        {
            Quaternion rot = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
            return Play(effect, position, rot, 1f);
        }

        /// <summary>
        /// Play a one-shot effect with an explicit rotation and uniform scale. The
        /// pooled instance is positioned, its clear-behaviour forced to Disable, its
        /// particle systems replayed, and a <see cref="PooledEffect"/> watcher armed
        /// to return it to the pool when it finishes (GDD §11).
        /// </summary>
        public ParticleSystem Play(Effect effect, Vector3 position, Quaternion rotation, float scale)
        {
            if (!_pools.TryGetValue(effect, out CoreholdPool<Transform> pool) || pool == null)
                return null;

            Transform t = pool.Get();
            if (t == null)
                return null;

            // The watcher must exist BEFORE localScale is touched: it captures the
            // prefab's AUTHORED scale once, and capturing it after the line below
            // would record whatever this call asked for instead. That inversion is
            // why authored effect sizes were being replaced by a flat 1 (and any
            // caller-requested scale silently dropped on every later play).
            var watcher = t.GetComponent<PooledEffect>();
            if (watcher == null)
            {
                // First time this pooled instance is played. CFXR's GlobalDisableLights
                // only muzzles CFXR effects; the agnostic pool clones ANY prefab, and
                // packs like Epic Toon FX embed real-time Lights that would otherwise put
                // per-pixel lights back on the (WebGL, fill-rate-bound) target. Strip them
                // once here — the PooledEffect persists across releases, so this never runs
                // again for the same instance.
                DisableEmbeddedLights(t);
                watcher = t.gameObject.AddComponent<PooledEffect>();
                watcher.CaptureAuthoredScale();
            }

            t.SetPositionAndRotation(position, rotation);

            // If this happens to be a Cartoon FX effect, neutralise its own clear
            // pass so pooling owns the lifetime (otherwise CFXR would Destroy or
            // Disable the object out from under the pool, leaking the count). Prefabs
            // from any other pack simply have no CFXR_Effect and are skipped.
            var cfxr = t.GetComponent<CFXR_Effect>();
            if (cfxr == null)
                cfxr = t.GetComponentInChildren<CFXR_Effect>(true);
            if (cfxr != null)
            {
                cfxr.clearBehavior = CFXR_Effect.ClearBehavior.None;
                cfxr.ResetState();
            }

            // Restart every particle system so a reused instance plays from frame 0.
            var systems = t.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                systems[i].Clear(true);
                systems[i].Play(true);
            }

            // Arm the watcher that returns this instance to its pool once every
            // particle system has finished (provider-agnostic — no CFXR dependency).
            // The requested scale rides ALONG so Arm can apply it over the authored
            // scale instead of overwriting both with a stale baseline.
            watcher.Arm(pool, t, systems, scale);

            if (logFirstPlayPerEffect && _diagnosed.Add(effect))
                StartCoroutine(ReportFirstPlay(effect, t, systems));

            // Return the root particle system as a convenience handle for callers.
            return systems.Length > 0 ? systems[0] : null;
        }

        /// <summary>
        /// One-shot per-slot report, taken a frame AFTER the play so the particle
        /// count is real. Everything a "nothing appeared" bug can hide behind is
        /// in the line: particles spawned, renderers enabled, world scale (a zero
        /// or micro scale is invisible), position + on-screen test (an effect
        /// played off-camera is not a rendering bug), and the shader actually
        /// bound (a stripped shader shows up here as the error shader).
        /// </summary>
        private System.Collections.IEnumerator ReportFirstPlay(Effect effect, Transform t, ParticleSystem[] systems)
        {
            yield return null;
            if (t == null)
                yield break;

            int alive = 0;
            for (int i = 0; i < systems.Length; i++)
                if (systems[i] != null) alive += systems[i].particleCount;

            var renderers = t.GetComponentsInChildren<Renderer>(true);
            int enabled = 0;
            var shaders = new List<string>();
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].enabled) enabled++;
                var mat = renderers[i].sharedMaterial;
                string s = mat == null ? "NO MATERIAL"
                    : mat.shader == null ? "NO SHADER" : mat.shader.name;
                if (!shaders.Contains(s)) shaders.Add(s);
            }

            Camera cam = Camera.main;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, t.position) : -1f;
            bool onScreen = false;
            if (cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(t.position);
                onScreen = vp.z > 0f && vp.x > -0.1f && vp.x < 1.1f && vp.y > -0.1f && vp.y < 1.1f;
            }

            Debug.Log($"[VFXDirector] first play '{effect}': {alive} particle(s) after 1 frame across " +
                      $"{systems.Length} system(s); {enabled}/{renderers.Length} renderer(s) enabled; " +
                      $"world scale {t.lossyScale.x:0.###}; at ({t.position.x:0.#}, {t.position.y:0.#}, " +
                      $"{t.position.z:0.#}), {dist:0.#} m from the camera, on screen: {onScreen}; " +
                      $"shader(s): {string.Join(" | ", shaders)}");
        }

        // ---- Convenience wrappers for gameplay call sites ----

        /// <summary>Muzzle flash for the given damage type at the muzzle point (GDD §11).</summary>
        public ParticleSystem PlayMuzzle(DamageType type, Vector3 position, Vector3 forward)
        {
            Effect e = type switch
            {
                DamageType.Kinetic => Effect.MuzzleKinetic,
                DamageType.Energy => Effect.MuzzleEnergy,
                DamageType.Explosive => Effect.MuzzleExplosive,
                _ => Effect.MuzzleKinetic
            };
            return Play(e, position, forward);
        }

        /// <summary>
        /// Impact spark where a shot strikes a unit (GDD §11), plus the standard
        /// small screen kick (R5 — CameraShake owns intensity and accessibility).
        /// </summary>
        public ParticleSystem PlayImpact(Vector3 position)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickImpact(position);
            return Play(Effect.ImpactSpark, position);
        }

        /// <summary>Damage multiplier at or above which a hit reads as super-effective (R22).</summary>
        public const float StrongHitThreshold = 1.25f;

        /// <summary>Damage multiplier at or below which a hit reads as resisted (R22).</summary>
        public const float WeakHitThreshold = 0.75f;

        /// <summary>
        /// Impact spark whose look encodes the counter system (GDD §7.1 — the game's
        /// core "visible counter" pillar). The damage-vs-armour <paramref name="multiplier"/>
        /// selects the effect so the player can SEE whether their pick countered the
        /// target: a big bright burst when super-effective, a muted deflection when
        /// resisted, and — for a Shielded target specifically — an energy ripple.
        /// Falls back to the neutral <see cref="Effect.ImpactSpark"/> otherwise, so it
        /// is always safe to call. Carries the same R5 screen kick as <see cref="PlayImpact"/>.
        /// </summary>
        public ParticleSystem PlayImpactEffective(Vector3 position, float multiplier, ArmourType armour)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickImpact(position);

            // A shot that barely scratches a Shielded enemy reads as an energy
            // deflection off the shield rather than a generic weak spark.
            if (armour == ArmourType.Shielded && multiplier <= WeakHitThreshold)
                return Play(Effect.ShieldHit, position);

            if (multiplier >= StrongHitThreshold)
                return Play(Effect.ImpactStrong, position);
            if (multiplier <= WeakHitThreshold)
                return Play(Effect.ImpactWeak, position);
            return Play(Effect.ImpactSpark, position);
        }

        /// <summary>
        /// Splash explosion sized by radius: the smaller effect below the threshold,
        /// the larger one at or above it (GDD §11 — "two explosion sizes for splash").
        /// Explosions carry the larger screen kick of the R5 impact standard.
        /// </summary>
        public ParticleSystem PlayExplosion(Vector3 position, float splashRadius)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickExplosion(position);
            Effect e = splashRadius >= LargeSplashThreshold ? Effect.ExplosionLarge : Effect.ExplosionSmall;
            return Play(e, position);
        }

        /// <summary>
        /// Splash explosion whose LOOK encodes the firing weapon's damage type (VFX
        /// color-language rule): a Kinetic/Energy/Explosive explosion shares its
        /// muzzle+tracer palette, so a kill reinforces which weapon scored it. The
        /// three damage-type palettes are kept distinct from the three armour-identity
        /// colours (Energy must NOT read as Shielded-blue) so this deepens the counter
        /// language rather than muddying it.
        ///
        /// The tinted slot is OPTIONAL: when its prefab is unassigned this falls back
        /// to the neutral size-based <see cref="PlayExplosion(Vector3,float)"/>, so it
        /// is always safe to call and never regresses an un-wired scene. Carries the
        /// same R5 explosion screen kick.
        /// </summary>
        public ParticleSystem PlayExplosion(Vector3 position, float splashRadius, DamageType type)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickExplosion(position);

            Effect typed = type switch
            {
                DamageType.Kinetic => Effect.ExplosionKinetic,
                DamageType.Energy => Effect.ExplosionEnergy,
                DamageType.Explosive => Effect.ExplosionExplosive,
                _ => Effect.ExplosionKinetic
            };

            // Use the typed explosion when its slot is wired; otherwise fall back to
            // the size-based neutral explosion (kick already applied above).
            if (HasPool(typed))
                return Play(typed, position);

            Effect sized = splashRadius >= LargeSplashThreshold ? Effect.ExplosionLarge : Effect.ExplosionSmall;
            return Play(sized, position);
        }

        /// <summary>Death burst when an enemy dies (GDD §11).</summary>
        public ParticleSystem PlayEnemyDeath(Vector3 position) => Play(Effect.EnemyDeath, position);

        /// <summary>
        /// Flash on the Core when it takes a leak hit (GDD §11), with the medium
        /// screen kick (R5). The trauma shake for Core hits stays where it was
        /// (Enemy.ReachCore → ShakeCoreHit); this is the sharper directional nudge.
        /// </summary>
        public ParticleSystem PlayCoreHit(Vector3 position)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickCoreHit(position);
            return Play(Effect.CoreHit, position);
        }

        /// <summary>Puff when a turret is built on a hardpoint (GDD §11).</summary>
        public ParticleSystem PlayBuildPuff(Vector3 position) => Play(Effect.BuildPuff, position);

        /// <summary>
        /// Stun crackle over a unit (R18). One-shot; <see cref="Corehold.Enemies.Enemy"/>
        /// re-fires it about once a second while the status runs, so the pool sees
        /// only ordinary one-shots and no looping-effect lifetime management.
        /// </summary>
        public ParticleSystem PlayStun(Vector3 position) => Play(Effect.Stun, position);

        /// <summary>Slow chill glow over a unit (R18). Pulsed the same way as the stun.</summary>
        public ParticleSystem PlaySlow(Vector3 position) => Play(Effect.Slow, position);

        /// <summary>Per-unit materialisation flash at a spawn point (silent until wired).</summary>
        public ParticleSystem PlaySpawnFlash(Vector3 position) => Play(Effect.SpawnFlash, position);

        /// <summary>One-shot portal at a spawner, facing its route direction (silent until wired).</summary>
        public ParticleSystem PlaySpawnPortal(Vector3 position, Vector3 forward) =>
            Play(Effect.SpawnPortal, position, forward);

        /// <summary>
        /// Persistent spawn portal: play the SpawnPortal effect and HOLD it open —
        /// the instance survives the pool's finished/timeout checks until the
        /// returned handle's <see cref="PooledEffect.EndHold"/> fades it out
        /// (WaveManager calls that once the spawner's last unit of the wave has
        /// actually appeared). Null when the slot is unwired, like every slot; a
        /// looping vortex prefab is the intended pick here.
        /// </summary>
        public PooledEffect PlaySpawnPortalOpen(Vector3 position, Vector3 forward,
            float sizeMultiplier = 1f, float pulseAmplitude = 0f, float pulseHz = 0f)
        {
            Quaternion rot = forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
            return PlaySpawnPortalOpen(position, rot, sizeMultiplier, pulseAmplitude, pulseHz);
        }

        /// <summary>Rotation-explicit variant: prefab authoring differs (some portal
        /// effects are gates facing +Z, some are GROUND RINGS lying flat), so the
        /// caller composes the exact orientation — WaveManager adds its [TUNE]
        /// euler offset on top of the spawner facing.</summary>
        public PooledEffect PlaySpawnPortalOpen(Vector3 position, Quaternion rotation,
            float sizeMultiplier = 1f, float pulseAmplitude = 0f, float pulseHz = 0f)
        {
            ParticleSystem ps = Play(Effect.SpawnPortal, position, rotation, 1f);
            if (ps == null)
                return null;
            PooledEffect fx = ps.GetComponentInParent<PooledEffect>();
            if (fx != null)
            {
                fx.SetSizeMultiplier(sizeMultiplier);
                fx.Hold(pulseAmplitude, pulseHz);
            }
            return fx;
        }

        /// <summary>Friendly target marker at the Strike Wing's committed point (silent until wired).</summary>
        public ParticleSystem PlayStrikeMarker(Vector3 position) => Play(Effect.StrikeMarker, position);

        /// <summary>
        /// Strike Wing EM burst (R19): the pooled effect scaled up to read at the
        /// 6 m ability radius, with the explosion-grade screen kick (R5).
        /// </summary>
        public ParticleSystem PlayStrikeWingBurst(Vector3 position)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickExplosion(position);
            return Play(Effect.StrikeWingBurst, position, Quaternion.identity, 2.2f);
        }

        /// <summary>Splash radius (m) at or above which the larger explosion plays.</summary>
        public const float LargeSplashThreshold = 4f;

        // ---- Hitscan tracer (Autocannon + Arc Node) ----

        /// <summary>
        /// Draw a pooled FRIENDLY (tower) hitscan tracer between two world points
        /// (GDD §11). The segment fades and returns itself to the pool. No-op if the
        /// tracer pool is not built.
        /// </summary>
        public void DrawTracer(Vector3 from, Vector3 to) =>
            DrawTracer(from, to, TracerFaction.Friendly, 1f);

        /// <summary>
        /// Draw a pooled hitscan tracer for a specific FACTION. Each faction supplies
        /// its OWN colour, glow (HDR brightness) and line width, so tower and enemy
        /// fire read distinctly (cool blue vs. hot red). <paramref name="alpha"/>
        /// scales the fade envelope — pass 0 to honour "no tracer" authoring.
        /// </summary>
        public void DrawTracer(Vector3 from, Vector3 to, TracerFaction faction, float alpha = 1f,
                               Transform follow = null)
        {
            if (_tracerPool == null || alpha <= 0f)
                return;

            VfxTracer tracer = _tracerPool.Get();
            if (tracer == null)
                return;

            Color color;
            float glowMul;
            float width;
            if (faction == TracerFaction.Hostile)
            {
                color = hostileTracerColor;
                glowMul = hostileTracerGlow;
                width = hostileTracerWidth;
            }
            else
            {
                color = friendlyTracerColor;
                glowMul = friendlyTracerGlow;
                width = friendlyTracerWidth;
            }

            // The faction colour drives the coloured HALO (alpha-blended, hue kept).
            // Its glow multiplier stays MODERATE on purpose — pushing every channel
            // bright is exactly what made the old single additive line wash to white
            // under ACES/Neutral tonemapping. The white-hot CORE (built inside the
            // tracer at tracerCoreGlow) supplies the Bloom glow instead. The fade
            // envelope rides on alpha, scaled by the caller's alpha so a mount
            // authored with alpha 0 draws nothing.
            Color haloColor = new Color(color.r * glowMul, color.g * glowMul, color.b * glowMul, color.a * alpha);

            tracer.Play(_tracerPool, from, to, haloColor, width, follow);
        }

        // ================================================================================
        //  Diagnostics (done-condition: pool counts must stay stable)
        // ================================================================================

        /// <summary>True when an effect has a built pool (its prefab was assigned and valid).</summary>
        public bool HasPool(Effect effect) =>
            _pools.TryGetValue(effect, out var p) && p != null;

        /// <summary>Total copies ever created for a given effect pool (for profiling).</summary>
        public int TotalCount(Effect effect) =>
            _pools.TryGetValue(effect, out var p) && p != null ? p.TotalCount : 0;

        /// <summary>Copies currently active (playing) for a given effect pool.</summary>
        public int ActiveCount(Effect effect) =>
            _pools.TryGetValue(effect, out var p) && p != null ? p.ActiveCount : 0;

        /// <summary>Total tracer copies ever created (for profiling).</summary>
        public int TracerTotalCount => _tracerPool != null ? _tracerPool.TotalCount : 0;

        /// <summary>Tracer copies currently active (for profiling).</summary>
        public int TracerActiveCount => _tracerPool != null ? _tracerPool.ActiveCount : 0;
    }

    /// <summary>
    /// Lightweight watcher that returns a pooled effect instance (its root
    /// <see cref="Transform"/>) to its pool once ALL of its particle systems have
    /// finished (GDD §11). Provider-agnostic: it watches the particle systems
    /// directly rather than any vendor component, so Cartoon FX, Epic Toon FX and any
    /// other Shuriken-based effect are all returned the same way with no allocation.
    ///
    /// A safety timeout guards against effects that never report "done" (e.g. a
    /// looping system left on by accident): once it elapses the instance is force-
    /// released so a pool can never be starved by a stuck copy.
    /// </summary>
    [DisallowMultipleComponent]
    public class PooledEffect : MonoBehaviour
    {
        // Force-release after this long even if a system reports itself still alive,
        // so an accidentally-looping effect can never permanently drain the pool.
        private const float SafetyTimeout = 12f;

        // A HELD instance (a spawn portal kept open while its wave emits) gets a
        // far larger cap — spawn-in phases exceed 12 s routinely — but never NO
        // cap: a leaked hold must still return to the pool eventually.
        private const float HeldSafetyTimeout = 180f;

        private CoreholdPool<Transform> _pool;
        private Transform _root;
        private ParticleSystem[] _systems;
        private bool _armed;
        private bool _held;
        private float _age;

        // Authored (prefab) scale, captured once per pooled instance so plays can
        // be sized RELATIVE to how the effect was authored and reuse re-baselines.
        private Vector3 _authoredScale = Vector3.one;
        private bool _authoredScaleCaptured;
        private float _sizeMult = 1f;
        private float _pulseAmplitude;
        private float _pulseHz;

        /// <summary>True while a caller keeps this instance open via <see cref="Hold"/>.</summary>
        public bool IsHeld => _held;

        /// <summary>
        /// Keep this instance alive past the normal finished/timeout checks — for
        /// effects whose lifetime is OWNED by a system (the persistent spawn
        /// portal, held open until its spawner's last unit appears). While held it
        /// can PULSE: a sinusoidal breathing of the whole instance's scale at the
        /// given amplitude (fraction of size) and rate (Hz); 0 disables. Pair with
        /// <see cref="EndHold"/>; a leaked hold is still force-released at
        /// <see cref="HeldSafetyTimeout"/>.
        /// </summary>
        public void Hold(float pulseAmplitude = 0f, float pulseHz = 0f)
        {
            _held = true;
            _age = 0f;
            _pulseAmplitude = Mathf.Max(0f, pulseAmplitude);
            _pulseHz = Mathf.Max(0f, pulseHz);
        }

        /// <summary>
        /// End a hold: stop EMISSION only (live particles finish naturally, so a
        /// looping portal FADES instead of blinking off) and hand the instance
        /// back to the normal finished checks for release.
        /// </summary>
        public void EndHold()
        {
            if (!_held)
                return;
            _held = false;
            _age = 0f;   // a fresh window for the fade-out
            _pulseAmplitude = 0f;
            ApplyScale(0f);   // settle out of the pulse
            if (_systems != null)
                for (int i = 0; i < _systems.Length; i++)
                    if (_systems[i] != null)
                        _systems[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>
        /// Scale this instance to <paramref name="mult"/> × its AUTHORED size (a
        /// held portal grows to fit the units emerging from it — callable while
        /// live, so an open portal can widen for a bigger late group). Forces
        /// Hierarchy scaling on the particle systems so root scale actually
        /// reaches the particles; pooled reuse re-baselines in <see cref="Arm"/>.
        /// </summary>
        public void SetSizeMultiplier(float mult)
        {
            _sizeMult = Mathf.Max(0.01f, mult);
            if (_systems != null && !Mathf.Approximately(_sizeMult, 1f))
                for (int i = 0; i < _systems.Length; i++)
                    if (_systems[i] != null)
                    {
                        var main = _systems[i].main;
                        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    }
            ApplyScale(0f);
        }

        private void ApplyScale(float pulse)
        {
            transform.localScale = _authoredScale * (_sizeMult * (1f + pulse));
        }

        /// <summary>
        /// Bind this instance to a pool so it can release itself when its particle
        /// systems have all finished. The systems array is supplied by the caller
        /// (already gathered when it restarted them) to avoid a second traversal.
        /// </summary>
        /// <summary>
        /// Record the prefab's authored scale. MUST be called before anything
        /// writes localScale on a fresh instance — the director calls it the
        /// moment it adds this component, straight out of the pool.
        /// </summary>
        public void CaptureAuthoredScale()
        {
            if (_authoredScaleCaptured)
                return;
            _authoredScale = transform.localScale;
            _authoredScaleCaptured = true;
        }

        public void Arm(CoreholdPool<Transform> pool, Transform root, ParticleSystem[] systems,
                        float requestedScale = 1f)
        {
            _pool = pool;
            _root = root;
            _systems = systems;
            if (_systems == null || _systems.Length == 0)
                _systems = GetComponentsInChildren<ParticleSystem>(true);
            _armed = true;
            _held = false;
            _age = 0f;

            // Re-baseline for this play: the AUTHORED scale (captured before any
            // play touched it) times what this call asked for, clearing any sizing
            // a previous (portal) use applied.
            CaptureAuthoredScale();
            _sizeMult = 1f;
            _pulseAmplitude = 0f;
            _pulseHz = 0f;
            transform.localScale = _authoredScale *
                (requestedScale > 0.0001f ? requestedScale : 1f);
        }

        private void LateUpdate()
        {
            if (!_armed)
                return;

            _age += Time.deltaTime;

            if (_held)
            {
                // Held instances skip the finished check (a burst prefab may sit
                // invisible until the hold ends) and use the larger cap only.
                if (_pulseAmplitude > 0f && _pulseHz > 0f)
                    ApplyScale(_pulseAmplitude * Mathf.Sin(Time.time * _pulseHz * 2f * Mathf.PI));
                if (_age < HeldSafetyTimeout)
                    return;
                _held = false;
            }

            if (!AnyAlive() || _age >= SafetyTimeout)
            {
                _armed = false;
                _held = false;
                if (_authoredScaleCaptured)
                    transform.localScale = _authoredScale;
                if (_pool != null)
                    _pool.Release(_root != null ? _root : transform);
                else
                    gameObject.SetActive(false);
            }
        }

        private bool AnyAlive()
        {
            if (_systems == null)
                return false;
            for (int i = 0; i < _systems.Length; i++)
            {
                var ps = _systems[i];
                // withChildren:false — each system in the array is checked on its own,
                // so a null/destroyed entry simply counts as finished.
                if (ps != null && ps.IsAlive(false))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A pooled hitscan tracer segment for the Autocannon and Arc Node (GDD §11):
    /// a two-point <see cref="LineRenderer"/> on a two-frame fade, all sharing one
    /// additive material owned by <see cref="VFXDirector"/>. Frame-based rather than
    /// time-based so it reads as an instantaneous flash at any framerate and never
    /// lingers. Pooled — no Instantiate/Destroy on the firing path.
    /// </summary>
    [DisallowMultipleComponent]
    public class VfxTracer : MonoBehaviour
    {
        // Time-based lifetime so the bolt reads as a visible streak regardless of
        // framerate (frame-count lifetimes vanish too fast at 60+ fps — item a).
        private const float LifeSeconds = 0.2f;
        private float _lifeLeft;

        private CoreholdPool<VfxTracer> _pool;

        // A tracer is drawn as TWO overlaid lines (GDD §11 colour-language):
        //   • _halo — wide, saturated, ALPHA-BLENDED. Carries the faction hue and
        //     survives tonemapping because alpha-blend replaces (not adds) toward
        //     the colour, so it never washes to white over the bright desert.
        //   • _core — thin, near-white, ADDITIVE and HDR-bright. This is what Bloom
        //     smears into "glow". Sitting inside the coloured halo, the eye reads a
        //     glowing COLOURED bolt.
        //
        // These MUST be [SerializeField] (not plain private) because the tracer is
        // POOLED: CoreholdPool clones the prefab with Object.Instantiate, and only
        // serialized fields survive that clone. When they were plain private fields,
        // Configure() set them on the PREFAB only, every clone got null, Play()
        // no-opped and NOTHING rendered in play mode. Instantiate rewires serialized
        // references to the cloned child hierarchy automatically.
        [SerializeField] private LineRenderer _core;
        [SerializeField] private LineRenderer _halo;
        [SerializeField] private float _haloWidthScale = 3f;
        [SerializeField] private float _coreGlow = 1.6f;

        // Cached gradients / key arrays (one pair per line) so fading allocates nothing.
        private Gradient _coreGradient;
        private Gradient _haloGradient;
        private GradientColorKey[] _coreColorKeys;
        private GradientAlphaKey[] _coreAlphaKeys;
        private GradientColorKey[] _haloColorKeys;
        private GradientAlphaKey[] _haloAlphaKeys;

        private Color _coreColor = Color.white;
        private Color _haloColor = Color.white;
        private float _baseAlpha = 1f;
        private float _baseWidth = 0.08f;

        // Endpoint tracking for fast targets (flak): while alive the line's far
        // end rides this transform; null = classic fixed line.
        private Transform _follow;
        private Vector3 _followLocal;
        private Vector3 _from;

        /// <summary>
        /// Wire up the two child <see cref="LineRenderer"/>s built by the director.
        /// Called once when the pooled prefab is constructed.
        /// </summary>
        public void Configure(LineRenderer core, LineRenderer halo, float haloWidthScale, float coreGlow)
        {
            _core = core;
            _halo = halo;
            _haloWidthScale = Mathf.Max(1f, haloWidthScale);
            _coreGlow = Mathf.Max(0f, coreGlow);
            EnsureInit();
        }

        private void EnsureInit()
        {
            // Safety net for pooled clones: if the serialized child references did
            // not survive (e.g. a hand-built prefab), resolve them from the child
            // hierarchy by name so a clone is never left with no lines to draw.
            if (_halo == null || _core == null)
            {
                var lines = GetComponentsInChildren<LineRenderer>(true);
                foreach (var lr in lines)
                {
                    if (_halo == null && lr.gameObject.name == "Halo") _halo = lr;
                    else if (_core == null && lr.gameObject.name == "Core") _core = lr;
                }
                // Last-resort: assign by order if names differ.
                if ((_halo == null || _core == null) && lines.Length >= 2)
                {
                    _halo ??= lines[0];
                    _core ??= lines[1];
                }
            }

            if (_coreGradient == null)
            {
                _coreColorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) };
                _coreAlphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
                _coreGradient = new Gradient();
            }
            if (_haloGradient == null)
            {
                _haloColorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) };
                _haloAlphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
                _haloGradient = new Gradient();
            }
        }

        /// <summary>
        /// Draw between two points using the coloured halo hue and base width, then
        /// fade out over the tracer lifetime. The white-hot core is derived from the
        /// halo hue (desaturated toward white) so a single authored colour produces
        /// both the hue and its matching glow.
        /// </summary>
        public void Play(CoreholdPool<VfxTracer> pool, Vector3 from, Vector3 to, Color haloColor, float width,
                         Transform follow = null)
        {
            EnsureInit();
            _pool = pool;
            _baseWidth = width;

            // A hitscan line frozen for its 0.2 s life visibly overshoots (or
            // undershoots) a FAST target — flak vs 8 m/s flyers moves the enemy
            // most of a body length while the line stands still. Following the
            // victim's hit anchor keeps the bolt pinned to the thing it hit.
            // The offset is stored in the anchor's LOCAL space so authored
            // anchors and geometric-centre fallbacks both track exactly.
            _from = from;
            _follow = follow;
            _followLocal = follow != null ? follow.InverseTransformPoint(to) : Vector3.zero;

            transform.position = Vector3.zero;

            // The halo carries the saturated faction hue at moderate brightness.
            _haloColor = new Color(haloColor.r, haloColor.g, haloColor.b, 1f);

            // The core is the SAME hue pushed toward white and up into HDR so Bloom
            // catches it. Mixing 65% toward white keeps a hint of the faction colour
            // in the hot centre while still reading as a bright glowing filament.
            Color hot = Color.Lerp(new Color(haloColor.r, haloColor.g, haloColor.b, 1f), Color.white, 0.65f);
            _coreColor = new Color(hot.r * _coreGlow, hot.g * _coreGlow, hot.b * _coreGlow, 1f);

            _baseAlpha = haloColor.a <= 0f ? 1f : haloColor.a;

            SetLine(_halo, from, to, _baseWidth * _haloWidthScale);
            SetLine(_core, from, to, _baseWidth);

            ApplyAlpha(_baseAlpha);

            _lifeLeft = LifeSeconds;
        }

        private static void SetLine(LineRenderer lr, Vector3 from, Vector3 to, float width)
        {
            if (lr == null)
                return;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.widthMultiplier = width;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
        }

        private void ApplyAlpha(float alpha)
        {
            if (_halo != null)
            {
                _haloColorKeys[0].color = _haloColor;
                _haloColorKeys[1].color = _haloColor;
                _haloAlphaKeys[0].alpha = alpha;
                _haloAlphaKeys[1].alpha = alpha;
                _haloGradient.SetKeys(_haloColorKeys, _haloAlphaKeys);
                _halo.colorGradient = _haloGradient;
            }
            if (_core != null)
            {
                _coreColorKeys[0].color = _coreColor;
                _coreColorKeys[1].color = _coreColor;
                _coreAlphaKeys[0].alpha = alpha;
                _coreAlphaKeys[1].alpha = alpha;
                _coreGradient.SetKeys(_coreColorKeys, _coreAlphaKeys);
                _core.colorGradient = _coreGradient;
            }
        }

        private void LateUpdate()
        {
            if (_lifeLeft <= 0f)
                return;

            _lifeLeft -= Time.deltaTime;

            // Keep the far end pinned to a moving victim (a dead/pooled anchor
            // simply freezes the line where it last was).
            if (_follow != null && _follow.gameObject.activeInHierarchy)
            {
                Vector3 to = _follow.TransformPoint(_followLocal);
                SetLine(_halo, _from, to, _baseWidth * _haloWidthScale);
                SetLine(_core, _from, to, _baseWidth);
            }

            // Fade linearly to zero over the lifetime, then release to the pool.
            float alpha = _baseAlpha * Mathf.Clamp01(_lifeLeft / LifeSeconds);
            ApplyAlpha(alpha);

            if (_lifeLeft <= 0f)
                Release();
        }

        private void Release()
        {
            if (_pool != null)
                _pool.Release(this);
            else
                gameObject.SetActive(false);
        }
    }
}
