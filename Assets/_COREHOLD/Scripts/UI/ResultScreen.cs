using Corehold.Core;
using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Real uGUI Victory / Defeat screen (GDD §9.1, §9.2, §3.3). Event-driven:
    /// subscribes to <see cref="GameManager.OnStateChanged"/> and shows itself when
    /// the state flips to Victory or Defeat.
    ///
    ///   • Victory: waves survived, integrity remaining, star rating, score, any new
    ///     unlock. Star thresholds are FRACTIONAL (GDD §3.3): 3 at ≥90% of starting
    ///     integrity, 2 at ≥50%, 1 above zero.
    ///   • Defeat: wave reached and a single prominent Retry.
    ///
    /// Both have Retry and Main Menu; neither has more than four elements. The best
    /// score is submitted to <see cref="SaveData"/> and the tier marked cleared on a
    /// win, unlocking the next difficulty on the title screen.
    /// </summary>
    [DisallowMultipleComponent]
    public class ResultScreen : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private WaveManager waveManager;
        [SerializeField] private UITheme theme;

        [Header("Layout")]
        [SerializeField] private GameObject root;
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text bodyLabel;
        [SerializeField] private TMP_Text scoreLabel;
        [SerializeField] private Image[] stars = new Image[3];
        [SerializeField] private Button retryButton;
        [SerializeField] private Button menuButton;

        private GameManager _gm;

        // Campaign context captured at Show time, so the button handlers know
        // whether "menu" means Continue (victory) or Abandon (defeat). The same
        // two physical buttons serve both modes — existing generated scenes get
        // campaign support without any UI rebuild (plan v2 §A.6).
        private bool _inCampaign;

        /// <summary>This screen is the campaign's closing screen: the last level
        /// was just won. Changes the title, the body and both buttons.</summary>
        private bool _campaignComplete;
        private bool _lastVictory;

        private void Awake()
        {
            if (waveManager == null) waveManager = FindFirstObjectByType<WaveManager>();
            if (theme == null) theme = UITheme.Instance;
            if (root != null) root.SetActive(false);
        }

        private void OnEnable()
        {
            _gm = GameManager.Instance;
            if (_gm != null) _gm.OnStateChanged += HandleStateChanged;
            if (retryButton != null) retryButton.onClick.AddListener(Retry);
            if (menuButton != null) menuButton.onClick.AddListener(MainMenu);
        }

        private void OnDisable()
        {
            if (_gm != null) _gm.OnStateChanged -= HandleStateChanged;
            if (retryButton != null) retryButton.onClick.RemoveListener(Retry);
            if (menuButton != null) menuButton.onClick.RemoveListener(MainMenu);
        }

        private void Start()
        {
            if (_gm == null)
            {
                _gm = GameManager.Instance;
                if (_gm != null) _gm.OnStateChanged += HandleStateChanged;
            }
        }

        private void HandleStateChanged(GameState state)
        {
            if (state == GameState.Victory) Show(true);
            else if (state == GameState.Defeat) Show(false);
        }

        private void Show(bool victory)
        {
            if (root != null) root.SetActive(true);

            // Campaign mode changes what persistence this screen may touch: a
            // campaign level must not mark difficulty tiers cleared (that would
            // unlock Nightmare for beating campaign level 1) nor mix its per-level
            // score into the single-map bests on the title screen. Campaign
            // records get their own keys in A1 (plan v2 §A.7).
            var campaign = CampaignManager.Instance;
            _inCampaign = campaign != null && campaign.HasActiveCampaign;
            _lastVictory = victory;

            int integrity = _gm != null ? _gm.Integrity : 0;
            Difficulty diff = _gm != null ? _gm.Difficulty : Difficulty.Normal;
            int maxIntegrity = GameManager.StartingIntegrityFor(diff);
            // Carried integrity (A2): star fractions measure against what the
            // player ENTERED with, not the tier maximum — entering at 12/20
            // must still be able to earn 3 stars by protecting all 12.
            if (_inCampaign && campaign.CurrentEntryIntegrity > 0)
                maxIntegrity = campaign.CurrentEntryIntegrity;
            int waveCount = waveManager != null ? waveManager.WaveCount : 10;

            int wavesSurvived = victory
                ? waveCount
                : (waveManager != null ? Mathf.Max(0, waveManager.NextWaveIndex - 1)
                                       : (_gm != null ? Mathf.Max(0, _gm.WaveIndex) : 0));

            int salvage = _gm != null ? _gm.Salvage : 0;
            int score = SaveData.ComputeScore(wavesSurvived, integrity, salvage, diff);
            bool newBest = !_inCampaign && SaveData.SubmitScore(diff, score);

            int starCount = 0;
            if (victory)
            {
                if (!_inCampaign)
                    SaveData.MarkCleared(diff);
                float frac = maxIntegrity > 0 ? (float)integrity / maxIntegrity : 0f;
                if (frac >= 0.9f) starCount = 3;
                else if (frac >= 0.5f) starCount = 2;
                else if (integrity > 0) starCount = 1;
            }

            if (_inCampaign)
                campaign.ReportLevelResult(victory, starCount, score);

            // THE DEBRIEF. Winning the last level ends the campaign here, over
            // the field the player just held, instead of handing off to a
            // separate Closing scene. One less scene to build, and — the reason
            // that matters — one less place where the game stops looking like
            // itself at the moment it should feel best.
            _campaignComplete = _inCampaign && victory && campaign.IsFinalStage;
            if (_campaignComplete)
                campaign.CompleteCampaign();

            if (titleLabel != null)
            {
                titleLabel.text = _campaignComplete ? "CAMPAIGN COMPLETE"
                                : victory ? "VICTORY" : "CORE LOST";
                titleLabel.color = victory
                    ? (theme != null ? theme.cyan : Color.cyan)
                    : (theme != null ? theme.danger : Color.red);
            }

            // ----- Run stats + personal records (R4) -----
            // Per-map + per-difficulty bests live in SaveData (PlayerPrefs keys);
            // a stat that strictly beats its stored best gets a NEW RECORD badge.
            string map = waveManager != null ? waveManager.LevelId : "default";
            int salvageEarned = _gm != null ? _gm.RunSalvageEarned : 0;
            int longestStreak = _gm != null ? _gm.RunLongestStreak : 0;
            int runSeconds = _gm != null ? Mathf.Max(1, Mathf.RoundToInt(_gm.RunSeconds)) : 0;

            bool recWaves = !_inCampaign && SaveData.SubmitRecordMax(map, diff, "waves", wavesSurvived);
            bool recIntegrity = !_inCampaign && victory && SaveData.SubmitRecordMax(map, diff, "integrity", integrity);
            bool recSalvage = !_inCampaign && SaveData.SubmitRecordMax(map, diff, "salvage", salvageEarned);
            bool recStreak = !_inCampaign && SaveData.SubmitRecordMax(map, diff, "streak", longestStreak);
            bool recTime = !_inCampaign && victory && SaveData.SubmitRecordMin(map, diff, "time", runSeconds);

            string Badge(bool isRecord) => isRecord ? "  <color=#FF9919>NEW RECORD</color>" : "";
            string timeText = $"{runSeconds / 60}:{runSeconds % 60:00}";

            if (bodyLabel != null)
            {
                if (victory)
                {
                    bodyLabel.text =
                        $"Waves survived {wavesSurvived}/{waveCount}{Badge(recWaves)}\n" +
                        $"Integrity {integrity}/{maxIntegrity}{Badge(recIntegrity)}\n" +
                        $"Salvage earned {salvageEarned}{Badge(recSalvage)}\n" +
                        $"Longest streak ×{longestStreak}{Badge(recStreak)}\n" +
                        $"Time {timeText}{Badge(recTime)}";
                }
                else
                {
                    bodyLabel.text =
                        $"Reached wave {wavesSurvived + 1}   Difficulty {diff}\n" +
                        $"Salvage earned {salvageEarned}{Badge(recSalvage)}\n" +
                        $"Longest streak ×{longestStreak}{Badge(recStreak)}\n" +
                        $"Time {timeText}";
                }
            }

            // The campaign's own totals replace the level's stat block: at the
            // end of ten levels nobody wants this level's salvage, they want the
            // run's.
            if (_campaignComplete && bodyLabel != null)
            {
                int total = Mathf.Max(1, Mathf.RoundToInt(campaign.ElapsedSeconds));
                string runTime = $"{total / 60}:{total % 60:00}";
                bodyLabel.text =
                    $"{campaign.LevelCount} levels held   Difficulty {diff}\n" +
                    $"Campaign score {campaign.CumulativeScore}" +
                    (campaign.CompletedNewBestScore ? "  <color=#FF9919>NEW BEST</color>" : "") + "\n" +
                    $"Total time {runTime}" +
                    (campaign.CompletedNewBestTime ? "  <color=#FF9919>NEW BEST</color>" : "");
            }

            if (scoreLabel != null)
            {
                if (_campaignComplete)
                    scoreLabel.text = $"SCORE {campaign.CumulativeScore}";
                else if (_inCampaign)
                    scoreLabel.text = $"SCORE {score}   LEVEL {campaign.CurrentLevelNumber}/{campaign.LevelCount}";
                else
                    scoreLabel.text = newBest ? $"SCORE {score}  (NEW BEST)" : $"SCORE {score}   BEST {SaveData.GetBestScore(diff)}";
            }

            // Campaign relabels the second button in place: Continue on victory,
            // Abandon on defeat. Retry keeps its meaning in both modes.
            if (menuButton != null)
            {
                var label = menuButton.GetComponentInChildren<TMP_Text>();
                if (label != null)
                    label.text = _campaignComplete ? "MAIN MENU"
                               : _inCampaign ? (victory ? "CONTINUE" : "ABANDON")
                               : "MAIN MENU";
            }

            // Retry means PLAY AGAIN once the campaign is over — reloading the
            // last level would be a strange thing to offer someone who just
            // finished it.
            if (_campaignComplete && retryButton != null)
            {
                var rlabel = retryButton.GetComponentInChildren<TMP_Text>();
                if (rlabel != null) rlabel.text = "PLAY AGAIN";
            }

            // Stars.
            if (stars != null)
            {
                for (int i = 0; i < stars.Length; i++)
                {
                    if (stars[i] == null) continue;
                    stars[i].gameObject.SetActive(victory);
                    bool full = i < starCount;
                    if (theme != null)
                        stars[i].sprite = full ? theme.starFull : theme.starEmpty;
                    stars[i].color = full ? Color.white : new Color(1f, 1f, 1f, 0.35f);
                }
            }
        }

        private void Retry()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();

            // PLAY AGAIN on the debrief: run the whole campaign from level one,
            // not the level that was just finished.
            if (_campaignComplete && CampaignManager.Instance != null &&
                CampaignManager.Instance.HasActiveCampaign)
            {
                var m = CampaignManager.Instance.Active;
                var d = CampaignManager.Instance.ChosenDifficulty;
                CampaignManager.Instance.StartCampaign(m, d);
                return;
            }

            if (_inCampaign && CampaignManager.Instance != null)
                CampaignManager.Instance.RetryCurrentStage();
            else
                GameFlow.RestartCurrentLevel();
        }

        private void MainMenu()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();

            if (_inCampaign && CampaignManager.Instance != null)
            {
                // The relabeled second button: Main Menu once the campaign is
                // over, Continue after a win, Abandon after a loss (see Show).
                if (_campaignComplete) CampaignManager.Instance.AbandonToWelcome();
                else if (_lastVictory) CampaignManager.Instance.AdvanceToNextStage();
                else CampaignManager.Instance.AbandonToWelcome();
                return;
            }

            // Single-scene build: there is no separate title scene, so "menu" is a
            // reload of THIS level, which comes back up on its own title overlay.
            GameFlow.RestartCurrentLevel();
        }
    }
}
