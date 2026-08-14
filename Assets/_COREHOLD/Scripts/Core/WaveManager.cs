using System;
using System.Collections;
using System.Collections.Generic;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;
using UnityEngine;

namespace Corehold.Core
{
    /// <summary>
    /// Drives the wave schedule (GDD §8.1, §8.4, §12.2, §12.3).
    ///
    /// Responsibilities:
    ///   • Owns the authoritative live-enemy registry that <see cref="Corehold.Towers.TowerTargeting"/>
    ///     reads. (The static <see cref="Enemy.Live"/> list is kept in sync by the
    ///     enemies themselves; this manager tracks the enemies it spawned so it can
    ///     count them, apply the concurrency cap, and know when a wave is done.)
    ///   • Runs one spawn coroutine per <see cref="SpawnGroup"/>, honouring
    ///     <see cref="SpawnGroup.spawnGap"/> and <see cref="SpawnGroup.startOffset"/>.
    ///   • Enforces a hard cap of <see cref="_maxLiveEnemies"/> (14) live enemies
    ///     (GDD §8.1). When the cap is hit the spawn queue holds and resumes as
    ///     units die — deterministic performance, and a gentle rubber band.
    ///   • Supports more than one active wave at once, so pressing Start Wave while
    ///     a wave is on the field chains the next one on top (GDD §8.4). Chaining
    ///     pays 8 salvage per enemy still alive at the moment of the call, capped
    ///     at 80 — and is refused while the field is already at
    ///     <see cref="ChainLockAt"/> committed enemies. Unbounded, the call stops
    ///     being a pacing choice: every remaining wave lands as a single pile.
    ///   • Applies the wave HP scalar 1.0 + 0.18·(wave − 1) and the difficulty
    ///     multipliers (§8.2) at spawn time.
    ///
    /// <see cref="GameManager"/> stays in the Wave state until the live count is
    /// zero AND no wave remains unstarted in the queue.
    /// </summary>
    [DisallowMultipleComponent]
    public class WaveManager : MonoBehaviour
    {
        [Header("Level")]
        [Tooltip("The level whose wave sequence and rules drive this manager (GDD §12.2).")]
        [SerializeField] private LevelDefinition level;

        [Header("Spawners")]
        [Tooltip("Spawn points, matched to SpawnGroup.spawnerIndex by their Index (GDD §12.2). " +
                 "0 = west ground, 1 = north ground, 2 = air.")]
        [SerializeField] private Spawner[] spawners;

        [Header("Pooling")]
        [Tooltip("Pools enemy prefabs so nothing calls Instantiate/Destroy during a wave (GDD §11). Optional — falls back to Instantiate.")]
        [SerializeField] private PoolRegistry pool;

        [Header("Fallback rules (used when no LevelDefinition is assigned)")]
        [Tooltip("Ordered wave sequence used when no LevelDefinition is set.")]
        [SerializeField] private WaveDefinition[] waves;

        [Tooltip("Hard cap on concurrently live enemies (GDD §8.1).")]
        [SerializeField] private int maxLiveEnemiesFallback = 14;

        [Tooltip("Per-wave HP growth (GDD §8.2): scalar = 1 + hpGrowthPerWave·(wave − 1).")]
        [SerializeField] private float hpGrowthPerWaveFallback = 0.18f;

        [Tooltip("Chain bonus salvage per live enemy at the moment of the call (GDD §8.4).")]
        [SerializeField] private int chainBonusPerLiveEnemyFallback = 8;

        [Tooltip("Maximum chain bonus per call (GDD §8.4).")]
        [SerializeField] private int chainBonusCapFallback = 80;

        [Tooltip("Chaining locks when this fraction of the live cap is already committed (alive + queued). " +
                 "0.75 of a 14-cap locks at 11. 0 or less = never lock.")]
        [SerializeField] private float chainLockFieldLoadFallback = 0.75f;

        [Header("Navigation liveness (GDD redesign §Gap 4)")]
        [Tooltip("Seconds a live enemy may make no path progress before the watchdog culls it silently (no Core damage, no bounty). Should never fire if navigation is healthy; it exists so a regression logs loudly instead of bricking the session.")]
        [SerializeField] private float stallWatchdogSeconds = 8f;

        [Tooltip("Largest body radius any spawnable enemy has, used for the conservative derived-capacity calculation. Set to your biggest unit's radius.")]
        [SerializeField] private float largestBodyRadius = 1.2f;

        // ----- Runtime rule values (resolved from the level or the fallbacks) -----
        private int _maxLiveEnemies = 14;
        private float _hpGrowthPerWave = 0.18f;
        private int _chainBonusPerLiveEnemy = 8;
        private int _chainBonusCap = 80;
        private float _chainLockFieldLoad = 0.75f;
        private bool _spreadGroundGroups;

        // ----- Runtime state -----

        /// <summary>Enemies this manager has spawned and that are still alive.</summary>
        private readonly List<Enemy> _live = new List<Enemy>();

        /// <summary>Per-live-enemy stall tracking: last observed frontness and the time it last advanced.</summary>
        private readonly Dictionary<Enemy, StallRecord> _stall = new Dictionary<Enemy, StallRecord>();

        /// <summary>Enemies that spawned but are held pending because the cap was hit.</summary>
        private readonly Queue<PendingSpawn> _pending = new Queue<PendingSpawn>();

        /// <summary>All spawn coroutines currently running across all active waves.</summary>
        private readonly List<Coroutine> _spawnRoutines = new List<Coroutine>();

        private int _activeSpawnGroups;   // groups still emitting (not yet drained)
        private int _nextWaveIndex;       // 0-based index of the next wave to start

        /// <summary>Wave number per still-emitting group — one entry per group, so two
        /// groups of the same wave both have to drain before that wave leaves flight.</summary>
        private readonly List<int> _emittingWaves = new List<int>();

        /// <summary>Reused by <see cref="WavesInFlight"/> so a per-frame UI query allocates nothing.</summary>
        private readonly HashSet<int> _inFlightScratch = new HashSet<int>();

        /// <summary>
        /// How many DISTINCT waves currently have anything on the field — live
        /// enemies, queued spawns, or groups still emitting.
        ///
        /// Reporting only. It was the chain gate for one commit, and it was the
        /// wrong measure: a wave with four stragglers counts the same as a wave of
        /// twenty, so the button locked on an almost empty field. The gate is
        /// <see cref="CanStartNextWave"/>, on committed headcount.
        /// </summary>
        public int WavesInFlight
        {
            get
            {
                _inFlightScratch.Clear();
                for (int i = 0; i < _live.Count; i++)
                    if (_live[i] != null)
                        _inFlightScratch.Add(_live[i].WaveNumber);
                foreach (PendingSpawn p in _pending)
                    _inFlightScratch.Add(p.WaveNumber);
                for (int i = 0; i < _emittingWaves.Count; i++)
                    _inFlightScratch.Add(_emittingWaves[i]);
                return _inFlightScratch.Count;
            }
        }

        /// <summary>
        /// Enemies already committed to the field: alive, plus queued behind the
        /// live cap. The queue counts because those units are paid for and coming
        /// — measuring only the living would call an empty field "empty" while
        /// twenty enemies wait to walk on.
        /// </summary>
        public int CommittedCount => _live.Count + _pending.Count;

        /// <summary>Committed count at which chaining locks; 0 means it never does.</summary>
        public int ChainLockAt => _chainLockFieldLoad <= 0f
            ? 0
            : Mathf.Max(1, Mathf.RoundToInt(_maxLiveEnemies * _chainLockFieldLoad));

        /// <summary>
        /// Whether the Start/Chain button may fire.
        ///
        /// The bound is on HOW FULL THE FIELD IS, not on how many waves are on it.
        /// Counting waves was the obvious rule and the wrong one: a wave with four
        /// stragglers left counts the same as a wave of twenty, so the button
        /// locked on a nearly empty field and read as broken. What actually needs
        /// bounding is the pile, and the pile is a headcount.
        ///
        /// It also closes the farming case on its own. The chain bonus is 8 per
        /// LIVE enemy, so calling into an empty field pays almost nothing, and
        /// calling into a full one is what this refuses — the exploit needed both
        /// halves and can no longer have either.
        /// </summary>
        public bool CanStartNextWave =>
            HasNextWave && (ChainLockAt <= 0 || !WaveInProgress || CommittedCount < ChainLockAt);

        /// <summary>Number of enemies alive on the field right now.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Stable id of the level driving this manager — the LevelDefinition asset name (R4 records are keyed per map).</summary>
        public string LevelId => level != null ? level.name : "default";

        /// <summary>Number of enemies waiting for a free slot under the 14-cap.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>0-based index of the next wave the Start Wave button will launch.</summary>
        public int NextWaveIndex => _nextWaveIndex;

        /// <summary>Total number of waves in the sequence.</summary>
        public int WaveCount => Waves != null ? Waves.Length : 0;

        /// <summary>
        /// The <see cref="WaveDefinition"/> the Start Wave button will launch next,
        /// or null if there is none. Used by the HUD to build the next-wave preview
        /// (unit icons, counts and armour pips — GDD §9.1, §9.4).
        /// </summary>
        public WaveDefinition NextWave => GetWave(_nextWaveIndex);

        /// <summary>Get the wave definition at a 0-based index, or null if out of range.</summary>
        public WaveDefinition GetWave(int index)
        {
            var w = Waves;
            if (w == null || index < 0 || index >= w.Length)
                return null;
            return w[index];
        }

        /// <summary>True while any wave still has enemies alive or unspawned in the queue.</summary>
        public bool WaveInProgress =>
            _live.Count > 0 || _pending.Count > 0 || _activeSpawnGroups > 0;

        /// <summary>True if there is at least one more wave that has not been started.</summary>
        public bool HasNextWave => _nextWaveIndex < WaveCount;

        /// <summary>Raised when a wave is started. Argument is the 1-based wave number.</summary>
        public event Action<int> OnWaveStarted;

        /// <summary>
        /// Raised when the field is fully clear — no live enemies, none pending,
        /// and no group still spawning (GDD §12.3). Argument is the highest
        /// 1-based wave number that had been started.
        /// </summary>
        public event Action<int> OnWaveComplete;

        /// <summary>Raised whenever the live-enemy count changes. Argument is the new count.</summary>
        public event Action<int> OnLiveCountChanged;

        private readonly struct PendingSpawn
        {
            public readonly EnemyDefinition Enemy;
            public readonly int SpawnerIndex;
            public readonly int WaveNumber; // 1-based

            public PendingSpawn(EnemyDefinition enemy, int spawnerIndex, int waveNumber)
            {
                Enemy = enemy;
                SpawnerIndex = spawnerIndex;
                WaveNumber = waveNumber;
            }
        }

        private struct StallRecord
        {
            public float LastFrontness;
            public float LastAdvanceTime;
        }

        private WaveDefinition[] Waves => level != null && level.waves != null && level.waves.Length > 0
            ? level.waves
            : waves;

        private void Awake()
        {
            ResolveRules();
        }

        private void Update()
        {
            TickStallWatchdog();

            // Pending units enter as track entrances clear over time (not only on
            // deaths): the front unit walks away and frees the entrance. Retry a few
            // times a second — cheap and keeps chained-wave surplus flowing.
            if (_pending.Count > 0)
            {
                _drainTimer -= Time.deltaTime;
                if (_drainTimer <= 0f)
                {
                    _drainTimer = 0.25f;
                    DrainPending();
                }
            }
        }

        private float _drainTimer;

        /// <summary>
        /// Navigation liveness net (GDD redesign §Gap 4). If any live enemy makes no
        /// path progress for <see cref="stallWatchdogSeconds"/>, cull it silently
        /// (no Core damage, no bounty) and log loudly. With the 1-D car-following
        /// model this must never fire; it exists so a future regression yields a log
        /// line and a still-completable wave, never a bricked session.
        /// </summary>
        private void TickStallWatchdog()
        {
            if (_live.Count == 0)
                return;

            float now = Time.time;
            _strandedScratch.Clear();
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Enemy e = _live[i];

                // A destroyed or already-dead enemy still on this list will NEVER
                // leave it on its own — OnDied fires once, and an entry that
                // missed it is stranded permanently. Skipping it (which is what
                // this loop used to do) means the live count never falls, so the
                // wave cannot complete and the chain lock never releases. The
                // observed cause was an exception in the kill-streak HUD, now
                // contained at the source; this is the net that makes any future
                // cause cost a log line instead of the run.
                if (e == null)
                {
                    _live.RemoveAt(i);
                    _liveDirty = true;
                    continue;
                }
                if (!e.IsAlive)
                {
                    _strandedScratch.Add(e);
                    continue;
                }
                var mover = e.Mover;
                if (mover == null)
                    continue;

                float front = mover.Frontness;
                if (!_stall.TryGetValue(e, out StallRecord rec))
                {
                    rec = new StallRecord { LastFrontness = front, LastAdvanceTime = now };
                    _stall[e] = rec;
                    continue;
                }

                if (front > rec.LastFrontness + 0.01f)
                {
                    rec.LastFrontness = front;
                    rec.LastAdvanceTime = now;
                    _stall[e] = rec;
                }
                else if (now - rec.LastAdvanceTime > stallWatchdogSeconds)
                {
                    e.CullSilently();
                }
            }

            // Reclaimed AFTER the walk: HandleEnemyGone removes from _live and can
            // drain pending spawns back into it, which is not something to do to a
            // list mid-iteration.
            for (int i = 0; i < _strandedScratch.Count; i++)
            {
                Debug.LogWarning($"[Corehold] Reclaimed '{_strandedScratch[i].name}': dead but still on the " +
                                 "live list, so something threw before OnDied was raised. The wave would " +
                                 "otherwise never complete — check the Console for the exception above.");
                HandleEnemyGone(_strandedScratch[i]);
            }
            _strandedScratch.Clear();

            if (_liveDirty)
            {
                _liveDirty = false;
                OnLiveCountChanged?.Invoke(_live.Count);
                CheckWaveComplete();
            }
        }

        /// <summary>Reused per frame so the watchdog's recovery path allocates nothing.</summary>
        private readonly List<Enemy> _strandedScratch = new List<Enemy>();
        private bool _liveDirty;

        private void ResolveRules()
        {
            if (level != null)
            {
                _maxLiveEnemies = level.maxLiveEnemies > 0 ? level.maxLiveEnemies : maxLiveEnemiesFallback;
                _hpGrowthPerWave = level.hpGrowthPerWave > 0f ? level.hpGrowthPerWave : hpGrowthPerWaveFallback;
                _chainBonusPerLiveEnemy = level.chainBonusPerLiveEnemy > 0 ? level.chainBonusPerLiveEnemy : chainBonusPerLiveEnemyFallback;
                _chainBonusCap = level.chainBonusCap > 0 ? level.chainBonusCap : chainBonusCapFallback;
                _chainLockFieldLoad = level.chainLockFieldLoad > 0f ? level.chainLockFieldLoad : chainLockFieldLoadFallback;
                _spreadGroundGroups = level.spreadGroundGroupsAcrossSpawners;
            }
            else
            {
                _maxLiveEnemies = maxLiveEnemiesFallback;
                _hpGrowthPerWave = hpGrowthPerWaveFallback;
                _chainBonusPerLiveEnemy = chainBonusPerLiveEnemyFallback;
                _chainBonusCap = chainBonusCapFallback;
                _chainLockFieldLoad = chainLockFieldLoadFallback;
                _spreadGroundGroups = false;
            }
        }

        /// <summary>Assign the level at runtime (e.g. from the flow) and reset the queue.</summary>
        public void SetLevel(LevelDefinition newLevel)
        {
            level = newLevel;
            ResolveRules();
            ResetSequence();
        }

        /// <summary>Reset to the first wave with an empty field. Does not stop coroutines mid-wave.</summary>
        public void ResetSequence()
        {
            _nextWaveIndex = 0;
        }

        /// <summary>
        /// Debug/tuning only (GDD §12.4, Ticket 29): fast-forward the sequence to the
        /// given 1-based wave and start it, skipping the intervening waves entirely
        /// (they are not spawned and pay no bonus). Used by the DebugConsole to reach
        /// wave 9 quickly for the Frame Debugger draw-call check.
        /// </summary>
        public bool JumpToWave(int waveNumber)
        {
            int target = Mathf.Clamp(waveNumber, 1, WaveCount);
            _nextWaveIndex = target - 1; // 0-based index of the wave to start
            return StartWave(ignoreFlightCap: true);   // a debug jump is not a chain
        }

        /// <summary>
        /// Start the next wave (GDD §8.4). Pressing it while a wave is on the field
        /// chains the next one on top and pays the chain bonus — but only while the
        /// field is under <see cref="ChainLockAt"/>. Returns false if there is no
        /// wave left, or if the field is already that full.
        /// </summary>
        public bool StartNextWave() => StartWave(ignoreFlightCap: false);

        private bool StartWave(bool ignoreFlightCap)
        {
            if (ignoreFlightCap ? !HasNextWave : !CanStartNextWave)
                return false;

            // Chain bonus: 8 salvage per live enemy at the moment of the call,
            // capped at 80 (GDD §8.4). Only when a wave is already on the field.
            if (WaveInProgress && GameManager.Instance != null)
            {
                int aliveNow = _live.Count;
                if (aliveNow > 0)
                {
                    int bonus = Mathf.Min(aliveNow * _chainBonusPerLiveEnemy, _chainBonusCap);
                    bonus = ApplyEconomyMultiplier(bonus);
                    if (bonus > 0)
                        GameManager.Instance.AddSalvage(bonus);
                }
            }

            int waveIndex = _nextWaveIndex;
            _nextWaveIndex++;

            WaveDefinition wave = Waves[waveIndex];
            int waveNumber = waveIndex + 1; // 1-based for the HP scalar

            if (GameManager.Instance != null)
            {
                GameManager.Instance.WaveIndex = waveIndex;
                GameManager.Instance.SetState(GameState.Wave);
            }

            StartWaveGroups(wave, waveNumber);
            OnWaveStarted?.Invoke(waveNumber);
            return true;
        }

        private void StartWaveGroups(WaveDefinition wave, int waveNumber)
        {
            if (wave == null || wave.groups == null)
                return;

            int groundOrdinal = 0;
            foreach (var group in wave.groups)
            {
                if (group.enemy == null || group.count <= 0)
                    continue;

                // R40: a siege map has more approaches than the wave tables know
                // how to address, so its ground groups are dealt out across the
                // spawners that exist. Rotating by wave as well as by group keeps
                // the same approach from always going first. Air is left alone —
                // it has one spawner and the tables address it correctly.
                int spawnerIndex = group.spawnerIndex;
                if (_spreadGroundGroups && !(group.enemy != null && group.enemy.isAir))
                {
                    int[] ground = GroundSpawnerIndices();
                    if (ground.Length > 0)
                        spawnerIndex = ground[(groundOrdinal + waveNumber) % ground.Length];
                    groundOrdinal++;
                }

                _activeSpawnGroups++;
                _emittingWaves.Add(waveNumber);
                Coroutine c = StartCoroutine(SpawnGroupRoutine(group, waveNumber, spawnerIndex));
                _spawnRoutines.Add(c);
            }
        }

        /// <summary>
        /// Spawn one group: wait its startOffset, then emit <c>count</c> units
        /// spaced by <c>spawnGap</c>. Timing uses <see cref="WaitForSeconds"/> so it
        /// scales with <c>Time.timeScale</c> (the 2× toggle, GDD §9.6).
        /// </summary>
        private IEnumerator SpawnGroupRoutine(SpawnGroup group, int waveNumber, int spawnerIndex)
        {
            if (group.startOffset > 0f)
                yield return new WaitForSeconds(group.startOffset);

            for (int i = 0; i < group.count; i++)
            {
                RequestSpawn(group.enemy, spawnerIndex, waveNumber);

                if (i < group.count - 1 && group.spawnGap > 0f)
                    yield return new WaitForSeconds(group.spawnGap);
            }

            _activeSpawnGroups = Mathf.Max(0, _activeSpawnGroups - 1);
            _emittingWaves.Remove(waveNumber);   // one entry per group, so remove one
            CheckWaveComplete();
        }

        /// <summary>
        /// Try to spawn immediately. Admission is slot-based (GDD redesign): a unit
        /// enters only if the derived concurrency ceiling is not exceeded AND its
        /// track entrance is clear by min-spacing (so a track can never be
        /// oversubscribed and chained waves can never pile up on top of each other).
        /// Otherwise it waits in the pending queue and enters as the field drains.
        /// </summary>
        private void RequestSpawn(EnemyDefinition def, int spawnerIndex, int waveNumber)
        {
            if (CanSpawnNow(def, spawnerIndex))
                SpawnEnemy(def, spawnerIndex, waveNumber);
            else
                _pending.Enqueue(new PendingSpawn(def, spawnerIndex, waveNumber));
        }

        /// <summary>
        /// Admission test: under the derived capacity ceiling and the track entrance
        /// is clear for a unit of this definition's radius. Falls back to the flat
        /// cap only if the traffic manager is somehow unavailable.
        /// </summary>
        private bool CanSpawnNow(EnemyDefinition def, int spawnerIndex)
        {
            var rt = RouteTraffic.InstanceOrNull;
            if (rt == null)
                return _live.Count < _maxLiveEnemies;

            int ceiling = Mathf.Min(_maxLiveEnemies, rt.DerivedCapacity(largestBodyRadius));
            if (_live.Count >= ceiling)
                return false;

            Spawner spawner = FindSpawner(spawnerIndex);
            PathRoute route = spawner != null ? spawner.Route : null;
            bool isAir = def != null && def.isAir;
            float radius = ResolvePrefabRadius(def);

            // Entrance frontness: 0 for ground (route start); for air it is the
            // negative distance from the spawner to the Core (= MinFrontness).
            float entranceFrontness = 0f;
            if (isAir)
            {
                Vector3 spawnPos = spawner != null ? spawner.Position : transform.position;
                Transform core = spawner != null ? spawner.CoreTarget : null;
                if (core != null)
                    entranceFrontness = -Vector3.Distance(spawnPos, core.position);
            }

            return rt.CanAdmit(route, isAir, radius, entranceFrontness);
        }

        /// <summary>Read the body radius from the definition's prefab mover (fallback 0.6).</summary>
        private static float ResolvePrefabRadius(EnemyDefinition def)
        {
            if (def != null && def.prefab != null)
            {
                var m = def.prefab.GetComponent<EnemyMover>();
                if (m != null)
                    return m.BodyRadius;
            }
            return 0.6f;
        }

        private void SpawnEnemy(EnemyDefinition def, int spawnerIndex, int waveNumber)
        {
            if (def == null || def.prefab == null)
            {
                Debug.LogWarning($"[WaveManager] Spawn group has a null enemy/prefab; skipping.");
                return;
            }

            Spawner spawner = FindSpawner(spawnerIndex);
            Vector3 spawnPos = spawner != null ? spawner.Position : transform.position;

            // Acquire the Enemy instance (pooled when available, GDD §11).
            Enemy prefabEnemy = def.prefab.GetComponent<Enemy>();
            Enemy enemy;
            if (pool != null && prefabEnemy != null)
            {
                var enemyPool = pool.GetEnemyPool(prefabEnemy);
                enemy = enemyPool.Get();
            }
            else
            {
                GameObject go = Instantiate(def.prefab);
                enemy = go.GetComponent<Enemy>();
                if (enemy == null)
                {
                    Debug.LogWarning($"[WaveManager] Prefab '{def.prefab.name}' has no Enemy component.");
                    Destroy(go);
                    return;
                }
            }

            enemy.transform.position = spawnPos;
            if (spawner != null)
                enemy.transform.rotation = spawner.transform.rotation;

            ConfigureSpawn(enemy, def, spawner, waveNumber);
            TrackEnemy(enemy);
        }

        /// <summary>
        /// Apply the definition, route, wave HP scalar and difficulty multipliers
        /// to a freshly spawned enemy (GDD §8.2, §12.2).
        /// </summary>
        private void ConfigureSpawn(Enemy enemy, EnemyDefinition def, Spawner spawner, int waveNumber)
        {
            enemy.Configure(def);

            // Route the mover: ground units walk the spawner's route; air units
            // fly straight from the spawner to the Core (EnemyMover reads isAir).
            var mover = enemy.Mover;
            if (mover != null)
            {
                PathRoute route = spawner != null ? spawner.Route : null;
                Transform core = spawner != null ? spawner.CoreTarget : null;
                mover.Configure(def, route, core);
            }

            var bridge = enemy.GetComponent<EnemyAnimatorBridge>();
            if (bridge != null)
                bridge.SetDefinition(def);

            enemy.SetWaveNumber(waveNumber);

            // Wave HP scalar (GDD §8.2): 1 + growth·(wave − 1).
            float waveScalar = 1f + _hpGrowthPerWave * (waveNumber - 1);

            // Difficulty HP and economy multipliers (GDD §8.2).
            Difficulty diff = GameManager.Instance != null ? GameManager.Instance.Difficulty : Difficulty.Normal;
            float hpMul = DifficultyHpMultiplier(diff);
            float ecoMul = DifficultyEconomyMultiplier(diff);

            float finalHp = def.baseHealth * waveScalar * hpMul;
            enemy.SetMaxHealth(finalHp);

            // Bounty scales with the economy multiplier; leak damage does not (GDD §8.2).
            enemy.SetBounty(Mathf.RoundToInt(def.bounty * ecoMul));
            enemy.SetLeakDamage(def.leakDamage);
        }

        /// <summary>Subscribe to an enemy's death/leak and register it as live.</summary>
        private void TrackEnemy(Enemy enemy)
        {
            _live.Add(enemy);

            // Seed the stall watchdog record so a unit that never moves at all is caught.
            var mv = enemy.Mover;
            _stall[enemy] = new StallRecord
            {
                LastFrontness = mv != null ? mv.Frontness : 0f,
                LastAdvanceTime = Time.time
            };

            enemy.OnDied += HandleEnemyGone;
            enemy.OnLeaked += HandleEnemyGone;

            // Wire the leak → Core damage path (GDD §3.3).
            if (GameManager.Instance != null)
                GameManager.Instance.RegisterEnemy(enemy);

            OnLiveCountChanged?.Invoke(_live.Count);
        }

        private void HandleEnemyGone(Enemy enemy)
        {
            if (enemy == null)
                return;

            enemy.OnDied -= HandleEnemyGone;
            enemy.OnLeaked -= HandleEnemyGone;

            if (GameManager.Instance != null)
                GameManager.Instance.UnregisterEnemy(enemy);

            _live.Remove(enemy);
            _stall.Remove(enemy);
            OnLiveCountChanged?.Invoke(_live.Count);

            // A slot freed up — let held units in as admission allows (GDD redesign).
            DrainPending();

            CheckWaveComplete();
        }

        /// <summary>
        /// Release held spawns while their track entrance is clear (slot-based
        /// admission). Peeks the head and stops as soon as it cannot be admitted, so
        /// ordering is preserved and a blocked track does not starve the queue check.
        /// </summary>
        private void DrainPending()
        {
            int guard = _pending.Count;
            while (_pending.Count > 0 && guard-- > 0)
            {
                PendingSpawn next = _pending.Peek();
                if (!CanSpawnNow(next.Enemy, next.SpawnerIndex))
                {
                    // Rotate so a blocked entry does not permanently head-of-line
                    // block a different track's ready unit.
                    _pending.Dequeue();
                    _pending.Enqueue(next);
                    continue;
                }
                _pending.Dequeue();
                SpawnEnemy(next.Enemy, next.SpawnerIndex, next.WaveNumber);
            }
        }

        /// <summary>
        /// When the field is fully clear — nothing alive, nothing pending, no group
        /// still spawning — the wave(s) are done (GDD §12.3). Pay the clear bonus
        /// for the highest wave started and flip GameManager back to Build unless
        /// the run is over.
        /// </summary>
        private void CheckWaveComplete()
        {
            if (WaveInProgress)
                return;

            int lastWaveNumber = _nextWaveIndex; // highest 1-based wave started

            PayClearBonus(lastWaveNumber);
            OnWaveComplete?.Invoke(lastWaveNumber);

            if (GameManager.Instance != null && GameManager.Instance.State == GameState.Wave)
            {
                if (HasNextWave)
                    GameManager.Instance.SetState(GameState.Build);
                else
                    GameManager.Instance.SetState(GameState.Victory);
            }
        }

        /// <summary>
        /// Wave-clear bonus (GDD §8.4): 60 + 18·waveNumber, scaled by the
        /// difficulty economy multiplier. Uses the WaveDefinition's clearBonus
        /// when authored (non-zero), else the formula.
        /// </summary>
        private void PayClearBonus(int waveNumber)
        {
            if (GameManager.Instance == null || waveNumber <= 0)
                return;

            int bonus = 0;
            var wavesArr = Waves;
            if (wavesArr != null && waveNumber - 1 < wavesArr.Length && wavesArr[waveNumber - 1] != null)
                bonus = wavesArr[waveNumber - 1].clearBonus;

            if (bonus <= 0)
                bonus = 60 + 18 * waveNumber;

            bonus = ApplyEconomyMultiplier(bonus);
            GameManager.Instance.AddSalvage(bonus);
        }

        private int ApplyEconomyMultiplier(int amount)
        {
            Difficulty diff = GameManager.Instance != null ? GameManager.Instance.Difficulty : Difficulty.Normal;
            return Mathf.RoundToInt(amount * DifficultyEconomyMultiplier(diff));
        }

        /// <summary>
        /// Indices of the spawners that own a route, ascending — the ground
        /// approaches. Air has no route, which is what distinguishes it, so this
        /// never redirects an air group onto the floor.
        /// </summary>
        private int[] GroundSpawnerIndices()
        {
            if (spawners == null)
                return System.Array.Empty<int>();

            var indices = new List<int>();
            foreach (Spawner s in spawners)
                if (s != null && s.Route != null)
                    indices.Add(s.Index);
            indices.Sort();
            return indices.ToArray();
        }

        private Spawner FindSpawner(int index)
        {
            if (spawners == null)
                return null;
            foreach (var s in spawners)
            {
                if (s != null && s.Index == index)
                    return s;
            }
            return null;
        }

        // ----- Difficulty multipliers (GDD §8.2) -----

        /// <summary>Enemy HP multiplier: 1.00 Normal, 1.25 Veteran, 1.55 Nightmare.</summary>
        public static float DifficultyHpMultiplier(Difficulty d)
        {
            switch (d)
            {
                case Difficulty.Veteran: return 1.25f;
                case Difficulty.Nightmare: return 1.55f;
                default: return 1.00f;
            }
        }

        /// <summary>Economy multiplier: 1.00 Normal, 1.12 Veteran, 1.22 Nightmare.</summary>
        public static float DifficultyEconomyMultiplier(Difficulty d)
        {
            switch (d)
            {
                case Difficulty.Veteran: return 1.12f;
                case Difficulty.Nightmare: return 1.22f;
                default: return 1.00f;
            }
        }
    }
}
