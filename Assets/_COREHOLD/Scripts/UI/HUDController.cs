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
            }
            if (waveManager != null)
            {
                waveManager.OnWaveStarted += HandleWaveChanged;
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
            }
            if (waveManager != null)
            {
                waveManager.OnWaveStarted -= HandleWaveChanged;
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
                }
            }

            BuildIntegritySegments();
            RefreshAll();
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

            if (_closeCallRoutine != null)
                StopCoroutine(_closeCallRoutine);
            _closeCallRoutine = StartCoroutine(CloseCallBannerRoutine());
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
            rt.sizeDelta = new Vector2(640f, 96f);

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

        private IEnumerator CloseCallBannerRoutine()
        {
            EnsureCloseCallBanner();
            _closeCallBanner.SetActive(true);

            var rt = (RectTransform)_closeCallBanner.transform;
            float total = Mathf.Max(0.5f, closeCallBannerSeconds);
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
        /// Build the next-wave preview from its groups: one cell per enemy TYPE with
        /// its icon, total count and an armour pip (GDD §9.1, §9.4). Cells are pooled
        /// GameObjects cloned from a template.
        /// </summary>
        private void BuildPreview(WaveDefinition wave)
        {
            if (previewRow == null || previewCellTemplate == null)
                return;

            // Hide all existing cells first.
            foreach (var c in _previewCells)
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
                GameObject cell = GetPreviewCell(i);
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
                    countTxt.text = $"×{counts[def]}";
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

        private GameObject GetPreviewCell(int index)
        {
            while (_previewCells.Count <= index)
            {
                var c = Instantiate(previewCellTemplate, previewRow);
                c.name = $"PreviewCell_{_previewCells.Count}";
                _previewCells.Add(c);
            }
            return _previewCells[index];
        }

        // ----- Start wave button -----

        private void RefreshStartButton()
        {
            if (startWaveButton == null)
                return;

            bool hasNext = waveManager != null && waveManager.HasNextWave;
            startWaveButton.gameObject.SetActive(hasNext);
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
                    startWaveLabel.text = $"WAVE {nextNum}\nFIELD FULL";
                }
                else if (waveLive)
                {
                    // Show the chain bonus: 8 per live enemy, capped 80 (GDD §8.4).
                    int bonus = Mathf.Min(waveManager.LiveCount * 8, 80);
                    startWaveLabel.text = $"CHAIN WAVE {nextNum}\n+{bonus}";
                }
                else
                {
                    startWaveLabel.text = $"START WAVE {nextNum}";
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
                speedLabel.text = Time.timeScale > 1.5f ? "2×" : "1×";
        }

        private void OnToggleSpeed()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            Time.timeScale = Time.timeScale > 1.5f ? 1f : 2f;
            RefreshSpeed();
        }

        // ----- Pause -----

        private void OnPause()
        {
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (pauseScreen != null)
                pauseScreen.Show();
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

        private void Update()
        {
            // The ONE per-frame HUD read allowed: the boss bar, only while a boss is
            // on the field. Everything else is event-driven (GDD §9.1).
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
