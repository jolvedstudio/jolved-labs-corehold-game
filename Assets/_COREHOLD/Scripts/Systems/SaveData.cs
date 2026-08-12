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
    }
}
