using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// HUD button for the Strike Wing ability (R19). Tap to arm targeting, tap
    /// again to cancel; while cooling down it shows a radial sweep and a seconds
    /// countdown. Built and wired by BuildRealUI; on Awake it ENSURES the scene
    /// has a <see cref="StrikeWingAbility"/>, so the button is the only piece a
    /// scene needs to carry.
    /// </summary>
    [DisallowMultipleComponent]
    public class StrikeWingButton : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text label;

        [Tooltip("Radial Image laid over the button; fillAmount sweeps 1 → 0 with the cooldown.")]
        [SerializeField] private Image cooldownFill;

        private StrikeWingAbility _ability;
        private StrikeWingAbility.Phase _shownPhase = (StrikeWingAbility.Phase)(-1);
        private int _shownSeconds = -1;
        private string _readyLabel;

        private void Awake()
        {
            _ability = StrikeWingAbility.Ensure();
            if (button == null)
                button = GetComponent<Button>();
            _readyLabel = _ability != null ? $"STRIKE {_ability.Cost}" : "STRIKE";
        }

        private void OnEnable()
        {
            if (button != null)
                button.onClick.AddListener(HandleClick);
        }

        private void OnDisable()
        {
            if (button != null)
                button.onClick.RemoveListener(HandleClick);
        }

        private void HandleClick()
        {
            if (_ability == null)
                return;

            if (_ability.CurrentPhase == StrikeWingAbility.Phase.Armed)
                _ability.Disarm();
            else if (_ability.CurrentPhase == StrikeWingAbility.Phase.Ready)
                _ability.Arm();
            else
                return;

            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayUIClick();
        }

        private void Update()
        {
            if (_ability == null)
                return;

            var phase = _ability.CurrentPhase;

            // Live affordability check while Ready (salvage moves constantly).
            if (button != null)
                button.interactable =
                    phase == StrikeWingAbility.Phase.Armed ||
                    (phase == StrikeWingAbility.Phase.Ready && _ability.CanArm);

            if (cooldownFill != null)
                cooldownFill.fillAmount =
                    phase == StrikeWingAbility.Phase.Cooldown ? _ability.CooldownFraction :
                    phase == StrikeWingAbility.Phase.Telegraph ? 1f : 0f;

            // Label: rebuild only on phase change or a new countdown second, so
            // steady state allocates nothing.
            if (phase == StrikeWingAbility.Phase.Cooldown)
            {
                int secs = Mathf.CeilToInt(_ability.CooldownRemaining);
                if (phase != _shownPhase || secs != _shownSeconds)
                {
                    _shownSeconds = secs;
                    if (label != null)
                    {
                        label.text = $"REARM {secs}";
                        label.color = Color.white;
                    }
                }
            }
            else if (phase != _shownPhase && label != null)
            {
                switch (phase)
                {
                    case StrikeWingAbility.Phase.Armed:
                        label.text = "TAP TARGET";
                        label.color = new Color(1f, 0.8f, 0.3f, 1f);
                        break;
                    case StrikeWingAbility.Phase.Telegraph:
                        label.text = "INBOUND";
                        label.color = new Color(1f, 0.8f, 0.3f, 1f);
                        break;
                    default:
                        label.text = _readyLabel;
                        label.color = Color.white;
                        break;
                }
            }

            _shownPhase = phase;
        }
    }
}
