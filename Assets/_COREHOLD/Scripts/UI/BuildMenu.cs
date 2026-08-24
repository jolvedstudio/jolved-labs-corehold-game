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
    /// A single bottom-sheet layout is used on all viewports (the radial variant is
    /// explicitly a nicety on the upside list, GDD §9.1). Listens to
    /// <see cref="InputRouter"/> for pad taps; when a pad is occupied it defers to
    /// the <see cref="TowerPanel"/> instead.
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

        private readonly List<GameObject> _entries = new List<GameObject>();
        private TowerHardpoint _selected;
        private TowerDefinition[] _turrets;

        private bool _subscribed;

        private void Awake()
        {
            if (theme == null) theme = UITheme.Instance;
            _turrets = theme != null ? theme.turrets : null;
            Hide();
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
            _subscribed = false;
        }

        private void HandleHardpointTapped(TowerHardpoint pad)
        {
            if (pad == null) return;

            if (pad.IsOccupied)
            {
                // Occupied pads open the tower panel, not the build menu.
                Hide();
                if (towerPanel != null) towerPanel.Open(pad);
                return;
            }

            if (towerPanel != null) towerPanel.Hide();
            Open(pad);
        }

        private void HandleEmptyTapped()
        {
            Hide();
        }

        public void Open(TowerHardpoint pad)
        {
            _selected = pad;
            if (root != null) root.SetActive(true);
            BuildEntries();
        }

        public void Hide()
        {
            _selected = null;
            if (root != null) root.SetActive(false);
            if (rangeRing != null) rangeRing.Hide();
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

                var icon = cell.transform.Find("Icon")?.GetComponent<Image>();
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

        private static string RoleTag(TowerDefinition def)
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
