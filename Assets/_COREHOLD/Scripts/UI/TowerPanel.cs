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
    /// Real uGUI tower panel (GDD §9.1, §7.1). Opens on tapping an OCCUPIED
    /// hardpoint and shows:
    ///   • current tier, damage type, DPS and range,
    ///   • next tier's cost and stat deltas (or "MAX"),
    ///   • sell value (60% of invested),
    ///   • a targeting-priority selector (First / Closest / Strongest),
    ///   • the 3×3 damage-vs-armour counter grid with the current turret's row
    ///     highlighted (GDD §7.1).
    ///
    /// Docks to the screen side OPPOSITE the inspected tower (R-UI-3) so the
    /// panel never covers the tower or its range ring; wide bottom-sheet roots
    /// are detected and left alone.
    ///
    /// Rebuilt each time it opens and after every upgrade; nothing polls in Update.
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerPanel : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private UITheme theme;

        [Header("Layout")]
        [SerializeField] private GameObject root;
        [Tooltip("Dock the panel to the screen side opposite the selected tower so it never covers it (R-UI-3). Roots authored as wide bottom sheets are auto-detected and never moved.")]
        [SerializeField] private bool dockOppositeTower = true; // [TUNE]
        [Tooltip("Horizontal inset from the screen edge when docked, in canvas units.")]
        [SerializeField] private float dockInset = 24f;         // [TUNE]

        [Header("Header")]
        [SerializeField] private TMP_Text nameLabel;
        [SerializeField] private TMP_Text tierLabel;
        [SerializeField] private TMP_Text damageTypeLabel;

        [Header("Stats")]
        [SerializeField] private TMP_Text dpsLabel;
        [SerializeField] private TMP_Text rangeLabel;
        [SerializeField] private TMP_Text nextLabel; // next-tier cost + deltas

        [Header("Actions")]
        [SerializeField] private Button upgradeButton;
        [SerializeField] private TMP_Text upgradeLabel;
        [SerializeField] private Button sellButton;
        [SerializeField] private TMP_Text sellLabel;
        [SerializeField] private Button closeButton;

        [Header("Mid-term features (M-a/M-c)")]
        [SerializeField] private Button moveButton;
        [SerializeField] private TMP_Text moveLabel;
        [SerializeField] private Button camButton;
        [SerializeField] private Button controlButton;

        [Header("Targeting priority")]
        [SerializeField] private Button priorityFirst;
        [SerializeField] private Button priorityClosest;
        [SerializeField] private Button priorityStrongest;

        [Header("Counter grid (GDD §7.1)")]
        [Tooltip("Nine cells (row-major: Kinetic, Energy, Explosive × Unarmoured, Plated, Shielded).")]
        [SerializeField] private TMP_Text[] gridCells = new TMP_Text[9];
        [Tooltip("Three row-highlight images, one per damage type row.")]
        [SerializeField] private Image[] rowHighlights = new Image[3];
        [SerializeField] private RangeRing rangeRing;

        private TowerHardpoint _pad;

        private bool _dockCaptured;
        private Vector2 _dockOrigAnchorMin, _dockOrigAnchorMax, _dockOrigPivot, _dockOrigAnchoredPos;

        private void Awake()
        {
            if (theme == null) theme = UITheme.Instance;
            Hide();
        }

        private void OnEnable()
        {
            if (upgradeButton != null) upgradeButton.onClick.AddListener(OnUpgrade);
            if (sellButton != null) sellButton.onClick.AddListener(OnSell);
            if (closeButton != null) closeButton.onClick.AddListener(Hide);
            if (moveButton != null) moveButton.onClick.AddListener(OnMove);
            if (camButton != null) camButton.onClick.AddListener(OnCam);
            if (controlButton != null) controlButton.onClick.AddListener(OnControl);
            if (priorityFirst != null) priorityFirst.onClick.AddListener(() => SetPriority(TargetingPriority.First));
            if (priorityClosest != null) priorityClosest.onClick.AddListener(() => SetPriority(TargetingPriority.Closest));
            if (priorityStrongest != null) priorityStrongest.onClick.AddListener(() => SetPriority(TargetingPriority.Strongest));
        }

        private void OnDisable()
        {
            if (upgradeButton != null) upgradeButton.onClick.RemoveAllListeners();
            if (sellButton != null) sellButton.onClick.RemoveAllListeners();
            if (closeButton != null) closeButton.onClick.RemoveAllListeners();
            if (moveButton != null) moveButton.onClick.RemoveAllListeners();
            if (camButton != null) camButton.onClick.RemoveAllListeners();
            if (controlButton != null) controlButton.onClick.RemoveAllListeners();
            if (priorityFirst != null) priorityFirst.onClick.RemoveAllListeners();
            if (priorityClosest != null) priorityClosest.onClick.RemoveAllListeners();
            if (priorityStrongest != null) priorityStrongest.onClick.RemoveAllListeners();
        }

        public void Open(TowerHardpoint pad)
        {
            if (pad == null || !pad.IsOccupied)
                return;
            _pad = pad;
            if (root != null) root.SetActive(true);
            DockOppositeTo(pad);
            Refresh();

            // Show the current range as a ground ring while the panel is open.
            if (rangeRing != null && _pad.Occupant != null)
                rangeRing.Show(_pad.transform.position, _pad.Occupant.EffectiveRange);

            TurretCamera.NotifySelected(pad);
        }

        public void Hide()
        {
            _pad = null;
            if (root != null) root.SetActive(false);
            if (rangeRing != null) rangeRing.Hide();
            TurretCamera.NotifyDeselected();
        }

        // ----- R-UI-3: dock opposite the inspected tower -----

        /// <summary>
        /// Moves the panel root to the screen side opposite <paramref name="pad"/>
        /// so the panel never covers the tower or its range ring (the BTD6
        /// lesson — its community's chief complaint is menus over the field).
        /// Horizontally stretched or >55%-width roots are authored bottom
        /// sheets: their layout is restored and left untouched.
        /// </summary>
        private void DockOppositeTo(TowerHardpoint pad)
        {
            if (root == null || pad == null) return;
            var rootRect = root.transform as RectTransform;
            if (rootRect == null) return;

            if (!_dockCaptured)
            {
                _dockOrigAnchorMin = rootRect.anchorMin;
                _dockOrigAnchorMax = rootRect.anchorMax;
                _dockOrigPivot = rootRect.pivot;
                _dockOrigAnchoredPos = rootRect.anchoredPosition;
                _dockCaptured = true;
            }

            var parentRect = rootRect.parent as RectTransform;
            Camera cam = Camera.main;
            bool stretched = !Mathf.Approximately(rootRect.anchorMin.x, rootRect.anchorMax.x);
            bool tooWide = parentRect != null && rootRect.rect.width > parentRect.rect.width * 0.55f;
            if (!dockOppositeTower || parentRect == null || cam == null || stretched || tooWide)
            {
                RestoreDockLayout(rootRect);
                return;
            }

            Vector3 vp = cam.WorldToViewportPoint(pad.transform.position);
            if (vp.z <= 0f)
            {
                RestoreDockLayout(rootRect);
                return;
            }

            bool padOnLeft = vp.x < 0.5f;
            float edgeX = padOnLeft ? 1f : 0f;
            rootRect.anchorMin = new Vector2(edgeX, _dockOrigAnchorMin.y);
            rootRect.anchorMax = new Vector2(edgeX, _dockOrigAnchorMax.y);
            rootRect.pivot = new Vector2(edgeX, _dockOrigPivot.y);
            rootRect.anchoredPosition = new Vector2(padOnLeft ? -dockInset : dockInset, _dockOrigAnchoredPos.y);
        }

        private void RestoreDockLayout(RectTransform rootRect)
        {
            if (!_dockCaptured || rootRect == null) return;
            rootRect.anchorMin = _dockOrigAnchorMin;
            rootRect.anchorMax = _dockOrigAnchorMax;
            rootRect.pivot = _dockOrigPivot;
            rootRect.anchoredPosition = _dockOrigAnchoredPos;
        }

        // ----- Mid-term features (M-a camera/control, M-c relocation) -----

        private void OnMove()
        {
            if (_pad == null) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            TurretRelocation.Begin(_pad);
            Hide(); // the next free-pad tap completes the move; empty ground cancels
        }

        private void OnCam()
        {
            if (_pad == null) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            TurretCamera.Toggle(_pad);
        }

        private void OnControl()
        {
            if (_pad == null) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            var pad = _pad;
            Hide();
            ManualTurretControl.Enter(pad);
        }

        private void Refresh()
        {
            if (_pad == null || !_pad.IsOccupied)
            {
                Hide();
                return;
            }

            Tower tower = _pad.Occupant;
            TowerDefinition def = tower.Definition;
            var gm = GameManager.Instance;

            if (nameLabel != null) nameLabel.text = def != null ? def.displayName : "TURRET";
            if (tierLabel != null) tierLabel.text = $"TIER {tower.TierIndex + 1}";
            if (damageTypeLabel != null)
            {
                if (tower.IsSupportRelay)
                {
                    damageTypeLabel.text = "SUPPORT";
                    damageTypeLabel.color = theme != null ? theme.cyan : Color.cyan;
                }
                else
                {
                    damageTypeLabel.text = def.damageType.ToString().ToUpperInvariant();
                    damageTypeLabel.color = theme != null ? theme.cyan : Color.cyan;
                }
            }

            // DPS = effective damage × effective fire rate. Support relays show —.
            if (dpsLabel != null)
            {
                if (tower.IsSupportRelay)
                    dpsLabel.text = "DPS  —";
                else
                {
                    float dps = tower.EffectiveDps;
                    dpsLabel.text = $"DPS  {dps:0.0}";
                }
            }
            if (rangeLabel != null)
                rangeLabel.text = $"RANGE  {tower.EffectiveRange:0.#} m";

            // Next tier cost + deltas.
            BuildNextTierText(tower, def);

            // Sell value.
            if (sellLabel != null) sellLabel.text = $"SELL  +{_pad.SellValue}";

            // Relocation reads differently by phase: free instant MOVE while
            // building, a weapons-offline WALK mid-wave (M-c).
            if (moveLabel != null)
                moveLabel.text = gm != null && gm.State == GameState.Wave ? "WALK" : "MOVE";

            // Upgrade button.
            if (upgradeButton != null)
            {
                bool canUpgrade = _pad.CanUpgrade;
                int cost = _pad.NextUpgradeCost;
                bool affordable = canUpgrade && gm != null && gm.Salvage >= cost;
                upgradeButton.gameObject.SetActive(canUpgrade);
                upgradeButton.interactable = affordable;
                if (upgradeLabel != null && canUpgrade)
                    upgradeLabel.text = $"UPGRADE  {cost}";
            }

            BuildCounterGrid(tower, def);
            HighlightPriority(tower);
        }

        private void BuildNextTierText(Tower tower, TowerDefinition def)
        {
            if (nextLabel == null) return;

            if (!_pad.CanUpgrade)
            {
                nextLabel.text = "MAX TIER";
                return;
            }

            int next = tower.TierIndex + 1;
            TowerTier cur = def.tiers[tower.TierIndex];
            TowerTier nx = def.tiers[next];

            if (tower.IsSupportRelay)
            {
                nextLabel.text =
                    $"NEXT (T{next + 1}): {nx.cost}\n" +
                    $"rate +{nx.auraFireRateBonus * 100f:0}%  range +{nx.auraRangeBonus * 100f:0}%";
            }
            else
            {
                float curDps = cur.TotalDps;
                float nxDps = nx.TotalDps;
                nextLabel.text =
                    $"NEXT (T{next + 1}): {nx.cost}\n" +
                    $"DPS {curDps:0.0} → {nxDps:0.0}   RNG {cur.range:0.#} → {nx.range:0.#}";
            }
        }

        private void BuildCounterGrid(Tower tower, TowerDefinition def)
        {
            DamageTable table = theme != null ? theme.damageTable : null;
            if (gridCells == null)
                return;

            for (int d = 0; d < 3; d++)
            {
                for (int a = 0; a < 3; a++)
                {
                    int idx = d * 3 + a;
                    if (idx >= gridCells.Length || gridCells[idx] == null) continue;
                    float mul = table != null ? table.Multiplier((DamageType)d, (ArmourType)a) : 1f;
                    gridCells[idx].text = $"×{mul:0.00}";
                }
            }

            // Highlight the current turret's damage-type row (support relays: none).
            int activeRow = tower.IsSupportRelay ? -1 : (int)def.damageType;
            if (rowHighlights != null)
            {
                for (int r = 0; r < rowHighlights.Length; r++)
                {
                    if (rowHighlights[r] == null) continue;
                    bool on = r == activeRow;
                    rowHighlights[r].enabled = on;
                    if (on)
                    {
                        Color c = theme != null ? theme.cyan : Color.cyan;
                        c.a = 0.30f;
                        rowHighlights[r].color = c;
                    }
                }
            }
        }

        private void SetPriority(TargetingPriority p)
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (_pad == null || _pad.Occupant == null) return;
            var targeting = _pad.Occupant.GetComponent<TowerTargeting>();
            if (targeting != null) targeting.Priority = p;
            HighlightPriority(_pad.Occupant);
        }

        private void HighlightPriority(Tower tower)
        {
            var targeting = tower != null ? tower.GetComponent<TowerTargeting>() : null;
            TargetingPriority p = targeting != null ? targeting.Priority : TargetingPriority.First;
            Color on = theme != null ? theme.cyan : Color.cyan;
            Color off = new Color(0.5f, 0.55f, 0.6f, 1f);
            SetPriorityButton(priorityFirst, p == TargetingPriority.First, on, off);
            SetPriorityButton(priorityClosest, p == TargetingPriority.Closest, on, off);
            SetPriorityButton(priorityStrongest, p == TargetingPriority.Strongest, on, off);

            // Support relays cannot target, so hide the selector for them.
            bool showSelector = tower != null && !tower.IsSupportRelay;
            if (priorityFirst != null) priorityFirst.transform.parent.gameObject.SetActive(showSelector);
        }

        private static void SetPriorityButton(Button b, bool selected, Color on, Color off)
        {
            if (b == null) return;
            var label = b.GetComponentInChildren<TMP_Text>();
            if (label != null) label.color = selected ? on : off;
        }

        private void OnUpgrade()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (_pad == null) return;
            if (_pad.TryUpgrade())
            {
                Refresh();
                if (rangeRing != null && _pad.Occupant != null)
                    rangeRing.Show(_pad.transform.position, _pad.Occupant.EffectiveRange);
            }
        }

        private void OnSell()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (_pad == null) return;
            _pad.Sell();
            Hide();
        }
    }
}
