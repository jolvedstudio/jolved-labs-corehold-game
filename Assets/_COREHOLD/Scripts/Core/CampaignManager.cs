using System.Collections.Generic;
using Corehold.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Corehold.Core
{
    /// <summary>
    /// The campaign sequencer (plan v2 §A.4) — the project's first and only
    /// <c>DontDestroyOnLoad</c> object. Everything else in a scene dies with
    /// the scene; this survives to carry the run from Welcome through the
    /// levels to Closing.
    ///
    /// It deliberately owns NO per-scene wiring. Level scenes stay
    /// campaign-agnostic: when a scene loads and a campaign is active, the
    /// manager finds that scene's <see cref="GameFlow"/> and hands it the
    /// chosen difficulty via <see cref="GameFlow.BeginCampaignRun"/> — the
    /// scene's own flow then skips its title screen and starts the run. With
    /// no campaign active the manager does nothing and every scene behaves
    /// exactly as it always has (single-map play untouched).
    ///
    /// A0 scope: reset economy per level (the one mode the generation gates
    /// certify truthfully — see <see cref="ProgressionRules"/>), results kept
    /// in memory for the Closing screen. Run persistence to PlayerPrefs and
    /// the carry models are A1/A2 (plan v2 §A.6–A.7).
    /// </summary>
    public class CampaignManager : MonoBehaviour
    {
        public static CampaignManager Instance { get; private set; }

        public CampaignManifest Active { get; private set; }
        public Difficulty ChosenDifficulty { get; private set; } = Difficulty.Normal;
        public int CurrentStageIndex { get; private set; } = -1;

        public bool HasActiveCampaign => Active != null;

        /// <summary>Per-level results of the current run, for the Closing screen.</summary>
        public class LevelResult
        {
            public string title;
            public int stars;
            public int score;
            public bool victory;
        }

        public List<LevelResult> Results { get; } = new List<LevelResult>();

        public int CumulativeScore
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < Results.Count; i++)
                    if (Results[i] != null && Results[i].victory) sum += Results[i].score;
                return sum;
            }
        }

        /// <summary>Find or create the manager. Call from the Welcome screen.</summary>
        public static CampaignManager EnsureExists()
        {
            if (Instance == null)
            {
                var go = new GameObject("CampaignManager");
                go.AddComponent<CampaignManager>(); // Awake sets Instance
            }
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                SceneManager.sceneLoaded -= HandleSceneLoaded;
            }
        }

        // ------------------------------------------------------------ control

        public void StartCampaign(CampaignManifest manifest, Difficulty difficulty)
        {
            if (manifest == null)
            {
                Debug.LogError("[Campaign] StartCampaign called with no manifest.");
                return;
            }

            int first = manifest.FirstLevelIndex();
            if (first < 0)
            {
                Debug.LogError($"[Campaign] Manifest '{manifest.name}' has no Level stages.");
                return;
            }

            if (manifest.progression.economyCarry != ProgressionRules.EconomyCarry.ResetPerLevel)
                Debug.LogWarning("[Campaign] Carry modes are not implemented yet (they need the balance model's " +
                                 "--starting-salvage extension); falling back to ResetPerLevel.");

            Active = manifest;
            ChosenDifficulty = difficulty;
            Results.Clear();
            LoadStage(first);
        }

        /// <summary>Victory → next level, or Closing after the last one.</summary>
        public void AdvanceToNextStage()
        {
            if (!HasActiveCampaign) return;

            int next = Active.NextLevelIndex(CurrentStageIndex);
            if (next >= 0)
            {
                LoadStage(next);
                return;
            }

            var closing = Active.StageOfKind(CampaignStageKind.Closing);
            if (closing != null)
            {
                CurrentStageIndex = Active.stages.IndexOf(closing);
                GameFlow.LoadSceneClean(closing.scenePath);
            }
            else
            {
                Debug.LogWarning("[Campaign] No Closing stage in the manifest — returning to Welcome.");
                AbandonToWelcome();
            }
        }

        /// <summary>Defeat → Retry. Reloads the current level; with reset economy the
        /// re-entry state IS the entry state, so nothing needs re-applying (the entry
        /// snapshot becomes real state when carry modes land — plan v2 §A.6).</summary>
        public void RetryCurrentStage()
        {
            if (!HasActiveCampaign || CurrentStageIndex < 0) return;
            GameFlow.LoadSceneClean(Active.stages[CurrentStageIndex].scenePath);
        }

        /// <summary>Leave the campaign and return to the Welcome scene.</summary>
        public void AbandonToWelcome()
        {
            var welcome = HasActiveCampaign ? Active.StageOfKind(CampaignStageKind.Welcome) : null;
            Active = null;
            CurrentStageIndex = -1;

            if (welcome != null && !string.IsNullOrEmpty(welcome.scenePath))
                GameFlow.LoadSceneClean(welcome.scenePath);
            else
                GameFlow.RestartCurrentLevel(); // no Welcome known: fall back to the old behavior
        }

        /// <summary>Called by ResultScreen when a campaign level ends (plan v2 §A.6).</summary>
        public void ReportLevelResult(bool victory, int stars, int score)
        {
            if (!HasActiveCampaign || CurrentStageIndex < 0) return;

            int levelNumber = Active.LevelNumberOf(CurrentStageIndex); // 1-based
            while (Results.Count < levelNumber)
                Results.Add(null);

            Results[levelNumber - 1] = new LevelResult
            {
                title = Active.stages[CurrentStageIndex].title,
                stars = stars,
                score = score,
                victory = victory,
            };
        }

        // ------------------------------------------------------- level display

        public int CurrentLevelNumber => HasActiveCampaign ? Active.LevelNumberOf(CurrentStageIndex) : 0;
        public int LevelCount => HasActiveCampaign ? Active.LevelCount : 0;

        public string CurrentBriefing =>
            HasActiveCampaign && CurrentStageIndex >= 0 ? Active.stages[CurrentStageIndex].briefing : null;

        // ------------------------------------------------------------ internal

        private void LoadStage(int index)
        {
            CurrentStageIndex = index;
            GameFlow.LoadSceneClean(Active.stages[index].scenePath);
        }

        /// <summary>
        /// The takeover (plan v2 §A.6). sceneLoaded fires after the scene's
        /// Awake/OnEnable but before any Start, so handing GameFlow the campaign
        /// start here always lands before its title-screen path runs.
        /// </summary>
        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!HasActiveCampaign || CurrentStageIndex < 0) return;

            var stage = Active.stages[CurrentStageIndex];
            if (stage.kind != CampaignStageKind.Level) return;
            if (!SameScene(scene, stage.scenePath)) return;

            var flow = FindFirstObjectByType<GameFlow>();
            if (flow == null)
            {
                Debug.LogError($"[Campaign] Level scene '{scene.name}' has no GameFlow — cannot start the run.");
                return;
            }

            // Reset economy: -1 sentinels mean "the difficulty's own defaults",
            // which is exactly what the generation gates certified for this map.
            flow.BeginCampaignRun(ChosenDifficulty, -1, -1);
        }

        private static bool SameScene(Scene scene, string manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath)) return false;
            if (!string.IsNullOrEmpty(scene.path) && scene.path == manifestPath) return true;

            // Fallback by name, so a manifest written with forward slashes on
            // another OS still matches.
            string file = manifestPath;
            int slash = file.LastIndexOf('/');
            if (slash >= 0) file = file.Substring(slash + 1);
            if (file.EndsWith(".unity")) file = file.Substring(0, file.Length - 6);
            return scene.name == file;
        }
    }
}
