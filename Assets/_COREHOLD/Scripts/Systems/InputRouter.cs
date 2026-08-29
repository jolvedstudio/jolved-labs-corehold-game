using System;
using Corehold.Towers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using ETouch = UnityEngine.InputSystem.EnhancedTouch;

namespace Corehold.Systems
{
    /// <summary>
    /// One tap, one result (GDD §1, §9.3, Ticket 22).
    ///
    /// The routing order is deliberate and non-negotiable:
    ///
    /// 1. <b>Ask the UI first.</b> Call
    ///    <c>EventSystem.current.IsPointerOverGameObject(fingerId)</c> and return
    ///    immediately if uGUI consumed the tap. uGUI is NOT in the physics scene,
    ///    so a physics layer mask can never hit a Canvas element — the only way to
    ///    know the UI ate the tap is to ask the EventSystem. The <b>fingerId</b>
    ///    overload matters: the parameterless <c>IsPointerOverGameObject()</c> is
    ///    mouse-only and is a well-known mobile bug where touches always report
    ///    "not over UI". On touch we pass the real finger id; on mouse we pass the
    ///    left-button pointer id (-1).
    ///
    /// 2. <b>Only then raycast</b> a single <c>Physics.Raycast</c> against the
    ///    Hardpoint layer only, and report the pad that was hit.
    ///
    /// Single touch only (GDD §1): we act on the FIRST active touch and ignore the
    /// rest. No pinch, no long-press, no multi-touch — and taps still fire ON
    /// PRESS, exactly as before. The ONE sanctioned gesture (user call, tower
    /// relocation): a press that lands on an OCCUPIED pad and then MOVES beyond a
    /// small threshold becomes a drag — no timers, so tap latency is unchanged;
    /// the drag simply emerges from movement. Listeners get
    /// <see cref="OnPadDragBegin"/> / <see cref="OnPadDragUpdate"/> /
    /// <see cref="OnPadDragEnd"/>.
    ///
    /// The router does not open menus or spend salvage itself; it raises
    /// <see cref="OnHardpointTapped"/> (a pad was tapped) and
    /// <see cref="OnEmptyTapped"/> (a tap that hit neither UI nor a pad — used to
    /// dismiss an open panel). The temporary <c>BuildPanel</c> and the real UI in
    /// Ticket 36 listen to these.
    /// </summary>
    [DisallowMultipleComponent]
    public class InputRouter : MonoBehaviour
    {
        [Header("Raycast")]
        [Tooltip("Camera used to turn a screen tap into a world ray. Defaults to Camera.main.")]
        [SerializeField] private Camera raycastCamera;

        [Tooltip("Physics layers the tap ray tests against. Set to the Hardpoint layer only.")]
        [SerializeField] private LayerMask hardpointMask;

        [Tooltip("Maximum ray distance in metres. The camera sits ~100 m out (GDD §5.1).")]
        [SerializeField] private float maxRayDistance = 500f;

        /// <summary>Raised when a tap lands on a hardpoint. Argument is the tapped pad.</summary>
        public event Action<TowerHardpoint> OnHardpointTapped;

        /// <summary>
        /// Raised when a tap hits neither UI nor a hardpoint (empty ground). UI uses
        /// this to dismiss an open build/tower panel.
        /// </summary>
        public event Action OnEmptyTapped;

        /// <summary>A press on this OCCUPIED pad moved past the drag threshold —
        /// a tower drag has begun (the tap already fired on press; listeners hide
        /// what it opened).</summary>
        public event Action<TowerHardpoint> OnPadDragBegin;

        /// <summary>Screen position each frame while a pad drag is held.</summary>
        public event Action<Vector2> OnPadDragUpdate;

        /// <summary>The pad drag was released at this screen position.</summary>
        public event Action<Vector2> OnPadDragEnd;

        [Tooltip("Pixels of pointer travel that turn a press on an occupied pad into a tower drag.")]
        [SerializeField] private float dragThresholdPixels = 18f;

        // Press-tracking state for the drag gesture.
        private TowerHardpoint _pressPad;
        private Vector2 _pressPos;
        private bool _pressActive;
        private bool _dragging;

        // Mouse "finger id" used by the EventSystem for the left button.
        private const int MousePointerId = -1;

        // An armed ability (Strike Wing R19) that claims field taps ahead of pad
        // routing. UI still gets first refusal — the claimant only sees taps the
        // EventSystem declined. Returns true when it consumed the tap.
        private Func<Vector2, bool> _tapClaimant;

        /// <summary>Route field taps to <paramref name="claimant"/> until cleared.</summary>
        public void SetTapClaimant(Func<Vector2, bool> claimant) => _tapClaimant = claimant;

        /// <summary>Clear the claimant (only if it is still the registered one).</summary>
        public void ClearTapClaimant(Func<Vector2, bool> claimant)
        {
            if (_tapClaimant == claimant)
                _tapClaimant = null;
        }

        private void Awake()
        {
            if (raycastCamera == null)
                raycastCamera = Camera.main;

            // Default the mask to the Hardpoint layer if the inspector left it unset,
            // so a freshly added router still works.
            if (hardpointMask == 0)
            {
                int layer = LayerMask.NameToLayer("Hardpoint");
                if (layer >= 0)
                    hardpointMask = 1 << layer;
            }
        }

        private void OnEnable()
        {
            // EnhancedTouch is required to read touches through the new Input System.
            if (!ETouch.EnhancedTouchSupport.enabled)
                ETouch.EnhancedTouchSupport.Enable();
        }

        private void Update()
        {
            // New Input System. Touch takes priority over mouse so the same code path
            // serves phone and desktop, single-touch only (GDD §1).
            var active = ETouch.Touch.activeTouches;
            if (active.Count > 0)
            {
                var t = active[0]; // first touch only (GDD §1)
                switch (t.phase)
                {
                    case UnityEngine.InputSystem.TouchPhase.Began:
                        BeginPress(RouteTap(t.screenPosition, t.touchId), t.screenPosition);
                        break;
                    case UnityEngine.InputSystem.TouchPhase.Moved:
                    case UnityEngine.InputSystem.TouchPhase.Stationary:
                        TrackPress(t.screenPosition);
                        break;
                    case UnityEngine.InputSystem.TouchPhase.Ended:
                    case UnityEngine.InputSystem.TouchPhase.Canceled:
                        EndPress(t.screenPosition);
                        break;
                }
                return;
            }

            var mouse = Mouse.current;
            if (mouse == null)
                return;
            if (mouse.leftButton.wasPressedThisFrame)
                BeginPress(RouteTap(mouse.position.ReadValue(), MousePointerId), mouse.position.ReadValue());
            else if (mouse.leftButton.isPressed)
                TrackPress(mouse.position.ReadValue());
            else if (mouse.leftButton.wasReleasedThisFrame)
                EndPress(mouse.position.ReadValue());
        }

        // ----- The one sanctioned gesture: drag a tower off its pad -----

        private void BeginPress(TowerHardpoint pad, Vector2 screenPos)
        {
            _pressPad = pad != null && pad.IsOccupied ? pad : null;
            _pressPos = screenPos;
            _pressActive = _pressPad != null;
            _dragging = false;
        }

        private void TrackPress(Vector2 screenPos)
        {
            if (!_pressActive)
                return;
            if (!_dragging)
            {
                if ((screenPos - _pressPos).magnitude < dragThresholdPixels)
                    return;
                if (_pressPad == null || !_pressPad.IsOccupied)
                {
                    _pressActive = false;
                    return;
                }
                _dragging = true;
                OnPadDragBegin?.Invoke(_pressPad);
            }
            OnPadDragUpdate?.Invoke(screenPos);
        }

        private void EndPress(Vector2 screenPos)
        {
            if (_dragging)
                OnPadDragEnd?.Invoke(screenPos);
            _pressActive = false;
            _dragging = false;
            _pressPad = null;
        }

        /// <summary>
        /// Route a single tap at <paramref name="screenPos"/> with the given pointer
        /// id. UI is asked first; if it did not consume the tap, one raycast tests
        /// the Hardpoint layer. Returns the pad that was hit (null otherwise) so the
        /// caller can arm the drag gesture on the same press.
        /// </summary>
        private TowerHardpoint RouteTap(Vector2 screenPos, int pointerId)
        {
            // 0. First-person turret control owns the mouse (M-a): LMB is the
            //    trigger there, not a tap — routing it would pop panels while
            //    the player is firing.
            if (ManualTurretControl.IsActive)
                return null;

            // 1. UI gets first refusal. Must pass the fingerId — the parameterless
            //    overload is mouse-only and misreports on mobile (GDD §9.3).
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId))
                return null;

            // 1b. An armed ability claims the tap before pad routing (R19).
            if (_tapClaimant != null && _tapClaimant(screenPos))
                return null;

            if (raycastCamera == null)
            {
                raycastCamera = Camera.main;
                if (raycastCamera == null)
                    return null;
            }

            // 2. One raycast, Hardpoint layer only.
            Ray ray = raycastCamera.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, hardpointMask, QueryTriggerInteraction.Collide))
            {
                var pad = hit.collider.GetComponentInParent<TowerHardpoint>();
                if (pad != null)
                {
                    OnHardpointTapped?.Invoke(pad);
                    return pad;
                }
            }

            // Tapped nothing actionable — let listeners dismiss any open panel.
            OnEmptyTapped?.Invoke();
            return null;
        }

        /// <summary>The pad under a screen point right now (drag hover), or null.</summary>
        public TowerHardpoint PadAt(Vector2 screenPos)
        {
            if (raycastCamera == null)
                raycastCamera = Camera.main;
            if (raycastCamera == null)
                return null;
            Ray ray = raycastCamera.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, hardpointMask, QueryTriggerInteraction.Collide))
                return hit.collider.GetComponentInParent<TowerHardpoint>();
            return null;
        }
    }
}
