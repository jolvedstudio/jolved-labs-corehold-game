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
            ShieldHit
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

        [Header("Effects (GDD §11) — assign any Shuriken-based effect prefabs")]
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
        };

        [Header("Hitscan tracer (GDD §11) — Autocannon + Arc Node only")]
        [Tooltip("Shared additive material for the tracer LineRenderer. One material, shared by every tracer segment. Built at runtime if left null.")]
        [SerializeField] private Material tracerMaterial;

        [Tooltip("Tracer line width in metres.")]
        [SerializeField] private float tracerWidth = 0.15f;   // shipped Game.unity value

        [Tooltip("Brightness multiplier applied to EVERY tracer's HDR colour (enemy + tower). Values above 1 push the colour further into HDR so it blooms more intensely. 1 = author-authored colour unchanged.")]
        [Min(0f)]
        [SerializeField] private float tracerGlow = 1f;

        [Tooltip("Copies of the tracer prewarmed into its pool.")]
        [SerializeField] private int tracerPrewarm = 8;

        [Tooltip("Default tracer colour (additive, so RGB reads as the glow colour). HDR-bright so it stands out.")]
        [ColorUsage(true, true)]
        // The shipped scene's tuned tracer: an intensely HDR cyan, not the warm
        // orange this defaulted to. A generated scene inherited the default and
        // fired visibly different-looking shots (see SetupVFXDirector).
        [SerializeField] private Color defaultTracerColor = new Color(0f, 207.88327f, 705.2075f, 1f);

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
                Destroy(this);
                return;
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
                foreach (var entry in effects)
                {
                    if (entry.prefab == null || _pools.ContainsKey(entry.id))
                        continue;

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
                    _pools.Add(entry.id, pool);
                }
            }

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
            return prefab.transform;
        }

        private void BuildTracerPool()
        {
            if (_tracerPool != null)
                return;

            EnsureTracerMaterial();

            var rootGo = new GameObject("Pool_Tracers");
            rootGo.transform.SetParent(transform, false);
            _tracerRoot = rootGo.transform;

            _tracerPrefab = BuildTracerPrefab();
            _tracerPool = new CoreholdPool<VfxTracer>(_tracerPrefab, _tracerRoot, Mathf.Max(0, tracerPrewarm));
        }

        private void EnsureTracerMaterial()
        {
            if (tracerMaterial != null)
                return;

            // One shared additive material. In URP the built-in legacy particle
            // shaders do NOT render (they show as invisible/magenta), so we MUST use
            // a URP-native shader. URP Particles/Unlit with additive blend renders
            // the LineRenderer vertex/gradient colour and, being unlit + HDR, glows
            // through Bloom.
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default"); // URP-compatible fallback

            tracerMaterial = new Material(shader) { name = "VFX_Tracer_Additive (shared)" };

            // Configure additive blending (URP path).
            if (tracerMaterial.HasProperty("_Surface")) tracerMaterial.SetFloat("_Surface", 1f); // Transparent
            if (tracerMaterial.HasProperty("_Blend")) tracerMaterial.SetFloat("_Blend", 1f);      // Additive
            if (tracerMaterial.HasProperty("_SrcBlend")) tracerMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (tracerMaterial.HasProperty("_DstBlend")) tracerMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (tracerMaterial.HasProperty("_ZWrite")) tracerMaterial.SetFloat("_ZWrite", 0f);
            // Ensure vertex colours from the LineRenderer gradient are applied.
            tracerMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            tracerMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private VfxTracer BuildTracerPrefab()
        {
            var go = new GameObject("VfxTracer");
            go.transform.SetParent(_tracerRoot, false);
            go.SetActive(false);

            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.widthMultiplier = tracerWidth;
            lr.numCapVertices = 2;
            lr.numCornerVertices = 0;
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            lr.sharedMaterial = tracerMaterial;

            return go.AddComponent<VfxTracer>();
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

            t.SetPositionAndRotation(position, rotation);
            t.localScale = Mathf.Approximately(scale, 1f) ? Vector3.one : Vector3.one * scale;

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
            var watcher = t.GetComponent<PooledEffect>();
            if (watcher == null)
                watcher = t.gameObject.AddComponent<PooledEffect>();
            watcher.Arm(pool, t, systems);

            // Return the root particle system as a convenience handle for callers.
            return systems.Length > 0 ? systems[0] : null;
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
        /// Draw a pooled hitscan tracer between two world points with the default
        /// colour (GDD §11). The segment fades over two frames and returns itself to
        /// the pool. No-op if the tracer pool is not built.
        /// </summary>
        public void DrawTracer(Vector3 from, Vector3 to) => DrawTracer(from, to, defaultTracerColor);

        /// <summary>Draw a pooled hitscan tracer between two world points with an explicit colour.</summary>
        public void DrawTracer(Vector3 from, Vector3 to, Color color)
        {
            if (_tracerPool == null)
                return;

            VfxTracer tracer = _tracerPool.Get();
            if (tracer == null)
                return;

            // Push the author-authored colour further into HDR so it blooms brighter.
            // The RGB carries the glow (additive material), so we scale RGB only and
            // leave alpha (the fade envelope) untouched. Applied to EVERY tracer, so
            // enemy and tower bolts brighten together from one control.
            Color glow = tracerGlow == 1f
                ? color
                : new Color(color.r * tracerGlow, color.g * tracerGlow, color.b * tracerGlow, color.a);

            tracer.Play(_tracerPool, from, to, glow, tracerWidth);
        }

        // ================================================================================
        //  Diagnostics (done-condition: pool counts must stay stable)
        // ================================================================================

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

        private CoreholdPool<Transform> _pool;
        private Transform _root;
        private ParticleSystem[] _systems;
        private bool _armed;
        private float _age;

        /// <summary>
        /// Bind this instance to a pool so it can release itself when its particle
        /// systems have all finished. The systems array is supplied by the caller
        /// (already gathered when it restarted them) to avoid a second traversal.
        /// </summary>
        public void Arm(CoreholdPool<Transform> pool, Transform root, ParticleSystem[] systems)
        {
            _pool = pool;
            _root = root;
            _systems = systems;
            if (_systems == null || _systems.Length == 0)
                _systems = GetComponentsInChildren<ParticleSystem>(true);
            _armed = true;
            _age = 0f;
        }

        private void LateUpdate()
        {
            if (!_armed)
                return;

            _age += Time.deltaTime;

            if (!AnyAlive() || _age >= SafetyTimeout)
            {
                _armed = false;
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
    [RequireComponent(typeof(LineRenderer))]
    public class VfxTracer : MonoBehaviour
    {
        // Time-based lifetime so the bolt reads as a visible streak regardless of
        // framerate (frame-count lifetimes vanish too fast at 60+ fps — item a).
        private const float LifeSeconds = 0.2f;
        private float _lifeLeft;

        private LineRenderer _line;
        private CoreholdPool<VfxTracer> _pool;
        private Gradient _gradient;
        private GradientColorKey[] _colorKeys;
        private GradientAlphaKey[] _alphaKeys;
        private Color _color = Color.white;
        private float _baseAlpha = 1f;

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

        /// <summary>Draw between two points at a colour and width, then fade over two frames.</summary>
        public void Play(CoreholdPool<VfxTracer> pool, Vector3 from, Vector3 to, Color color, float width)
        {
            EnsureInit();
            _pool = pool;

            transform.position = Vector3.zero;
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.widthMultiplier = width;
            _line.SetPosition(0, from);
            _line.SetPosition(1, to);

            _color = color;
            _baseAlpha = color.a <= 0f ? 1f : color.a;
            ApplyAlpha(_baseAlpha);

            _lifeLeft = LifeSeconds;
        }

        private void ApplyAlpha(float alpha)
        {
            _colorKeys[0].color = _color;
            _colorKeys[1].color = _color;
            _alphaKeys[0].alpha = alpha;
            _alphaKeys[1].alpha = alpha;
            _gradient.SetKeys(_colorKeys, _alphaKeys);
            _line.colorGradient = _gradient;
        }

        private void LateUpdate()
        {
            if (_lifeLeft <= 0f)
                return;

            _lifeLeft -= Time.deltaTime;

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
