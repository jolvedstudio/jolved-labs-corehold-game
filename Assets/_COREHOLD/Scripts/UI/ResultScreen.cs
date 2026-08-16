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

            if (titleLabel != null)
            {
                titleLabel.text = victory ? "VICTORY" : "CORE LOST";
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

            if (scoreLabel != null)
            {
                if (_inCampaign)
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
                    label.text = _inCampaign ? (victory ? "CONTINUE" : "ABANDON") : "MAIN MENU";
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
                // The relabeled second button: Continue after a win, Abandon after
                // a loss (see Show).
                if (_lastVictory) CampaignManager.Instance.AdvanceToNextStage();
                else CampaignManager.Instance.AbandonToWelcome();
                return;
            }

            // Single-scene build: there is no separate title scene, so "menu" is a
            // reload of THIS level, which comes back up on its own title overlay.
            GameFlow.RestartCurrentLevel();
        }
    }
}
