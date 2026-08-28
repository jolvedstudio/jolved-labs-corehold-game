using System;
using Corehold.Core;
using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// The Strike Wing player active ability (R19), mechanic-first: pay salvage,
    /// tap once to target — the point SNAPS to the nearest point on a ground
    /// route — then after a telegraph ring an EM burst stuns and slows everything
    /// in radius through the R18 status system. The flyer presentation is a later
    /// cosmetic ticket; today the burst is a pooled VFX + one-shot.
    ///
    /// Input: while armed, the ability CLAIMS field taps from <see cref="InputRouter"/>
    /// (UI still gets first refusal), so a tap targets the strike instead of
    /// opening a pad. Disarming (tapping the HUD button again) returns taps to
    /// normal routing. Cost is spent at COMMIT (the targeting tap), which is also
    /// when the cooldown starts.
    ///
    /// Lives on a runtime-created GameObject: <see cref="Ensure"/> is called by
    /// the HUD button, so any scene whose HUD carries the button gets the ability
    /// with no scene-file or pipeline changes. Timing runs on scaled time — the
    /// telegraph freezes with pause exactly like the R18 statuses do.
    /// </summary>
    [DisallowMultipleComponent]
    public class StrikeWingAbility : MonoBehaviour
    {
        /// <summary>Where the ability is in its use cycle.</summary>
        public enum Phase
        {
            /// <summary>Idle and affordable-or-not; Arm() enters targeting.</summary>
            Ready,
            /// <summary>Waiting for the targeting tap (field taps are claimed).</summary>
            Armed,
            /// <summary>Target committed, telegraph ring running.</summary>
            Telegraph,
            /// <summary>Fired; waiting out the cooldown.</summary>
            Cooldown
        }

        [Header("Cost / cooldown (R19)")]
        [Tooltip("[TUNE] Salvage cost, spent at the targeting tap.")]
        [SerializeField] private int cost = 120;

        [Tooltip("[TUNE] Seconds from commit until the ability is Ready again.")]
        [SerializeField] private float cooldownSeconds = 45f;

        [Header("Burst (R19)")]
        [Tooltip("[TUNE] EM burst radius in metres (XZ planar — air units are hit too).")]
        [SerializeField] private float radius = 6f;

        [Tooltip("[TUNE] Stun duration in seconds (R18; stunResistance shortens it per unit).")]
        [SerializeField] private float stunSeconds = 3f;

        [Tooltip("[TUNE] Slow duration in seconds, running alongside and past the stun.")]
        [SerializeField] private float slowSeconds = 3f;

        [Tooltip("[TUNE] Slow strength (0.5 = half speed) applied with the stun.")]
        [Range(0f, 1f)] [SerializeField] private float slowStrength = 0.5f;

        [Header("Telegraph (R19)")]
        [Tooltip("[TUNE] Seconds the ground ring telegraphs before the burst lands.")]
        [SerializeField] private float telegraphSeconds = 1.2f;

        private static StrikeWingAbility _instance;

        /// <summary>The scene's ability instance, if one exists.</summary>
        public static StrikeWingAbility Instance => _instance;

        /// <summary>Raised at strike COMMIT with the impact point (M-a flyby cam).</summary>
        public static event System.Action<Vector3> OnStrikeCommitted;

        private InputRouter _router;
        private PathRoute[] _routes;
        private Corehold.UI.RangeRing _ring;
        private Func<Vector2, bool> _claimant;

        private Phase _phase = Phase.Ready;
        private Vector3 _target;
        private float _telegraphUntil;
        private float _cooldownUntil;

        /// <summary>Current phase of the use cycle.</summary>
        public Phase CurrentPhase => _phase;

        /// <summary>Salvage cost of one use.</summary>
        public int Cost => cost;

        /// <summary>Seconds left on the cooldown (0 outside Cooldown).</summary>
        public float CooldownRemaining =>
            _phase == Phase.Cooldown ? Mathf.Max(0f, _cooldownUntil - Time.time) : 0f;

        /// <summary>Cooldown remaining as 1 → 0 (for a radial fill).</summary>
        public float CooldownFraction =>
            cooldownSeconds > 0f ? Mathf.Clamp01(CooldownRemaining / cooldownSeconds) : 0f;

        /// <summary>True when Ready, affordable, and there is a route to snap to.</summary>
        public bool CanArm =>
            _phase == Phase.Ready &&
            GameManager.Instance != null && GameManager.Instance.Salvage >= cost &&
            Routes().Length > 0;

        /// <summary>
        /// Find the scene's ability, creating its GameObject if the scene has none
        /// yet. Called by the HUD button on Awake.
        /// </summary>
        public static StrikeWingAbility Ensure()
        {
            if (_instance != null)
                return _instance;
            var existing = FindFirstObjectByType<StrikeWingAbility>();
            if (existing != null)
                return existing;
            var go = new GameObject("StrikeWingAbility");
            return go.AddComponent<StrikeWingAbility>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            _claimant = HandleTargetTap;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            if (_router != null && _claimant != null)
                _router.ClearTapClaimant(_claimant);
        }

        /// <summary>Enter targeting: the next field tap commits the strike.</summary>
        public void Arm()
        {
            if (!CanArm)
                return;

            var router = Router();
            if (router == null)
            {
                Debug.LogWarning("[StrikeWing] No InputRouter in the scene — cannot target.");
                return;
            }

            _phase = Phase.Armed;
            router.SetTapClaimant(_claimant);
        }

        /// <summary>Leave targeting without firing (no cost, no cooldown).</summary>
        public void Disarm()
        {
            if (_phase != Phase.Armed)
                return;
            _phase = Phase.Ready;
            if (_router != null)
                _router.ClearTapClaimant(_claimant);
        }

        private void Update()
        {
            if (_phase == Phase.Telegraph)
            {
                // Urgency pulse: the ring breathes ±6% while the strike is inbound.
                if (_ring != null)
                {
                    float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 18f);
                    _ring.Show(_target, radius * pulse);
                }
                if (Time.time >= _telegraphUntil)
                    Fire();
            }
            else if (_phase == Phase.Cooldown && Time.time >= _cooldownUntil)
            {
                _phase = Phase.Ready;
            }
        }

        /// <summary>
        /// The targeting tap (routed by InputRouter after UI refusal). Resolves the
        /// tap to the ground plane, snaps to the nearest route point, spends the
        /// cost and starts the telegraph. Returns true when the tap was consumed.
        /// </summary>
        private bool HandleTargetTap(Vector2 screenPos)
        {
            if (_phase != Phase.Armed)
                return false;

            Camera cam = Camera.main;
            if (cam == null)
                return false;

            // Ground plane at y = 0 — routes run on the floor, so the snap search
            // needs a floor point, not a physics hit.
            Ray ray = cam.ScreenPointToRay(screenPos);
            var ground = new Plane(Vector3.up, 0f);
            if (!ground.Raycast(ray, out float enter))
                return true; // consumed: an armed tap at the sky should not open pads

            Vector3 point = ray.GetPoint(enter);
            if (!TryNearestRoutePoint(point, out Vector3 snapped))
                return true;

            var gm = GameManager.Instance;
            if (gm == null || !gm.TrySpend(cost))
            {
                // Salvage ran out while armed (e.g. a tower was upgraded first).
                Disarm();
                return true;
            }

            _target = snapped;
            _phase = Phase.Telegraph;
            _telegraphUntil = Time.time + Mathf.Max(0.05f, telegraphSeconds);
            // Spectacle hook (M-a): the turret-cam panel plays a flyby over the
            // strike point. Fired at commit so the sweep covers the telegraph.
            OnStrikeCommitted?.Invoke(snapped);
            // Cooldown runs from COMMIT; it dwarfs the telegraph so the overlap
            // is invisible, and commit is the moment the player paid.
            _cooldownUntil = Time.time + Mathf.Max(1f, cooldownSeconds);

            if (_router != null)
                _router.ClearTapClaimant(_claimant);

            Ring().Show(_target, radius);
            // Friendly TARGET marker layered over the telegraph ring (VFX plan —
            // a marker, never a danger warning: warnings mean incoming threats,
            // and this is the player's own strike). Silent until wired.
            if (VFXDirector.Instance != null)
                VFXDirector.Instance.PlayStrikeMarker(_target);
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayUIClick();
            return true;
        }

        /// <summary>Deliver the EM burst: stun + slow everything in radius (R18).</summary>
        private void Fire()
        {
            _phase = Phase.Cooldown;
            if (_ring != null)
                _ring.Hide();

            if (VFXDirector.Instance != null)
                VFXDirector.Instance.PlayStrikeWingBurst(_target);
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayStrikeWing();

            int hit = 0;
            for (int i = 0; i < Enemy.Live.Count; i++)
            {
                var e = Enemy.Live[i];
                if (e == null || !e.IsAlive)
                    continue;

                // Planar distance: the EM burst is a ground-centred column, so a
                // Wasp at altitude directly overhead is inside it (GDD §6.1 EM).
                Vector3 d = e.transform.position - _target;
                d.y = 0f;
                if (d.sqrMagnitude > radius * radius)
                    continue;

                e.ApplyStun(stunSeconds);
                e.ApplySlow(slowSeconds, slowStrength);
                hit++;
            }

            Debug.Log($"[StrikeWing] Burst at {_target} hit {hit} unit(s) " +
                      $"(stun {stunSeconds}s + slow {slowStrength:P0}/{slowSeconds}s, radius {radius} m).");
        }

        // ------------------------------------------------------------ helpers

        private InputRouter Router()
        {
            if (_router == null)
                _router = FindFirstObjectByType<InputRouter>();
            return _router;
        }

        private PathRoute[] Routes()
        {
            if (_routes == null || _routes.Length == 0)
            {
                var all = FindObjectsByType<PathRoute>(FindObjectsSortMode.None);
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].Length > 1f)
                        n++;
                _routes = new PathRoute[n];
                int w = 0;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].Length > 1f)
                        _routes[w++] = all[i];
            }
            return _routes;
        }

        private Corehold.UI.RangeRing Ring()
        {
            if (_ring == null)
            {
                // Own instance — the shared "RangeRing" belongs to BuildMenu's
                // hover preview and would fight the telegraph over Show/Hide.
                var go = new GameObject("StrikeWingTelegraphRing");
                _ring = go.AddComponent<Corehold.UI.RangeRing>();
            }
            return _ring;
        }

        /// <summary>
        /// Nearest point on any ground route to <paramref name="point"/> (XZ):
        /// a 2 m coarse walk over each route's arc length, then a 0.25 m refine
        /// around the best coarse hit. A few hundred samples once per tap.
        /// </summary>
        private bool TryNearestRoutePoint(Vector3 point, out Vector3 snapped)
        {
            snapped = point;
            var routes = Routes();
            float bestSq = float.MaxValue;
            bool found = false;

            for (int r = 0; r < routes.Length; r++)
            {
                var route = routes[r];
                float len = route.Length;

                float coarseBestS = 0f;
                float coarseBestSq = float.MaxValue;
                for (float s = 0f; s <= len; s += 2f)
                {
                    Vector3 p = route.SamplePosition(s, out _);
                    float dx = p.x - point.x, dz = p.z - point.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < coarseBestSq) { coarseBestSq = d2; coarseBestS = s; }
                }

                float lo = Mathf.Max(0f, coarseBestS - 2f);
                float hi = Mathf.Min(len, coarseBestS + 2f);
                for (float s = lo; s <= hi; s += 0.25f)
                {
                    Vector3 p = route.SamplePosition(s, out _);
                    float dx = p.x - point.x, dz = p.z - point.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < bestSq)
                    {
                        bestSq = d2;
                        snapped = p;
                        found = true;
                    }
                }
            }

            return found;
        }
    }
}
