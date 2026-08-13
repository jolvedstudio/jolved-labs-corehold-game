using System;
using Corehold.Data;
using Corehold.Enemies;
using UnityEngine;

namespace Corehold.Core
{
    /// <summary>High-level game state (GDD §3.2).</summary>
    public enum GameState
    {
        Boot,
        Title,
        Briefing,
        Build,
        Wave,
        Victory,
        Defeat
    }

    /// <summary>Difficulty tier (GDD §2.3, §3.3).</summary>
    public enum Difficulty
    {
        Normal,
        Veteran,
        Nightmare
    }

    /// <summary>
    /// Central game state, salvage economy and core integrity (GDD §3.2, §3.3).
    /// Singleton via a static Instance set in Awake. Everything reacts to the
    /// plain C# events below — nothing polls in Update.
    /// </summary>
    [DisallowMultipleComponent]
    public class GameManager : MonoBehaviour
    {
        /// <summary>
        /// Global access point. Set in Awake. Self-heals: if the backing field is
        /// ever null while a GameManager still lives in the scene (e.g. an ordering
        /// or domain-reload edge case), it is re-resolved on demand so gameplay code
        /// like TrySpend never silently no-ops.
        /// </summary>
        public static GameManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindFirstObjectByType<GameManager>();
                return _instance;
            }
            private set => _instance = value;
        }
        private static GameManager _instance;

        [Header("Starting economy")]
        [Tooltip("Salvage the player begins the run with.")]
        [SerializeField] private int startingSalvage = 300;

        [Header("Difficulty")]
        [SerializeField] private Difficulty difficulty = Difficulty.Normal;

        [Header("Kill streak (R2)")]
        [Tooltip("Streak tuning SO. When unset, the code defaults below apply (+5%/step, +50% cap, 2 s window).")]
        [SerializeField] private StreakConfig streakConfig;

        [Tooltip("[TUNE] Pitch scale added per streak step to the StreakStep one-shot (0.06 = +6% pitch per step).")]
        [SerializeField] private float streakPitchStep = 0.06f;

        // ----- Runtime state -----

        /// <summary>Current game state. Setting it raises OnStateChanged.</summary>
        public GameState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                    return;
                _state = value;
                OnStateChanged?.Invoke(_state);
            }
        }
        private GameState _state = GameState.Boot;

        /// <summary>Current salvage balance.</summary>
        public int Salvage { get; private set; }

        /// <summary>Current core integrity.</summary>
        public int Integrity { get; private set; }

        /// <summary>Index of the current wave (0-based).</summary>
        public int WaveIndex { get; set; }

        /// <summary>Current difficulty tier.</summary>
        public Difficulty Difficulty
        {
            get => difficulty;
            set => difficulty = value;
        }

        /// <summary>When true, DamageCore does nothing (debug console, GDD §12.4).</summary>
        public bool CoreInvulnerable { get; set; }

        /// <summary>Current kill-streak count (R2). 1 = a lone kill; resets when the window lapses.</summary>
        public int CurrentStreak { get; private set; }

        private float _lastKillTime = -999f; // scaled game time, like kill pacing

        private float StreakPerStep => streakConfig != null ? streakConfig.perStepBonus : 0.05f;
        private float StreakCap => streakConfig != null ? streakConfig.bonusCap : 0.5f;
        private float StreakWindow => streakConfig != null ? streakConfig.windowSeconds : 2f;

        // ----- Events (UI subscribes to these) -----

        /// <summary>Raised whenever the salvage balance changes. Argument is the new balance.</summary>
        public event Action<int> OnSalvageChanged;

        /// <summary>Raised whenever core integrity changes. Argument is the new integrity.</summary>
        public event Action<int> OnIntegrityChanged;

        /// <summary>Raised whenever the game state changes. Argument is the new state.</summary>
        public event Action<GameState> OnStateChanged;

        /// <summary>
        /// Raised on every kill while a streak is running (R2). Arguments: streak
        /// count, bonus salvage paid on this kill, world position of the kill.
        /// OverlayManager listens to place the world-space combo count.
        /// </summary>
        public event Action<int, int, Vector3> OnStreakChanged;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            Salvage = startingSalvage;
            Integrity = StartingIntegrityFor(difficulty);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>Starting integrity per tier (GDD §3.3): 20 Normal, 15 Veteran, 10 Nightmare.</summary>
        public static int StartingIntegrityFor(Difficulty d)
        {
            switch (d)
            {
                case Difficulty.Veteran: return 15;
                case Difficulty.Nightmare: return 10;
                default: return 20;
            }
        }

        /// <summary>Change the game state. Public entry point for other systems.</summary>
        public void SetState(GameState newState)
        {
            State = newState;
        }

        /// <summary>
        /// Apply a difficulty tier and (re)initialise the run's economy and integrity
        /// for it (GDD §8.2, §3.3). Called by the title screen once the player picks a
        /// tier, before the Build phase begins. Raises the salvage and integrity
        /// events so any already-live UI updates.
        /// </summary>
        public void ConfigureRun(Difficulty tier)
        {
            difficulty = tier;

            // Economy multiplier applies to starting salvage (GDD §8.2).
            float ecoMul = WaveManager.DifficultyEconomyMultiplier(tier);
            Salvage = Mathf.RoundToInt(startingSalvage * ecoMul);
            Integrity = StartingIntegrityFor(tier);

            // Fresh run — clear the kill streak (R2).
            CurrentStreak = 0;
            _lastKillTime = -999f;

            OnSalvageChanged?.Invoke(Salvage);
            OnIntegrityChanged?.Invoke(Integrity);
        }

        /// <summary>
        /// Attempt to spend salvage. Returns false and spends nothing if the
        /// balance is short.
        /// </summary>
        public bool TrySpend(int amount)
        {
            if (amount < 0 || Salvage < amount)
                return false;

            Salvage -= amount;
            OnSalvageChanged?.Invoke(Salvage);
            return true;
        }

        /// <summary>Add salvage to the balance.</summary>
        public void AddSalvage(int amount)
        {
            if (amount <= 0)
                return;

            Salvage += amount;
            OnSalvageChanged?.Invoke(Salvage);
        }

        /// <summary>
        /// Award a kill's bounty through the streak system (R2): rapid consecutive
        /// kills escalate a bonus (+perStepBonus of the bounty per step, capped at
        /// bonusCap) and the payout routes through <see cref="AddSalvage"/> so
        /// <see cref="OnSalvageChanged"/> fires normally. The window is measured in
        /// scaled game time so the 2× toggle does not break streaks that kill
        /// pacing itself still supports. Feedback stays in the existing directors:
        /// a rising-pitch StreakStep one-shot here, the world-space combo count via
        /// OverlayManager listening to <see cref="OnStreakChanged"/>.
        /// </summary>
        public void AddKillSalvage(int bounty, Vector3 worldPos)
        {
            if (bounty <= 0)
                return;

            float now = Time.time;
            CurrentStreak = (now - _lastKillTime) <= StreakWindow ? CurrentStreak + 1 : 1;
            _lastKillTime = now;

            float bonusFraction = Mathf.Min(StreakPerStep * (CurrentStreak - 1), StreakCap);
            int bonus = Mathf.RoundToInt(bounty * bonusFraction);
            AddSalvage(bounty + bonus);

            if (CurrentStreak >= 2)
            {
                if (Corehold.Systems.AudioDirector.Instance != null)
                {
                    float pitch = 1f + streakPitchStep * (CurrentStreak - 2);
                    Corehold.Systems.AudioDirector.Instance.Play(
                        Corehold.Systems.AudioDirector.Sfx.StreakStep, 1f, pitch);
                }
                OnStreakChanged?.Invoke(CurrentStreak, bonus, worldPos);
            }
        }

        /// <summary>
        /// Remove core integrity. When integrity reaches zero or below the state
        /// flips to Defeat. Ignored while CoreInvulnerable is set.
        /// </summary>
        public void DamageCore(int amount)
        {
            if (amount <= 0 || CoreInvulnerable)
                return;

            Integrity -= amount;
            if (Integrity < 0)
                Integrity = 0;

            OnIntegrityChanged?.Invoke(Integrity);

            if (Integrity <= 0)
                State = GameState.Defeat;
        }

        /// <summary>
        /// Wire an enemy's leak event to DamageCore. Call this when an enemy is
        /// spawned so a leaker decrements integrity (GDD §3.3).
        /// </summary>
        public void RegisterEnemy(Enemy enemy)
        {
            if (enemy == null)
                return;
            enemy.OnLeaked += HandleEnemyLeaked;
        }

        /// <summary>Stop listening to an enemy (e.g. when returned to a pool).</summary>
        public void UnregisterEnemy(Enemy enemy)
        {
            if (enemy == null)
                return;
            enemy.OnLeaked -= HandleEnemyLeaked;
        }

        private void HandleEnemyLeaked(Enemy enemy)
        {
            DamageCore(Mathf.CeilToInt(enemy.LeakDamage));
        }
    }
}
