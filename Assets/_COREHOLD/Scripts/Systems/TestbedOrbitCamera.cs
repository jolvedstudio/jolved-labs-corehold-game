using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Corehold.Systems
{
    /// <summary>
    /// Drives an orbit / pan / zoom camera rig for the combat testbed so you can look
    /// at the Grid or a Duel from any angle while it plays. It moves a three-part rig
    /// (yaw pivot → pitch arm → camera) and a <see cref="Unity.Cinemachine"/>
    /// CinemachineCamera rides the camera node, so the CinemachineBrain on the Main
    /// Camera renders the pose. Nothing here references Cinemachine types directly —
    /// the rig is just transforms — so it compiles with or without the package and
    /// the brain simply follows whichever node the CinemachineCamera sits on.
    ///
    /// Controls (new Input System):
    ///   • Left OR right mouse drag ... orbit (yaw + pitch)
    ///   • Middle mouse drag .......... pan the focus point
    ///   • Mouse wheel ................ zoom (dolly in/out)
    ///   • W/S/A/D .................... move focus (forward/back/left/right, screen-relative)
    ///   • Q/E ........................ move focus down/up
    ///   • R .......................... reset to the framing it started with
    /// </summary>
    [DisallowMultipleComponent]
    public class TestbedOrbitCamera : MonoBehaviour
    {
        [Header("Rig (auto-found by name if left empty)")]
        [Tooltip("Yaw pivot at the focus point. Rotated around Y for horizontal orbit and moved for panning.")]
        public Transform yawPivot;

        [Tooltip("Pitch arm, child of the yaw pivot. Rotated around X for vertical orbit.")]
        public Transform pitchArm;

        [Tooltip("Camera node, child of the pitch arm. Dollied along local -Z for zoom. The CinemachineCamera lives here.")]
        public Transform cameraNode;

        [Header("Orbit")]
        [Tooltip("Degrees of yaw/pitch per screen pixel of mouse drag.")]
        public float orbitSpeed = 0.25f;

        [Tooltip("Minimum pitch angle (looking more level).")]
        public float minPitch = 5f;

        [Tooltip("Maximum pitch angle (looking more top-down).")]
        public float maxPitch = 85f;

        [Header("Zoom")]
        [Tooltip("Distance from the focus point (metres).")]
        public float distance = 38f;

        public float minDistance = 4f;
        public float maxDistance = 160f;

        [Tooltip("Metres of dolly per wheel notch.")]
        public float zoomSpeed = 6f;

        [Header("Pan / move")]
        [Tooltip("Metres the focus pans per screen pixel of middle-drag, scaled by distance.")]
        public float panSpeed = 0.0015f;

        [Tooltip("Metres per second the focus moves with WASD/QE, scaled by distance.")]
        public float moveSpeed = 0.6f;

        [Header("Smoothing")]
        [Tooltip("Higher = snappier. Position and rotation are eased toward their targets.")]
        public float responsiveness = 14f;

        // Targets the rig eases toward (so motion feels smooth, not jittery per frame).
        private float _yaw;
        private float _pitch;
        private float _targetDistance;
        private Vector3 _focus;

        // Starting framing for the reset key.
        private float _startYaw, _startPitch, _startDistance;
        private Vector3 _startFocus;

        private void Start()
        {
            ResolveRig();

            _focus = yawPivot != null ? yawPivot.position : transform.position;
            Vector3 e = yawPivot != null ? yawPivot.eulerAngles : Vector3.zero;
            _yaw = e.y;
            _pitch = Mathf.Clamp(pitchArm != null ? NormalizePitch(pitchArm.localEulerAngles.x) : 30f, minPitch, maxPitch);
            _targetDistance = distance;

            _startYaw = _yaw;
            _startPitch = _pitch;
            _startDistance = _targetDistance;
            _startFocus = _focus;

            ApplyImmediate();
        }

        private void ResolveRig()
        {
            if (yawPivot == null)
                yawPivot = transform;
            if (pitchArm == null && yawPivot.childCount > 0)
                pitchArm = yawPivot.GetChild(0);
            if (cameraNode == null && pitchArm != null && pitchArm.childCount > 0)
                cameraNode = pitchArm.GetChild(0);
        }

        private static float NormalizePitch(float x) => x > 180f ? x - 360f : x;

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            ReadInput(Time.unscaledDeltaTime);
#endif
            EaseTowardTargets(Time.unscaledDeltaTime);
        }

#if ENABLE_INPUT_SYSTEM
        private void ReadInput(float dt)
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (mouse == null)
                return;

            Vector2 delta = mouse.delta.ReadValue();

            bool orbiting = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            bool panning = mouse.middleButton.isPressed;

            if (orbiting && !panning)
            {
                _yaw += delta.x * orbitSpeed;
                _pitch = Mathf.Clamp(_pitch - delta.y * orbitSpeed, minPitch, maxPitch);
            }

            if (panning)
            {
                // Pan in the camera's screen plane, scaled by distance so it feels
                // consistent whether zoomed in or out.
                float scale = panSpeed * _targetDistance;
                Vector3 right = cameraNode != null ? cameraNode.right : Vector3.right;
                Vector3 up = cameraNode != null ? cameraNode.up : Vector3.up;
                _focus -= right * (delta.x * scale) + up * (delta.y * scale);
            }

            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                // Wheel notches report ~120 per step on many platforms; normalise.
                float step = Mathf.Sign(wheel) * zoomSpeed;
                _targetDistance = Mathf.Clamp(_targetDistance - step, minDistance, maxDistance);
            }

            if (kb != null)
            {
                Vector3 move = Vector3.zero;
                if (kb.wKey.isPressed) move += FlatForward();
                if (kb.sKey.isPressed) move -= FlatForward();
                if (kb.dKey.isPressed) move += FlatRight();
                if (kb.aKey.isPressed) move -= FlatRight();
                if (kb.eKey.isPressed) move += Vector3.up;
                if (kb.qKey.isPressed) move -= Vector3.up;

                if (move.sqrMagnitude > 0.0001f)
                    _focus += move.normalized * (moveSpeed * _targetDistance * dt);

                if (kb.rKey.wasPressedThisFrame)
                {
                    _yaw = _startYaw;
                    _pitch = _startPitch;
                    _targetDistance = _startDistance;
                    _focus = _startFocus;
                }
            }
        }

        private Vector3 FlatForward()
        {
            Vector3 f = cameraNode != null ? cameraNode.forward : Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
        }

        private Vector3 FlatRight()
        {
            Vector3 r = cameraNode != null ? cameraNode.right : Vector3.right;
            r.y = 0f;
            return r.sqrMagnitude > 0.0001f ? r.normalized : Vector3.right;
        }
#endif

        private void EaseTowardTargets(float dt)
        {
            if (yawPivot == null || pitchArm == null || cameraNode == null)
                return;

            float t = 1f - Mathf.Exp(-responsiveness * dt); // frame-rate independent ease

            yawPivot.position = Vector3.Lerp(yawPivot.position, _focus, t);

            Quaternion yawRot = Quaternion.Euler(0f, _yaw, 0f);
            yawPivot.rotation = Quaternion.Slerp(yawPivot.rotation, yawRot, t);

            Quaternion pitchRot = Quaternion.Euler(_pitch, 0f, 0f);
            pitchArm.localRotation = Quaternion.Slerp(pitchArm.localRotation, pitchRot, t);

            distance = Mathf.Lerp(distance, _targetDistance, t);
            cameraNode.localPosition = new Vector3(0f, 0f, -distance);
            cameraNode.localRotation = Quaternion.identity; // faces +Z, back toward the focus
        }

        private void ApplyImmediate()
        {
            if (yawPivot == null || pitchArm == null || cameraNode == null)
                return;
            yawPivot.position = _focus;
            yawPivot.rotation = Quaternion.Euler(0f, _yaw, 0f);
            pitchArm.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            distance = _targetDistance;
            cameraNode.localPosition = new Vector3(0f, 0f, -distance);
            cameraNode.localRotation = Quaternion.identity;
        }

        /// <summary>Re-centre the orbit focus on a world point (e.g. the duel midpoint).</summary>
        public void SetFocus(Vector3 worldPoint) => _focus = worldPoint;

        /// <summary>Set the target orbit distance (metres), clamped to the zoom range.</summary>
        public void SetDistance(float d) => _targetDistance = Mathf.Clamp(d, minDistance, maxDistance);
    }
}
