using Corehold.Core;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Local persistence (GDD §2.5, §9.2). The only thing this game stores is a
    /// best score per difficulty in <see cref="PlayerPrefs"/> — no accounts, no
    /// backend, no leaderboards. Also mirrors the mute toggle so the title screen
    /// remembers it between sessions.
    ///
    /// Score (GDD §9.2):
    ///   score = wavesCleared·1000 + integrityRemaining·250 + salvageUnspent + difficultyBonus
    ///   difficultyBonus = 0 (Normal), 2500 (Veteran), 6000 (Nightmare)
    /// </summary>
    public static class SaveData
    {
        private const string BestScorePrefix = "corehold.bestscore.";
        private const string MuteKey = "corehold.muted";

        /// <summary>Difficulty completion unlock: Veteran needs Normal cleared, Nightmare needs Veteran.</summary>
        private const string ClearedPrefix = "corehold.cleared.";

        /// <summary>Compute the run score (GDD §9.2).</summary>
        public static int ComputeScore(int wavesCleared, int integrityRemaining, int salvageUnspent, Difficulty difficulty)
        {
            return wavesCleared * 1000
                 + integrityRemaining * 250
                 + Mathf.Max(0, salvageUnspent)
                 + DifficultyBonus(difficulty);
        }

        /// <summary>Difficulty score bonus (GDD §9.2): 0 / 2500 / 6000.</summary>
        public static int DifficultyBonus(Difficulty difficulty)
        {
            switch (difficulty)
            {
                case Difficulty.Veteran: return 2500;
                case Difficulty.Nightmare: return 6000;
                default: return 0;
            }
        }

        /// <summary>Best score recorded for a difficulty tier (0 if none).</summary>
        public static int GetBestScore(Difficulty difficulty)
        {
            return PlayerPrefs.GetInt(BestScorePrefix + difficulty, 0);
        }

        /// <summary>Record a score if it beats the stored best. Returns true if it was a new best.</summary>
        public static bool SubmitScore(Difficulty difficulty, int score)
        {
            if (score <= GetBestScore(difficulty))
                return false;
            PlayerPrefs.SetInt(BestScorePrefix + difficulty, score);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>True once the given tier has been beaten at least once.</summary>
        public static bool IsCleared(Difficulty difficulty)
        {
            return PlayerPrefs.GetInt(ClearedPrefix + difficulty, 0) != 0;
        }

        /// <summary>Mark a tier as cleared (unlocks the next one on the title screen).</summary>
        public static void MarkCleared(Difficulty difficulty)
        {
            PlayerPrefs.SetInt(ClearedPrefix + difficulty, 1);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Whether a difficulty tier is unlocked (GDD §2.3): Normal always, Veteran
        /// after clearing Normal, Nightmare after clearing Veteran.
        /// </summary>
        public static bool IsUnlocked(Difficulty difficulty)
        {
            switch (difficulty)
            {
                case Difficulty.Veteran: return IsCleared(Difficulty.Normal);
                case Difficulty.Nightmare: return IsCleared(Difficulty.Veteran);
                default: return true;
            }
        }

        /// <summary>Persisted mute preference.</summary>
        public static bool Muted
        {
            get => PlayerPrefs.GetInt(MuteKey, 0) != 0;
            set { PlayerPrefs.SetInt(MuteKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        // ----- Settings (Welcome & Settings screen) -----

        private const string SettingsPrefix = "corehold.settings.";

        /// <summary>
        /// Persisted volume for a channel ("master"/"sfx"/"music"), or −1 when the
        /// player has never moved that slider — callers keep their authored scene
        /// value in that case, so defaults stay tuned in one place.
        /// </summary>
        public static float GetVolume(string channel) =>
            PlayerPrefs.GetFloat(SettingsPrefix + channel, -1f);

        /// <summary>Persist a channel volume (clamped 0..1).</summary>
        public static void SetVolume(string channel, float value01)
        {
            PlayerPrefs.SetFloat(SettingsPrefix + channel, Mathf.Clamp01(value01));
            PlayerPrefs.Save();
        }

        /// <summary>Accessibility: camera shake master switch (default ON).</summary>
        public static bool ShakeEnabled
        {
            get => PlayerPrefs.GetInt(SettingsPrefix + "shake", 1) != 0;
            set { PlayerPrefs.SetInt(SettingsPrefix + "shake", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>Night lighting preference — applied at map load when the scene has the rig (R23).</summary>
        public static bool NightPreferred
        {
            get => PlayerPrefs.GetInt(SettingsPrefix + "night", 0) != 0;
            set { PlayerPrefs.SetInt(SettingsPrefix + "night", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>
        /// Radial build menu (R-UI-1) — empty-pad taps grow a ring of turret nodes
        /// around the pad instead of opening the bottom sheet. DEFAULT ON.
        ///
        /// It shipped opt-in while it was new. It is the better answer and is now
        /// the default: the ring appears AT the pad you tapped, so the choice and
        /// its consequence sit in one place, while the bottom sheet drags the eye
        /// to the other end of the screen and back. The sheet remains one toggle
        /// away for anyone who prefers it, and for the roster sizes where a ring
        /// gets crowded.
        ///
        /// The stored key is unchanged, so a player who already chose the sheet
        /// keeps it — only the default for someone who never touched the setting
        /// moves.
        /// </summary>
        public static bool RadialBuildMenu
        {
            get => PlayerPrefs.GetInt(SettingsPrefix + "radial", 1) != 0;
            set { PlayerPrefs.SetInt(SettingsPrefix + "radial", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        // ----- Field-guide sighting flags (R-UI-7) -----
        //
        // corehold.seen.<kind>.<id> = 1 once the player has met a unit: an enemy
        // on its first spawn, a turret when a level first offers it. The in-memory
        // cache exists because enemies spawn every few seconds — without it every
        // spawn would hit PlayerPrefs.Save (an IndexedDB write on WebGL).

        private const string SeenPrefix = "corehold.seen.";
        private static readonly System.Collections.Generic.HashSet<string> _seenCache =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>True once <see cref="MarkSeen"/> recorded this unit (kind: "enemy"/"turret").</summary>
        public static bool IsSeen(string kind, string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            string key = SeenPrefix + kind + "." + id;
            if (_seenCache.Contains(key))
                return true;
            bool seen = PlayerPrefs.GetInt(key, 0) != 0;
            if (seen)
                _seenCache.Add(key);
            return seen;
        }

        /// <summary>Record a first sighting. Cheap when already recorded (no disk write).</summary>
        public static void MarkSeen(string kind, string id)
        {
            if (string.IsNullOrEmpty(id) || IsSeen(kind, id))
                return;
            string key = SeenPrefix + kind + "." + id;
            _seenCache.Add(key);
            PlayerPrefs.SetInt(key, 1);
            PlayerPrefs.Save();
        }

        // ----- Per-map + per-difficulty personal records (R4) -----
        //
        // Same store, new keys: corehold.record.<map>.<difficulty>.<stat>. The
        // map id is the LevelDefinition asset name (one map today; the generator
        // makes this plural — roadmap P6). This is the sink R34's leaderboard and
        // R35's medals extend.

        private const string RecordPrefix = "corehold.record.";

        private static string RecordKey(string map, Difficulty difficulty, string stat)
            => $"{RecordPrefix}{map}.{difficulty}.{stat}";

        /// <summary>Stored personal best for a stat on a map+difficulty (0 if none).</summary>
        public static int GetRecord(string map, Difficulty difficulty, string stat)
            => PlayerPrefs.GetInt(RecordKey(map, difficulty, stat), 0);

        /// <summary>
        /// Record a higher-is-better stat (waves, integrity, salvage, streak).
        /// Returns true only when the value strictly beats the stored best.
        /// </summary>
        public static bool SubmitRecordMax(string map, Difficulty difficulty, string stat, int value)
        {
            if (value <= GetRecord(map, difficulty, stat))
                return false;
            PlayerPrefs.SetInt(RecordKey(map, difficulty, stat), value);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>
        /// Record a lower-is-better stat (clear time in seconds). 0 means "no
        /// record yet", so non-positive values are rejected. Returns true only
        /// when the value strictly beats the stored best.
        /// </summary>
        public static bool SubmitRecordMin(string map, Difficulty difficulty, string stat, int value)
        {
            if (value <= 0)
                return false;
            int current = GetRecord(map, difficulty, stat);
            if (current > 0 && value >= current)
                return false;
            PlayerPrefs.SetInt(RecordKey(map, difficulty, stat), value);
            PlayerPrefs.Save();
            return true;
        }

        // ----- Campaign persistence (plan v2 §A.7) -----
        //
        // Keys: corehold.campaign.<id>.* — the id comes from the manifest, and
        // per-stage records key by LEVEL NUMBER, not the LevelDefinition name:
        // definition names embed the generation seed, so regenerating a stage
        // would orphan every record keyed by them.

        private const string CampaignPrefix = "corehold.campaign.";

        /// <summary>The in-flight run blob (JSON), written at level boundaries so
        /// a WebGL tab refresh cannot destroy a campaign. Empty = no saved run.</summary>
        public static string GetCampaignRun(string campaignId)
            => PlayerPrefs.GetString($"{CampaignPrefix}{campaignId}.run", "");

        public static void SaveCampaignRun(string campaignId, string json)
        {
            PlayerPrefs.SetString($"{CampaignPrefix}{campaignId}.run", json ?? "");
            PlayerPrefs.Save();
        }

        public static void ClearCampaignRun(string campaignId)
        {
            PlayerPrefs.DeleteKey($"{CampaignPrefix}{campaignId}.run");
            PlayerPrefs.Save();
        }

        public static int GetCampaignBestScore(string campaignId)
            => PlayerPrefs.GetInt($"{CampaignPrefix}{campaignId}.bestScore", 0);

        /// <summary>Returns true only when the score strictly beats the stored best.</summary>
        public static bool SubmitCampaignBestScore(string campaignId, int score)
        {
            if (score <= GetCampaignBestScore(campaignId))
                return false;
            PlayerPrefs.SetInt($"{CampaignPrefix}{campaignId}.bestScore", score);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>Best full-campaign time in seconds (0 = none yet).</summary>
        public static int GetCampaignBestTime(string campaignId)
            => PlayerPrefs.GetInt($"{CampaignPrefix}{campaignId}.bestTime", 0);

        public static bool SubmitCampaignBestTime(string campaignId, int seconds)
        {
            if (seconds <= 0)
                return false;
            int current = GetCampaignBestTime(campaignId);
            if (current > 0 && seconds >= current)
                return false;
            PlayerPrefs.SetInt($"{CampaignPrefix}{campaignId}.bestTime", seconds);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>Best stars for a campaign level, keyed by 1-based level number.</summary>
        public static int GetCampaignStageStars(string campaignId, int levelNumber)
            => PlayerPrefs.GetInt($"{CampaignPrefix}{campaignId}.stage.{levelNumber}.stars", 0);

        public static void SubmitCampaignStageStars(string campaignId, int levelNumber, int stars)
        {
            if (stars <= GetCampaignStageStars(campaignId, levelNumber))
                return;
            PlayerPrefs.SetInt($"{CampaignPrefix}{campaignId}.stage.{levelNumber}.stars", stars);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Erase everything stored for one campaign — run blob, bests, per-stage
        /// stars — so the Welcome screen reads as a first-time player's. Used by
        /// the debug console; PlayerPrefs cannot enumerate keys, so stage stars
        /// are cleared over a generous fixed range rather than discovered.
        /// </summary>
        public static void ClearCampaignData(string campaignId)
        {
            PlayerPrefs.DeleteKey($"{CampaignPrefix}{campaignId}.run");
            PlayerPrefs.DeleteKey($"{CampaignPrefix}{campaignId}.bestScore");
            PlayerPrefs.DeleteKey($"{CampaignPrefix}{campaignId}.bestTime");
            for (int level = 1; level <= MaxClearableStages; level++)
                PlayerPrefs.DeleteKey($"{CampaignPrefix}{campaignId}.stage.{level}.stars");
            PlayerPrefs.Save();
        }

        /// <summary>Upper bound for the star-key wipe above — far past any authored
        /// campaign length, so a longer campaign than expected still clears fully.</summary>
        private const int MaxClearableStages = 100;
    }
}
