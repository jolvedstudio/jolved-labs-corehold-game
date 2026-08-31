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
        [SerializeField] private Button almanacButton;
        [SerializeField] private Button howToPlayButton;

        [Tooltip("The scene's Settings panel. Pause is the ONLY place settings can be reached once a " +
                 "run has started — before this, the panel existed in every level scene but the title " +
                 "overlay was the sole door to it, so a player who wanted the volume down mid-run had " +
                 "to abandon the level to find it.")]
        [SerializeField] private SettingsPanel settingsPanel;
        [SerializeField] private Button settingsButton;

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
            if (almanacButton != null) almanacButton.onClick.AddListener(OpenAlmanac);
            if (howToPlayButton != null) howToPlayButton.onClick.AddListener(OpenHowToPlay);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
        }

        private void OnDisable()
        {
            if (resumeButton != null) resumeButton.onClick.RemoveListener(Resume);
            if (retryButton != null) retryButton.onClick.RemoveListener(Retry);
            if (menuButton != null) menuButton.onClick.RemoveListener(MainMenu);
            if (muteButton != null) muteButton.onClick.RemoveListener(ToggleMute);
            if (almanacButton != null) almanacButton.onClick.RemoveListener(OpenAlmanac);
            if (howToPlayButton != null) howToPlayButton.onClick.RemoveListener(OpenHowToPlay);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
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

            // Pausing set timeScale to 0; every exit below leaves this scene, so
            // restore it here rather than trusting each destination to do it.
            Time.timeScale = 1f;

            // 1. IN A CAMPAIGN the Welcome scene IS the main menu, and it is the
            //    only branch that used to be missing — the campaign flow hides
            //    the title overlay (GameFlow.ExecuteCampaignStart), so the old
            //    fallback reloaded the level and came back with no menu at all.
            //    That is the "Main Menu goes nowhere" report. Leaving KEEPS the
            //    save: stepping out mid-level is navigating, not surrendering.
            var campaign = Corehold.Core.CampaignManager.Instance;
            if (campaign != null && campaign.HasActiveCampaign)
            {
                campaign.LeaveToWelcome();
                return;
            }

            // 2. An authored title scene, when the build has one.
            if (!string.IsNullOrEmpty(titleSceneName))
            {
                Corehold.Enemies.Enemy.Live.Clear();
                SceneManager.LoadScene(titleSceneName, LoadSceneMode.Single);
                return;
            }

            // 3. Single-scene build: reload, which comes back on its own title
            //    overlay (Retry ignores all of this — it always reloads).
            Corehold.Core.GameFlow.RestartCurrentLevel();
        }

        private void OpenHowToPlay()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            var canvas = root != null ? root.GetComponentInParent<Canvas>() : GetComponentInParent<Canvas>();
            if (canvas != null)
                HowToPlayScreen.Toggle(canvas.rootCanvas.transform);
        }

        /// <summary>
        /// Open Settings over the pause menu. The panel is the SAME instance the
        /// title overlay uses — one panel per scene, so volume changed here and
        /// volume changed at the title are the same control, not two that can
        /// disagree.
        ///
        /// Pause is left standing underneath rather than hidden: closing
        /// Settings should return the player to the menu they opened it from,
        /// and the run stays paused throughout.
        /// </summary>
        private void OpenSettings()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (settingsPanel != null)
                settingsPanel.Show();
        }

        private void OpenAlmanac()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            var canvas = root != null ? root.GetComponentInParent<Canvas>() : GetComponentInParent<Canvas>();
            if (canvas != null)
                AlmanacScreen.Toggle(canvas.rootCanvas.transform);
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
