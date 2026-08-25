using Corehold.Towers;
using TMPro;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.Systems
{
    /// <summary>
    /// The turret cam (M-a, tier 1): a picture-in-picture panel showing the
    /// selected turret from a CINEMACHINE rig, plus a short flyby over every
    /// Strike Wing impact. Pure spectacle — no gameplay coupling — so it
    /// self-assembles at runtime and no scene or builder needs to know it
    /// exists. Toggled from the tower panel; remembers the choice per session.
    ///
    /// Rig: CinemachineCamera + OrbitalFollow (orbit centred a little ABOVE
    /// the turret) + RotationComposer (aims at a gaze point — the current
    /// target when engaging, ahead of the barrel otherwise) + Deoccluder
    /// (pushes clear of terrain hills and props; the terrain stage bakes a
    /// collider for exactly this). Idle turrets get a slow cinematic orbit;
    /// an engaging turret's camera swings behind the gun.
    ///
    /// Cost control: renders only while the panel is visible, into a 1024×576
    /// target — displayed at ~328 px wide, so the PiP is effectively
    /// supersampled (sharper on low-res texture budgets) yet still a bounded
    /// fill-rate slice.
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretCamera : MonoBehaviour
    {
        private const int RtWidth = 1024, RtHeight = 576;

        /// <summary>[TUNE] Orbit shape: metres above the turret base the orbit
        /// centres on, orbit radius, camera elevation in degrees, idle orbit
        /// speed (deg/s) and how fast the camera swings behind an engaging gun.</summary>
        private const float OrbitCentreUp = 1.4f;
        private const float OrbitRadius = 6.5f;
        private const float OrbitElevation = 24f;
        private const float IdleOrbitSpeed = 9f;
        private const float EngageSwingSpeed = 140f;

        private static TurretCamera _instance;

        private Camera _cam;
        private CinemachineBrain _brain;
        private RenderTexture _rt;
        private GameObject _panel;
        private TMP_Text _label;

        private GameObject _vcamGo;
        private CinemachineCamera _vcam;
        private CinemachineOrbitalFollow _orbital;
        private Transform _gaze;          // what the composer aims at

        private TowerHardpoint _pad;      // followed turret, null = none
        private TurretAim _aim;           // its head, for the gaze direction
        private TowerTargeting _targeting;

        // Flyby state (overrides the rig while active).
        private Vector3 _flybyPoint;
        private float _flybyUntil = -1f;

        /// <summary>Session preference: the player turned the cam on.</summary>
        public static bool Enabled { get; private set; }

        public static bool IsShowing => _instance != null && _instance._panel != null &&
                                        _instance._panel.activeSelf;

        private static TurretCamera Ensure()
        {
            if (_instance == null)
            {
                var go = new GameObject("TurretCamera(Runtime)");
                _instance = go.AddComponent<TurretCamera>();
            }
            return _instance;
        }

        /// <summary>Tower-panel toggle: turn the cam on for this pad / off entirely.</summary>
        public static void Toggle(TowerHardpoint pad)
        {
            var tc = Ensure();
            if (Enabled && tc._pad == pad)
            {
                Enabled = false;
                tc.Detach();
                return;
            }
            Enabled = true;
            tc.Follow(pad);
        }

        /// <summary>Tower panel selection changed; follow the new pad if the cam is on.</summary>
        public static void NotifySelected(TowerHardpoint pad)
        {
            if (_instance == null || !Enabled) return;
            _instance.Follow(pad);
        }

        public static void NotifyDeselected()
        {
            // The panel closed. Keep a flyby alive; otherwise go dark (the cam
            // is a companion to the panel, not a permanent HUD element).
            if (_instance == null) return;
            if (Time.time < _instance._flybyUntil) return;
            _instance.Detach();
        }

        /// <summary>Hard off — used when manual turret control takes the screen:
        /// two Cinemachine brains must never contest the same live rig.</summary>
        internal static void Suppress()
        {
            if (_instance == null) return;
            _instance._flybyUntil = -1f;
            _instance.Detach();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            StrikeWingAbility.OnStrikeCommitted += HandleStrike;
        }

        private void OnDestroy()
        {
            StrikeWingAbility.OnStrikeCommitted -= HandleStrike;
            if (_instance == this) _instance = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }

        private void HandleStrike(Vector3 point)
        {
            // Flybys play regardless of the toggle — they're the airstrike's
            // payoff shot, 2.2 s, then the cam returns to whatever it was doing.
            if (ManualTurretControl.IsActive) return; // the player IS a camera right now
            BuildRig();
            _flybyPoint = point;
            _flybyUntil = Time.time + 2.2f;
            SetVisible(true);
            if (_label != null) _label.text = "STRIKE WING";
        }

        private void Follow(TowerHardpoint pad)
        {
            if (pad == null || !pad.IsOccupied) { Detach(); return; }
            BuildRig();
            _pad = pad;
            var tower = pad.Occupant;
            _aim = tower != null ? tower.GetComponentInChildren<TurretAim>() : null;
            _targeting = tower != null ? tower.GetComponent<TowerTargeting>() : null;

            _vcam.Follow = tower != null ? tower.transform : pad.transform;
            _vcam.LookAt = _gaze;

            // Seed the orbit behind wherever the gun currently points, so the
            // first frame is already a sensible over-shoulder shot.
            Transform head = HeadOf();
            _orbital.HorizontalAxis.Value = head.eulerAngles.y + 180f;
            _orbital.VerticalAxis.Value = OrbitElevation;

            SetVisible(true);
            if (_label != null && tower != null && tower.Definition != null)
                _label.text = $"CAM · {tower.Definition.displayName.ToUpperInvariant()}";
        }

        private void Detach()
        {
            _pad = null;
            _aim = null;
            _targeting = null;
            if (Time.time >= _flybyUntil)
                SetVisible(false);
        }

        private Transform HeadOf()
        {
            return _aim != null
                ? (_aim.PitchPivot != null ? _aim.PitchPivot
                   : _aim.YawPivot != null ? _aim.YawPivot : _pad.transform)
                : (_pad != null ? _pad.transform : transform);
        }

        private void LateUpdate()
        {
            if (_cam == null || !_cam.enabled) return;

            if (Time.time < _flybyUntil)
            {
                // Manual arcing sweep down toward the strike point — the brain
                // is off for these 2.2 s so the raw camera can fly free.
                if (_brain != null) _brain.enabled = false;
                float k = 1f - (_flybyUntil - Time.time) / 2.2f;
                Vector3 from = _flybyPoint + new Vector3(-14f, 16f, -10f);
                Vector3 to = _flybyPoint + new Vector3(8f, 9f, 6f);
                _cam.transform.position = Vector3.Lerp(from, to, k);
                _cam.transform.LookAt(_flybyPoint + Vector3.up * 0.5f);
                return;
            }
            if (_brain != null && !_brain.enabled) _brain.enabled = true;

            if (_pad == null || !_pad.IsOccupied)
            {
                // Followed turret sold/destroyed/walked away mid-view.
                Detach();
                return;
            }

            Transform head = HeadOf();

            // The gaze: the live target while engaging, else a point out along
            // the barrel. The composer keeps it framed; the deoccluder keeps
            // the line to it clear of hills and props.
            var enemy = _targeting != null ? _targeting.CurrentTarget : null;
            if (enemy != null && enemy.IsAlive)
            {
                _gaze.position = enemy.HitPoint;
                // Swing behind the gun so the shot reads over-the-shoulder.
                float gunYaw = head.eulerAngles.y;
                _orbital.HorizontalAxis.Value = Mathf.MoveTowardsAngle(
                    _orbital.HorizontalAxis.Value, gunYaw + 180f, EngageSwingSpeed * Time.deltaTime);
            }
            else
            {
                _gaze.position = head.position + head.forward * 14f + Vector3.up * 0.5f;
                // Idle: a slow cinematic orbit around the turret.
                _orbital.HorizontalAxis.Value += IdleOrbitSpeed * Time.deltaTime;
            }
        }

        // ------------------------------------------------------------- assembly

        private void SetVisible(bool on)
        {
            if (_panel != null) _panel.SetActive(on);
            if (_cam != null) _cam.enabled = on;
            if (_vcamGo != null) _vcamGo.SetActive(on);
        }

        private void BuildRig()
        {
            if (_cam != null) return;

            _rt = new RenderTexture(RtWidth, RtHeight, 24, RenderTextureFormat.Default);
            _rt.name = "TurretCamRT";

            var camGo = new GameObject("TurretCamLens");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.fieldOfView = 50f;
            _cam.nearClipPlane = 0.15f;
            _cam.farClipPlane = 300f;
            _cam.enabled = false;
            _brain = camGo.AddComponent<CinemachineBrain>();

            _gaze = new GameObject("TurretCamGaze").transform;
            _gaze.SetParent(transform, false);

            _vcamGo = new GameObject("TurretCamRig");
            _vcamGo.transform.SetParent(transform, false);
            _vcam = _vcamGo.AddComponent<CinemachineCamera>();
            var lens = _vcam.Lens;
            lens.FieldOfView = 50f;
            lens.NearClipPlane = 0.15f;
            lens.FarClipPlane = 300f;
            _vcam.Lens = lens;

            _orbital = _vcamGo.AddComponent<CinemachineOrbitalFollow>();
            _orbital.TargetOffset = new Vector3(0f, OrbitCentreUp, 0f);
            _orbital.Radius = OrbitRadius;
            _orbital.HorizontalAxis.Wrap = true;
            _orbital.VerticalAxis.Range = new Vector2(-10f, 70f);
            _orbital.VerticalAxis.Value = OrbitElevation;

            _vcamGo.AddComponent<CinemachineRotationComposer>();

            var deocc = _vcamGo.AddComponent<CinemachineDeoccluder>();
            int hardpoint = LayerMask.NameToLayer("Hardpoint");
            deocc.CollideAgainst = hardpoint >= 0 ? ~(1 << hardpoint) : ~0;

            _vcamGo.SetActive(false);

            // Own tiny overlay canvas: bottom-left, clear of the build bar's
            // centre span and the integrity panel above it.
            var canvasGo = new GameObject("TurretCamCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(canvasGo.transform, false);
            var frame = _panel.AddComponent<Image>();
            frame.color = new Color(0.04f, 0.07f, 0.10f, 0.9f);
            var rt = _panel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(20f, 190f);
            rt.sizeDelta = new Vector2(328f, 212f);

            var viewGo = new GameObject("View");
            viewGo.transform.SetParent(_panel.transform, false);
            var raw = viewGo.AddComponent<RawImage>();
            raw.texture = _rt;
            var vrt = raw.rectTransform;
            vrt.anchorMin = new Vector2(0f, 0f);
            vrt.anchorMax = new Vector2(1f, 1f);
            vrt.offsetMin = new Vector2(4f, 4f);
            vrt.offsetMax = new Vector2(-4f, -26f);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_panel.transform, false);
            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.fontSize = 14f;
            _label.alignment = TextAlignmentOptions.MidlineLeft;
            var theme = Corehold.UI.UITheme.Instance;
            if (theme != null && theme.font != null) _label.font = theme.font;
            _label.color = theme != null ? theme.cyan : Color.cyan;
            var lrt = _label.rectTransform;
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.anchoredPosition = new Vector2(0f, 0f);
            lrt.sizeDelta = new Vector2(-16f, 22f);

            _panel.SetActive(false);
        }
    }
}
