using Corehold.Data;
using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Bridges gameplay state to the Animator (GDD §6.3). Drives the
    /// <c>Speed</c> float from <see cref="EnemyMover"/>'s current velocity, fires
    /// the <c>Die</c> trigger when the enemy dies, and applies the foot-slide
    /// correction so scripted movement speed matches the clip's authored ground
    /// speed:
    ///
    ///     Animator.speed = moveSpeed / animatorClipSpeedRef
    ///
    /// The Slava Z. packs ship locomotion clips with root motion (disabled on the
    /// Animator here — see the prefab setup), so the clip advances at its own
    /// authored pace regardless of world movement. Scaling Animator.speed by the
    /// ratio of actual speed to the clip's implied speed keeps the feet planted.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Enemy))]
    public class EnemyAnimatorBridge : MonoBehaviour
    {
        // Animator parameter names (must match the controllers built for §6.3).
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int DieHash = Animator.StringToHash("Die");
        private static readonly int RollHash = Animator.StringToHash("Roll");
        private static readonly int TransformHash = Animator.StringToHash("Transform");

        [Header("References")]
        [Tooltip("Animator driving this enemy. Auto-found in children if unset.")]
        [SerializeField] private Animator animator;

        [Tooltip("Mover whose velocity drives the Speed parameter. Auto-found if unset.")]
        [SerializeField] private EnemyMover mover;

        [Header("Foot-slide correction (GDD §6.3)")]
        [Tooltip("Implied ground speed of the locomotion clip in m/s. Animator.speed is scaled by moveSpeed / this value so feet do not slide. " +
                 "Set from the EnemyDefinition, or authored per prefab here as a fallback.")]
        [SerializeField] private float animatorClipSpeedRef = 1f;

        [Tooltip("Optional definition. When assigned its animatorClipSpeedRef and moveSpeed override the serialized fallbacks.")]
        [SerializeField] private EnemyDefinition definition;

        [Tooltip("Fallback move speed (m/s) used for the correction when no definition and no mover speed is available.")]
        [SerializeField] private float moveSpeedFallback = 5f;

        private Enemy _enemy;
        private float _clipSpeedRef = 1f;
        private bool _dead;
        private bool _hasRollParam;
        private bool _hasTransformParam;

        /// <summary>Implied clip ground speed used for the foot-slide correction.</summary>
        public float AnimatorClipSpeedRef => _clipSpeedRef;

        private void Awake()
        {
            _enemy = GetComponent<Enemy>();
            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);
            if (mover == null)
                mover = GetComponent<EnemyMover>();

            if (animator != null)
                animator.applyRootMotion = false; // GDD §6.3 — safety, mirrors the prefab setting.
        }

        private void OnEnable()
        {
            _dead = false;
            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);
            if (mover == null)
                mover = GetComponent<EnemyMover>();

            ResolveClipSpeedRef();
            ApplyMoveSpeedCorrection();

            if (animator != null)
            {
                animator.Rebind();      // reset to Locomotion after a pooled reuse
                animator.ResetTrigger(DieHash);
                animator.SetFloat(SpeedHash, 0f);

                CacheOptionalParams();

                // Roller only: start in roll form (fast phase, GDD §6.2). The
                // controller's default state is Roll, and setting the bool keeps
                // it there until the phase change fires the Transform trigger.
                if (_hasRollParam)
                    animator.SetBool(RollHash, true);
                if (_hasTransformParam)
                    animator.ResetTrigger(TransformHash);
            }

            if (_enemy != null)
                _enemy.OnDied += HandleDied;

            if (mover != null)
                mover.OnPhaseChange += HandlePhaseChange;
        }

        private void OnDisable()
        {
            if (_enemy != null)
                _enemy.OnDied -= HandleDied;

            if (mover != null)
                mover.OnPhaseChange -= HandlePhaseChange;
        }

        private void Update()
        {
            if (_dead || animator == null)
                return;

            // Lazily resolve the mover in case it was added after this bridge's
            // Awake ran (e.g. assembled at runtime). In shipped prefabs the mover
            // is present so this only ever runs once.
            if (mover == null)
                mover = GetComponent<EnemyMover>();

            float speed = mover != null ? mover.Velocity.magnitude : 0f;
            animator.SetFloat(SpeedHash, speed);

            // Keep the correction current in case the mover's speed changed
            // (Roller phase change, Colossus enrage — GDD §6.2).
            ApplyMoveSpeedCorrection();
        }

        /// <summary>
        /// Resolve the clip reference speed, preferring the definition when set.
        /// </summary>
        private void ResolveClipSpeedRef()
        {
            float fromDef = definition != null ? definition.animatorClipSpeedRef : 0f;
            _clipSpeedRef = fromDef > 0.0001f
                ? fromDef
                : (animatorClipSpeedRef > 0.0001f ? animatorClipSpeedRef : 1f);
        }

        /// <summary>
        /// Set Animator.speed to moveSpeed / clipSpeedRef so the locomotion clip
        /// plays at the pace the unit is actually travelling (GDD §6.3).
        /// </summary>
        private void ApplyMoveSpeedCorrection()
        {
            if (animator == null || _dead)
                return;

            float moveSpeed = ResolveMoveSpeed();
            float ratio = _clipSpeedRef > 0.0001f ? moveSpeed / _clipSpeedRef : 1f;
            animator.speed = Mathf.Max(0.01f, ratio);
        }

        private float ResolveMoveSpeed()
        {
            // DesiredSpeed, not MoveSpeed: under a stun/slow (R18) the multiplier
            // side collapses toward zero and the unit travels at the clamped
            // crawl floor — the clip must play at THAT pace, or the feet slide
            // at full walk speed on a crawling body. For an unaffected unit the
            // two values are identical.
            if (mover != null)
                return mover.DesiredSpeed;
            if (definition != null && definition.moveSpeed > 0.0001f)
                return definition.moveSpeed;
            return moveSpeedFallback;
        }

        /// <summary>Assign the definition at spawn (drives clip speed ref and move speed).</summary>
        public void SetDefinition(EnemyDefinition def)
        {
            definition = def;
            ResolveClipSpeedRef();
            ApplyMoveSpeedCorrection();
        }

        /// <summary>
        /// The Roller reached its phase-change fraction (GDD §6.2). Play
        /// Transform-to-Spider: clear the Roll bool and fire the Transform
        /// trigger so the controller unpacks from roller to spider form.
        /// </summary>
        private void HandlePhaseChange()
        {
            if (_dead || animator == null)
                return;

            if (_hasRollParam)
                animator.SetBool(RollHash, false);
            if (_hasTransformParam)
                animator.SetTrigger(TransformHash);
        }

        /// <summary>Cache which optional Roller parameters this controller exposes.</summary>
        private void CacheOptionalParams()
        {
            _hasRollParam = false;
            _hasTransformParam = false;
            if (animator == null)
                return;

            foreach (var p in animator.parameters)
            {
                if (p.nameHash == RollHash)
                    _hasRollParam = true;
                else if (p.nameHash == TransformHash)
                    _hasTransformParam = true;
            }
        }

        private void HandleDied(Enemy e)
        {
            if (_dead)
                return;
            _dead = true;

            if (animator != null)
            {
                animator.speed = 1f;            // play death at authored speed
                animator.SetFloat(SpeedHash, 0f);
                animator.ResetTrigger(DieHash);
                animator.SetTrigger(DieHash);
            }
        }
    }
}
