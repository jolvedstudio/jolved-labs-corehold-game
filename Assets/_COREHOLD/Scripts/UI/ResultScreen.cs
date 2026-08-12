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
        [SerializeField] private string gameSceneName = "Game";

        [Header("Layout")]
        [SerializeField] private GameObject root;
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text bodyLabel;
        [SerializeField] private TMP_Text scoreLabel;
        [SerializeField] private Image[] stars = new Image[3];
        [SerializeField] private Button retryButton;
        [SerializeField] private Button menuButton;

        private GameManager _gm;

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
            bool newBest = SaveData.SubmitScore(diff, score);

            int starCount = 0;
            if (victory)
            {
                SaveData.MarkCleared(diff);
                float frac = maxIntegrity > 0 ? (float)integrity / maxIntegrity : 0f;
                if (frac >= 0.9f) starCount = 3;
                else if (frac >= 0.5f) starCount = 2;
                else if (integrity > 0) starCount = 1;
            }

            if (titleLabel != null)
            {
                titleLabel.text = victory ? "VICTORY" : "CORE LOST";
                titleLabel.color = victory
                    ? (theme != null ? theme.cyan : Color.cyan)
                    : (theme != null ? theme.danger : Color.red);
            }

            if (bodyLabel != null)
            {
                if (victory)
                    bodyLabel.text = $"Waves survived {wavesSurvived}/{waveCount}\nIntegrity {integrity}/{maxIntegrity}";
                else
                    bodyLabel.text = $"Reached wave {wavesSurvived + 1}\nDifficulty {diff}";
            }

            if (scoreLabel != null)
                scoreLabel.text = newBest ? $"SCORE {score}  (NEW BEST)" : $"SCORE {score}   BEST {SaveData.GetBestScore(diff)}";

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
            Corehold.Enemies.Enemy.Live.Clear();
            Time.timeScale = 1f;
            string scene = string.IsNullOrEmpty(gameSceneName) ? SceneManager.GetActiveScene().name : gameSceneName;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }

        private void MainMenu()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Corehold.Enemies.Enemy.Live.Clear();
            Time.timeScale = 1f;
            string scene = string.IsNullOrEmpty(gameSceneName) ? SceneManager.GetActiveScene().name : gameSceneName;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }
    }
}
