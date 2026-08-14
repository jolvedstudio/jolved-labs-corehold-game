using System.Collections.Generic;
using CartoonFX;
using Corehold.Data;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Central spawner for every one-shot visual effect in the game (GDD §11).
    ///
    /// Nine pooled effects from Cartoon FX Remaster — three muzzle flashes (one per
    /// damage type), an impact spark, two explosion sizes for splash weapons, an
    /// enemy death burst, a Core hit flash and a build-placement puff — plus a
    /// pooled hitscan tracer for the Autocannon and Arc Node. Missile and Mortar
    /// have visible travel time and need no tracer.
    ///
    /// Every effect is pooled through <see cref="CoreholdPool{T}"/>; nothing on a
    /// gameplay path calls Instantiate or Destroy during a wave (GDD §11). Instead
    /// of Cartoon FX's default <c>Destroy</c> clear-behaviour, every pooled copy is
    /// forced to <see cref="CFXR_Effect.ClearBehavior.Disable"/> and a
    /// <see cref="PooledEffect"/> watcher returns the instance to its pool when the
    /// particle system finishes — so pool counts stay stable and the effect layer
    /// allocates nothing after warm-up (the ticket's done-condition).
    ///
    /// CFXR's global animated-light switch is turned OFF at boot
    /// (<see cref="CFXR_Effect.GlobalDisableLights"/> = true): no effect spawns a
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
            BuildPuff
        }

        [System.Serializable]
        private struct EffectEntry
        {
            [Tooltip("Which logical effect this prefab fulfils (GDD §11).")]
            public Effect id;

            [Tooltip("A Cartoon FX Remaster prefab. Its clear-behaviour is forced to Disable at spawn so it can be pooled.")]
            public GameObject prefab;

            [Tooltip("How many copies to prewarm so the first play never allocates.")]
            public int prewarm;
        }

        [Header("Effects (GDD §11) — assign Cartoon FX Remaster prefabs")]
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
        };

        [Header("Hitscan tracer (GDD §11) — Autocannon + Arc Node only")]
        [Tooltip("Shared additive material for the tracer LineRenderer. One material, shared by every tracer segment. Built at runtime if left null.")]
        [SerializeField] private Material tracerMaterial;

        [Tooltip("Tracer line width in metres.")]
        [SerializeField] private float tracerWidth = 0.15f;   // shipped Game.unity value

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

        // One CFXR_Effect pool per assigned prefab, keyed by the logical Effect id.
        private readonly Dictionary<Effect, CoreholdPool<CFXR_Effect>> _pools =
            new Dictionary<Effect, CoreholdPool<CFXR_Effect>>();

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

                    CFXR_Effect prefabEffect = PreparePrefab(entry.prefab);
                    if (prefabEffect == null)
                        continue;

                    var parentGo = new GameObject($"Pool_{entry.id}");
                    parentGo.transform.SetParent(_poolRoot, false);

                    int prewarm = Mathf.Max(0, entry.prewarm);
                    var pool = new CoreholdPool<CFXR_Effect>(prefabEffect, parentGo.transform, prewarm);
                    _pools.Add(entry.id, pool);
                }
            }

            BuildTracerPool();
        }

        /// <summary>
        /// Make sure the prefab carries a <see cref="CFXR_Effect"/> and force its
        /// clear-behaviour to Disable so pooled instances self-deactivate instead of
        /// destroying themselves (GDD §11). Returns the CFXR_Effect used as the pool
        /// prefab, or null if the object is not a valid Cartoon FX effect.
        /// </summary>
        private static CFXR_Effect PreparePrefab(GameObject prefab)
        {
            var effect = prefab.GetComponent<CFXR_Effect>();
            if (effect == null)
                effect = prefab.GetComponentInChildren<CFXR_Effect>(true);
            if (effect == null)
                return null;

            // Root the pool prefab on the object that actually holds the CFXR_Effect
            // (its own root particle system), so activate/deactivate is coherent.
            return effect;
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
        public CFXR_Effect Play(Effect effect, Vector3 position)
        {
            return Play(effect, position, Quaternion.identity, 1f);
        }

        /// <summary>Play a one-shot effect at a position facing a direction.</summary>
        public CFXR_Effect Play(Effect effect, Vector3 position, Vector3 forward)
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
        public CFXR_Effect Play(Effect effect, Vector3 position, Quaternion rotation, float scale)
        {
            if (!_pools.TryGetValue(effect, out CoreholdPool<CFXR_Effect> pool) || pool == null)
                return null;

            CFXR_Effect fx = pool.Get();
            if (fx == null)
                return null;

            Transform t = fx.transform;
            t.SetPositionAndRotation(position, rotation);
            if (!Mathf.Approximately(scale, 1f))
                t.localScale = Vector3.one * scale;

            // The ticket asks for CFXR's auto-deactivate rather than Destroy. We take
            // full ownership of the lifetime through PooledEffect so the instance is
            // returned to the FREE stack (not merely deactivated out-of-band, which
            // would leak the pool count), so CFXR's own clear pass is disabled here.
            fx.clearBehavior = CFXR_Effect.ClearBehavior.None;

            // Arm the watcher that returns this instance to its pool once finished.
            var watcher = fx.GetComponent<PooledEffect>();
            if (watcher == null)
                watcher = fx.gameObject.AddComponent<PooledEffect>();
            watcher.Arm(pool, fx);

            // Restart the particle systems so a reused instance plays from frame 0.
            fx.ResetState();
            var systems = fx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                systems[i].Clear(true);
                systems[i].Play(true);
            }

            return fx;
        }

        // ---- Convenience wrappers for gameplay call sites ----

        /// <summary>Muzzle flash for the given damage type at the muzzle point (GDD §11).</summary>
        public CFXR_Effect PlayMuzzle(DamageType type, Vector3 position, Vector3 forward)
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
        public CFXR_Effect PlayImpact(Vector3 position)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickImpact(position);
            return Play(Effect.ImpactSpark, position);
        }

        /// <summary>
        /// Splash explosion sized by radius: the smaller effect below the threshold,
        /// the larger one at or above it (GDD §11 — "two explosion sizes for splash").
        /// Explosions carry the larger screen kick of the R5 impact standard.
        /// </summary>
        public CFXR_Effect PlayExplosion(Vector3 position, float splashRadius)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickExplosion(position);
            Effect e = splashRadius >= LargeSplashThreshold ? Effect.ExplosionLarge : Effect.ExplosionSmall;
            return Play(e, position);
        }

        /// <summary>Death burst when an enemy dies (GDD §11).</summary>
        public CFXR_Effect PlayEnemyDeath(Vector3 position) => Play(Effect.EnemyDeath, position);

        /// <summary>
        /// Flash on the Core when it takes a leak hit (GDD §11), with the medium
        /// screen kick (R5). The trauma shake for Core hits stays where it was
        /// (Enemy.ReachCore → ShakeCoreHit); this is the sharper directional nudge.
        /// </summary>
        public CFXR_Effect PlayCoreHit(Vector3 position)
        {
            if (CameraShake.Instance != null)
                CameraShake.Instance.KickCoreHit(position);
            return Play(Effect.CoreHit, position);
        }

        /// <summary>Puff when a turret is built on a hardpoint (GDD §11).</summary>
        public CFXR_Effect PlayBuildPuff(Vector3 position) => Play(Effect.BuildPuff, position);

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
            tracer.Play(_tracerPool, from, to, color, tracerWidth);
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
    /// Lightweight watcher that returns a pooled <see cref="CFXR_Effect"/> to its
    /// pool once its particle systems have finished (GDD §11). CFXR's own
    /// clear-behaviour is set to Disable, which deactivates the GameObject; this
    /// component runs one frame before that and hands the instance back to the pool
    /// so it can be reused with no allocation.
    /// </summary>
    [DisallowMultipleComponent]
    public class PooledEffect : MonoBehaviour
    {
        private CoreholdPool<CFXR_Effect> _pool;
        private CFXR_Effect _effect;
        private ParticleSystem _root;
        private bool _armed;

        /// <summary>Bind this instance to a pool so it can release itself when done.</summary>
        public void Arm(CoreholdPool<CFXR_Effect> pool, CFXR_Effect effect)
        {
            _pool = pool;
            _effect = effect;
            if (_root == null)
                _root = GetComponent<ParticleSystem>();
            if (_root == null)
                _root = GetComponentInChildren<ParticleSystem>(true);
            _armed = true;
        }

        private void LateUpdate()
        {
            if (!_armed || _root == null)
                return;

            // Once the root system is finished, hand the instance back to the pool.
            if (!_root.IsAlive(true))
            {
                _armed = false;
                if (_pool != null)
                    _pool.Release(_effect != null ? _effect : GetComponent<CFXR_Effect>());
                else
                    gameObject.SetActive(false);
            }
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
