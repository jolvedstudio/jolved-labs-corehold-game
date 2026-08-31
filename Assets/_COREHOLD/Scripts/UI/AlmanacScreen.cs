using System.Collections.Generic;
using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// The field guide (R-UI-7, PvZ's almanac): every enemy and turret met so
    /// far, one card each — portrait, name, role word, armour/damage pip and
    /// the definition's own one-sentence description. Units not yet
    /// encountered show as silhouettes, so the catalogue itself teases what is
    /// coming. Sightings persist via <see cref="SaveData.IsSeen"/> (enemies
    /// unlock on first spawn, turrets when a level first offers them).
    ///
    /// Opens from the pause screen and from the HUD between waves; never
    /// during combat by itself. Entirely programmatic — no scene edits, themed
    /// via <see cref="UITheme"/>. This is where ALL story depth beyond the
    /// one-liners lives (onboarding plan: the game never lectures).
    /// </summary>
    public class AlmanacScreen : MonoBehaviour
    {
        private static AlmanacScreen _instance;

        private UITheme _theme;
        private RectTransform _content;
        private TMP_Text _enemiesTabLabel;
        private TMP_Text _turretsTabLabel;
        private bool _showingEnemies = true;
        private readonly List<GameObject> _cards = new List<GameObject>();

        /// <summary>Open (or close, when already open) the guide on the given canvas.</summary>
        public static void Toggle(Transform canvasRoot)
        {
            if (canvasRoot == null)
                return;
            if (_instance != null)
            {
                bool show = !_instance.gameObject.activeSelf;
                _instance.gameObject.SetActive(show);
                if (show)
                {
                    _instance.transform.SetAsLastSibling();
                    _instance.Rebuild();
                }
                return;
            }
            _instance = Build(canvasRoot);
            _instance.Rebuild();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        // ----- Construction -----

        private static AlmanacScreen Build(Transform canvasRoot)
        {
            var go = new GameObject("AlmanacScreen", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvasRoot, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f); // dim, and eats field taps
            rt.SetAsLastSibling();

            var screen = go.AddComponent<AlmanacScreen>();
            screen._theme = UITheme.Instance;
            var theme = screen._theme;

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            var panelRt = (RectTransform)panel.transform;
            panelRt.SetParent(rt, false);
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(860f, 500f);
            var panelImg = panel.GetComponent<Image>();
            if (theme != null && theme.popup != null)
            {
                panelImg.sprite = theme.popup;
                panelImg.type = Image.Type.Sliced;
                panelImg.color = Color.white;
            }
            else
            {
                panelImg.color = new Color(0.08f, 0.11f, 0.14f, 0.97f);
            }

            var title = MakeText(panelRt, "Title", "FIELD GUIDE",
                theme != null ? theme.fontSizeLarge : 34f, FontStyles.Bold, screen);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = titleRt.anchorMax = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -34f);
            titleRt.sizeDelta = new Vector2(500f, 44f);
            title.color = theme != null ? theme.cyan : Color.cyan;

            screen._enemiesTabLabel = screen.MakeTab(panelRt, "ENEMIES", new Vector2(-110f, -84f), () =>
            {
                screen._showingEnemies = true;
                screen.Rebuild();
            });
            screen._turretsTabLabel = screen.MakeTab(panelRt, "TURRETS", new Vector2(110f, -84f), () =>
            {
                screen._showingEnemies = false;
                screen.Rebuild();
            });

            // Scrollable card grid.
            var viewGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            var viewRt = (RectTransform)viewGo.transform;
            viewRt.SetParent(panelRt, false);
            viewRt.anchorMin = new Vector2(0f, 0f);
            viewRt.anchorMax = new Vector2(1f, 1f);
            viewRt.offsetMin = new Vector2(20f, 76f);    // room for CLOSE below
            viewRt.offsetMax = new Vector2(-20f, -108f); // below title + tabs
            viewGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            viewGo.GetComponent<Mask>().showMaskGraphic = true;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            screen._content = (RectTransform)contentGo.transform;
            screen._content.SetParent(viewRt, false);
            screen._content.anchorMin = new Vector2(0f, 1f);
            screen._content.anchorMax = new Vector2(1f, 1f);
            screen._content.pivot = new Vector2(0.5f, 1f);
            var grid = contentGo.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(396f, 112f);
            grid.spacing = new Vector2(10f, 10f);
            grid.padding = new RectOffset(6, 6, 6, 6);
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewGo.AddComponent<ScrollRect>();
            scroll.content = screen._content;
            scroll.viewport = viewRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            // Close button.
            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            var closeRt = (RectTransform)closeGo.transform;
            closeRt.SetParent(panelRt, false);
            closeRt.anchorMin = closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0f, 16f);
            closeRt.sizeDelta = new Vector2(240f, 50f);
            var closeImg = closeGo.GetComponent<Image>();
            if (theme != null && theme.buttonNormal != null)
            {
                closeImg.sprite = theme.buttonNormal;
                closeImg.type = Image.Type.Sliced;
            }
            else
            {
                closeImg.color = new Color(0.13f, 0.2f, 0.25f, 1f);
            }
            var closeTxt = MakeText(closeRt, "Label", "CLOSE",
                theme != null ? theme.fontSizeSmall : 22f, FontStyles.Bold, screen);
            closeTxt.rectTransform.anchorMin = Vector2.zero;
            closeTxt.rectTransform.anchorMax = Vector2.one;
            closeTxt.rectTransform.offsetMin = closeTxt.rectTransform.offsetMax = Vector2.zero;
            closeGo.GetComponent<Button>().onClick.AddListener(() =>
            {
                if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
                go.SetActive(false);
            });

            return screen;
        }

        private TMP_Text MakeTab(RectTransform panel, string label, Vector2 pos, UnityEngine.Events.UnityAction onTap)
        {
            var go = new GameObject($"Tab_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(panel, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(200f, 40f);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.1f, 0.14f, 0.17f, 0.9f);
            var txt = MakeText(rt, "Label", label, _theme != null ? _theme.fontSizeSmall : 22f, FontStyles.Bold, this);
            txt.rectTransform.anchorMin = Vector2.zero;
            txt.rectTransform.anchorMax = Vector2.one;
            txt.rectTransform.offsetMin = txt.rectTransform.offsetMax = Vector2.zero;
            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
                onTap();
            });
            return txt;
        }

        private static TMP_Text MakeText(RectTransform parent, string name, string text,
                                         float size, FontStyles style, AlmanacScreen screen)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = size;
            txt.fontStyle = style;
            txt.alignment = TextAlignmentOptions.Center;
            txt.raycastTarget = false;
            if (screen._theme != null && screen._theme.font != null)
                txt.font = screen._theme.font;
            return txt;
        }

        // ----- Cards -----

        private void Rebuild()
        {
            foreach (var c in _cards)
                Destroy(c);
            _cards.Clear();

            Color on = _theme != null ? _theme.cyan : Color.cyan;
            Color off = new Color(0.55f, 0.6f, 0.65f, 1f);
            if (_enemiesTabLabel != null) _enemiesTabLabel.color = _showingEnemies ? on : off;
            if (_turretsTabLabel != null) _turretsTabLabel.color = _showingEnemies ? off : on;

            if (_showingEnemies)
            {
                foreach (var def in EnemyCatalogue())
                    AddCard(def.icon, def.displayName, EnemyRole(def),
                            UITheme.ArmourLetter(def.armourType),
                            _theme != null ? _theme.ArmourColor(def.armourType) : Color.white,
                            def.description, SaveData.IsSeen("enemy", def.id));
            }
            else
            {
                var turrets = _theme != null ? _theme.turrets : null;
                if (turrets != null)
                {
                    foreach (var def in turrets)
                    {
                        if (def == null || def.basePrefab == null)
                            continue; // WIP roster entries stay out of the book
                        AddCard(def.icon, def.displayName, BuildMenu.RoleTag(def),
                                DamageLetter(def.damageType),
                                _theme != null ? _theme.cyan : Color.cyan,
                                def.description, SaveData.IsSeen("turret", def.id));
                    }
                }
            }
        }

        /// <summary>The enemy catalogue: the theme's wired list, else harvested
        /// from the current level's waves (scenes built before the theme carried
        /// enemies still get a working, if shorter, book).</summary>
        private List<EnemyDefinition> EnemyCatalogue()
        {
            var result = new List<EnemyDefinition>();
            var seen = new HashSet<EnemyDefinition>();

            if (_theme != null && _theme.enemies != null)
                foreach (var d in _theme.enemies)
                    if (d != null && seen.Add(d))
                        result.Add(d);

            if (result.Count == 0)
            {
                var wm = FindFirstObjectByType<WaveManager>();
                if (wm != null)
                {
                    for (int i = 0; i < wm.WaveCount; i++)
                    {
                        var wave = wm.GetWave(i);
                        if (wave == null || wave.groups == null) continue;
                        foreach (var g in wave.groups)
                            if (g.enemy != null && seen.Add(g.enemy))
                                result.Add(g.enemy);
                    }
                    result.Sort((a, b) => a.baseHealth.CompareTo(b.baseHealth));
                }
            }
            return result;
        }

        private static string DamageLetter(DamageType t) =>
            t == DamageType.Kinetic ? "K" : t == DamageType.Energy ? "E" : "X";

        private static string EnemyRole(EnemyDefinition def)
        {
            if (def.baseHealth >= 1000f) return "BOSS";
            if (def.isAir) return "AIR";
            if (def.armourType == ArmourType.Shielded) return "SHIELDED";
            if (def.armourType == ArmourType.Plated) return "ARMOURED";
            if (def.moveSpeed >= 3f) return "FAST";
            if (def.baseHealth <= 60f) return "SWARM";
            return "GROUND";
        }

        private void AddCard(Sprite icon, string name, string role,
                             string pipLetter, Color pipColor, string flavour, bool unlocked)
        {
            var go = new GameObject("Card", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_content, false);
            go.GetComponent<Image>().color = new Color(0.10f, 0.14f, 0.17f, 0.92f);
            _cards.Add(go);

            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            var iconRt = (RectTransform)iconGo.transform;
            iconRt.SetParent(rt, false);
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.anchoredPosition = new Vector2(10f, 0f);
            iconRt.sizeDelta = new Vector2(84f, 84f);
            var iconImg = iconGo.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.enabled = icon != null;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            iconImg.color = unlocked ? Color.white : Color.black; // silhouette until met

            var nameTxt = MakeText(rt, "Name", unlocked ? name : "?????",
                _theme != null ? _theme.fontSizeSmall : 22f, FontStyles.Bold, this);
            nameTxt.alignment = TextAlignmentOptions.TopLeft;
            var nameRt = nameTxt.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 1f);
            nameRt.anchorMax = new Vector2(1f, 1f);
            nameRt.pivot = new Vector2(0f, 1f);
            nameRt.anchoredPosition = new Vector2(104f, -10f);
            nameRt.sizeDelta = new Vector2(-114f, 26f);
            nameTxt.color = Color.white;

            var roleTxt = MakeText(rt, "Role", unlocked ? $"{role}  <color=#{ColorUtility.ToHtmlStringRGB(pipColor)}>[{pipLetter}]</color>" : "—",
                15f, FontStyles.Bold, this);
            roleTxt.alignment = TextAlignmentOptions.TopLeft;
            roleTxt.richText = true;
            var roleRt = roleTxt.rectTransform;
            roleRt.anchorMin = new Vector2(0f, 1f);
            roleRt.anchorMax = new Vector2(1f, 1f);
            roleRt.pivot = new Vector2(0f, 1f);
            roleRt.anchoredPosition = new Vector2(104f, -38f);
            roleRt.sizeDelta = new Vector2(-114f, 20f);
            roleTxt.color = _theme != null ? _theme.amber : new Color(1f, 0.6f, 0.1f);

            var descTxt = MakeText(rt, "Desc",
                unlocked ? (string.IsNullOrEmpty(flavour) ? "" : flavour) : "Not yet encountered.",
                14f, FontStyles.Normal, this);
            descTxt.alignment = TextAlignmentOptions.TopLeft;
            descTxt.textWrappingMode = TextWrappingModes.Normal;
            var descRt = descTxt.rectTransform;
            descRt.anchorMin = new Vector2(0f, 0f);
            descRt.anchorMax = new Vector2(1f, 1f);
            descRt.pivot = new Vector2(0f, 1f);
            descRt.offsetMin = new Vector2(104f, 8f);
            descRt.offsetMax = new Vector2(-10f, -60f);
            descTxt.color = new Color(0.8f, 0.84f, 0.87f, 1f);
        }
    }
}
