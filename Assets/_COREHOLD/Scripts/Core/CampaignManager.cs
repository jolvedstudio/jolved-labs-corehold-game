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
            public int entrySalvage = -1;
            public int entryIntegrity = -1;
            public List<LevelResult> results = new List<LevelResult>();
        }

        public List<LevelResult> Results { get; } = new List<LevelResult>();

        /// <summary>Gameplay seconds across the run's completed levels.</summary>
        public float ElapsedSeconds { get; private set; }

        /// <summary>Set at campaign completion, for the Closing screen's badges.</summary>
        public bool CompletedNewBestScore { get; private set; }
        public bool CompletedNewBestTime { get; private set; }

        /// <summary>
        /// The current stage's ENTRY snapshot (plan v2 §A.6) — what the player
        /// walks in with. -1 = the difficulty's own defaults (reset economy).
        /// Computed ONCE when the stage is entered from a Victory (heal and
        /// carry applied there), then immutable: Retry re-applies exactly this,
        /// so integrity heal cannot be farmed by deliberate retries.
        /// </summary>
        public int CurrentEntrySalvage { get; private set; } = -1;
        public int CurrentEntryIntegrity { get; private set; } = -1;

        // End-of-level state captured at Victory, before the scene dies.
        private int _endSalvage = -1;
        private int _endIntegrity = -1;

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

            Active = manifest;
            ChosenDifficulty = difficulty;
            Results.Clear();
            ElapsedSeconds = 0f;
            CompletedNewBestScore = false;
            CompletedNewBestTime = false;
            _endSalvage = _endIntegrity = -1;

            // First level: nothing to carry yet. The base floor still applies
            // (a campaign that grants 400/level grants it on level 1 too).
            var rules = manifest.progression;
            CurrentEntrySalvage = rules.baseSalvagePerLevel > 0 ? rules.baseSalvagePerLevel : -1;
            CurrentEntryIntegrity = -1;

            SaveData.ClearCampaignRun(manifest.campaignId); // a fresh run replaces any saved one
            LoadStage(first);
        }

        /// <summary>
        /// Begin a campaign in the scene ALREADY OPEN, which must be its first
        /// level.
        ///
        /// This is what lets the campaign's entry screen be an overlay on level
        /// one rather than a scene of its own: the player is already standing in
        /// the field they are about to defend, so starting must not reload it.
        /// Reloading would work, but it would throw away a scene Unity has just
        /// finished streaming and cost the player a black frame for nothing.
        ///
        /// Everything else matches <see cref="StartCampaign"/> exactly — the two
        /// differ only in whether a scene load happens.
        /// </summary>
        public void StartCampaignInPlace(CampaignManifest manifest, Difficulty difficulty)
        {
            if (manifest == null)
            {
                Debug.LogError("[Campaign] StartCampaignInPlace called with no manifest.");
                return;
            }

            int first = manifest.FirstLevelIndex();
            if (first < 0)
            {
                Debug.LogError($"[Campaign] Manifest '{manifest.name}' has no Level stages.");
                return;
            }

            Active = manifest;
            ChosenDifficulty = difficulty;
            Results.Clear();
            ElapsedSeconds = 0f;
            CompletedNewBestScore = false;
            CompletedNewBestTime = false;
            _endSalvage = _endIntegrity = -1;

            var rules = manifest.progression;
            CurrentEntrySalvage = rules.baseSalvagePerLevel > 0 ? rules.baseSalvagePerLevel : -1;
            CurrentEntryIntegrity = -1;

            SaveData.ClearCampaignRun(manifest.campaignId);

            // Adopt the open scene as the current stage instead of loading it.
            CurrentStageIndex = first;
            ApplyStageToOpenScene();
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
            CurrentEntrySalvage = run.entrySalvage;
            CurrentEntryIntegrity = run.entryIntegrity;
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

        /// <summary>True when the level now playing is the LAST one — there is no
        /// next level, so winning it finishes the campaign. Read by the result
        /// screen, which becomes the debrief rather than handing off to a
        /// separate scene for it.</summary>
        public bool IsFinalStage =>
            HasActiveCampaign && CurrentStageIndex >= 0 && Active.NextLevelIndex(CurrentStageIndex) < 0;

        /// <summary>
        /// Bank the campaign's records and retire its saved run.
        ///
        /// Split out of <see cref="AdvanceToNextStage"/> so the RESULT SCREEN can
        /// finish a campaign in place. Idempotent by way of the run blob: once
        /// cleared, a second call re-submits the same records, and both submit
        /// calls keep the better value.
        /// </summary>
        public void CompleteCampaign()
        {
            if (!HasActiveCampaign) return;
            CompletedNewBestScore = SaveData.SubmitCampaignBestScore(Active.campaignId, CumulativeScore);
            CompletedNewBestTime = SaveData.SubmitCampaignBestTime(
                Active.campaignId, Mathf.Max(1, Mathf.RoundToInt(ElapsedSeconds)));
            SaveData.ClearCampaignRun(Active.campaignId);
        }

        /// <summary>Victory → next level, or the campaign ends.</summary>
        public void AdvanceToNextStage()
        {
            if (!HasActiveCampaign) return;

            int next = Active.NextLevelIndex(CurrentStageIndex);
            if (next >= 0)
            {
                ComputeNextEntry();
                LoadStage(next);
                return;
            }

            // No next level: the campaign is COMPLETE.
            CompleteCampaign();

            // A manifest MAY still carry a Closing scene — older campaigns do,
            // and one is honoured when present. It is no longer required: the
            // result screen shows the debrief over the field the player just
            // held, which is both one less scene to build and one less place
            // for the game to stop looking like itself.
            var closing = Active.StageOfKind(CampaignStageKind.Closing);
            if (closing != null && !string.IsNullOrEmpty(closing.scenePath))
            {
                CurrentStageIndex = Active.stages.IndexOf(closing);
                GameFlow.LoadSceneClean(closing.scenePath);
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
        /// <summary>
        /// Give up the run: the save is CLEARED, so Welcome offers no CONTINUE.
        /// This is the result screen's explicit ABANDON after a defeat.
        /// </summary>
        public void AbandonToWelcome() => ExitToWelcome(clearRun: true);

        /// <summary>
        /// Leave to Welcome KEEPING the save, so CONTINUE RUN still resumes from
        /// the last completed stage. This is pause → Main Menu: a player stepping
        /// out mid-level is navigating, not surrendering, and silently destroying
        /// their campaign for it would be indefensible.
        /// </summary>
        public void LeaveToWelcome() => ExitToWelcome(clearRun: false);

        private void ExitToWelcome(bool clearRun)
        {
            if (clearRun && HasActiveCampaign)
                SaveData.ClearCampaignRun(Active.campaignId);

            var welcome = HasActiveCampaign ? Active.StageOfKind(CampaignStageKind.Welcome) : null;
            // Where "the menu" is, when there is no Welcome scene: the campaign's
            // FIRST LEVEL, whose title overlay is the entry screen. Captured
            // before Active is cleared.
            string firstLevel = null;
            if (HasActiveCampaign)
            {
                int first = Active.FirstLevelIndex();
                if (first >= 0) firstLevel = Active.stages[first].scenePath;
            }

            Active = null;
            CurrentStageIndex = -1;

            if (welcome != null && !string.IsNullOrEmpty(welcome.scenePath))
                GameFlow.LoadSceneClean(welcome.scenePath);
            else if (!string.IsNullOrEmpty(firstLevel))
                GameFlow.LoadSceneClean(firstLevel);
            else
                GameFlow.RestartCurrentLevel(); // nothing known: fall back to the old behavior
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
            {
                ElapsedSeconds += GameManager.Instance.RunSeconds;

                // Capture the end state NOW — the scene (and its GameManager)
                // dies on advance, and the carry rules need these (§A.6).
                if (victory)
                {
                    _endSalvage = GameManager.Instance.Salvage;
                    _endIntegrity = GameManager.Instance.Integrity;
                }
            }
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

        /// <summary>
        /// The carry rules (plan v2 §A.6), applied once per Victory→next-level
        /// transition to produce the next stage's immutable entry snapshot:
        ///
        ///   • ResetPerLevel — entry is the difficulty's own defaults (plus the
        ///     base floor when one is set): what the generation gates certified.
        ///   • CarryFraction/CarryFull — carried = keep × end-of-level salvage;
        ///     entry = max(base floor, carried). The floor is a GUARANTEE, not a
        ///     bonus — spending everything before the win cannot brick the next
        ///     level, and banking a fortune is capped only by play.
        ///   • carryIntegrity — entry integrity = min(tier max, end + heal),
        ///     heal applied HERE (once), never on Retry.
        /// </summary>
        private void ComputeNextEntry()
        {
            var rules = Active.progression;

            switch (rules.economyCarry)
            {
                case ProgressionRules.EconomyCarry.CarryFull:
                case ProgressionRules.EconomyCarry.CarryFraction:
                    float keep = rules.economyCarry == ProgressionRules.EconomyCarry.CarryFull
                        ? 1f
                        : Mathf.Clamp01(rules.salvageKeepFraction);
                    int carried = _endSalvage > 0 ? Mathf.RoundToInt(_endSalvage * keep) : 0;
                    int floor = rules.baseSalvagePerLevel;
                    CurrentEntrySalvage = Mathf.Max(floor, carried);
                    if (CurrentEntrySalvage <= 0)
                    {
                        // Nothing carried and no floor authored: falling back to
                        // the tier default beats loading an unplayable level.
                        Debug.LogWarning("[Campaign] Carry produced 0 entry salvage and no base floor is set — " +
                                         "using the difficulty default. Author baseSalvagePerLevel on the campaign.");
                        CurrentEntrySalvage = -1;
                    }
                    break;

                default: // ResetPerLevel
                    CurrentEntrySalvage = rules.baseSalvagePerLevel > 0 ? rules.baseSalvagePerLevel : -1;
                    break;
            }

            if (rules.carryIntegrity && _endIntegrity > 0)
            {
                int cap = GameManager.StartingIntegrityFor(ChosenDifficulty);
                CurrentEntryIntegrity = Mathf.Min(cap, _endIntegrity + Mathf.Max(0, rules.integrityHealPerLevel));
            }
            else
            {
                CurrentEntryIntegrity = -1;
            }
        }

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
                entrySalvage = CurrentEntrySalvage,
                entryIntegrity = CurrentEntryIntegrity,
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

            // The stage's entry snapshot: -1 sentinels mean "the difficulty's
            // own defaults" (reset economy — what the gates certified); carry
            // modes put real values here via ComputeNextEntry, and Retry sees
            // the same snapshot because nothing recomputes it on reload.
            flow.BeginCampaignRun(ChosenDifficulty, CurrentEntrySalvage, CurrentEntryIntegrity);
        }

        /// <summary>
        /// The takeover, for a stage whose scene is ALREADY LOADED.
        ///
        /// The same two steps <see cref="LoadStage"/> reaches through a scene
        /// load — persist the run, hand GameFlow the entry snapshot — done
        /// directly, because sceneLoaded will not fire for a scene that is
        /// already open.
        /// </summary>
        private void ApplyStageToOpenScene()
        {
            SaveRun();

            var flow = FindFirstObjectByType<GameFlow>();
            if (flow == null)
            {
                Debug.LogError("[Campaign] The open scene has no GameFlow — cannot start the run in place.");
                return;
            }
            flow.BeginCampaignRun(ChosenDifficulty, CurrentEntrySalvage, CurrentEntryIntegrity);
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
