using Corehold.Towers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.Systems
{
    /// <summary>
    /// The turret cam (M-a, tier 1): a picture-in-picture panel showing the
    /// selected turret's muzzle view, plus a short flyby over every Strike Wing
    /// impact. Pure spectacle — no gameplay coupling — so it self-assembles at
    /// runtime (camera, RenderTexture, its own small overlay canvas) and no
    /// scene or builder needs to know it exists. Toggled from the tower panel;
    /// remembers the choice per session.
    ///
    /// Cost control: the second camera renders only while the panel is visible,
    /// into a 512×288 target — a bounded fill-rate slice even on WebGL.
    /// </summary>
    [DisallowMultipleComponent]
    public class TurretCamera : MonoBehaviour
    {
        private const int RtWidth = 512, RtHeight = 288;
        private static TurretCamera _instance;

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _panel;
        private TMP_Text _label;

        private TowerHardpoint _pad;      // followed turret, null = none
        private TurretAim _aim;           // its head, for muzzle placement

        // Flyby state (overrides the follow while active).
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
            _aim = pad.Occupant != null ? pad.Occupant.GetComponentInChildren<TurretAim>() : null;
            SetVisible(true);
            if (_label != null && pad.Occupant != null && pad.Occupant.Definition != null)
                _label.text = $"CAM · {pad.Occupant.Definition.displayName.ToUpperInvariant()}";
        }

        private void Detach()
        {
            _pad = null;
            _aim = null;
            if (Time.time >= _flybyUntil)
                SetVisible(false);
        }

        private void LateUpdate()
        {
            if (_cam == null || !_cam.enabled) return;

            if (Time.time < _flybyUntil)
            {
                // A slow arcing sweep down toward the strike point.
                float k = 1f - (_flybyUntil - Time.time) / 2.2f;
                Vector3 from = _flybyPoint + new Vector3(-14f, 16f, -10f);
                Vector3 to = _flybyPoint + new Vector3(8f, 9f, 6f);
                _cam.transform.position = Vector3.Lerp(from, to, k);
                _cam.transform.LookAt(_flybyPoint + Vector3.up * 0.5f);
                return;
            }

            if (_pad == null || !_pad.IsOccupied)
            {
                // Followed turret sold/destroyed/walked away mid-view.
                Detach();
                return;
            }

            Transform head = _aim != null
                ? (_aim.PitchPivot != null ? _aim.PitchPivot
                   : _aim.YawPivot != null ? _aim.YawPivot : _pad.transform)
                : _pad.transform;

            // Over-the-barrel: slightly up and behind the head, looking along it.
            _cam.transform.position = head.position - head.forward * 1.1f + Vector3.up * 0.55f;
            _cam.transform.rotation = Quaternion.LookRotation(
                (head.forward + Vector3.down * 0.08f).normalized, Vector3.up);
        }

        // ------------------------------------------------------------- assembly

        private void SetVisible(bool on)
        {
            if (_panel != null) _panel.SetActive(on);
            if (_cam != null) _cam.enabled = on;
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
            _cam.fieldOfView = 55f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 300f;
            _cam.enabled = false;

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
