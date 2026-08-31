using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// HOW TO PLAY (onboarding plan §2): one screen, ~15 seconds — the story in
    /// three plain sentences, then five one-line rules (BUILD / DEFEND / EARN /
    /// WIN / PACE). No paging, no jargon, closable instantly. This is where a
    /// new player learns the game AND where the wave-pacing difficulty tiers
    /// are spelled out in words.
    ///
    /// Opens from the pause screen and the title screen's HELP button. Entirely
    /// programmatic (no scene edits), themed via <see cref="UITheme"/>; the
    /// styled prefab seam from the onboarding plan can replace the look later
    /// without touching the callers.
    /// </summary>
    public class HowToPlayScreen : MonoBehaviour
    {
        private static HowToPlayScreen _instance;

        /// <summary>Open (or close, when already open) on the given canvas.</summary>
        public static void Toggle(Transform canvasRoot)
        {
            if (canvasRoot == null)
                return;
            if (_instance != null)
            {
                bool show = !_instance.gameObject.activeSelf;
                _instance.gameObject.SetActive(show);
                if (show)
                    _instance.transform.SetAsLastSibling();
                return;
            }
            _instance = Build(canvasRoot);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private static HowToPlayScreen Build(Transform canvasRoot)
        {
            var theme = UITheme.Instance;

            var go = new GameObject("HowToPlayScreen", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvasRoot, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);
            rt.SetAsLastSibling();
            var screen = go.AddComponent<HowToPlayScreen>();

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            var panelRt = (RectTransform)panel.transform;
            panelRt.SetParent(rt, false);
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(780f, 520f);
            var panelImg = panel.GetComponent<Image>();
            if (theme != null && theme.popup != null)
            {
                panelImg.sprite = theme.popup;
                panelImg.type = Image.Type.Sliced;
            }
            else
            {
                panelImg.color = new Color(0.08f, 0.11f, 0.14f, 0.97f);
            }

            TMP_Text Make(string name, string text, float size, FontStyles style,
                          Vector2 anchor, Vector2 pos, Vector2 sizeDelta,
                          TextAlignmentOptions align)
            {
                var tgo = new GameObject(name, typeof(RectTransform));
                var trt = (RectTransform)tgo.transform;
                trt.SetParent(panelRt, false);
                trt.anchorMin = trt.anchorMax = anchor;
                trt.pivot = new Vector2(0.5f, 1f);
                trt.anchoredPosition = pos;
                trt.sizeDelta = sizeDelta;
                var txt = tgo.AddComponent<TextMeshProUGUI>();
                txt.text = text;
                txt.fontSize = size;
                txt.fontStyle = style;
                txt.alignment = align;
                txt.richText = true;
                txt.raycastTarget = false;
                txt.color = Color.white;
                if (theme != null && theme.font != null)
                    txt.font = theme.font;
                return txt;
            }

            var title = Make("Title", "HOW TO PLAY",
                theme != null ? theme.fontSizeLarge : 34f, FontStyles.Bold,
                new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(700f, 44f),
                TextAlignmentOptions.Center);
            title.color = theme != null ? theme.cyan : Color.cyan;

            // The story — three sentences, plain words. This IS the narrative
            // explainer; everything deeper lives in the field guide's cards.
            var story = Make("Story",
                "The machines we sent to build this world turned on it.\n" +
                "They're coming to destroy the Cores that make the air.\n" +
                "Hold them — I'll help.   <color=#8fd48f>— ARBOR, your field advisor</color>",
                18f, FontStyles.Italic,
                new Vector2(0.5f, 1f), new Vector2(0f, -66f), new Vector2(700f, 84f),
                TextAlignmentOptions.Center);
            story.color = new Color(0.85f, 0.9f, 0.88f, 1f);

            string amber = ColorUtility.ToHtmlStringRGB(theme != null ? theme.amber : Color.yellow);
            (string word, string line)[] rules =
            {
                ("BUILD",  "Tap a pad — or drag a turret from the top rail onto one. It costs salvage."),
                ("DEFEND", "Enemies walk the paths to your Core. Kinetic breaks shields, energy cuts plate, splash clears swarms."),
                ("EARN",   "Every kill pays salvage. Spend it between waves; start a wave early for a bonus."),
                ("WIN",    "Survive every wave. Lose all Core integrity and the node falls."),
                ("PACE",   "NORMAL: build at your own pace. VETERAN: waves launch on a countdown. NIGHTMARE: they barely wait."),
            };
            for (int i = 0; i < rules.Length; i++)
            {
                Make($"Rule{i}",
                    $"<color=#{amber}><b>{rules[i].word}</b></color>  —  {rules[i].line}",
                    17f, FontStyles.Normal,
                    new Vector2(0.5f, 1f), new Vector2(0f, -160f - i * 52f), new Vector2(710f, 46f),
                    TextAlignmentOptions.TopLeft);
            }

            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            var closeRt = (RectTransform)closeGo.transform;
            closeRt.SetParent(panelRt, false);
            closeRt.anchorMin = closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0f, 18f);
            closeRt.sizeDelta = new Vector2(240f, 52f);
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
            var closeTxt = Make("CloseLabel", "GOT IT",
                theme != null ? theme.fontSizeSmall : 22f, FontStyles.Bold,
                new Vector2(0.5f, 0f), Vector2.zero, Vector2.zero, TextAlignmentOptions.Center);
            closeTxt.rectTransform.SetParent(closeRt, false);
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
    }
}
