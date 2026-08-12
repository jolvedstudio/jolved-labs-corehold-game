using UnityEngine;

namespace Corehold.UI
{
    /// <summary>
    /// Full-screen overlay shown when the viewport is in portrait orientation
    /// (GDD §5.1: "Landscape only. A portrait mobile browser gets a full-screen
    /// rotate prompt; there is no portrait layout.").
    ///
    /// Watches the screen aspect and toggles a child panel on when the screen is
    /// taller than it is wide (with a small hysteresis band so it doesn't flicker
    /// at exactly square). Lives on its own high-sorting-order Canvas so it draws
    /// over every other UI. Also pauses the game (Time.timeScale = 0) while the
    /// prompt is up, and restores the previous speed when rotated back.
    /// </summary>
    [DisallowMultipleComponent]
    public class RotateDeviceOverlay : MonoBehaviour
    {
        [Tooltip("The panel to show/hide (the dark full-screen prompt).")]
        [SerializeField] private GameObject panel;

        [Tooltip("Portrait if aspect (w/h) falls below this. ~0.95 gives a small dead-band around square.")]
        [SerializeField] private float portraitAspectThreshold = 0.95f;

        [Tooltip("Pause the game (timeScale 0) while the rotate prompt is shown.")]
        [SerializeField] private bool pauseWhilePortrait = true;

        private bool _isPortrait;
        private float _savedTimeScale = 1f;

        private void Awake()
        {
            if (panel == null && transform.childCount > 0)
                panel = transform.GetChild(0).gameObject;
        }

        private void OnEnable()
        {
            _isPortrait = false;
            Evaluate(force: true);
        }

        private void Update()
        {
            Evaluate(force: false);
        }

        private void Evaluate(bool force)
        {
            bool portrait = Screen.height > 0 &&
                            (float)Screen.width / Screen.height < portraitAspectThreshold;

            if (!force && portrait == _isPortrait)
                return;

            _isPortrait = portrait;

            if (panel != null)
                panel.SetActive(portrait);

            if (pauseWhilePortrait)
            {
                if (portrait)
                {
                    // Only capture if we're not already paused by the prompt.
                    if (Time.timeScale > 0f)
                        _savedTimeScale = Time.timeScale;
                    Time.timeScale = 0f;
                }
                else
                {
                    Time.timeScale = _savedTimeScale <= 0f ? 1f : _savedTimeScale;
                }
            }
        }
    }
}
