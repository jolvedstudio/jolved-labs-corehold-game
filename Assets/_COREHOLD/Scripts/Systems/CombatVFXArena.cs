using System.Collections;
using System.Collections.Generic;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Towers;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// A self-assembling visual test arena for EVERY tower and enemy combat
    /// interaction (VFX debug harness — not shipped in a wave). Drop this on an empty
    /// GameObject in a scene (or use the editor scene builder) and press Play.
    ///
    /// TWO MODES, switchable live from the on-screen panel (top-left in Game view):
    ///
    ///   • GRID — every tower is lined up, each with its OWN target pack directly in
    ///     front (one ground + one air dummy at flight altitude), so ground-only,
    ///     air-capable and air-ONLY (Flak Array) turrets all engage. A showcase row of
    ///     one-of-every-enemy sits behind the towers so each enemy's OWN return fire
    ///     (muzzle/tracer/impact) is visible too. Best for a broad overview.
    ///
    ///   • DUEL — pick ONE tower and ONE enemy from the dropdowns and watch them fight
    ///     one-on-one at close range: the tower manual-targets the chosen enemy, the
    ///     enemy auto-fires back at the tower (its stock behaviour), and BOTH are kept
    ///     alive so the exchange loops until you pick a new pairing. Best for
    ///     inspecting a single interaction in isolation.
    ///
    /// Enemies are stationary (mover stripped — no RouteTraffic dependency) and are
    /// Configure()'d from their definition so armour type + air flag are correct,
    /// which drives counter-readable impacts and the air-target gate. Towers are built
    /// via <see cref="Tower.Build"/> at top tier (multi-weapon / projectile behaviour
    /// shows) with TowerHealth + health bars so enemies can shoot back.
    /// </summary>
    [DisallowMultipleComponent]
    public class CombatVFXArena : MonoBehaviour
    {
        public enum Mode { Grid, Duel }

        [System.Serializable]
        public struct EnemyEntry
        {
            public GameObject prefab;
            public EnemyDefinition definition; // optional; sets armour + air flag
        }

        [Header("Catalogues")]
        [Tooltip("Every tower prefab. Each must carry a Tower + TowerWeapon.")]
        public GameObject[] towerPrefabs;

        [Tooltip("Every enemy prefab, paired with its definition so armour/air are set.")]
        public EnemyEntry[] enemies;

        [Tooltip("The damage-vs-armour table used for counter-readable impacts.")]
        public DamageTable damageTable;

        [Header("Start-up")]
        [Tooltip("Which mode the arena builds on Play.")]
        public Mode startMode = Mode.Grid;

        [Header("Layout")]
        [Tooltip("Metres between adjacent towers along the grid row.")]
        public float towerSpacing = 10f;

        [Tooltip("How far in front of each tower its target pack sits (metres). Short so every turret is in range.")]
        public float targetDistance = 8f;

        [Tooltip("Flight altitude for air dummies (metres).")]
        public float airAltitude = 4f;

        [Tooltip("Distance behind the towers for the enemy showcase row (metres). Kept within enemy weapon range so the showcase actively returns fire.")]
        public float showcaseGap = 10f;

        [Header("Duel layout")]
        [Tooltip("Distance between the tower and the enemy in Duel mode (metres).")]
        public float duelDistance = 9f;

        [Header("Behaviour")]
        [Tooltip("Keep combatants alive by refilling health so the show never ends.")]
        public bool immortal = true;

        [Tooltip("GRID only: every N seconds kill one showcase enemy so the death burst is visible, then respawn it. 0 = never.")]
        public float reapInterval = 4f;

        // ---- Runtime ----
        private Mode _mode;
        private readonly List<Enemy> _immortalEnemies = new List<Enemy>();
        private readonly List<Enemy> _reapable = new List<Enemy>();
        private readonly List<TowerHealth> _immortalTowers = new List<TowerHealth>();
        private readonly List<GameObject> _spawned = new List<GameObject>();

        // Duel selection state.
        private int _duelTowerIndex;
        private int _duelEnemyIndex;
        private string[] _towerNames;
        private string[] _enemyNames;

        // Simple in-Game-view UI state.
        private bool _showEnemyList;
        private bool _showTowerList;
        private Vector2 _towerScroll;
        private Vector2 _enemyScroll;

        private void Start()
        {
            EnsureCore();
            EnsureDamageTable();
            BuildNames();
            _mode = startMode;
            Rebuild();
        }

        private void OnDisable() => ClearSpawned();

        private void BuildNames()
        {
            _towerNames = new string[towerPrefabs != null ? towerPrefabs.Length : 0];
            for (int i = 0; i < _towerNames.Length; i++)
                _towerNames[i] = towerPrefabs[i] != null ? towerPrefabs[i].name : "(null)";

            _enemyNames = new string[enemies != null ? enemies.Length : 0];
            for (int i = 0; i < _enemyNames.Length; i++)
                _enemyNames[i] = enemies[i].prefab != null ? enemies[i].prefab.name : "(null)";
        }

        // -------------------------------------------------------------------------

        private void EnsureCore()
        {
            if (VFXDirector.Instance == null && Object.FindFirstObjectByType<VFXDirector>() == null)
            {
                var go = new GameObject("VFXDirector (Arena)");
                go.AddComponent<VFXDirector>();
                Debug.LogWarning("[Arena] No VFXDirector found — created a bare one. Run " +
                                 "'Tools/COREHOLD/Scene Setup/VFX Director' for the authored effect prefabs.");
            }
        }

        private void EnsureDamageTable()
        {
            if (damageTable != null)
                Projectile.SharedDamageTable = damageTable;
            else
                Debug.LogWarning("[Arena] No DamageTable assigned — impacts read as neutral.");

            // With Enter-Play-Mode domain reload disabled, the static projectile pools
            // survive a play-session restart holding references to instances Unity
            // destroyed on stop — the next fire pops a destroyed instance and throws.
            // Clear them so each session starts with fresh pools.
            Projectile.ClearPools();
        }

        private void ClearSpawned()
        {
            StopAllCoroutines();
            foreach (var go in _spawned)
                if (go != null) Destroy(go);
            _spawned.Clear();
            _immortalEnemies.Clear();
            _reapable.Clear();
            _immortalTowers.Clear();
        }

        private void Rebuild()
        {
            ClearSpawned();
            if (_mode == Mode.Grid) BuildGrid();
            else BuildDuel();
            if (_mode == Mode.Grid && reapInterval > 0f)
                StartCoroutine(ReapLoop());
        }

        // -------------------------------------------------------------------------

        private void BuildGrid()
        {
            if (!Validate()) return;

            float rowWidth = (towerPrefabs.Length - 1) * towerSpacing;
            var groundPrefab = FindEnemy(false);
            var airPrefab = FindEnemy(true);

            // Each tower faces its OWN pair of targets, both directly in front on the
            // tower's centreline so nothing overlaps when viewed from above:
            //   • a GROUND dummy close in (z = +targetDistance),
            //   • an AIR dummy a little further out and up (z = +targetDistance + 4,
            //     y = airAltitude), so the two never share a screen footprint.
            for (int i = 0; i < towerPrefabs.Length; i++)
            {
                if (towerPrefabs[i] == null) continue;
                float x = -rowWidth * 0.5f + i * towerSpacing;
                Vector3 towerPos = new Vector3(x, 0f, 0f);
                SpawnTower(towerPrefabs[i], towerPos);

                if (groundPrefab != null)
                {
                    var g = SpawnEnemy(groundPrefab, EntryFor(groundPrefab),
                        towerPos + new Vector3(0f, 0f, targetDistance));
                    if (g != null) _immortalEnemies.Add(g);
                }
                if (airPrefab != null)
                {
                    var a = SpawnEnemy(airPrefab, EntryFor(airPrefab),
                        towerPos + new Vector3(0f, airAltitude, targetDistance + 4f));
                    if (a != null) _immortalEnemies.Add(a);
                }
            }

            // Showcase row: one of EVERY enemy, on its own row BEHIND the towers and
            // close enough (showcaseGap) to be in weapon range, so these enemies
            // actively fire back rather than standing inert. It sits FARTHER from each
            // tower than the front target pack, so towers keep facing forward and the
            // showcase peppers them from behind. Wider spacing than the towers so the
            // large models never overlap each other.
            float showSpacing = towerSpacing * 1.4f;
            float showWidth = (enemies.Length - 1) * showSpacing;
            for (int i = 0; i < enemies.Length; i++)
            {
                var entry = enemies[i];
                if (entry.prefab == null) continue;
                float x = -showWidth * 0.5f + i * showSpacing;
                bool isAir = entry.definition != null && entry.definition.isAir;
                var e = SpawnEnemy(entry.prefab, entry,
                    new Vector3(x, isAir ? airAltitude : 0f, -showcaseGap));
                if (e != null) _immortalEnemies.Add(e);
            }

            Debug.Log($"[Arena] GRID: {towerPrefabs.Length} towers (each with a ground+air target) + " +
                      $"a showcase row of {enemies.Length} enemies, all within range and returning fire.");

            // Pull back and centre the orbit camera to take in the whole grid.
            FocusCamera(new Vector3(0f, 2f, 0f), 46f);

            StartCoroutine(ReportCombat());
        }

        /// <summary>
        /// A few seconds after building, log an authoritative combat report: how many
        /// towers registered in Tower.Live and each one's health (below max = it is
        /// taking enemy return fire). Confirms both directions of combat are live.
        /// </summary>
        private IEnumerator ReportCombat()
        {
            yield return new WaitForSeconds(3f);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Arena] Combat report — Tower.Live has {Tower.Live.Count} registered towers:");
            foreach (var t in Tower.Live)
            {
                if (t == null) continue;
                var h = t.GetComponent<TowerHealth>();
                string hp = h != null
                    ? $"{h.CurrentHealth:0}/{h.MaxHealth:0} ({(h.HealthFraction < 1f ? "TAKING FIRE" : "full")})"
                    : "no TowerHealth";
                sb.AppendLine($"    {t.name}: {hp}");
            }
            Debug.Log(sb.ToString());
        }

        private void BuildDuel()
        {
            if (!Validate()) return;

            _duelTowerIndex = Mathf.Clamp(_duelTowerIndex, 0, towerPrefabs.Length - 1);
            _duelEnemyIndex = Mathf.Clamp(_duelEnemyIndex, 0, enemies.Length - 1);

            var towerPrefab = towerPrefabs[_duelTowerIndex];
            var entry = enemies[_duelEnemyIndex];
            if (towerPrefab == null || entry.prefab == null) return;

            Vector3 towerPos = new Vector3(0f, 0f, -duelDistance * 0.5f);
            SpawnTower(towerPrefab, towerPos);

            bool isAir = entry.definition != null && entry.definition.isAir;
            Vector3 enemyPos = new Vector3(0f, isAir ? airAltitude : 0f, duelDistance * 0.5f);
            var enemy = SpawnEnemy(entry.prefab, entry, enemyPos);
            if (enemy != null)
                _immortalEnemies.Add(enemy);

            // Force the matchup: the tower manual-targets THIS enemy (overrides
            // automatic acquisition), and the enemy's own weapon auto-fires at the
            // only tower present.
            var tower = _spawned.Count > 0 ? _spawned[0].GetComponent<Tower>() : null;
            var targeting = tower != null ? tower.GetComponent<TowerTargeting>() : null;
            if (targeting != null && enemy != null)
                targeting.ManualTarget = enemy;

            // Frame the orbit camera on the midpoint of the duel so a fresh pairing
            // is centred; you can still orbit/pan/zoom freely from there.
            FocusCamera(new Vector3(0f, isAir ? airAltitude * 0.5f : 1f, 0f), 16f);

            Debug.Log($"[Arena] DUEL: {towerPrefab.name} vs {entry.prefab.name}. " +
                      "Tower manual-targets the enemy; enemy returns fire. Both kept alive.");
        }

        /// <summary>Re-centre and optionally re-distance the testbed orbit camera.</summary>
        private void FocusCamera(Vector3 worldPoint, float distance = -1f)
        {
            var orbit = Object.FindFirstObjectByType<TestbedOrbitCamera>();
            if (orbit == null) return;
            orbit.SetFocus(worldPoint);
            if (distance > 0f)
                orbit.SetDistance(distance);
        }

        private bool Validate()
        {
            if (towerPrefabs == null || towerPrefabs.Length == 0)
            {
                Debug.LogError("[Arena] No tower prefabs assigned.");
                return false;
            }
            if (enemies == null || enemies.Length == 0)
            {
                Debug.LogError("[Arena] No enemy prefabs assigned.");
                return false;
            }
            return true;
        }

        private Enemy SpawnEnemy(GameObject prefab, EnemyEntry entry, Vector3 pos)
        {
            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, 180f, 0f), transform);
            go.name = prefab.name;
            _spawned.Add(go);

            var mover = go.GetComponent<EnemyMover>();
            if (mover != null)
                DestroyImmediate(mover);

            var enemy = go.GetComponent<Enemy>();
            if (enemy == null)
                return null;

            if (entry.definition != null)
                enemy.Configure(entry.definition);
            enemy.SetMaxHealth(Mathf.Max(enemy.MaxHealth, 800f));

            // Enemy prefabs now SHIP with EnemyAim (baked by Tools ▸ COREHOLD ▸ Scene
            // Setup ▸ Bake EnemyAim), just like towers ship with TurretAim. This is
            // only a safety net for a hand-placed test enemy whose prefab predates the
            // bake — it turns the yaw ring + gun pitch pivots toward the target (or
            // yaws the body on a gun-in-hand humanoid), never fighting the (stripped)
            // mover here.
            if (go.GetComponent<EnemyAim>() == null)
                go.AddComponent<EnemyAim>();

            return enemy;
        }

        private void SpawnTower(GameObject prefab, Vector3 pos)
        {
            var go = Instantiate(prefab, pos, Quaternion.identity, transform);
            go.name = prefab.name;
            _spawned.Add(go);

            var weapon = go.GetComponent<TowerWeapon>();
            var def = weapon != null ? weapon.Definition : null;

            // The shipped tower prefab is a bare chassis carrying only a TowerWeapon;
            // the Tower component (registry + health) is normally added at build time
            // by the hardpoint. Mirror that here so the tower registers in Tower.Live
            // (enemies find it to return fire) and gets a TowerHealth + health bar.
            var tower = go.GetComponent<Tower>();
            if (tower == null)
                tower = go.AddComponent<Tower>();

            if (def != null)
            {
                int topTier = def.tiers != null ? def.tiers.Length - 1 : 0;
                tower.Build(def, Mathf.Max(0, topTier));
            }
            else if (weapon != null && weapon.Definition != null && weapon.Definition.tiers != null)
            {
                weapon.SetTier(weapon.Definition.tiers.Length - 1);
            }

            // Track the health so we can keep the tower alive — otherwise enemy return
            // fire destroys the mortal (220 HP) towers within seconds and the testbed
            // empties. Kept alive, they visibly take hits (health bar dips) but persist.
            var th = go.GetComponent<TowerHealth>();
            if (th != null)
                _immortalTowers.Add(th);
        }

        private void Update()
        {
            if (!immortal)
                return;

            for (int i = 0; i < _immortalEnemies.Count; i++)
            {
                var e = _immortalEnemies[i];
                if (e == null) continue;
                if (!e.IsAlive)
                    Respawn(e);
                else if (e.CurrentHealth < e.MaxHealth * 0.5f)
                    e.SetMaxHealth(e.MaxHealth);
            }

            // Keep towers alive so the field of towers persists under enemy return
            // fire. Heal once they dip below half so the health bar still visibly
            // reacts to incoming hits.
            for (int i = 0; i < _immortalTowers.Count; i++)
            {
                var th = _immortalTowers[i];
                if (th != null && th.HealthFraction < 0.5f)
                    th.Heal();
            }
        }

        private void Respawn(Enemy e)
        {
            if (e == null) return;
            e.gameObject.SetActive(false);
            e.gameObject.SetActive(true);
        }

        private IEnumerator ReapLoop()
        {
            var wait = new WaitForSeconds(reapInterval);
            int idx = 0;
            while (true)
            {
                yield return wait;
                if (_reapable.Count == 0) continue;

                for (int n = 0; n < _reapable.Count; n++)
                {
                    idx = (idx + 1) % _reapable.Count;
                    var e = _reapable[idx];
                    if (e != null && e.IsAlive && e.gameObject.activeSelf)
                    {
                        e.TakeDamage(e.CurrentHealth + 1f);
                        break;
                    }
                }
                for (int n = 0; n < _reapable.Count; n++)
                {
                    var e = _reapable[n];
                    if (e != null && !e.IsAlive)
                        Respawn(e);
                }
            }
        }

        // ------------------------- On-screen control panel -----------------------

        private void OnGUI()
        {
            const float pad = 10f;
            float w = 260f;
            GUILayout.BeginArea(new Rect(pad, pad, w, Screen.height - pad * 2));
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label("<b>COMBAT VFX ARENA</b>", RichLabel());

            // Mode switch.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode:", GUILayout.Width(44f));
            if (ModeButton("Grid", Mode.Grid)) SwitchMode(Mode.Grid);
            if (ModeButton("Duel", Mode.Duel)) SwitchMode(Mode.Duel);
            GUILayout.EndHorizontal();

            if (_mode == Mode.Duel)
                DrawDuelControls(w);
            else
                GUILayout.Label("Every tower faces a ground + air target.\nShowcase row (back) returns fire.\nSwitch to Duel to isolate a matchup.",
                    WrapLabel());

            GUILayout.FlexibleSpace();
            GUILayout.Label("Enemies auto-fire at towers in range.", WrapLabel());
            GUILayout.Space(4f);
            GUILayout.Label("<b>Camera</b>", RichLabel());
            GUILayout.Label("Drag: orbit  •  Middle-drag: pan\nWheel: zoom  •  WASD/QE: move  •  R: reset",
                WrapLabel());
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawDuelControls(float panelWidth)
        {
            GUILayout.Space(6f);
            GUILayout.Label("<b>Pick the matchup</b>", RichLabel());

            // Tower selector.
            GUILayout.Label("Tower:", MiniLabel());
            if (GUILayout.Button(_towerNames[Mathf.Clamp(_duelTowerIndex, 0, _towerNames.Length - 1)]))
                _showTowerList = !_showTowerList;
            if (_showTowerList)
            {
                _towerScroll = GUILayout.BeginScrollView(_towerScroll, GUILayout.Height(140f));
                for (int i = 0; i < _towerNames.Length; i++)
                    if (GUILayout.Button(_towerNames[i]))
                    {
                        _duelTowerIndex = i;
                        _showTowerList = false;
                        Rebuild();
                    }
                GUILayout.EndScrollView();
            }

            // Enemy selector.
            GUILayout.Label("Enemy:", MiniLabel());
            if (GUILayout.Button(_enemyNames[Mathf.Clamp(_duelEnemyIndex, 0, _enemyNames.Length - 1)]))
                _showEnemyList = !_showEnemyList;
            if (_showEnemyList)
            {
                _enemyScroll = GUILayout.BeginScrollView(_enemyScroll, GUILayout.Height(140f));
                for (int i = 0; i < _enemyNames.Length; i++)
                    if (GUILayout.Button(_enemyNames[i]))
                    {
                        _duelEnemyIndex = i;
                        _showEnemyList = false;
                        Rebuild();
                    }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Restart duel"))
                Rebuild();
        }

        private bool ModeButton(string label, Mode mode)
        {
            var prev = GUI.color;
            GUI.color = _mode == mode ? new Color(0.4f, 0.9f, 1f) : Color.white;
            bool clicked = GUILayout.Button(label);
            GUI.color = prev;
            return clicked;
        }

        private void SwitchMode(Mode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            _showTowerList = _showEnemyList = false;
            Rebuild();
        }

        private static GUIStyle _rich, _wrap, _mini;
        private static GUIStyle RichLabel() => _rich ??= new GUIStyle(GUI.skin.label) { richText = true };
        private static GUIStyle WrapLabel() => _wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
        private static GUIStyle MiniLabel() => _mini ??= new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };

        // ------------------------- Helpers ---------------------------------------

        private EnemyEntry EntryFor(GameObject prefab)
        {
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i].prefab == prefab)
                    return enemies[i];
            return new EnemyEntry { prefab = prefab };
        }

        private GameObject FindEnemy(bool air)
        {
            if (enemies == null) return null;
            for (int i = 0; i < enemies.Length; i++)
            {
                var e = enemies[i];
                if (e.prefab == null || e.definition == null) continue;
                if (e.definition.isAir == air)
                    return e.prefab;
            }
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i].prefab != null)
                    return enemies[i].prefab;
            return null;
        }
    }
}
