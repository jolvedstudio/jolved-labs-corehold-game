using System;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Title screen and the audio gate (GDD §9.1). Shows the logo, three difficulty
    /// buttons with locked states, the best score from <see cref="SaveData"/> and a
    /// mute toggle.
    ///
    /// It is the <b>audio gate</b>: browsers refuse audio without a user gesture, so
    /// NOTHING plays until a difficulty is tapped. Tapping a difficulty is the
    /// gesture — it calls <see cref="AudioDirector.StartMusic"/> and then raises
    /// <see cref="OnPlay"/> with the chosen tier for the game flow to begin the run.
    /// There is deliberately no auto-playing menu track (it fails silently on the
    /// web, GDD §9.1).
    /// </summary>
    [DisallowMultipleComponent]
    public class TitleScreen : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] private GameObject root;
        [SerializeField] private TMP_Text bestScoreLabel;

        [Header("Difficulty buttons")]
        [SerializeField] private Button normalButton;
        [SerializeField] private Button veteranButton;
        [SerializeField] private Button nightmareButton;
        [SerializeField] private TMP_Text normalBest;
        [SerializeField] private TMP_Text veteranBest;
        [SerializeField] private TMP_Text nightmareBest;

        [Header("Mute")]
        [SerializeField] private Button muteButton;
        [SerializeField] private TMP_Text muteLabel;

        [Header("Settings")]
        [SerializeField] private Button settingsButton;
        [SerializeField] private SettingsPanel settingsPanel;
        [SerializeField] private Button helpButton;

        [Header("Locked-state icons (optional)")]
        [SerializeField] private GameObject veteranLock;
        [SerializeField] private GameObject nightmareLock;

        [Header("Campaign entry")]
        [Tooltip("The campaign this level belongs to. Set, this overlay IS the campaign's front " +
                 "door: the difficulty buttons start the campaign in place — no separate Welcome " +
                 "scene, and no reload, because the player is already standing in level one. Left " +
                 "empty the overlay behaves exactly as it always has, starting a single-map run.")]
        [SerializeField] private CampaignManifest campaign;

        [Tooltip("Shown only when a saved run exists for the campaign. Resumes from the last " +
                 "completed level instead of restarting it.")]
        [SerializeField] private Button continueButton;

        [Tooltip("Optional: displays the campaign's name over the difficulty buttons.")]
        [SerializeField] private TMP_Text campaignLabel;

        /// <summary>Raised when a difficulty is chosen (the audio-gating gesture).</summary>
        public event Action<Difficulty> OnPlay;

        private void Awake()
        {
            // Sync the persisted mute preference into the director.
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.Muted = SaveData.Muted;
        }

        private void OnEnable()
        {
            if (normalButton != null) normalButton.onClick.AddListener(() => Play(Difficulty.Normal));
            if (veteranButton != null) veteranButton.onClick.AddListener(() => Play(Difficulty.Veteran));
            if (nightmareButton != null) nightmareButton.onClick.AddListener(() => Play(Difficulty.Nightmare));
            if (muteButton != null) muteButton.onClick.AddListener(ToggleMute);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
            if (continueButton != null) continueButton.onClick.AddListener(ContinueRun);
            if (helpButton != null) helpButton.onClick.AddListener(OpenHelp);
            Refresh();
        }

        private void OnDisable()
        {
            if (normalButton != null) normalButton.onClick.RemoveAllListeners();
            if (veteranButton != null) veteranButton.onClick.RemoveAllListeners();
            if (nightmareButton != null) nightmareButton.onClick.RemoveAllListeners();
            if (muteButton != null) muteButton.onClick.RemoveListener(ToggleMute);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
            if (continueButton != null) continueButton.onClick.RemoveListener(ContinueRun);
            if (helpButton != null) helpButton.onClick.RemoveListener(OpenHelp);
        }

        private void OpenHelp()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null && root != null) canvas = root.GetComponentInParent<Canvas>();
            if (canvas != null)
                HowToPlayScreen.Toggle(canvas.rootCanvas.transform);
        }

        private void OpenSettings()
        {
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayUIClick();
            if (settingsPanel != null)
                settingsPanel.Show();
        }

        public void Show()
        {
            if (root != null) root.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        private void Refresh()
        {
            bool vetUnlocked = SaveData.IsUnlocked(Difficulty.Veteran);
            bool nightUnlocked = SaveData.IsUnlocked(Difficulty.Nightmare);

            if (veteranButton != null) veteranButton.interactable = vetUnlocked;
            if (nightmareButton != null) nightmareButton.interactable = nightUnlocked;
            if (veteranLock != null) veteranLock.SetActive(!vetUnlocked);
            if (nightmareLock != null) nightmareLock.SetActive(!nightUnlocked);

            if (normalBest != null) normalBest.text = $"BEST {SaveData.GetBestScore(Difficulty.Normal)}";
            if (veteranBest != null) veteranBest.text = vetUnlocked ? $"BEST {SaveData.GetBestScore(Difficulty.Veteran)}" : "LOCKED";
            if (nightmareBest != null) nightmareBest.text = nightUnlocked ? $"BEST {SaveData.GetBestScore(Difficulty.Nightmare)}" : "LOCKED";

            if (bestScoreLabel != null)
            {
                int best = Mathf.Max(SaveData.GetBestScore(Difficulty.Normal),
                            Mathf.Max(SaveData.GetBestScore(Difficulty.Veteran), SaveData.GetBestScore(Difficulty.Nightmare)));
                bestScoreLabel.text = best > 0 ? $"BEST SCORE  {best}" : "";
            }

            // Campaign entry: name the campaign, and offer CONTINUE only when
            // there is something to continue. A dead button is worse than no
            // button — it promises a saved run that is not there.
            if (campaignLabel != null)
                campaignLabel.text = campaign != null ? campaign.displayName : "";

            if (continueButton != null)
                continueButton.gameObject.SetActive(
                    campaign != null && CampaignManager.HasSavedRun(campaign));

            RefreshMute();
        }

        /// <summary>Resume the saved campaign run from where it stopped.</summary>
        private void ContinueRun()
        {
            if (campaign == null)
                return;

            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = SaveData.Muted;
                AudioDirector.Instance.PlayUIClick();
                AudioDirector.Instance.StartMusic();
            }

            var mgr = CampaignManager.EnsureExists();
            if (mgr != null && mgr.TryResumeCampaign(campaign))
            {
                Hide();
                return;
            }

            // The save went stale between the button appearing and the tap
            // (a manifest edit, a wiped save). Say so by refreshing rather than
            // doing nothing, which reads as a broken button.
            Debug.LogWarning("[Title] No resumable run for this campaign — refreshing the entry screen.");
            Refresh();
        }

        private void Play(Difficulty difficulty)
        {
            if (!SaveData.IsUnlocked(difficulty))
                return;

            // The audio gate: the tap is the user gesture that unlocks Web Audio.
            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = SaveData.Muted;
                AudioDirector.Instance.PlayUIClick();
                AudioDirector.Instance.StartMusic();
            }

            Hide();

            // With a campaign wired, the difficulty buttons START THE CAMPAIGN,
            // in this scene, which must already be its first level. Without one
            // they raise OnPlay and the single-map path takes over exactly as
            // before.
            if (campaign != null)
            {
                var mgr = CampaignManager.EnsureExists();
                if (mgr != null)
                {
                    mgr.StartCampaignInPlace(campaign, difficulty);
                    return;
                }
            }

            OnPlay?.Invoke(difficulty);
        }

        private void ToggleMute()
        {
            bool muted = !(AudioDirector.Instance != null ? AudioDirector.Instance.Muted : SaveData.Muted);
            if (AudioDirector.Instance != null) AudioDirector.Instance.Muted = muted;
            SaveData.Muted = muted;
            RefreshMute();
        }

        private void RefreshMute()
        {
            bool muted = AudioDirector.Instance != null ? AudioDirector.Instance.Muted : SaveData.Muted;
            if (muteLabel != null) muteLabel.text = muted ? "♪ OFF" : "♪ ON";
        }
    }
}
