using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using Corehold.Towers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Real uGUI build menu (GDD §9.1). Opens when an EMPTY hardpoint is tapped and
    /// shows the five turrets — icon, name, cost and a one-word role tag — with
    /// unaffordable entries desaturated and non-interactive. The selected turret's
    /// range previews as a ground ring. Tapping elsewhere dismisses.
    ///
    /// The bottom sheet is the default layout on all viewports; the player can
    /// switch to the radial pad menu (R-UI-1, <see cref="RadialBuildMenu"/>) in
    /// Settings — same entries, same rules, grown around the pad instead.
    /// Listens to <see cref="InputRouter"/> for pad taps; when a pad is occupied
    /// it defers to the <see cref="TowerPanel"/> instead.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildMenu : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private InputRouter router;
        [SerializeField] private TowerPanel towerPanel;
        [SerializeField] private UITheme theme;

        [Header("Layout")]
        [SerializeField] private GameObject root;             // the panel shown/hidden
        [SerializeField] private RectTransform entriesRow;    // parent of the turret entries
        [SerializeField] private GameObject entryTemplate;    // has Icon, Name, Cost, Role, Button

        [Header("Range preview")]
        [SerializeField] private RangeRing rangeRing;

        [Header("Radial menu (R-UI-1) — opt-in via Settings, sheet is default")]
        [Tooltip("Ring radius in canvas units, pad centre to node centres.")]
        [SerializeField] private float radialRadius = 120f;       // [TUNE]
        [Tooltip("Diameter of each radial node in canvas units.")]
        [SerializeField] private float radialNodeSize = 76f;      // [TUNE]
        [Tooltip("Grow-out animation length in unscaled seconds (~120 ms feels right).")]
        [SerializeField] private float radialGrowSeconds = 0.12f; // [TUNE]

        [Header("Roster rail (R-UI-2) — always-visible turret chips")]
        [Tooltip("Show the persistent top-edge roster rail. Chips arm on tap (pads glow harder), build on pad tap or drag-to-pad.")]
        [SerializeField] private bool rosterRailEnabled = true;   // [TUNE]
        [Tooltip("Chip width in canvas units (height is 1.2×).")]
        [SerializeField] private float railChipSize = 76f;        // [TUNE]
        [Tooltip("Gap from the top screen edge to the rail, in canvas units.")]
        [SerializeField] private float railTopInset = 8f;         // [TUNE]

        [Header("PANIC auto-deploy (rail)")]
        [Tooltip("Times per level the PANIC button may fire. Each press spends the PLAYER'S salvage on certified turrets — assistance, not free power.")]
        [SerializeField] private int panicUsesPerLevel = 3;       // [TUNE]
        [Tooltip("Most turrets one PANIC press will place (fewer if salvage or pads run out).")]
        [SerializeField] private int panicMaxPerPress = 3;        // [TUNE]

        private readonly List<GameObject> _entries = new List<GameObject>();
        private TowerHardpoint _selected;
        private TowerDefinition[] _turrets;
        private RadialBuildMenu _radial;
        private RosterRail _rail;
        private WaveManager _waveManager;
        private int _panicLeft;

        private bool _subscribed;

        private void Awake()
        {
            if (theme == null) theme = UITheme.Instance;
            _turrets = theme != null ? theme.turrets : null;

            // Per-level roster gating (R-UI-2): a LevelDefinition with an
            // authored roster narrows EVERY build surface — sheet, radial and
            // rail — through this one array. Empty/absent = the full roster.
            _waveManager = FindFirstObjectByType<WaveManager>();
            var levelRoster = _waveManager != null ? _waveManager.LevelRoster : null;
            if (levelRoster != null && levelRoster.Length > 0)
                _turrets = levelRoster;

            _panicLeft = Mathf.Max(0, panicUsesPerLevel);

            Hide();
        }

        private void Start()
        {
            // The rail outlives menus — it is standing chrome, built once here
            // (Start, so UITheme.Instance and GameManager exist).
            if (rosterRailEnabled && _rail == null)
            {
                Canvas canvas = root != null ? root.GetComponentInParent<Canvas>(true)
                                             : GetComponentInParent<Canvas>(true);
                if (canvas != null)
                    _rail = RosterRail.Create(this, theme, canvas.rootCanvas.transform,
                                              _turrets, railChipSize, railTopInset);
            }
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            // The InputRouter is created at runtime by GameFlow. Depending on script
            // execution order it may not exist when this component's OnEnable runs,
            // so keep trying until we have wired the tap events. Without this, tapping
            // a hardpoint raised an event no one listened to — the build menu never
            // opened and no turrets could ever be placed.
            if (!_subscribed)
                TrySubscribe();
        }

        private void TrySubscribe()
        {
            if (_subscribed)
                return;
            if (router == null)
                router = FindFirstObjectByType<InputRouter>();
            if (router == null)
                return;

            router.OnHardpointTapped += HandleHardpointTapped;
            router.OnEmptyTapped += HandleEmptyTapped;
            router.OnPadDragBegin += HandlePadDragBegin;
            router.OnPadDragUpdate += HandlePadDragUpdate;
            router.OnPadDragEnd += HandlePadDragEnd;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || router == null)
            {
                _subscribed = false;
                return;
            }
            router.OnHardpointTapped -= HandleHardpointTapped;
            router.OnEmptyTapped -= HandleEmptyTapped;
            router.OnPadDragBegin -= HandlePadDragBegin;
            router.OnPadDragUpdate -= HandlePadDragUpdate;
            router.OnPadDragEnd -= HandlePadDragEnd;
            _subscribed = false;
        }

        // ----- Drag a tower between pads (user call — relocation's fast gesture) -----
        //
        // The press already opened the tower panel (taps fire on press, unchanged);
        // the drag hides it and arms the SAME relocation the panel's MOVE/WALK
        // button uses — identical rules and costs, mid-wave drops still walk.

        private void HandlePadDragBegin(TowerHardpoint pad)
        {
            if (pad == null || !pad.IsOccupied)
                return;
            if (_rail != null) _rail.Disarm();
            if (towerPanel != null) towerPanel.Hide();
            Hide();
            Corehold.Towers.TurretRelocation.Begin(pad); // free pads glow while pending
        }

        private void HandlePadDragUpdate(Vector2 screenPos)
        {
            if (!Corehold.Towers.TurretRelocation.Pending || router == null)
                return;
            var pad = router.PadAt(screenPos);
            var source = Corehold.Towers.TurretRelocation.Source;
            if (pad != null && !pad.IsOccupied && !pad.IsReserved && pad != source &&
                rangeRing != null && source != null && source.Occupant != null)
            {
                rangeRing.Show(pad.transform.position, source.Occupant.EffectiveRange);
            }
            else if (rangeRing != null)
            {
                rangeRing.Hide();
            }
        }

        private void HandlePadDragEnd(Vector2 screenPos)
        {
            if (rangeRing != null) rangeRing.Hide();
            if (!Corehold.Towers.TurretRelocation.Pending || router == null)
                return;
            var pad = router.PadAt(screenPos);
            if (pad != null && Corehold.Towers.TurretRelocation.TryCompleteAt(pad))
            {
                if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
                return;
            }
            Corehold.Towers.TurretRelocation.Cancel();
        }

        private void HandleHardpointTapped(TowerHardpoint pad)
        {
            if (pad == null) return;

            // A pending relocation claims the next FREE-pad tap (M-c): the move
            // completes instead of the build menu opening. Tapping an occupied
            // pad abandons the move and behaves normally — switching attention
            // should never leave a mode armed.
            if (Corehold.Towers.TurretRelocation.Pending)
            {
                if (!pad.IsOccupied && !pad.IsReserved &&
                    Corehold.Towers.TurretRelocation.TryCompleteAt(pad))
                {
                    if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
                    return;
                }
                Corehold.Towers.TurretRelocation.Cancel();
            }

            if (pad.IsOccupied)
            {
                // Occupied pads open the tower panel, not the build menu — and
                // switching attention disarms a pending rail chip (R-UI-2).
                if (_rail != null) _rail.Disarm();
                Hide();
                if (towerPanel != null) towerPanel.Open(pad);
                return;
            }

            if (pad.IsReserved)
                return; // a turret is already walking here

            // An armed rail chip claims the free-pad tap: build there directly,
            // no menu round-trip (R-UI-2).
            if (_rail != null && _rail.TryBuildArmed(pad))
                return;

            if (towerPanel != null) towerPanel.Hide();
            Open(pad);
        }

        private void HandleEmptyTapped()
        {
            Corehold.Towers.TurretRelocation.Cancel();
            if (_rail != null) _rail.Disarm();
            Hide();
        }

        public void Open(TowerHardpoint pad)
        {
            if (SaveData.RadialBuildMenu)
            {
                EnsureRadial();
                if (_radial != null)
                {
                    // Tapping the pad the ring is already open on closes it.
                    if (_radial.IsOpenFor(pad)) { Hide(); return; }
                    _selected = pad;
                    if (root != null) root.SetActive(false);
                    if (_radial.Open(pad, _turrets, radialRadius, radialNodeSize, radialGrowSeconds))
                        return;
                }
                // Radial unavailable (no canvas/camera): fall through to the sheet.
            }

            _selected = pad;
            if (root != null) root.SetActive(true);
            BuildEntries();
        }

        public void Hide()
        {
            _selected = null;
            if (root != null) root.SetActive(false);
            if (rangeRing != null) rangeRing.Hide();
            if (_radial != null) _radial.Close();
        }

        private void EnsureRadial()
        {
            if (_radial != null)
                return;
            Canvas canvas = root != null ? root.GetComponentInParent<Canvas>(true)
                                         : GetComponentInParent<Canvas>(true);
            if (canvas == null)
                return;
            _radial = RadialBuildMenu.Create(this, theme, canvas.rootCanvas.transform);
        }

        /// <summary>Second tap on the selected radial node (R-UI-1): build it.</summary>
        public void RadialConfirm(TowerDefinition def) => OnPick(def);

        /// <summary>Rail build (R-UI-2): place <paramref name="def"/> on
        /// <paramref name="pad"/> directly — tap-armed or dragged. Closes any
        /// open menu on success so the field stays clear.</summary>
        public bool RailBuild(TowerDefinition def, TowerHardpoint pad)
        {
            if (def == null || pad == null)
                return false;
            if (!pad.TryBuild(def))
                return false;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Hide();
            return true;
        }

        /// <summary>Range preview at an explicit pad (rail drag hover), independent
        /// of the menu's selected pad.</summary>
        public void PreviewRangeAt(TowerDefinition def, TowerHardpoint pad)
        {
            if (rangeRing == null || pad == null || def == null || def.tiers == null || def.tiers.Length == 0)
                return;
            rangeRing.Show(pad.transform.position, def.tiers[0].range);
        }

        // ----- PANIC auto-deploy (rail button; the AI advisor's first field act) -----

        /// <summary>PANIC presses left this level.</summary>
        public int PanicRemaining => _panicLeft;

        /// <summary>
        /// One PANIC press: greedily spend the player's OWN salvage on the best
        /// counters to the oncoming wave — damage-type multipliers against its
        /// armour mix, air handled by the targeting gates — placed on free pads
        /// nearest the Core (leaks get caught first). Places at most
        /// panicMaxPerPress turrets; a press that placed nothing is not consumed.
        /// Pure assistance: certified turrets, the player's economy, no new power.
        /// </summary>
        public bool PanicDeploy()
        {
            if (_panicLeft <= 0 || _turrets == null)
                return false;
            var gm = GameManager.Instance;
            if (gm == null)
                return false;

            WaveDefinition threat = null;
            if (_waveManager != null)
            {
                threat = _waveManager.NextWave;
                if (threat == null)
                    threat = _waveManager.PeekWave(-1); // final wave live: counter what is here
            }

            var pads = new List<TowerHardpoint>();
            foreach (var p in FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
                if (p != null && !p.IsOccupied && !p.IsReserved)
                    pads.Add(p);
            if (pads.Count == 0)
                return false;

            Transform core = _waveManager != null ? _waveManager.CoreTarget : null;
            if (core != null)
            {
                Vector3 c = core.position;
                pads.Sort((x, y) => (x.transform.position - c).sqrMagnitude
                    .CompareTo((y.transform.position - c).sqrMagnitude));
            }

            int placed = 0;
            for (int i = 0; i < Mathf.Max(1, panicMaxPerPress) && pads.Count > 0; i++)
            {
                TowerDefinition pick = BestCounter(threat, gm.Salvage);
                if (pick == null)
                    break;
                if (!RailBuild(pick, pads[0]))
                    break;
                pads.RemoveAt(0);
                placed++;
            }

            if (placed == 0)
                return false;
            _panicLeft--;
            return true;
        }

        /// <summary>Best affordable counter to the threat wave: per-group count ×
        /// damage-type multiplier vs its armour (air gated by targeting), scaled by
        /// tier-1 DPS per salvage. Support relays never answer a panic.</summary>
        private TowerDefinition BestCounter(WaveDefinition threat, int salvage)
        {
            var table = theme != null ? theme.damageTable : null;
            TowerDefinition best = null;
            float bestValue = 0f;

            foreach (var def in _turrets)
            {
                if (def == null || def.basePrefab == null || def.tiers == null || def.tiers.Length == 0)
                    continue;
                var t0 = def.tiers[0];
                if (t0.cost > salvage || t0.TotalDps <= 0f)
                    continue;

                float power = 0f;
                if (threat != null && threat.groups != null)
                {
                    foreach (var g in threat.groups)
                    {
                        if (g.enemy == null || g.count <= 0)
                            continue;
                        bool air = g.enemy.isAir;
                        if (air && !def.canTargetAir) continue;
                        if (!air && def.targetAirOnly) continue;
                        float mult = table != null ? table.Multiplier(def.damageType, g.enemy.armourType) : 1f;
                        power += g.count * mult * (air ? 1.15f : 1f); // AA scarcity nudge
                    }
                }
                else
                {
                    power = 1f; // no readable threat: fall back to raw efficiency
                }

                float value = power * t0.TotalDps / Mathf.Max(1, t0.cost);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = def;
                }
            }
            return best;
        }

        private void BuildEntries()
        {
            if (entriesRow == null || entryTemplate == null || _turrets == null)
                return;

            var gm = GameManager.Instance;
            entryTemplate.SetActive(false);

            for (int i = 0; i < _turrets.Length; i++)
            {
                TowerDefinition def = _turrets[i];
                GameObject cell = GetEntry(i);
                cell.SetActive(def != null);
                if (def == null) continue;

                int cost = def.tiers != null && def.tiers.Length > 0 ? def.tiers[0].cost : 0;
                bool affordable = gm != null && gm.Salvage >= cost;
                // Roster entries whose chassis prefab has not been authored yet
                // exist as data but cannot be built — show WIP, never a dead click.
                bool buildable = def.basePrefab != null;

                // The icon lives on a dark inset plate (Icon under IconPlate); fall
                // back to a flat "Icon" for older templates.
                var iconTf = cell.transform.Find("IconPlate/Icon") ?? cell.transform.Find("Icon");
                var icon = iconTf != null ? iconTf.GetComponent<Image>() : null;
                var nameTxt = cell.transform.Find("Name")?.GetComponent<TMP_Text>();
                var costTxt = cell.transform.Find("Cost")?.GetComponent<TMP_Text>();
                var roleTxt = cell.transform.Find("Role")?.GetComponent<TMP_Text>();
                var btn = cell.GetComponent<Button>();

                if (icon != null)
                {
                    icon.sprite = def.icon;
                    icon.enabled = def.icon != null;
                    icon.color = affordable ? Color.white : new Color(0.5f, 0.5f, 0.5f, 0.6f);
                }
                if (nameTxt != null) nameTxt.text = def.displayName;
                if (costTxt != null)
                {
                    costTxt.text = cost.ToString();
                    costTxt.color = affordable ? (theme != null ? theme.cyan : Color.cyan) : (theme != null ? theme.danger : Color.red);
                }
                if (roleTxt != null) roleTxt.text = buildable ? RoleTag(def) : "WIP";

                if (btn != null)
                {
                    btn.interactable = affordable && buildable;
                    btn.onClick.RemoveAllListeners();
                    var captured = def;
                    btn.onClick.AddListener(() => OnPick(captured));

                    // Hover range preview via the EventTrigger set up on the cell.
                    var hover = cell.GetComponent<BuildEntryHover>();
                    if (hover != null) hover.Setup(this, def);
                }
            }
        }

        private GameObject GetEntry(int index)
        {
            while (_entries.Count <= index)
            {
                var c = Instantiate(entryTemplate, entriesRow);
                c.name = $"Entry_{_entries.Count}";
                if (c.GetComponent<BuildEntryHover>() == null)
                    c.AddComponent<BuildEntryHover>();
                _entries.Add(c);
            }
            return _entries[index];
        }

        private void OnPick(TowerDefinition def)
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (_selected == null || def == null) return;
            if (_selected.TryBuild(def))
                Hide();
        }

        /// <summary>Preview a turret's tier-1 range ring at the selected pad (hover).</summary>
        public void PreviewRange(TowerDefinition def)
        {
            if (rangeRing == null || _selected == null || def == null || def.tiers == null || def.tiers.Length == 0)
                return;
            rangeRing.Show(_selected.transform.position, def.tiers[0].range);
        }

        public void ClearRangePreview()
        {
            if (rangeRing != null) rangeRing.Hide();
        }

        /// <summary>One-word role tag (GDD §9.1) — public: the field guide's
        /// turret cards (R-UI-7) reuse the same vocabulary.</summary>
        public static string RoleTag(TowerDefinition def)
        {
            // A one-word role tag (GDD §9.1). Derived from the turret's identity.
            // Ids must match the ASSETS' ids exactly — four of these once used
            // shorthand ("missile", "arcnode"…) that matched nothing, so those
            // turrets silently fell through to the generic heuristic below.
            switch (def.id)
            {
                case "autocannon": return "WORKHORSE";
                case "missile_battery": return "SPLASH";
                case "arc_node": return "CHAIN";
                case "siege_mortar": return "SIEGE";
                case "scan_relay": return "SUPPORT";
                case "floodlight": return "LIGHT";
            }
            if (def.damageType == DamageType.Explosive) return "SPLASH";
            if (def.tiers != null && def.tiers.Length > 0 && def.tiers[0].auraRadius > 0f) return "SUPPORT";
            return def.canTargetAir ? "AA" : "DAMAGE";
        }
    }
}
