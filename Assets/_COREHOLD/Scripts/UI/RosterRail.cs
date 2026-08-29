using System.Collections;
using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using Corehold.Towers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// Persistent roster rail (R-UI-2, the PvZ seed-rail pattern): a slim
    /// always-visible strip at the top of the screen showing THIS LEVEL's
    /// buildable turrets as chips with cost, live-dimmed by affordability —
    /// affordability becomes a glance, never a calculation.
    ///
    /// Interaction: tap a chip to ARM it (every empty pad turns its standing
    /// pulse up via <see cref="TowerHardpoint.BuildAttention"/>), then tap a
    /// pad to build there. Tapping the chip again, empty ground, or an
    /// occupied pad disarms. Dragging a chip onto a pad builds in one motion
    /// (the power-user path) with a live range-ring preview over the hovered
    /// pad.
    ///
    /// Per-level introductions: a chip whose turret has never been offered
    /// before slides in with a NEW tag (the campaign's roster count writes
    /// <see cref="LevelDefinition.roster"/>; sightings persist in
    /// <see cref="SaveData"/> and feed the field guide).
    ///
    /// Created at runtime by <see cref="BuildMenu"/> — no scene edits; themed
    /// via <see cref="UITheme"/>; sized via BuildMenu's [TUNE] knobs.
    /// </summary>
    public class RosterRail : MonoBehaviour
    {
        private BuildMenu _owner;
        private UITheme _theme;
        private RectTransform _rect;
        private RectTransform _canvasRect;
        private Canvas _canvas;
        private GameManager _gm;

        private TowerDefinition _armedDef;
        private RectTransform _ghost;      // drag preview icon
        private int _hardpointMask = ~0;

        private readonly List<Chip> _chips = new List<Chip>();

        private class Chip
        {
            public GameObject go;
            public CanvasGroup group;
            public Image plate;
            public Image icon;
            public TMP_Text cost;
            public GameObject newTag;
            public TowerDefinition def;
            public int tierCost;
        }

        public static RosterRail Create(BuildMenu owner, UITheme theme, Transform canvasRoot,
                                        TowerDefinition[] roster, float chipSize, float topInset)
        {
            var go = new GameObject("RosterRail", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvasRoot, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -Mathf.Max(0f, topInset));
            rt.sizeDelta = Vector2.zero;

            var rail = go.AddComponent<RosterRail>();
            rail._owner = owner;
            rail._theme = theme;
            rail._rect = rt;
            rail._canvas = canvasRoot.GetComponentInParent<Canvas>();
            rail._canvasRect = rail._canvas != null ? (RectTransform)rail._canvas.transform : null;

            int layer = LayerMask.NameToLayer("Hardpoint");
            rail._hardpointMask = layer >= 0 ? 1 << layer : ~0;

            rail.BuildChips(roster, chipSize);
            return rail;
        }

        private void OnEnable()
        {
            _gm = GameManager.Instance;
            if (_gm != null)
                _gm.OnSalvageChanged += HandleSalvageChanged;
            RefreshAffordability();
        }

        private void OnDisable()
        {
            if (_gm != null)
                _gm.OnSalvageChanged -= HandleSalvageChanged;
            Disarm();
        }

        private void HandleSalvageChanged(int _) => RefreshAffordability();

        // ----- Chips -----

        private void BuildChips(TowerDefinition[] roster, float chipSize)
        {
            if (roster == null)
                return;

            var buildable = new List<TowerDefinition>();
            foreach (var d in roster)
                if (d != null && d.basePrefab != null && d.tiers != null && d.tiers.Length > 0)
                    buildable.Add(d);

            float chipW = chipSize;
            float chipH = chipSize * 1.2f;
            float gap = 6f;

            // One extra slot on the right: the PANIC button (auto-deploy).
            int totalSlots = buildable.Count + 1;

            // Container plate: one quiet backdrop that groups the chips into a
            // single read (feedback: the bare chips floated). Never a raycast
            // target — pads behind the rail's margins must stay tappable.
            if (buildable.Count > 0)
            {
                var plate = new GameObject("RailPlate", typeof(RectTransform), typeof(Image));
                var plateRt2 = (RectTransform)plate.transform;
                plateRt2.SetParent(_rect, false);
                plateRt2.anchorMin = plateRt2.anchorMax = new Vector2(0.5f, 1f);
                plateRt2.pivot = new Vector2(0.5f, 1f);
                plateRt2.anchoredPosition = new Vector2(0f, 6f);
                plateRt2.sizeDelta = new Vector2(
                    totalSlots * (chipW + gap) - gap + 20f, chipH + 12f);
                var plateImg = plate.GetComponent<Image>();
                if (_theme != null && _theme.panel != null)
                {
                    plateImg.sprite = _theme.panel;
                    plateImg.type = Image.Type.Sliced;
                    plateImg.color = new Color(1f, 1f, 1f, 0.55f);
                }
                else
                {
                    plateImg.color = new Color(0.05f, 0.08f, 0.10f, 0.55f);
                }
                plateImg.raycastTarget = false;
                plateRt2.SetAsFirstSibling();
            }

            for (int i = 0; i < buildable.Count; i++)
            {
                TowerDefinition def = buildable[i];
                bool isNew = !SaveData.IsSeen("turret", def.id);

                var chip = new Chip { def = def, tierCost = def.tiers[0].cost };
                chip.go = new GameObject($"Chip_{def.id}", typeof(RectTransform), typeof(CanvasGroup));
                var rt = (RectTransform)chip.go.transform;
                rt.SetParent(_rect, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(chipW, chipH);
                rt.anchoredPosition = new Vector2((i - (totalSlots - 1) * 0.5f) * (chipW + gap), 0f);
                chip.group = chip.go.GetComponent<CanvasGroup>();

                var plateGo = new GameObject("Plate", typeof(RectTransform), typeof(Image));
                var plateRt = (RectTransform)plateGo.transform;
                plateRt.SetParent(rt, false);
                plateRt.anchorMin = Vector2.zero; plateRt.anchorMax = Vector2.one;
                plateRt.offsetMin = plateRt.offsetMax = Vector2.zero;
                chip.plate = plateGo.GetComponent<Image>();
                if (_theme != null && _theme.buttonNormal != null)
                {
                    chip.plate.sprite = _theme.buttonNormal;
                    chip.plate.type = Image.Type.Sliced;
                }
                chip.plate.color = PlateIdle();

                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                var iconRt = (RectTransform)iconGo.transform;
                iconRt.SetParent(plateRt, false);
                iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 0.62f);
                iconRt.sizeDelta = Vector2.one * chipW * 0.74f;
                chip.icon = iconGo.GetComponent<Image>();
                chip.icon.sprite = def.icon;
                chip.icon.enabled = def.icon != null;
                chip.icon.preserveAspect = true;
                chip.icon.raycastTarget = false;

                var costGo = new GameObject("Cost", typeof(RectTransform));
                var costRt = (RectTransform)costGo.transform;
                costRt.SetParent(plateRt, false);
                costRt.anchorMin = costRt.anchorMax = new Vector2(0.5f, 0.16f);
                costRt.sizeDelta = new Vector2(chipW, chipH * 0.3f);
                chip.cost = costGo.AddComponent<TextMeshProUGUI>();
                chip.cost.text = chip.tierCost.ToString();
                chip.cost.alignment = TextAlignmentOptions.Center;
                chip.cost.fontStyle = FontStyles.Bold;
                chip.cost.fontSize = Mathf.Max(13f, chipW * 0.26f);
                chip.cost.raycastTarget = false;
                if (_theme != null && _theme.font != null)
                    chip.cost.font = _theme.font;

                var tagGo = new GameObject("NewTag", typeof(RectTransform));
                var tagRt = (RectTransform)tagGo.transform;
                tagRt.SetParent(plateRt, false);
                tagRt.anchorMin = tagRt.anchorMax = new Vector2(0.5f, 1f);
                tagRt.pivot = new Vector2(0.5f, 0f);
                tagRt.anchoredPosition = new Vector2(0f, 2f);
                tagRt.sizeDelta = new Vector2(chipW, 18f);
                var tagTxt = tagGo.AddComponent<TextMeshProUGUI>();
                tagTxt.text = "NEW";
                tagTxt.alignment = TextAlignmentOptions.Center;
                tagTxt.fontStyle = FontStyles.Bold;
                tagTxt.fontSize = 14f;
                tagTxt.color = _theme != null ? _theme.amber : new Color(1f, 0.6f, 0.1f);
                tagTxt.raycastTarget = false;
                if (_theme != null && _theme.font != null)
                    tagTxt.font = _theme.font;
                chip.newTag = tagGo;
                chip.newTag.SetActive(isNew);

                var input = plateGo.AddComponent<ChipInput>();
                input.Init(this, chip);

                _chips.Add(chip);

                if (isNew)
                {
                    StartCoroutine(SlideIn(chip));
                    SaveData.MarkSeen("turret", def.id);   // introduced — the guide unlocks too
                }
            }

            BuildPanicChip(chipW, chipH,
                new Vector2((buildable.Count - (totalSlots - 1) * 0.5f) * (chipW + gap), 0f));

            RefreshAffordability();
        }

        // ----- PANIC (the AI advisor's field act: auto-deploy counters) -----

        private GameObject _panicGo;
        private CanvasGroup _panicGroup;
        private TMP_Text _panicLabel;
        private Button _panicButton;

        private void BuildPanicChip(float chipW, float chipH, Vector2 pos)
        {
            _panicGo = new GameObject("Chip_PANIC", typeof(RectTransform), typeof(CanvasGroup));
            var rt = (RectTransform)_panicGo.transform;
            rt.SetParent(_rect, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(chipW, chipH);
            rt.anchoredPosition = pos;
            _panicGroup = _panicGo.GetComponent<CanvasGroup>();

            var plateGo = new GameObject("Plate", typeof(RectTransform), typeof(Image));
            var plateRt = (RectTransform)plateGo.transform;
            plateRt.SetParent(rt, false);
            plateRt.anchorMin = Vector2.zero; plateRt.anchorMax = Vector2.one;
            plateRt.offsetMin = plateRt.offsetMax = Vector2.zero;
            var plate = plateGo.GetComponent<Image>();
            if (_theme != null && _theme.buttonNormal != null)
            {
                plate.sprite = _theme.buttonNormal;
                plate.type = Image.Type.Sliced;
            }
            plate.color = new Color(0.42f, 0.10f, 0.10f, 0.95f); // the one red button

            var bang = new GameObject("Bang", typeof(RectTransform));
            var bangRt = (RectTransform)bang.transform;
            bangRt.SetParent(plateRt, false);
            bangRt.anchorMin = bangRt.anchorMax = new Vector2(0.5f, 0.62f);
            bangRt.sizeDelta = new Vector2(chipW, chipH * 0.5f);
            var bangTxt = bang.AddComponent<TextMeshProUGUI>();
            bangTxt.text = "!";
            bangTxt.alignment = TextAlignmentOptions.Center;
            bangTxt.fontStyle = FontStyles.Bold;
            bangTxt.fontSize = chipW * 0.55f;
            bangTxt.color = Color.white;
            bangTxt.raycastTarget = false;
            if (_theme != null && _theme.font != null)
                bangTxt.font = _theme.font;

            var lblGo = new GameObject("Label", typeof(RectTransform));
            var lblRt = (RectTransform)lblGo.transform;
            lblRt.SetParent(plateRt, false);
            lblRt.anchorMin = lblRt.anchorMax = new Vector2(0.5f, 0.16f);
            lblRt.sizeDelta = new Vector2(chipW, chipH * 0.3f);
            _panicLabel = lblGo.AddComponent<TextMeshProUGUI>();
            _panicLabel.alignment = TextAlignmentOptions.Center;
            _panicLabel.fontStyle = FontStyles.Bold;
            _panicLabel.fontSize = Mathf.Max(12f, chipW * 0.19f);
            _panicLabel.color = Color.white;
            _panicLabel.raycastTarget = false;
            if (_theme != null && _theme.font != null)
                _panicLabel.font = _theme.font;

            _panicButton = plateGo.AddComponent<Button>();
            _panicButton.targetGraphic = plate;
            _panicButton.onClick.AddListener(OnPanic);

            RefreshPanic();
        }

        private void OnPanic()
        {
            if (_owner == null)
                return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Disarm();                      // an armed chip and a panic never mix
            _owner.PanicDeploy();
            RefreshPanic();
            RefreshAffordability();        // the deploy just spent salvage
        }

        private void RefreshPanic()
        {
            int left = _owner != null ? _owner.PanicRemaining : 0;
            if (_panicLabel != null)
                _panicLabel.text = $"PANIC {left}";
            if (_panicGroup != null)
                _panicGroup.alpha = left > 0 ? 1f : 0.4f;
            if (_panicButton != null)
                _panicButton.interactable = left > 0;
        }

        private Color PlateIdle() => new Color(0.10f, 0.14f, 0.17f, 0.92f);
        private Color PlateArmed()
        {
            Color c = _theme != null ? _theme.cyan : Color.cyan;
            c.a = 0.85f;
            return c;
        }

        private void RefreshAffordability()
        {
            var gm = _gm != null ? _gm : GameManager.Instance;
            foreach (var chip in _chips)
            {
                bool affordable = gm != null && gm.Salvage >= chip.tierCost;
                chip.group.alpha = affordable ? 1f : 0.55f;
                chip.icon.color = affordable ? Color.white : new Color(0.55f, 0.55f, 0.55f, 0.8f);
                chip.cost.color = affordable ? (_theme != null ? _theme.cyan : Color.cyan)
                                             : (_theme != null ? _theme.danger : Color.red);
            }
        }

        private IEnumerator SlideIn(Chip chip)
        {
            var rt = (RectTransform)chip.go.transform;
            Vector2 home = rt.anchoredPosition;
            float t = 0f;
            const float dur = 0.35f;
            while (t < dur && chip.go != null)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                float e = 1f - Mathf.Pow(1f - k, 3f);
                rt.anchoredPosition = home + Vector2.up * (34f * (1f - e));
                chip.group.alpha = Mathf.Min(chip.group.alpha, k); // never brighter than affordability allows
                yield return null;
            }
            if (chip.go != null)
                rt.anchoredPosition = home;
            RefreshAffordability();
        }

        // ----- Hover tip (name, role, power) -----

        private RectTransform _tip;
        private TMP_Text _tipText;

        private void OnChipHover(Chip chip, bool entered)
        {
            if (!entered || chip == null || chip.def == null)
            {
                if (_tip != null) _tip.gameObject.SetActive(false);
                return;
            }
            EnsureTip();
            var def = chip.def;
            var t0 = def.tiers[0];
            bool support = t0.auraRadius > 0f || t0.TotalDps <= 0f;
            string amber = ColorUtility.ToHtmlStringRGB(_theme != null ? _theme.amber : Color.yellow);
            string stats = support
                ? "SUPPORT AURA"
                : $"DPS {t0.TotalDps:0.#}  ·  RNG {t0.range:0.#} m";
            _tipText.text =
                $"<b>{def.displayName}</b>\n" +
                $"<size=80%><color=#{amber}>{BuildMenu.RoleTag(def)}</color>  ·  {t0.cost}  ·  {stats}</size>";
            _tip.gameObject.SetActive(true);
            var chipRt = (RectTransform)chip.go.transform;
            _tip.anchoredPosition = chipRt.anchoredPosition +
                Vector2.down * (chipRt.sizeDelta.y + 10f);
            _tip.SetAsLastSibling();
        }

        private void EnsureTip()
        {
            if (_tip != null)
                return;
            var go = new GameObject("RailTip", typeof(RectTransform), typeof(Image));
            _tip = (RectTransform)go.transform;
            _tip.SetParent(_rect, false);
            _tip.anchorMin = _tip.anchorMax = new Vector2(0.5f, 1f);
            _tip.pivot = new Vector2(0.5f, 1f);
            _tip.sizeDelta = new Vector2(250f, 54f);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.04f, 0.07f, 0.09f, 0.94f);
            img.raycastTarget = false;

            var txtGo = new GameObject("Text", typeof(RectTransform));
            var txtRt = (RectTransform)txtGo.transform;
            txtRt.SetParent(_tip, false);
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(8f, 4f);
            txtRt.offsetMax = new Vector2(-8f, -4f);
            _tipText = txtGo.AddComponent<TextMeshProUGUI>();
            _tipText.alignment = TextAlignmentOptions.Center;
            _tipText.fontSize = 17f;
            _tipText.richText = true;
            _tipText.raycastTarget = false;
            _tipText.color = Color.white;
            if (_theme != null && _theme.font != null)
                _tipText.font = _theme.font;
            go.SetActive(false);
        }

        // ----- Arm / build -----

        public bool IsArmed => _armedDef != null;

        /// <summary>BuildMenu forwards free-pad taps here first: an armed chip
        /// builds directly instead of opening a menu. Returns true when the
        /// build LANDED; a failed build (salvage spent meanwhile) disarms and
        /// returns false so the menu opens and shows the player why.</summary>
        public bool TryBuildArmed(TowerHardpoint pad)
        {
            if (_armedDef == null || pad == null)
                return false;
            var def = _armedDef;
            bool built = _owner != null && _owner.RailBuild(def, pad);
            Disarm();
            return built;
        }

        public void Disarm()
        {
            _armedDef = null;
            TowerHardpoint.BuildAttention = false;
            foreach (var chip in _chips)
                chip.plate.color = PlateIdle();
            if (_ghost != null)
                _ghost.gameObject.SetActive(false);
            if (_owner != null)
                _owner.ClearRangePreview();
        }

        private void OnChipTapped(Chip chip)
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (_armedDef == chip.def)
            {
                Disarm();
                return;
            }
            _armedDef = chip.def;
            TowerHardpoint.BuildAttention = true;
            foreach (var c in _chips)
                c.plate.color = c.def == _armedDef ? PlateArmed() : PlateIdle();
        }

        // ----- Drag-to-build -----

        private void OnChipBeginDrag(Chip chip, PointerEventData e)
        {
            _armedDef = chip.def;
            TowerHardpoint.BuildAttention = true;
            foreach (var c in _chips)
                c.plate.color = c.def == _armedDef ? PlateArmed() : PlateIdle();
            EnsureGhost();
            _ghost.gameObject.SetActive(true);
            var img = _ghost.GetComponent<Image>();
            img.sprite = chip.def.icon;
            img.enabled = chip.def.icon != null;
            MoveGhost(e);
        }

        private void OnChipDrag(Chip chip, PointerEventData e)
        {
            if (_armedDef != chip.def)
                return;
            MoveGhost(e);
            var pad = PadUnderPointer(e.position);
            if (pad != null && !pad.IsOccupied && !pad.IsReserved && _owner != null)
                _owner.PreviewRangeAt(chip.def, pad);
            else if (_owner != null)
                _owner.ClearRangePreview();
        }

        private void OnChipEndDrag(Chip chip, PointerEventData e)
        {
            if (_ghost != null)
                _ghost.gameObject.SetActive(false);
            if (_armedDef != chip.def)
                return;
            var pad = PadUnderPointer(e.position);
            if (pad != null && !pad.IsOccupied && !pad.IsReserved && _owner != null)
                _owner.RailBuild(chip.def, pad); // RailBuild carries the click sound
            Disarm();
        }

        private void EnsureGhost()
        {
            if (_ghost != null)
                return;
            var go = new GameObject("DragGhost", typeof(RectTransform), typeof(Image));
            _ghost = (RectTransform)go.transform;
            _ghost.SetParent(_canvasRect != null ? (Transform)_canvasRect : transform, false);
            _ghost.anchorMin = _ghost.anchorMax = new Vector2(0.5f, 0.5f);
            _ghost.sizeDelta = Vector2.one * 48f;
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;   // must never block the drop target
            img.preserveAspect = true;
            var group = go.AddComponent<CanvasGroup>();
            group.alpha = 0.85f;
            group.blocksRaycasts = false;
            go.SetActive(false);
        }

        private void MoveGhost(PointerEventData e)
        {
            if (_ghost == null || _canvasRect == null)
                return;
            Camera uiCam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _canvas.worldCamera : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, e.position, uiCam, out Vector2 local);
            _ghost.anchoredPosition = local;
        }

        private TowerHardpoint PadUnderPointer(Vector2 screenPos)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return null;
            Ray ray = cam.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit, 500f, _hardpointMask, QueryTriggerInteraction.Collide))
                return hit.collider.GetComponentInParent<TowerHardpoint>();
            return null;
        }

        /// <summary>Per-chip pointer handlers. A plain Image + interfaces rather
        /// than a Button: Buttons swallow the drag threshold and make
        /// drag-to-build feel sticky.</summary>
        private class ChipInput : MonoBehaviour,
            IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler,
            IPointerEnterHandler, IPointerExitHandler
        {
            private RosterRail _rail;
            private Chip _chip;
            private bool _dragging;

            public void Init(RosterRail rail, Chip chip) { _rail = rail; _chip = chip; }

            public void OnPointerEnter(PointerEventData e)
            {
                if (_rail != null && !_dragging) _rail.OnChipHover(_chip, true);
            }

            public void OnPointerExit(PointerEventData e)
            {
                if (_rail != null) _rail.OnChipHover(_chip, false);
            }

            public void OnPointerClick(PointerEventData e)
            {
                if (_dragging) return;
                if (_rail != null) _rail.OnChipTapped(_chip);
            }

            public void OnBeginDrag(PointerEventData e)
            {
                _dragging = true;
                if (_rail != null) _rail.OnChipHover(_chip, false); // tip out of the way
                if (_rail != null) _rail.OnChipBeginDrag(_chip, e);
            }

            public void OnDrag(PointerEventData e)
            {
                if (_rail != null) _rail.OnChipDrag(_chip, e);
            }

            public void OnEndDrag(PointerEventData e)
            {
                if (_rail != null) _rail.OnChipEndDrag(_chip, e);
                _dragging = false;
            }
        }
    }
}
