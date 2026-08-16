using System;
using System.Collections.Generic;
using Corehold.Data;
using Corehold.Systems;
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
        [Serializable]
        public class LevelResult
        {
            public string title;
            public int stars;
            public int score;
            public bool victory;
        }

        /// <summary>The run blob persisted to PlayerPrefs at level boundaries
        /// (plan v2 §A.7) — on WebGL a tab refresh destroys this manager, and a
        /// 10-level campaign WILL meet one. JSON via JsonUtility.</summary>
        [Serializable]
        private class SavedRun
        {
            public string campaignId;
            public int difficulty;
            public int stageIndex;
            public float elapsedSeconds;
            public List<LevelResult> results = new List<LevelResult>();
        }

        public List<LevelResult> Results { get; } = new List<LevelResult>();

        /// <summary>Gameplay seconds across the run's completed levels.</summary>
        public float ElapsedSeconds { get; private set; }

        /// <summary>Set at campaign completion, for the Closing screen's badges.</summary>
        public bool CompletedNewBestScore { get; private set; }
        public bool CompletedNewBestTime { get; private set; }

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
            ElapsedSeconds = 0f;
            CompletedNewBestScore = false;
            CompletedNewBestTime = false;
            SaveData.ClearCampaignRun(manifest.campaignId); // a fresh run replaces any saved one
            LoadStage(first);
        }

        /// <summary>Is there a persisted, still-valid run to resume for this manifest?</summary>
        public static bool HasSavedRun(CampaignManifest manifest)
        {
            if (manifest == null) return false;
            string json = SaveData.GetCampaignRun(manifest.campaignId);
            if (string.IsNullOrEmpty(json)) return false;
            var run = JsonUtility.FromJson<SavedRun>(json);
            return RunIsValidFor(run, manifest);
        }

        /// <summary>
        /// Resume the persisted run: same difficulty, same stage, past results
        /// restored. With reset economy the stage re-entry state IS the entry
        /// state, so resuming and retrying are the same load.
        /// </summary>
        public bool TryResumeCampaign(CampaignManifest manifest)
        {
            if (manifest == null) return false;
            string json = SaveData.GetCampaignRun(manifest.campaignId);
            if (string.IsNullOrEmpty(json)) return false;

            var run = JsonUtility.FromJson<SavedRun>(json);
            if (!RunIsValidFor(run, manifest))
            {
                // The campaign changed shape since the run was saved (stages
                // regenerated/reordered) — a stale resume would load the wrong
                // level. Discard rather than guess.
                Debug.LogWarning("[Campaign] Saved run no longer matches the manifest — discarding it.");
                SaveData.ClearCampaignRun(manifest.campaignId);
                return false;
            }

            Active = manifest;
            ChosenDifficulty = (Difficulty)run.difficulty;
            Results.Clear();
            if (run.results != null) Results.AddRange(run.results);
            ElapsedSeconds = run.elapsedSeconds;
            CompletedNewBestScore = false;
            CompletedNewBestTime = false;
            LoadStage(run.stageIndex);
            return true;
        }

        private static bool RunIsValidFor(SavedRun run, CampaignManifest manifest)
        {
            return run != null
                && run.campaignId == manifest.campaignId
                && run.stageIndex >= 0
                && run.stageIndex < manifest.stages.Count
                && manifest.stages[run.stageIndex].kind == CampaignStageKind.Level;
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

            // No next level: the campaign is COMPLETE. Submit the campaign
            // records, clear the run blob (it is no longer resumable), and keep
            // the in-memory results for the Closing screen to display.
            CompletedNewBestScore = SaveData.SubmitCampaignBestScore(Active.campaignId, CumulativeScore);
            CompletedNewBestTime = SaveData.SubmitCampaignBestTime(
                Active.campaignId, Mathf.Max(1, Mathf.RoundToInt(ElapsedSeconds)));
            SaveData.ClearCampaignRun(Active.campaignId);

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

        /// <summary>Leave the campaign and return to the Welcome scene. Abandoning
        /// forfeits the run — the saved blob goes with it (plan v2 §A.7).</summary>
        public void AbandonToWelcome()
        {
            if (HasActiveCampaign)
                SaveData.ClearCampaignRun(Active.campaignId);

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

            // The level's gameplay time joins the campaign clock; the per-stage
            // star record persists on wins (keyed by level NUMBER — definition
            // names embed seeds and would orphan records on regeneration).
            if (GameManager.Instance != null)
                ElapsedSeconds += GameManager.Instance.RunSeconds;
            if (victory && stars > 0)
                SaveData.SubmitCampaignStageStars(Active.campaignId, levelNumber, stars);

            SaveRun();
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
            SaveRun();
            GameFlow.LoadSceneClean(Active.stages[index].scenePath);
        }

        /// <summary>Persist the run at a level boundary (plan v2 §A.7).</summary>
        private void SaveRun()
        {
            if (!HasActiveCampaign || CurrentStageIndex < 0) return;
            if (Active.stages[CurrentStageIndex].kind != CampaignStageKind.Level) return;

            var run = new SavedRun
            {
                campaignId = Active.campaignId,
                difficulty = (int)ChosenDifficulty,
                stageIndex = CurrentStageIndex,
                elapsedSeconds = ElapsedSeconds,
            };
            run.results.AddRange(Results);
            SaveData.SaveCampaignRun(Active.campaignId, JsonUtility.ToJson(run));
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
