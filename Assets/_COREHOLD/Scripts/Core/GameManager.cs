using System;
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

        // ----- Events (UI subscribes to these) -----

        /// <summary>Raised whenever the salvage balance changes. Argument is the new balance.</summary>
        public event Action<int> OnSalvageChanged;

        /// <summary>Raised whenever core integrity changes. Argument is the new integrity.</summary>
        public event Action<int> OnIntegrityChanged;

        /// <summary>Raised whenever the game state changes. Argument is the new state.</summary>
        public event Action<GameState> OnStateChanged;

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
