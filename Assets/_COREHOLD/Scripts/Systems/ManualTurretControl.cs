using Corehold.Enemies;
using Corehold.Towers;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Corehold.Systems
{
    /// <summary>
    /// Man the turret (M-a, tier 2): full-screen control of one turret. The
    /// player aims with the mouse, the nearest engageable enemy to the cursor
    /// ray becomes the turret's target (fed through
    /// <see cref="TowerTargeting.ManualTarget"/> — the one seam everything
    /// downstream already reads), and rounds fire only while the left button is
    /// held. Manning grants the weapon's MannedFireRateBonus.
    ///
    /// Enter from the tower panel's CONTROL button; exit with Esc or
    /// right-click. The main camera is disabled, not moved, so leaving restores
    /// the exact framing. First playable slice: desktop mouse only — mobile
    /// stays on the automated loop.
    /// </summary>
    [DisallowMultipleComponent]
    public class ManualTurretControl : MonoBehaviour
    {
        private static ManualTurretControl _active;

        public static bool IsActive => _active != null;

        private TowerHardpoint _pad;
        private TowerTargeting _targeting;
        private TowerWeapon _weapon;
        private TurretAim _aim;
        private Camera _cam;
        private Camera _mainCam;
        private GameObject _hud;

        private float _yaw, _pitch;

        /// <summary>[TUNE] Mouse sensitivity, degrees per pixel of delta.</summary>
        private const float Sensitivity = 0.14f;
        private const float PitchMin = -8f, PitchMax = 28f;

        public static void Enter(TowerHardpoint pad)
        {
            if (pad == null || !pad.IsOccupied) return;
            Exit(); // one turret at a time

            var go = new GameObject("ManualTurretControl(Runtime)");
            _active = go.AddComponent<ManualTurretControl>();
            _active.Bind(pad);
        }

        public static void Exit()
        {
            if (_active != null)
                _active.Unbind();
        }

        private void Bind(TowerHardpoint pad)
        {
            _pad = pad;
            var tower = pad.Occupant;
            _targeting = tower.GetComponent<TowerTargeting>();
            _weapon = tower.GetComponent<TowerWeapon>();
            _aim = tower.GetComponentInChildren<TurretAim>();
            _mainCam = Camera.main;

            Transform head = _aim != null && _aim.YawPivot != null ? _aim.YawPivot : tower.transform;
            _yaw = head.eulerAngles.y;
            _pitch = 6f;

            var camGo = new GameObject("MannedCam");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.fieldOfView = 58f;
            _cam.nearClipPlane = 0.1f;

            if (_mainCam != null) _mainCam.enabled = false;
            if (_weapon != null) _weapon.ManualMode = true;

            BuildHud();
        }

        private void Unbind()
        {
            if (_weapon != null)
            {
                _weapon.ManualMode = false;
                _weapon.ManualTrigger = false;
            }
            if (_targeting != null) _targeting.ManualTarget = null;
            if (_mainCam != null) _mainCam.enabled = true;
            _active = null;
            Destroy(gameObject);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (_pad == null || !_pad.IsOccupied ||
                (kb != null && kb.escapeKey.wasPressedThisFrame) ||
                (mouse != null && mouse.rightButton.wasPressedThisFrame))
            {
                Unbind();
                return;
            }
            if (mouse == null) return;

            // Mouse look.
            Vector2 delta = mouse.delta.ReadValue();
            _yaw += delta.x * Sensitivity;
            _pitch = Mathf.Clamp(_pitch - delta.y * Sensitivity, PitchMin, PitchMax);

            // Pick the engageable enemy nearest the view ray; the targeting seam
            // does the rest (aim slews, weapon fires on trigger).
            if (_targeting != null)
                _targeting.ManualTarget = PickTarget();

            if (_weapon != null)
                _weapon.ManualTrigger = mouse.leftButton.isPressed;
        }

        private void LateUpdate()
        {
            if (_cam == null || _pad == null || !_pad.IsOccupied) return;

            Transform head = _aim != null
                ? (_aim.PitchPivot != null ? _aim.PitchPivot
                   : _aim.YawPivot != null ? _aim.YawPivot : _pad.transform)
                : _pad.transform;

            Quaternion look = Quaternion.Euler(_pitch, _yaw, 0f);
            _cam.transform.position = head.position - look * Vector3.forward * 1.6f + Vector3.up * 0.7f;
            _cam.transform.rotation = look;
        }

        private Enemy PickTarget()
        {
            Ray ray = _cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            Enemy best = null;
            float bestScore = float.MaxValue;
            var live = Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Enemy e = live[i];
                if (e == null || !e.IsAlive) continue;
                if (_targeting != null && !_targeting.CanEngage(e)) continue;

                // Angular distance from the crosshair ray beats world distance —
                // the player is pointing, not pathfinding.
                Vector3 to = e.HitPoint - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along <= 0f) continue;
                float off = Vector3.Cross(ray.direction, to).magnitude / Mathf.Max(0.1f, along);
                if (off < bestScore)
                {
                    bestScore = off;
                    best = e;
                }
            }
            return bestScore < 0.35f ? best : null; // ~19° cone; outside it, no pick
        }

        private void BuildHud()
        {
            _hud = new GameObject("MannedHud");
            _hud.transform.SetParent(transform, false);
            var canvas = _hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            var dotGo = new GameObject("Crosshair");
            dotGo.transform.SetParent(_hud.transform, false);
            var dot = dotGo.AddComponent<Image>();
            var theme = Corehold.UI.UITheme.Instance;
            dot.color = theme != null ? theme.cyan : Color.cyan;
            var rt = dot.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(5f, 5f);

            var hintGo = new GameObject("Hint");
            hintGo.transform.SetParent(_hud.transform, false);
            var hint = hintGo.AddComponent<TMPro.TextMeshProUGUI>();
            hint.text = "HOLD LMB TO FIRE   ·   ESC / RIGHT-CLICK TO RELEASE";
            hint.fontSize = 15f;
            hint.alignment = TMPro.TextAlignmentOptions.Center;
            if (theme != null && theme.font != null) hint.font = theme.font;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            var hrt = hint.rectTransform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0f);
            hrt.pivot = new Vector2(0.5f, 0f);
            hrt.anchoredPosition = new Vector2(0f, 18f);
            hrt.sizeDelta = new Vector2(700f, 24f);
        }
    }
}
