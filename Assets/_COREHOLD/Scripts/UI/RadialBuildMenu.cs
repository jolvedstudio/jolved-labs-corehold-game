using System.Collections;
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
    /// Radial build menu (R-UI-1, the Kingdom Rush pattern): tapping an empty
    /// pad grows a ring of turret nodes around the pad itself, so eyes never
    /// leave the field. First tap on a node selects it and previews its range
    /// ring; a second tap on the same node builds. Unaffordable nodes are
    /// dimmed and inert, exactly like the bottom sheet's entries.
    ///
    /// Opt-in: created at runtime by <see cref="BuildMenu"/> only when the
    /// player enables it in Settings (<see cref="SaveData.RadialBuildMenu"/>,
    /// default OFF) — the bottom sheet stays the default layout. Entirely
    /// programmatic (no scene edits, works in every generated level); themed
    /// via <see cref="UITheme"/>, sized/timed via BuildMenu's [TUNE] knobs.
    /// The screen-anchored ring never drifts because the gameplay camera is
    /// fixed (GDD: no scrolling battlefield).
    /// </summary>
    public class RadialBuildMenu : MonoBehaviour
    {
        private BuildMenu _owner;
        private UITheme _theme;
        private RectTransform _rect;        // this container, centred on the pad
        private RectTransform _canvasRect;
        private Canvas _canvas;
        private CanvasGroup _group;
        private TowerHardpoint _pad;
        private TowerDefinition _selectedDef;
        private Coroutine _grow;

        private readonly List<Node> _nodes = new List<Node>();

        private class Node
        {
            public GameObject go;
            public Image halo;   // selection ring behind the plate
            public Image plate;
            public Image icon;
            public TMP_Text cost;
            public Button button;
            public TowerDefinition def;
        }

        private static Sprite _circle; // generated once, shared by every node

        public static RadialBuildMenu Create(BuildMenu owner, UITheme theme, Transform canvasRoot)
        {
            var go = new GameObject("RadialBuildMenu", typeof(RectTransform), typeof(CanvasGroup));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvasRoot, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;

            var menu = go.AddComponent<RadialBuildMenu>();
            menu._owner = owner;
            menu._theme = theme;
            menu._rect = rt;
            menu._group = go.GetComponent<CanvasGroup>();
            menu._canvas = canvasRoot.GetComponentInParent<Canvas>();
            menu._canvasRect = menu._canvas != null ? (RectTransform)menu._canvas.transform : null;
            go.SetActive(false);
            return menu;
        }

        public bool IsOpenFor(TowerHardpoint pad) => gameObject.activeSelf && _pad == pad;

        /// <summary>
        /// Grow the ring around <paramref name="pad"/>. Returns false when the
        /// pad cannot be placed on screen (no camera / behind it) so the caller
        /// can fall back to the bottom sheet instead of a dead tap.
        /// </summary>
        public bool Open(TowerHardpoint pad, TowerDefinition[] turrets,
                         float radius, float nodeSize, float growSeconds)
        {
            if (pad == null || turrets == null || _canvasRect == null)
                return false;
            Camera worldCam = Camera.main;
            if (worldCam == null)
                return false;
            Vector3 screen = worldCam.WorldToScreenPoint(pad.transform.position);
            if (screen.z <= 0f)
                return false;

            int total = 0;
            foreach (var d in turrets)
                if (d != null) total++;
            if (total == 0)
                return false;

            _pad = pad;
            _selectedDef = null;

            // A crowded roster grows the ring rather than overlapping nodes
            // (the 10-slot roster, WIP entries included, same as the sheet).
            float ringRadius = Mathf.Max(radius, total * nodeSize * 1.15f / (2f * Mathf.PI));

            // Pad → canvas-local point, pulled inward so the whole ring stays
            // on screen even for pads hugging an edge.
            Camera uiCam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _canvas.worldCamera : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, uiCam, out Vector2 local);
            float reach = ringRadius + nodeSize * 0.85f;
            Rect cr = _canvasRect.rect;
            local.x = Mathf.Clamp(local.x, cr.xMin + reach, cr.xMax - reach);
            local.y = Mathf.Clamp(local.y, cr.yMin + reach, cr.yMax - reach);
            _rect.anchoredPosition = local;

            BuildNodes(turrets, ringRadius, nodeSize, total);

            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            if (_grow != null) StopCoroutine(_grow);
            _grow = StartCoroutine(GrowRoutine(growSeconds));
            return true;
        }

        public void Close()
        {
            _pad = null;
            _selectedDef = null;
            if (_grow != null) { StopCoroutine(_grow); _grow = null; }
            if (gameObject.activeSelf)
                gameObject.SetActive(false);
        }

        // ----- Ring construction -----

        private void BuildNodes(TowerDefinition[] turrets, float radius, float nodeSize, int total)
        {
            foreach (var n in _nodes)
                n.go.SetActive(false);

            var gm = GameManager.Instance;
            int shown = 0;

            for (int i = 0; i < turrets.Length; i++)
            {
                TowerDefinition def = turrets[i];
                if (def == null) continue;

                Node node = GetNode(shown, nodeSize);
                node.def = def;
                node.go.SetActive(true);

                // Evenly spaced, first node at 12 o'clock, clockwise.
                float ang = (90f - shown * (360f / total)) * Mathf.Deg2Rad;
                var rt = (RectTransform)node.go.transform;
                rt.anchoredPosition = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
                rt.sizeDelta = Vector2.one * nodeSize;

                int cost = def.tiers != null && def.tiers.Length > 0 ? def.tiers[0].cost : 0;
                bool affordable = gm != null && gm.Salvage >= cost;
                bool buildable = def.basePrefab != null; // WIP roster entries: data, no chassis yet

                node.plate.color = affordable && buildable
                    ? new Color(0.10f, 0.14f, 0.17f, 0.95f)
                    : new Color(0.08f, 0.10f, 0.12f, 0.75f);
                node.icon.sprite = def.icon;
                node.icon.enabled = def.icon != null;
                node.icon.color = affordable && buildable ? Color.white : new Color(0.5f, 0.5f, 0.5f, 0.6f);
                node.cost.text = buildable ? cost.ToString() : "WIP";
                node.cost.color = !buildable ? new Color(0.6f, 0.6f, 0.6f, 1f)
                    : affordable ? (_theme != null ? _theme.cyan : Color.cyan)
                                 : (_theme != null ? _theme.danger : Color.red);
                node.halo.enabled = false;

                node.button.interactable = affordable && buildable;
                node.button.onClick.RemoveAllListeners();
                var captured = node;
                node.button.onClick.AddListener(() => OnNodeTapped(captured));
                shown++;
            }
        }

        private Node GetNode(int index, float nodeSize)
        {
            while (_nodes.Count <= index)
            {
                var node = new Node();
                node.go = new GameObject($"Node_{_nodes.Count}", typeof(RectTransform));
                var rt = (RectTransform)node.go.transform;
                rt.SetParent(_rect, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                // Selection halo: a slightly larger circle behind the plate.
                var haloGo = new GameObject("Halo", typeof(RectTransform), typeof(Image));
                var haloRt = (RectTransform)haloGo.transform;
                haloRt.SetParent(rt, false);
                haloRt.anchorMin = Vector2.zero; haloRt.anchorMax = Vector2.one;
                haloRt.offsetMin = Vector2.one * (-nodeSize * 0.08f);
                haloRt.offsetMax = Vector2.one * (nodeSize * 0.08f);
                node.halo = haloGo.GetComponent<Image>();
                node.halo.sprite = Circle();
                node.halo.color = _theme != null ? _theme.cyan : Color.cyan;
                node.halo.raycastTarget = false;
                node.halo.enabled = false;

                var plateGo = new GameObject("Plate", typeof(RectTransform), typeof(Image));
                var plateRt = (RectTransform)plateGo.transform;
                plateRt.SetParent(rt, false);
                plateRt.anchorMin = Vector2.zero; plateRt.anchorMax = Vector2.one;
                plateRt.offsetMin = plateRt.offsetMax = Vector2.zero;
                node.plate = plateGo.GetComponent<Image>();
                node.plate.sprite = Circle();

                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                var iconRt = (RectTransform)iconGo.transform;
                iconRt.SetParent(plateRt, false);
                iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 0.60f);
                iconRt.sizeDelta = Vector2.one * nodeSize * 0.52f;
                node.icon = iconGo.GetComponent<Image>();
                node.icon.raycastTarget = false;
                node.icon.preserveAspect = true;

                var costGo = new GameObject("Cost", typeof(RectTransform));
                var costRt = (RectTransform)costGo.transform;
                costRt.SetParent(plateRt, false);
                costRt.anchorMin = costRt.anchorMax = new Vector2(0.5f, 0.22f);
                costRt.sizeDelta = new Vector2(nodeSize, nodeSize * 0.3f);
                node.cost = costGo.AddComponent<TextMeshProUGUI>();
                node.cost.alignment = TextAlignmentOptions.Center;
                node.cost.fontStyle = FontStyles.Bold;
                node.cost.fontSize = Mathf.Max(14f, nodeSize * 0.24f);
                node.cost.raycastTarget = false;
                if (_theme != null && _theme.font != null)
                    node.cost.font = _theme.font;

                node.button = plateGo.AddComponent<Button>();
                node.button.targetGraphic = node.plate;

                _nodes.Add(node);
            }
            return _nodes[index];
        }

        // ----- Interaction: tap selects + previews, second tap builds -----

        private void OnNodeTapped(Node node)
        {
            if (node == null || node.def == null || _pad == null)
                return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();

            if (_selectedDef != node.def)
            {
                _selectedDef = node.def;
                foreach (var n in _nodes)
                    n.halo.enabled = n.go.activeSelf && n.def == _selectedDef;
                if (_owner != null) _owner.PreviewRange(node.def);
                return;
            }

            if (_owner != null) _owner.RadialConfirm(node.def);
        }

        private IEnumerator GrowRoutine(float seconds)
        {
            float dur = Mathf.Max(0.02f, seconds);
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; // building mid-wave at 2× must not double the motion
                float k = Mathf.Clamp01(t / dur);
                float e = 1f - Mathf.Pow(1f - k, 3f); // ease-out cubic
                _rect.localScale = Vector3.one * Mathf.LerpUnclamped(0.4f, 1f, e);
                if (_group != null) _group.alpha = k;
                yield return null;
            }
            _rect.localScale = Vector3.one;
            if (_group != null) _group.alpha = 1f;
            _grow = null;
        }

        /// <summary>Runtime-generated anti-aliased filled circle — zero art assets.</summary>
        private static Sprite Circle()
        {
            if (_circle != null) return _circle;
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.ARGB32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            float radius = S * 0.5f - 1f;
            float c = (S - 1) * 0.5f;
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    px[y * S + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            _circle = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            _circle.hideFlags = HideFlags.HideAndDontSave;
            return _circle;
        }
    }
}
