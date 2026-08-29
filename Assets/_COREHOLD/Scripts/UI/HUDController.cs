using System.Collections;
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
    /// The in-game HUD (GDD §9.1). Every value is event-driven — nothing polls in
    /// Update (GDD §9.1, §12.3):
    ///
    ///   • Top-left  : core integrity as a segmented bar + numeric value.
    ///   • Top-centre: "WAVE n / 10" and the next wave's composition as unit icons
    ///                 with counts AND armour pips (GDD §9.1, §9.4).
    ///   • Top-right : salvage with an animated counter.
    ///   • Bot-right : Start Wave (shows the chain bonus while a wave is live) and a
    ///                 1× / 2× speed toggle (GDD §9.6).
    ///   • Bot-left  : pause.
    ///
    /// Subscribes to <see cref="GameManager"/> and <see cref="WaveManager"/> events;
    /// the salvage counter tween is the only coroutine and runs in unscaled time so
    /// the 2× toggle does not distort it (GDD §9.6).
    /// </summary>
    [DisallowMultipleComponent]
    public class HUDController : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private WaveManager waveManager;
        [SerializeField] private UITheme theme;

        [Header("Integrity (top-left)")]
        [SerializeField] private RectTransform integritySegments; // holds one Image per segment
        [SerializeField] private TMP_Text integrityValue;
        [SerializeField] private Image integritySegmentPrefabSource; // a template segment Image (disabled)

        [Header("Wave + preview (top-centre)")]
        [SerializeField] private TMP_Text waveLabel;
        [SerializeField] private RectTransform previewRow; // parent for the per-unit preview cells

        [Header("Salvage (top-right)")]
        [SerializeField] private TMP_Text salvageValue;

        [Header("Start wave / speed (bottom-right)")]
        [SerializeField] private Button startWaveButton;
        [SerializeField] private TMP_Text startWaveLabel;
        [SerializeField] private Button speedButton;
        [SerializeField] private TMP_Text speedLabel;

        [Header("Pause (bottom-left)")]
        [SerializeField] private Button pauseButton;
        [SerializeField] private PauseScreen pauseScreen;

        [Header("Colossus bar (GDD §9.4)")]
        [SerializeField] private RectTransform colossusBarRoot;
        [SerializeField] private Image colossusBarFill;
        [SerializeField] private TMP_Text colossusBarLabel;

        [Header("Preview cell template")]
        [SerializeField] private GameObject previewCellTemplate; // has icon Image, count TMP, pip Image

        [Header("Close call (R3)")]
        [Tooltip("[TUNE] Show CLOSE CALL when a wave completes with core integrity at or below this (0 disables).")]
        [SerializeField] private int closeCallIntegrityThreshold = 5;

        [Tooltip("[TUNE] Time.timeScale during the last-kill slow-mo dip.")]
        [SerializeField] private float closeCallDipScale = 0.30f;

        [Tooltip("[TUNE] Unscaled seconds the slow-mo dip lasts.")]
        [SerializeField] private float closeCallDipSeconds = 0.35f;

        [Tooltip("[TUNE] Unscaled seconds the CLOSE CALL banner stays on screen (including fades).")]
        [SerializeField] private float closeCallBannerSeconds = 1.6f;

        [Header("Wave banners (R-UI-6)")]
        [Tooltip("[TUNE] Unscaled seconds for a plain wave-start banner (0 disables plain banners; boss/doctrine banners still show).")]
        [SerializeField] private float waveBannerSeconds = 0.9f;

        [Tooltip("[TUNE] Unscaled seconds for boss and doctrine banners — the moments that deserve weight.")]
        [SerializeField] private float doctrineBannerSeconds = 1.5f;

        [Tooltip("[TUNE] An enemy with base health at or above this marks its wave as a BOSS wave in banners and the queue (matches the Colossus class).")]
        [SerializeField] private float bossPreviewHpThreshold = 1000f;

        [Header("Wave info placement")]
        [Tooltip("Retire the authored wave panel at runtime: the wave count folds into the Start button's label and the composition rows dock above that button, shown only while it is. Off = the scene's authored layout.")]
        [SerializeField] private bool demoteWavePanel = true;      // [TUNE]
        [Tooltip("Scale applied to the composition rows docked over the Start button.")]
        [SerializeField] private float wavePanelScale = 0.72f;     // [TUNE]

        [Header("Salvage pips (R-UI-5)")]
        [Tooltip("[TUNE] Max salvage pips in flight at once — kills beyond this just tick the counter (never queue).")]
        [SerializeField] private int maxConcurrentPips = 8;

        [Tooltip("[TUNE] Unscaled seconds a pip takes from the crash site to the salvage counter.")]
        [SerializeField] private float pipFlightSeconds = 0.55f;

        private GameManager _gm;
        private readonly List<Image> _segments = new List<Image>();
        private readonly List<GameObject> _previewCells = new List<GameObject>();
        private Coroutine _salvageTween;
        private int _shownSalvage;
        private Corehold.Enemies.Enemy _colossus;

        private void Awake()
        {
            if (waveManager == null)
                waveManager = FindFirstObjectByType<WaveManager>();
            if (theme == null)
                theme = UITheme.Instance;
        }

        private void OnEnable()
        {
            _gm = GameManager.Instance;
            if (_gm != null)
            {
                _gm.OnSalvageChanged += HandleSalvageChanged;
                _gm.OnIntegrityChanged += HandleIntegrityChanged;
                _gm.OnStateChanged += HandleStateChanged;
                _gm.OnKillSalvage += HandleKillSalvage;
            }
            if (waveManager != null)
            {
                waveManager.OnWaveStarted += HandleWaveChanged;
                waveManager.OnWaveStarted += HandleWaveStartedBanner;
                waveManager.OnWaveComplete += HandleWaveChanged;
                waveManager.OnWaveComplete += HandleWaveCompleteCloseCall;
                waveManager.OnLiveCountChanged += HandleLiveCountChanged;
            }

            if (startWaveButton != null) startWaveButton.onClick.AddListener(OnStartWave);
            if (speedButton != null) speedButton.onClick.AddListener(OnToggleSpeed);
            if (pauseButton != null) pauseButton.onClick.AddListener(OnPause);
        }

        private void OnDisable()
        {
            if (_gm != null)
            {
                _gm.OnSalvageChanged -= HandleSalvageChanged;
                _gm.OnIntegrityChanged -= HandleIntegrityChanged;
                _gm.OnStateChanged -= HandleStateChanged;
                _gm.OnKillSalvage -= HandleKillSalvage;
            }
            if (waveManager != null)
            {
                waveManager.OnWaveStarted -= HandleWaveChanged;
                waveManager.OnWaveStarted -= HandleWaveStartedBanner;
                waveManager.OnWaveComplete -= HandleWaveChanged;
                waveManager.OnWaveComplete -= HandleWaveCompleteCloseCall;
                waveManager.OnLiveCountChanged -= HandleLiveCountChanged;
            }
            if (startWaveButton != null) startWaveButton.onClick.RemoveListener(OnStartWave);
            if (speedButton != null) speedButton.onClick.RemoveListener(OnToggleSpeed);
            if (pauseButton != null) pauseButton.onClick.RemoveListener(OnPause);
        }

        private void Start()
        {
            // Late-bind in case GameManager was not ready during OnEnable.
            if (_gm == null)
            {
                _gm = GameManager.Instance;
                if (_gm != null)
                {
                    _gm.OnSalvageChanged += HandleSalvageChanged;
                    _gm.OnIntegrityChanged += HandleIntegrityChanged;
                    _gm.OnStateChanged += HandleStateChanged;
                    _gm.OnKillSalvage += HandleKillSalvage;
                }
            }

            BuildIntegritySegments();
            DemoteWavePanel();
            RefreshAll();
        }

        /// <summary>
        /// The wave panel is GONE (user: "ça sert à quoi?"). Its two facts moved
        /// to where they are used: the wave COUNT lives in the Start button's
        /// own label, and the next-wave composition docks right ABOVE that
        /// button — visible only while the button is (the info exists exactly
        /// when the start/chain decision does). For scenes still carrying the
        /// old authored WavePanel, the composition row is pulled OUT and the
        /// panel + label are disabled; rebuilt scenes bake this layout natively
        /// and re-applying is harmless.
        /// </summary>
        private void DemoteWavePanel()
        {
            if (!demoteWavePanel || previewRow == null)
                return;

            RectTransform panel = null;
            if (waveLabel != null &&
                waveLabel.rectTransform.parent == previewRow.parent &&
                previewRow.parent is RectTransform shared &&
                shared != (RectTransform)transform &&
                shared.GetComponent<Canvas>() == null)
            {
                panel = shared;
            }

            var startRt = startWaveButton != null ? (RectTransform)startWaveButton.transform : null;
            if (startRt != null)
            {
                previewRow.SetParent(startRt.parent, false);
                previewRow.anchorMin = startRt.anchorMin;
                previewRow.anchorMax = startRt.anchorMax;
                previewRow.pivot = startRt.pivot;
                previewRow.sizeDelta = new Vector2(300f, 70f);
                previewRow.anchoredPosition = startRt.anchoredPosition +
                    Vector2.up * (startRt.sizeDelta.y + 8f);
                previewRow.localScale = Vector3.one * Mathf.Clamp(wavePanelScale, 0.4f, 1f);
                var hlg = previewRow.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                    hlg.childAlignment = TextAnchor.MiddleRight;
            }

            if (panel != null)
                panel.gameObject.SetActive(false);
            else if (waveLabel != null)
                waveLabel.gameObject.SetActive(false);
        }

        // ----- Initial full refresh -----

        private void RefreshAll()
        {
            if (_gm != null)
            {
                _shownSalvage = _gm.Salvage;
                if (salvageValue != null) salvageValue.text = _shownSalvage.ToString();
                RefreshIntegrity(_gm.Integrity);
            }
            RefreshWave();
            RefreshStartButton();
            RefreshSpeed();
            if (_gm != null)
                RefreshGuideButton(_gm.State);
        }

        // ----- Salvage (animated counter, unscaled) -----

        private void HandleSalvageChanged(int value)
        {
            if (salvageValue == null)
                return;
            if (_salvageTween != null)
                StopCoroutine(_salvageTween);
            _salvageTween = StartCoroutine(TweenSalvage(value));
        }

        private IEnumerator TweenSalvage(int target)
        {
            int from = _shownSalvage;
            float t = 0f;
            const float dur = 0.35f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; // GDD §9.6 — UI does not scale with 2×
                int v = Mathf.RoundToInt(Mathf.Lerp(from, target, t / dur));
                salvageValue.text = v.ToString();
                yield return null;
            }
            _shownSalvage = target;
            salvageValue.text = target.ToString();
            _salvageTween = null;
        }

        // ----- Integrity (segmented bar) -----

        private void BuildIntegritySegments()
        {
            if (integritySegments == null || integritySegmentPrefabSource == null || _gm == null)
                return;

            int max = GameManager.StartingIntegrityFor(_gm.Difficulty);
            // Clear any old segments.
            foreach (var s in _segments)
                if (s != null) Destroy(s.gameObject);
            _segments.Clear();

            integritySegmentPrefabSource.gameObject.SetActive(false);
            for (int i = 0; i < max; i++)
            {
                var img = Instantiate(integritySegmentPrefabSource, integritySegments);
                img.gameObject.SetActive(true);
                img.name = $"Seg_{i}";
                _segments.Add(img);
            }
        }

        private void HandleIntegrityChanged(int value)
        {
            RefreshIntegrity(value);
            // Flash the bar red on a leak (GDD §3.3).
            StartCoroutine(FlashIntegrity());
        }

        private void RefreshIntegrity(int value)
        {
            if (integrityValue != null)
            {
                int max = _gm != null ? GameManager.StartingIntegrityFor(_gm.Difficulty) : value;
                integrityValue.text = $"{value}/{max}";
            }
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] == null) continue;
                bool lit = i < value;
                Color c = lit ? SegmentColor(value) : new Color(0.12f, 0.14f, 0.16f, 0.85f);
                _segments[i].color = c;
            }
        }

        private Color SegmentColor(int value)
        {
            // Cyan while healthy; amber shift below 20% (mirrors the Core, GDD §5.4).
            if (_gm == null)
                return theme != null ? theme.cyan : Color.cyan;
            int max = GameManager.StartingIntegrityFor(_gm.Difficulty);
            float frac = max > 0 ? (float)value / max : 1f;
            if (frac <= 0.2f) return theme != null ? theme.danger : Color.red;
            if (frac <= 0.5f) return theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f);
            return theme != null ? theme.cyan : Color.cyan;
        }

        private IEnumerator FlashIntegrity()
        {
            if (integritySegments == null)
                yield break;
            var img = integritySegments.GetComponent<Image>();
            if (img == null)
                yield break;
            Color baseCol = img.color;
            img.color = theme != null ? theme.danger : Color.red;
            float t = 0f;
            while (t < 0.25f)
            {
                t += Time.unscaledDeltaTime;
                img.color = Color.Lerp(theme != null ? theme.danger : Color.red, baseCol, t / 0.25f);
                yield return null;
            }
            img.color = baseCol;
        }

        // ----- Wave label + next-wave preview -----

        private void HandleWaveChanged(int waveNumber)
        {
            RefreshWave();
            RefreshStartButton();
        }

        private void HandleLiveCountChanged(int count)
        {
            RefreshStartButton();
            RefreshColossusBar();
        }

        private void HandleStateChanged(GameState state)
        {
            RefreshStartButton();
            RefreshGuideButton(state);
        }

        // ----- Field guide access (R-UI-7): a small button, build phase only -----

        [Tooltip("Show the standalone '?' guide button during build phases. OFF by default (screen rationalization): the FIELD GUIDE lives in the pause menu, one corner item fewer.")]
        [SerializeField] private bool guideButtonEnabled = false;  // [TUNE]

        private GameObject _guideButton;

        private void RefreshGuideButton(GameState state)
        {
            // Between waves only — mid-wave the book would be one more thing to
            // mis-tap; pause still offers it any time.
            bool show = guideButtonEnabled && state == GameState.Build;
            if (!show)
            {
                if (_guideButton != null) _guideButton.SetActive(false);
                return;
            }
            EnsureGuideButton();
            if (_guideButton != null) _guideButton.SetActive(true);
        }

        private void EnsureGuideButton()
        {
            if (_guideButton != null || pauseButton == null)
                return;

            // Ride beside the pause button, stacking AWAY from its screen edge
            // (pause sits at the bottom in the shipped layout — riding "down"
            // from there would leave the screen).
            var src = (RectTransform)pauseButton.transform;
            _guideButton = new GameObject("GuideButton", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)_guideButton.transform;
            rt.SetParent(src.parent, false);
            rt.anchorMin = src.anchorMin;
            rt.anchorMax = src.anchorMax;
            rt.pivot = src.pivot;
            rt.sizeDelta = src.sizeDelta;
            rt.anchoredPosition = src.anchoredPosition
                + (src.anchorMin.y < 0.5f ? Vector2.up : Vector2.down)
                  * (Mathf.Max(30f, src.sizeDelta.y) + 10f);

            var img = _guideButton.GetComponent<Image>();
            var srcImg = pauseButton.GetComponent<Image>();
            if (srcImg != null && srcImg.sprite != null)
            {
                img.sprite = srcImg.sprite;
                img.type = srcImg.type;
                img.color = srcImg.color;
            }
            else
            {
                img.color = new Color(0.12f, 0.18f, 0.22f, 0.9f);
            }

            var txtGo = new GameObject("Label", typeof(RectTransform));
            var txtRt = (RectTransform)txtGo.transform;
            txtRt.SetParent(rt, false);
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = txtRt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = "?";
            txt.alignment = TextAlignmentOptions.Center;
            txt.fontStyle = FontStyles.Bold;
            txt.fontSize = theme != null ? theme.fontSizeSmall : 22f;
            txt.color = theme != null ? theme.cyan : Color.cyan;
            txt.raycastTarget = false;
            if (theme != null && theme.font != null)
                txt.font = theme.font;

            _guideButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
                var canvas = GetComponentInParent<Canvas>();
                if (canvas != null)
                    AlmanacScreen.Toggle(canvas.rootCanvas.transform);
            });
        }

        // ----- Close call (R3) -----

        private GameObject _closeCallBanner;
        private CanvasGroup _closeCallGroup;
        private TMP_Text _closeCallText;
        private Coroutine _closeCallRoutine;

        /// <summary>
        /// R3: when the field clears with the Core nearly lost, stamp the moment —
        /// a CLOSE CALL banner, a sting, and a brief slow-mo on that last kill
        /// (the dip is owned by GameManager and is interrupt-safe).
        /// </summary>
        private void HandleWaveCompleteCloseCall(int waveNumber)
        {
            if (closeCallIntegrityThreshold <= 0 || _gm == null)
                return;
            if (_gm.Integrity <= 0 || _gm.Integrity > closeCallIntegrityThreshold)
                return;

            if (AudioDirector.Instance != null)
                AudioDirector.Instance.Play(AudioDirector.Sfx.CloseCall);

            _gm.TimeDip(closeCallDipScale, closeCallDipSeconds);

            ShowBanner("CLOSE CALL", theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f),
                       closeCallBannerSeconds);
        }

        /// <summary>
        /// One shared banner for every stamped moment (close call, wave start,
        /// boss contact, doctrine call-outs — R-UI-6). One line, centred high,
        /// pop-in/fade-out on UNSCALED time, never blocks input, and a new
        /// banner REPLACES the current one — moments never queue into a backlog.
        /// </summary>
        private void ShowBanner(string text, Color color, float seconds)
        {
            if (seconds <= 0f)
                return;
            EnsureCloseCallBanner();
            _closeCallText.text = text;
            _closeCallText.color = color;
            if (_closeCallRoutine != null)
                StopCoroutine(_closeCallRoutine);
            _closeCallRoutine = StartCoroutine(BannerRoutine(seconds));
        }

        /// <summary>
        /// Wave-start stamp (R-UI-6): a plain wave gets a short, quiet banner; a
        /// wave with a boss-class unit or a mutator gets the weighted moment —
        /// doctrine name + plain-words effect line, or HEAVY CONTACT. Names come
        /// from the narrative bible; the effect line stays plain gameplay words.
        /// </summary>
        private void HandleWaveStartedBanner(int waveNumber)
        {
            if (waveManager == null)
                return;

            // Assault levels announce their rule once, up front — pacing must be
            // told, never discovered (user feedback on e2).
            if (waveNumber == 1 && waveManager.AssaultPacing)
            {
                ShowBanner("ASSAULT PROTOCOL\n<size=55%>Waves keep coming while the field is clear</size>",
                           theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f), doctrineBannerSeconds);
                return;
            }

            WaveDefinition started = waveManager.PeekWave(-1);
            WaveMutator mutators = waveManager.MutatorsForWave(waveNumber);
            bool boss = WaveHasBoss(started);

            if (mutators != WaveMutator.None)
            {
                (string title, string clause) = DoctrineText(mutators);
                Color c = boss && theme != null ? theme.danger
                        : theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f);
                ShowBanner($"{title}\n<size=55%>{clause}</size>", c, doctrineBannerSeconds);
            }
            else if (boss)
            {
                ShowBanner($"WAVE {waveNumber} — HEAVY CONTACT",
                           theme != null ? theme.danger : Color.red, doctrineBannerSeconds);
            }
            else if (waveNumber > 1)
            {
                // Wave 1 skips the plain banner — the START WAVE tap was the moment.
                ShowBanner($"WAVE {waveNumber}",
                           theme != null ? theme.cyan : Color.cyan, waveBannerSeconds);
            }
        }

        private bool WaveHasBoss(WaveDefinition wave)
        {
            if (wave == null || wave.groups == null)
                return false;
            foreach (var g in wave.groups)
                if (g.enemy != null && g.count > 0 && g.enemy.baseHealth >= bossPreviewHpThreshold)
                    return true;
            return false;
        }

        /// <summary>Doctrine names per the narrative bible; effect clauses in
        /// plain gameplay words. Multiple flags fall back to a combined stamp.</summary>
        private static (string, string) DoctrineText(WaveMutator m)
        {
            bool storm = (m & WaveMutator.Storm) != 0;
            bool convoy = (m & WaveMutator.Convoy) != 0;
            bool over = (m & WaveMutator.Overcharge) != 0;
            bool black = (m & WaveMutator.Blackout) != 0;
            int flags = (storm ? 1 : 0) + (convoy ? 1 : 0) + (over ? 1 : 0) + (black ? 1 : 0);
            if (flags > 1)
                return ("COMBINED DOCTRINES", "The machines adapt — expect everything");
            if (storm) return ("TAILWIND DOCTRINE", "Air units move faster");
            if (convoy) return ("COLUMN DOCTRINE", "Everything comes down one approach");
            if (over) return ("BURNOUT DOCTRINE", "Tougher units, richer salvage");
            return ("GRIDCUT DOCTRINE", "Turrets see half as far — light them up");
        }

        private void EnsureCloseCallBanner()
        {
            if (_closeCallBanner != null)
                return;

            _closeCallBanner = new GameObject("CloseCallBanner",
                typeof(RectTransform), typeof(CanvasGroup));
            var rt = (RectTransform)_closeCallBanner.transform;
            rt.SetParent(transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.70f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(760f, 150f);   // room for a doctrine two-liner

            _closeCallGroup = _closeCallBanner.GetComponent<CanvasGroup>();
            _closeCallGroup.blocksRaycasts = false;
            _closeCallGroup.interactable = false;

            var textGo = new GameObject("Label", typeof(RectTransform));
            var textRt = (RectTransform)textGo.transform;
            textRt.SetParent(rt, false);
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = textRt.offsetMax = Vector2.zero;

            _closeCallText = textGo.AddComponent<TextMeshProUGUI>();
            _closeCallText.text = "CLOSE CALL";
            _closeCallText.alignment = TextAlignmentOptions.Center;
            _closeCallText.fontStyle = FontStyles.Bold;
            _closeCallText.fontSize = theme != null ? theme.fontSizeLarge * 1.6f : 54f;
            _closeCallText.color = theme != null ? theme.amber : new Color(1f, 0.6f, 0.1f);
            if (theme != null && theme.font != null)
                _closeCallText.font = theme.font;

            _closeCallBanner.SetActive(false);
        }

        private IEnumerator BannerRoutine(float seconds)
        {
            EnsureCloseCallBanner();
            _closeCallBanner.SetActive(true);

            var rt = (RectTransform)_closeCallBanner.transform;
            float total = Mathf.Max(0.4f, seconds);
            const float inDur = 0.14f;
            float outDur = Mathf.Min(0.45f, total * 0.35f);
            float hold = total - inDur - outDur;

            // Pop in (unscaled — the dip must not slow its own banner).
            float t = 0f;
            while (t < inDur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / inDur);
                _closeCallGroup.alpha = k;
                rt.localScale = Vector3.one * Mathf.Lerp(1.18f, 1f, k);
                yield return null;
            }
            _closeCallGroup.alpha = 1f;
            rt.localScale = Vector3.one;

            t = 0f;
            while (t < hold)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            t = 0f;
            while (t < outDur)
            {
                t += Time.unscaledDeltaTime;
                _closeCallGroup.alpha = 1f - Mathf.Clamp01(t / outDur);
                yield return null;
            }

            _closeCallBanner.SetActive(false);
            _closeCallRoutine = null;
        }

        private void RefreshWave()
        {
            if (waveManager == null)
                return;

            int count = waveManager.WaveCount;
            int current = Mathf.Clamp(waveManager.NextWaveIndex + 1, 1, Mathf.Max(1, count));
            if (waveLabel != null)
                waveLabel.text = $"WAVE {current} / {count}";

            BuildPreview(waveManager.NextWave);
        }

        /// <summary>
        /// Build the wave QUEUE (R-UI-4, Defense Grid pattern): the next wave as
        /// full-size cells, the wave after it as a smaller, dimmer row beneath —
        /// players plan two waves ahead, not one. One cell per enemy TYPE with
        /// icon, total count and an armour pip (GDD §9.1, §9.4); boss-class
        /// entries get the danger colour on their count. Cells are pooled.
        /// </summary>
        private void BuildPreview(WaveDefinition wave)
        {
            if (previewRow == null || previewCellTemplate == null)
                return;

            FillPreviewRow(_previewCells, previewRow, wave);

            EnsureQueueRow();
            if (_previewRow2 != null)
                FillPreviewRow(_previewCells2, _previewRow2,
                               waveManager != null ? waveManager.PeekWave(1) : null);
        }

        private void FillPreviewRow(List<GameObject> cells, RectTransform row, WaveDefinition wave)
        {
            foreach (var c in cells)
                c.SetActive(false);

            if (wave == null || wave.groups == null)
                return;

            // Aggregate counts per enemy definition, preserving first-seen order.
            var order = new List<EnemyDefinition>();
            var counts = new Dictionary<EnemyDefinition, int>();
            foreach (var g in wave.groups)
            {
                if (g.enemy == null || g.count <= 0) continue;
                if (!counts.ContainsKey(g.enemy)) { counts[g.enemy] = 0; order.Add(g.enemy); }
                counts[g.enemy] += g.count;
            }

            for (int i = 0; i < order.Count; i++)
            {
                GameObject cell = GetPreviewCell(cells, row, i);
                cell.SetActive(true);
                EnemyDefinition def = order[i];

                var icon = cell.transform.Find("Icon")?.GetComponent<Image>();
                var countTxt = cell.transform.Find("Count")?.GetComponent<TMP_Text>();
                var pip = cell.transform.Find("Pip")?.GetComponent<Image>();

                if (icon != null)
                {
                    icon.sprite = def.icon;
                    icon.enabled = def.icon != null;
                }
                if (countTxt != null)
                {
                    countTxt.text = $"×{counts[def]}";
                    // Boss-class read in the queue itself (R-UI-4).
                    countTxt.color = def.baseHealth >= bossPreviewHpThreshold && theme != null
                        ? theme.danger : Color.white;
                }
                if (pip != null)
                {
                    pip.color = theme != null ? theme.ArmourColor(def.armourType) : Color.white;
                    // Tooltip-free: also add the letter on the pip if it has a label child.
                    var pipLabel = pip.transform.Find("Letter")?.GetComponent<TMP_Text>();
                    if (pipLabel != null)
                        pipLabel.text = UITheme.ArmourLetter(def.armourType);
                }
            }
        }

        private GameObject GetPreviewCell(List<GameObject> cells, RectTransform row, int index)
        {
            while (cells.Count <= index)
            {
                var c = Instantiate(previewCellTemplate, row);
                c.name = $"PreviewCell_{cells.Count}";
                cells.Add(c);
            }
            return cells[index];
        }

        // Second queue row, built programmatically so every existing and generated
        // scene gets it with no scene edits (same doctrine as the banners/pips).
        private RectTransform _previewRow2;
        private readonly List<GameObject> _previewCells2 = new List<GameObject>();

        private void EnsureQueueRow()
        {
            if (_previewRow2 != null || previewRow == null)
                return;

            var go = new GameObject("PreviewRow_Next", typeof(RectTransform));
            _previewRow2 = (RectTransform)go.transform;
            _previewRow2.SetParent(previewRow.parent, false);
            _previewRow2.anchorMin = previewRow.anchorMin;
            _previewRow2.anchorMax = previewRow.anchorMax;
            _previewRow2.pivot = previewRow.pivot;
            _previewRow2.sizeDelta = previewRow.sizeDelta;
            // In the one-line bottom strip (middle-left anchored row) the future
            // row docks to the RIGHT of the current one; the stacked fallback
            // (top/bottom-anchored rows) keeps stacking away from its edge.
            bool strip = Mathf.Abs(previewRow.anchorMin.y - 0.5f) < 0.01f;
            if (strip)
            {
                _previewRow2.anchoredPosition = previewRow.anchoredPosition +
                    Vector2.right * (previewRow.sizeDelta.x + 8f);
            }
            else
            {
                float drop = Mathf.Max(24f, previewRow.rect.height) + 4f;
                bool nearBottom = previewRow.anchorMin.y < 0.5f;
                _previewRow2.anchoredPosition = previewRow.anchoredPosition +
                    (nearBottom ? Vector2.up : Vector2.down) * drop * previewRow.localScale.y;
            }
            _previewRow2.localScale = previewRow.localScale * 0.72f;

            // Mirror the first row's layout behaviour so cells arrange the same way.
            var src = previewRow.GetComponent<HorizontalLayoutGroup>();
            if (src != null)
            {
                var lg = go.AddComponent<HorizontalLayoutGroup>();
                lg.spacing = src.spacing;
                lg.childAlignment = src.childAlignment;
                lg.childControlWidth = src.childControlWidth;
                lg.childControlHeight = src.childControlHeight;
                lg.childForceExpandWidth = src.childForceExpandWidth;
                lg.childForceExpandHeight = src.childForceExpandHeight;
            }

            var group = go.AddComponent<CanvasGroup>();
            group.alpha = 0.7f;           // dimmer: it is the future, not the now
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        // ----- Start wave button -----

        private void RefreshStartButton()
        {
            if (startWaveButton == null)
                return;

            bool hasNext = waveManager != null && waveManager.HasNextWave;
            startWaveButton.gameObject.SetActive(hasNext);

            // The composition rows live and die with the button they inform.
            if (demoteWavePanel && previewRow != null)
            {
                previewRow.gameObject.SetActive(hasNext);
                if (_previewRow2 != null)
                    _previewRow2.gameObject.SetActive(hasNext);
            }

            if (!hasNext)
                return;

            bool waveLive = waveManager.WaveInProgress;
            int nextNum = waveManager.NextWaveIndex + 1;

            // The chain cap has to be VISIBLE, not just enforced: a button that
            // does nothing when pressed reads as a broken button.
            bool canStart = waveManager.CanStartNextWave;
            startWaveButton.interactable = canStart;

            if (startWaveLabel != null)
            {
                if (!canStart)
                {
                    // Name the number that is blocking, not just the fact of it —
                    // "FIELD FULL" over four visible enemies reads as a bug.
                    startWaveLabel.text = $"WAVE {nextNum}\nFIELD {waveManager.CommittedCount}/{waveManager.ChainLockAt}";
                }
                else if (waveLive)
                {
                    // Show the chain bonus: 8 per live enemy, capped 80 (GDD §8.4).
                    int bonus = Mathf.Min(waveManager.LiveCount * 8, 80);
                    startWaveLabel.text = $"CHAIN WAVE {nextNum}\n+{bonus}";
                }
                else
                {
                    // The wave TOTAL rides here since the wave panel's removal —
                    // and an armed countdown (e1) says so on the button itself.
                    int total = waveManager.WaveCount;
                    startWaveLabel.text = _shownAutoSec > 0
                        ? $"START WAVE {nextNum}/{total}\nAUTO IN {_shownAutoSec}s"
                        : $"START WAVE {nextNum}/{total}";
                }
            }
        }

        private void OnStartWave()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (waveManager != null) waveManager.StartNextWave();
            RefreshStartButton();
        }

        // ----- Speed toggle (GDD §9.6) -----

        private void RefreshSpeed()
        {
            if (speedLabel != null)
                speedLabel.text = Time.timeScale > 2.5f ? "3×" : Time.timeScale > 1.5f ? "2×" : "1×";
        }

        /// <summary>
        /// Speed stops (GDD §9.6 + R-UI-8): 1× → 2× always; a third 3× stop only
        /// on CLEARED content — a level (or campaign) the player has already
        /// beaten at this difficulty. First runs keep the certified pacing;
        /// replays get to be brisk (the Defender's Quest lesson: difficulty
        /// should live in decisions, not in waiting).
        /// </summary>
        private void OnToggleSpeed()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            float cur = Time.timeScale;
            if (cur > 2.5f) Time.timeScale = 1f;
            else if (cur > 1.5f) Time.timeScale = SpeedThreeUnlocked() ? 3f : 1f;
            else Time.timeScale = 2f;
            RefreshSpeed();
        }

        private bool SpeedThreeUnlocked()
        {
            var cm = CampaignManager.Instance;
            if (cm != null && cm.HasActiveCampaign)
                return SaveData.GetCampaignBestScore(cm.Active.campaignId) > 0;

            // Single-map runs: the integrity record only exists after a VICTORY
            // on this map at this difficulty (ResultScreen submits it then).
            // Fully qualified: Core and Data both declare a Difficulty enum, and
            // this file imports both namespaces (GameManager + SaveData speak
            // Core's).
            string map = waveManager != null ? waveManager.LevelId : "default";
            Corehold.Core.Difficulty diff = _gm != null ? _gm.Difficulty : Corehold.Core.Difficulty.Normal;
            return SaveData.GetRecord(map, diff, "integrity") > 0;
        }

        // ----- Pause -----

        private void OnPause()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (pauseScreen != null)
                pauseScreen.Show();
        }

        // ----- Salvage pips (R-UI-5) — the economy made physical -----
        // A kill pops a small amber shard at the crash site that flies to the
        // salvage counter (PvZ's world-visible currency, auto-collected — no
        // tapping; hands stay on strategy). Pooled UI images, unscaled time,
        // capped in flight: kills over the cap just tick the counter.

        private Canvas _canvas;
        private readonly List<RectTransform> _pipPool = new List<RectTransform>();
        private int _activePips;

        private Camera CanvasCamera()
        {
            if (_canvas == null)
                _canvas = GetComponentInParent<Canvas>();
            var root = _canvas != null ? _canvas.rootCanvas : null;
            if (root == null)
                return null;
            return root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        }

        private void HandleKillSalvage(int amount, Vector3 worldPos)
        {
            if (salvageValue == null || amount <= 0 || _activePips >= Mathf.Max(1, maxConcurrentPips))
                return;
            Camera worldCam = Camera.main;
            if (worldCam == null)
                return;
            Vector3 screen = worldCam.WorldToScreenPoint(worldPos);
            if (screen.z <= 0f)
                return;   // behind the camera — nothing to show

            var parent = (RectTransform)transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, CanvasCamera(), out Vector2 from);
            Vector2 toScreen = RectTransformUtility.WorldToScreenPoint(CanvasCamera(), salvageValue.rectTransform.position);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, toScreen, CanvasCamera(), out Vector2 to);

            RectTransform pip = GetPip();
            pip.gameObject.SetActive(true);
            pip.anchoredPosition = from;
            pip.sizeDelta = Vector2.one * (amount >= 24 ? 16f : 11f);   // big kills, big shards
            var img = pip.GetComponent<Image>();
            if (img != null)
            {
                Color c = theme != null ? theme.amber : new Color(1f, 0.72f, 0.25f);
                c.a = 1f;
                img.color = c;
            }
            StartCoroutine(FlyPip(pip, from, to));
        }

        private RectTransform GetPip()
        {
            for (int i = 0; i < _pipPool.Count; i++)
                if (!_pipPool[i].gameObject.activeSelf)
                    return _pipPool[i];
            var go = new GameObject($"SalvagePip_{_pipPool.Count}", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(transform, false);
            rt.localRotation = Quaternion.Euler(0f, 0f, 45f);   // diamond = salvage shard, zero assets
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            go.SetActive(false);
            _pipPool.Add(rt);
            return rt;
        }

        private IEnumerator FlyPip(RectTransform pip, Vector2 from, Vector2 to)
        {
            _activePips++;
            float dur = Mathf.Max(0.15f, pipFlightSeconds);
            // Arc through a lifted midpoint — a straight slide reads as UI, a
            // small arc reads as a thing FLUNG off the wreck.
            Vector2 mid = (from + to) * 0.5f + Vector2.up * 26f;
            var img = pip.GetComponent<Image>();
            float t = 0f;
            while (t < dur && pip != null)
            {
                t += Time.unscaledDeltaTime;   // GDD §9.6 — UI ignores the 2× clock
                float k = Mathf.Clamp01(t / dur);
                float e = k * k * (3f - 2f * k);
                pip.anchoredPosition = Vector2.Lerp(Vector2.Lerp(from, mid, e), Vector2.Lerp(mid, to, e), e);
                if (img != null && k > 0.8f)
                {
                    Color c = img.color;
                    c.a = 1f - (k - 0.8f) / 0.2f;
                    img.color = c;
                }
                yield return null;
            }
            if (pip != null)
                pip.gameObject.SetActive(false);
            _activePips = Mathf.Max(0, _activePips - 1);
        }

        // ----- Colossus bar (GDD §9.4) -----

        private void RefreshColossusBar()
        {
            if (colossusBarRoot == null)
                return;

            // Find a live boss (large max health / enrage capable). Cheap at ≤14.
            _colossus = null;
            var live = Corehold.Enemies.Enemy.Live;
            for (int i = 0; i < live.Count; i++)
            {
                var e = live[i];
                if (e != null && e.IsAlive && e.MaxHealth >= 1500f)
                {
                    _colossus = e;
                    break;
                }
            }

            bool show = _colossus != null;
            if (colossusBarRoot.gameObject.activeSelf != show)
                colossusBarRoot.gameObject.SetActive(show);
        }

        private int _shownAutoSec = -1;

        private void Update()
        {
            // Two per-frame HUD reads allowed: the boss bar while a boss is on the
            // field, and the auto-start countdown while one is armed (e1) — the
            // label only rebuilds when the whole second changes. Everything else
            // is event-driven (GDD §9.1).
            float remain = waveManager != null ? waveManager.AutoStartRemaining : -1f;
            int sec = remain > 0f ? Mathf.CeilToInt(remain) : -1;
            if (sec != _shownAutoSec)
            {
                _shownAutoSec = sec;
                RefreshStartButton();
            }

            if (_colossus != null && colossusBarFill != null)
            {
                if (!_colossus.IsAlive)
                {
                    _colossus = null;
                    if (colossusBarRoot != null) colossusBarRoot.gameObject.SetActive(false);
                    return;
                }
                colossusBarFill.fillAmount = Mathf.Clamp01(_colossus.CurrentHealth / Mathf.Max(1f, _colossus.MaxHealth));
                if (colossusBarLabel != null)
                    colossusBarLabel.text = _colossus.name.Replace("(Clone)", "").ToUpperInvariant();
            }
        }
    }
}
