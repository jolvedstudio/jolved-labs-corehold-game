using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Pause overlay (GDD §9.1). Four elements at most: Resume, Retry, Main Menu,
    /// mute toggle. Freezes the game with <see cref="Time.timeScale"/> = 0 and
    /// restores it on resume (the previous scale is remembered so a paused 2× run
    /// resumes at 2×).
    /// </summary>
    [DisallowMultipleComponent]
    public class PauseScreen : MonoBehaviour
    {
        [SerializeField] private GameObject root;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button retryButton;
        [SerializeField] private Button menuButton;
        [SerializeField] private Button muteButton;
        [SerializeField] private TMP_Text muteLabel;

        [Tooltip("Separate title scene, if the build has one. EMPTY (the single-scene build) sends Main Menu " +
                 "back through this level's own title overlay. Retry ignores this — it always reloads the " +
                 "scene being played, which is the only correct answer once levels are generated.")]
        [SerializeField] private string titleSceneName = "";

        private float _prevTimeScale = 1f;

        private void Awake()
        {
            Hide();
        }

        private void OnEnable()
        {
            if (resumeButton != null) resumeButton.onClick.AddListener(Resume);
            if (retryButton != null) retryButton.onClick.AddListener(Retry);
            if (menuButton != null) menuButton.onClick.AddListener(MainMenu);
            if (muteButton != null) muteButton.onClick.AddListener(ToggleMute);
        }

        private void OnDisable()
        {
            if (resumeButton != null) resumeButton.onClick.RemoveListener(Resume);
            if (retryButton != null) retryButton.onClick.RemoveListener(Retry);
            if (menuButton != null) menuButton.onClick.RemoveListener(MainMenu);
            if (muteButton != null) muteButton.onClick.RemoveListener(ToggleMute);
        }

        public void Show()
        {
            // End any running time dip first (R3) so we capture the TRUE game
            // speed — otherwise resume would inherit the dipped 0.3×.
            if (Corehold.Core.GameManager.Instance != null)
                Corehold.Core.GameManager.Instance.CancelTimeDip();

            _prevTimeScale = Time.timeScale > 0.01f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            if (root != null) root.SetActive(true);
            RefreshMute();
        }

        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        private void Resume()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Time.timeScale = _prevTimeScale;
            Hide();
        }

        private void Retry()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Corehold.Core.GameFlow.RestartCurrentLevel();
        }

        private void MainMenu()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();

            // A title scene is a genuinely DIFFERENT scene, so a name is the only
            // way to say which — unlike Retry, where the answer is always "this
            // one". Unset (the single-scene build) reloads this level, which comes
            // back up on its own title overlay.
            if (string.IsNullOrEmpty(titleSceneName))
            {
                Corehold.Core.GameFlow.RestartCurrentLevel();
                return;
            }

            Corehold.Enemies.Enemy.Live.Clear();
            Time.timeScale = 1f;
            SceneManager.LoadScene(titleSceneName, LoadSceneMode.Single);
        }

        private void ToggleMute()
        {
            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = !AudioDirector.Instance.Muted;
                SaveData.Muted = AudioDirector.Instance.Muted;
            }
            RefreshMute();
        }

        private void RefreshMute()
        {
            bool muted = AudioDirector.Instance != null ? AudioDirector.Instance.Muted : SaveData.Muted;
            if (muteLabel != null) muteLabel.text = muted ? "SOUND: OFF" : "SOUND: ON";
        }
    }
}
