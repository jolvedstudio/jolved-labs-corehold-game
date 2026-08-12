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
    /// rest. No drag, no pinch, no long-press, no multi-touch — a TD needs none of
    /// it and mobile-browser multi-touch is unreliable.
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

        // Mouse "finger id" used by the EventSystem for the left button.
        private const int MousePointerId = -1;

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
                if (t.phase == UnityEngine.InputSystem.TouchPhase.Began)
                    RouteTap(t.screenPosition, t.touchId);
                return;
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                RouteTap(mouse.position.ReadValue(), MousePointerId);
        }

        /// <summary>
        /// Route a single tap at <paramref name="screenPos"/> with the given pointer
        /// id. UI is asked first; if it did not consume the tap, one raycast tests the
        /// Hardpoint layer.
        /// </summary>
        private void RouteTap(Vector2 screenPos, int pointerId)
        {
            // 1. UI gets first refusal. Must pass the fingerId — the parameterless
            //    overload is mouse-only and misreports on mobile (GDD §9.3).
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId))
                return;

            if (raycastCamera == null)
            {
                raycastCamera = Camera.main;
                if (raycastCamera == null)
                    return;
            }

            // 2. One raycast, Hardpoint layer only.
            Ray ray = raycastCamera.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, hardpointMask, QueryTriggerInteraction.Collide))
            {
                var pad = hit.collider.GetComponentInParent<TowerHardpoint>();
                if (pad != null)
                {
                    OnHardpointTapped?.Invoke(pad);
                    return;
                }
            }

            // Tapped nothing actionable — let listeners dismiss any open panel.
            OnEmptyTapped?.Invoke();
        }
    }
}
