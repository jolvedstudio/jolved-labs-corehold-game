using System;
using System.Collections;
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
                // Entering combat or an end screen reclaims the clock: any
                // running time dip (R3 close call / R5 hit-stop) is restored
                // immediately. Build is exempt on purpose — the wave-complete
                // dip fires in the same call chain as the Wave→Build flip.
                if (_state == GameState.Wave || _state == GameState.Victory || _state == GameState.Defeat)
                    CancelTimeDip();
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

        // ----- Run stats (R4) — reset in ConfigureRun, read by ResultScreen -----

        /// <summary>Total salvage income this run (bounties, streak bonuses, clear/chain bonuses).</summary>
        public int RunSalvageEarned { get; private set; }

        /// <summary>Longest kill streak reached this run (R2/R4).</summary>
        public int RunLongestStreak { get; private set; }

        private float _runStartUnscaled;

        /// <summary>Wall-clock seconds since the run was configured (unscaled — the 2× toggle does not shorten it).</summary>
        public float RunSeconds => Time.unscaledTime - _runStartUnscaled;

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

        /// <summary>Every kill payout: (total salvage incl. streak bonus, kill
        /// world position). Drives the HUD's salvage pips (R-UI-5) — the economy
        /// made physically legible at the crash site. Cosmetic listeners only.</summary>
        public event Action<int, Vector3> OnKillSalvage;

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
            ConfigureCampaignRun(tier, -1, -1);
        }

        /// <summary>
        /// Campaign-aware run initialisation (plan v2 §A.6). Identical to
        /// <see cref="ConfigureRun"/> except the starting economy/integrity can be
        /// seeded explicitly (-1 = the tier's own defaults). This is the ONLY legal
        /// write path for carried values: <see cref="AddSalvage"/> would inflate
        /// <see cref="RunSalvageEarned"/> (corrupting the R4 record and the score),
        /// and <see cref="DamageCore"/> fires defeat side-effects — neither is an
        /// initialiser.
        /// </summary>
        public void ConfigureCampaignRun(Difficulty tier, int salvageOverride, int integrityOverride)
        {
            difficulty = tier;

            // Economy multiplier applies to starting salvage (GDD §8.2).
            float ecoMul = WaveManager.DifficultyEconomyMultiplier(tier);

            // Per-level economy (certified tuning): the LevelDefinition's own
            // startingSalvage, when authored, replaces this scene default.
            // Pulled here rather than pushed at scene load so Awake order can
            // never lose it; campaign carry (salvageOverride) outranks both.
            int levelSalvage = 0;
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm != null)
                levelSalvage = wm.LevelStartingSalvage;

            Salvage = salvageOverride >= 0
                ? salvageOverride
                : Mathf.RoundToInt((levelSalvage > 0 ? levelSalvage : startingSalvage) * ecoMul);
            Integrity = integrityOverride >= 0 ? integrityOverride : StartingIntegrityFor(tier);

            // Fresh run — clear the kill streak (R2) and the run stats (R4).
            CurrentStreak = 0;
            _lastKillTime = -999f;
            RunSalvageEarned = 0;
            RunLongestStreak = 0;
            _runStartUnscaled = Time.unscaledTime;

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
            RunSalvageEarned += amount; // run stat (R4)
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
            if (CurrentStreak > RunLongestStreak)
                RunLongestStreak = CurrentStreak; // run stat (R4)

            float bonusFraction = Mathf.Min(StreakPerStep * (CurrentStreak - 1), StreakCap);
            int bonus = Mathf.RoundToInt(bounty * bonusFraction);
            AddSalvage(bounty + bonus);

            // Salvage pips (R-UI-5): every kill, not just streaks. Contained the
            // same way as the streak label — a cosmetic listener must not cost
            // the kill that raised it anything but a log line.
            try
            {
                OnKillSalvage?.Invoke(bounty + bonus, worldPos);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            if (CurrentStreak >= 2)
            {
                if (Corehold.Systems.AudioDirector.Instance != null)
                {
                    float pitch = 1f + streakPitchStep * (CurrentStreak - 2);
                    Corehold.Systems.AudioDirector.Instance.Play(
                        Corehold.Systems.AudioDirector.Sfx.StreakStep, 1f, pitch);
                }
                // Contained here as well as at the call site: this event exists to
                // draw a number over a corpse, and a fault in whatever is drawing
                // it must not propagate back into the kill that raised it. The
                // salvage is already banked above, so a throwing listener costs a
                // log line and its own label — nothing else.
                try
                {
                    OnStreakChanged?.Invoke(CurrentStreak, bonus, worldPos);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // ----- Time dip (R3 close call, reused by the R5 micro hit-stop) -----

        private Coroutine _timeDip;
        private float _dipScale = 1f;
        private float _dipRestoreScale = 1f;

        /// <summary>
        /// Briefly dip <see cref="Time.timeScale"/> (R3): set it to
        /// <paramref name="scale"/> for <paramref name="seconds"/> of UNSCALED
        /// time, then restore the pre-dip scale (so a 2× run resumes at 2×).
        /// Interrupt-safe: if anything else takes the clock while the dip runs
        /// (pause, the speed toggle, a scene reload) the dip stands down without
        /// touching it, and entering Wave/Victory/Defeat cancels it outright.
        /// </summary>
        public void TimeDip(float scale, float seconds)
        {
            CancelTimeDip();
            // Never dip a paused clock (timeScale 0) — resume would inherit it.
            if (seconds <= 0f || Time.timeScale <= 0.01f || !isActiveAndEnabled)
                return;
            _timeDip = StartCoroutine(TimeDipRoutine(Mathf.Clamp01(scale), seconds));
        }

        /// <summary>End a running dip now, restoring the pre-dip scale (no-op when idle).</summary>
        public void CancelTimeDip()
        {
            if (_timeDip == null)
                return;
            StopCoroutine(_timeDip);
            _timeDip = null;
            // Only restore if the clock still holds OUR value — if pause or the
            // speed toggle already changed it, the clock is theirs now.
            if (Mathf.Approximately(Time.timeScale, _dipScale))
                Time.timeScale = _dipRestoreScale;
        }

        private IEnumerator TimeDipRoutine(float scale, float seconds)
        {
            _dipRestoreScale = Time.timeScale > 0.01f ? Time.timeScale : 1f;
            _dipScale = scale;
            Time.timeScale = scale;

            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                if (!Mathf.Approximately(Time.timeScale, _dipScale))
                {
                    // Someone else grabbed the clock mid-dip — hands off.
                    _timeDip = null;
                    yield break;
                }
                yield return null;
            }

            Time.timeScale = _dipRestoreScale;
            _timeDip = null;
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
