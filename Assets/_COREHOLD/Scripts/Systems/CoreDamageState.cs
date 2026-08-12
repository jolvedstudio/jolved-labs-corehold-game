using System.Collections;
using System.Collections.Generic;
using Corehold.Core;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Mirrors Core integrity physically on the Firadzo Shield Generator dome
    /// (GDD §5.4, §3.3). Event-driven off <see cref="GameManager.OnIntegrityChanged"/>
    /// — nothing polls except the flicker coroutine, which only runs in the final
    /// critical band.
    ///
    /// Three staged states, keyed off the integrity FRACTION (max integrity differs
    /// per tier, so absolute thresholds would be wrong):
    ///
    ///   • <b>≤ 66%</b> — one dome segment goes dark and periodically sparks.
    ///   • <b>≤ 33%</b> — a second dome segment goes dark and sparks.
    ///   • <b>&lt; 20%</b> — the whole structure flickers and the dome emissive
    ///     shifts from cyan toward amber.
    ///
    /// The amber shift is colour-only, so per the GDD it is <b>always paired with the
    /// darkened segments</b> (and the numeric HUD value, handled by the HUD): when the
    /// critical state is active both segments are already dark, so a colour-blind
    /// player still reads "two segments gone + structure flickering" without relying on
    /// the hue change alone.
    ///
    /// Emissive is driven through a <see cref="MaterialPropertyBlock"/> so no material
    /// is instanced or leaked, and the sparks reuse the pooled
    /// <see cref="VFXDirector.Effect.ImpactSpark"/> effect (no Instantiate on a
    /// gameplay path). All timing is unscaled where it must survive pause/2× — the
    /// spark cadence and flicker use <see cref="WaitForSecondsRealtime"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class CoreDamageState : MonoBehaviour
    {
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Dome")]
        [Tooltip("The rotating dome/head renderers whose emissive shifts cyan→amber below 20% and flickers in the critical band. Auto-found under the dome root if empty.")]
        [SerializeField] private Renderer[] domeRenderers;

        [Tooltip("Root the dome renderers are searched under when the array is empty (e.g. Shield_generator_2_head).")]
        [SerializeField] private Transform domeRoot;

        [Header("Damage segments (GDD §5.4)")]
        [Tooltip("World-space anchors where a darkened/sparking dome segment is represented. Index 0 darkens at 66%, index 1 at 33%. Created automatically around the dome if fewer than two are assigned.")]
        [SerializeField] private Transform[] segmentAnchors = new Transform[2];

        [Tooltip("Renderers to darken for segment 0 (the 66% segment). Optional — if empty, only sparks play at the anchor.")]
        [SerializeField] private Renderer[] segment0Renderers;

        [Tooltip("Renderers to darken for segment 1 (the 33% segment). Optional.")]
        [SerializeField] private Renderer[] segment1Renderers;

        [Header("Colours")]
        [Tooltip("Healthy dome emissive (cyan).")]
        [ColorUsage(false, true)]
        [SerializeField] private Color cyanEmissive = new Color(0.15f, 0.9f, 1f, 1f) * 2f;

        [Tooltip("Critical dome emissive (amber) reached below 20% integrity.")]
        [ColorUsage(false, true)]
        [SerializeField] private Color amberEmissive = new Color(1f, 0.55f, 0.08f, 1f) * 2.2f;

        [Tooltip("Emissive a darkened segment is driven to (near-black).")]
        [ColorUsage(false, true)]
        [SerializeField] private Color darkEmissive = new Color(0.02f, 0.02f, 0.03f, 1f);

        [Header("Fractional thresholds (GDD §5.4)")]
        [Range(0f, 1f)] [SerializeField] private float segment0Fraction = 0.66f;
        [Range(0f, 1f)] [SerializeField] private float segment1Fraction = 0.33f;
        [Range(0f, 1f)] [SerializeField] private float criticalFraction = 0.20f;

        [Header("Spark / flicker cadence (unscaled)")]
        [Tooltip("Seconds between spark bursts on a darkened segment (real time — survives pause and 2×).")]
        [SerializeField] private float sparkInterval = 1.4f;

        [Tooltip("Random jitter added to each spark interval so the two segments don't spark in lockstep.")]
        [SerializeField] private float sparkJitter = 0.6f;

        private GameManager _gm;
        private MaterialPropertyBlock _mpb;

        // Per-renderer captured base emissive so darkened segments and the dome can
        // be restored exactly (e.g. on retry / integrity refill).
        private readonly Dictionary<Renderer, Color> _baseEmissive = new Dictionary<Renderer, Color>();

        private bool _seg0Dark, _seg1Dark, _critical;
        private Coroutine _sparkRoutine;
        private Coroutine _flickerRoutine;
        private int _maxIntegrity = 20;

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            ResolveDomeRenderers();
            EnsureSegmentAnchors();
            CaptureBaseEmissive();
        }

        private void OnEnable()
        {
            _gm = GameManager.Instance;
            if (_gm != null)
            {
                _gm.OnIntegrityChanged += HandleIntegrityChanged;
                _gm.OnStateChanged += HandleStateChanged;
                _maxIntegrity = GameManager.StartingIntegrityFor(_gm.Difficulty);
            }
        }

        private void OnDisable()
        {
            if (_gm != null)
            {
                _gm.OnIntegrityChanged -= HandleIntegrityChanged;
                _gm.OnStateChanged -= HandleStateChanged;
            }
            StopSparks();
            StopFlicker();
        }

        private void Start()
        {
            // Late-bind in case GameManager was not ready during OnEnable.
            if (_gm == null)
            {
                _gm = GameManager.Instance;
                if (_gm != null)
                {
                    _gm.OnIntegrityChanged += HandleIntegrityChanged;
                    _gm.OnStateChanged += HandleStateChanged;
                }
            }
            if (_gm != null)
            {
                _maxIntegrity = GameManager.StartingIntegrityFor(_gm.Difficulty);
                Evaluate(_gm.Integrity);
            }
        }

        private void HandleStateChanged(GameState state)
        {
            // A fresh run (ConfigureRun) re-sets integrity to full and re-raises the
            // event, so max may have changed with the tier; refresh it here.
            if (_gm != null)
                _maxIntegrity = GameManager.StartingIntegrityFor(_gm.Difficulty);
        }

        private void HandleIntegrityChanged(int value)
        {
            Evaluate(value);
        }

        // ================================================================================
        //  State evaluation
        // ================================================================================

        private void Evaluate(int integrity)
        {
            if (_maxIntegrity <= 0)
                _maxIntegrity = _gm != null ? GameManager.StartingIntegrityFor(_gm.Difficulty) : 20;

            float frac = Mathf.Clamp01((float)integrity / _maxIntegrity);

            bool wantSeg0 = frac <= segment0Fraction;
            bool wantSeg1 = frac <= segment1Fraction;
            bool wantCritical = frac < criticalFraction;

            // GDD: the amber shift is colour-only, so it is ALWAYS paired with the
            // darkened segments. Entering the critical band forces both segments dark
            // regardless of their own thresholds (they will already be past them, but
            // this makes the pairing explicit and robust).
            if (wantCritical)
            {
                wantSeg0 = true;
                wantSeg1 = true;
            }

            SetSegment0Dark(wantSeg0);
            SetSegment1Dark(wantSeg1);
            SetCritical(wantCritical);

            UpdateSparkRoutine();
        }

        private void SetSegment0Dark(bool dark)
        {
            if (_seg0Dark == dark)
                return;
            _seg0Dark = dark;
            ApplyEmissive(segment0Renderers, dark ? (Color?)darkEmissive : null);
        }

        private void SetSegment1Dark(bool dark)
        {
            if (_seg1Dark == dark)
                return;
            _seg1Dark = dark;
            ApplyEmissive(segment1Renderers, dark ? (Color?)darkEmissive : null);
        }

        private void SetCritical(bool critical)
        {
            if (_critical == critical)
                return;
            _critical = critical;

            if (critical)
            {
                // Colour-only cue (paired with the two dark segments above).
                SetDomeEmissive(amberEmissive);
                StartFlicker();
            }
            else
            {
                StopFlicker();
                SetDomeEmissive(cyanEmissive);
            }
        }

        // ================================================================================
        //  Sparks — pooled ImpactSpark bursts on each darkened segment (unscaled cadence)
        // ================================================================================

        private void UpdateSparkRoutine()
        {
            bool anyDark = _seg0Dark || _seg1Dark;
            if (anyDark && _sparkRoutine == null && isActiveAndEnabled)
                _sparkRoutine = StartCoroutine(SparkLoop());
            else if (!anyDark)
                StopSparks();
        }

        private IEnumerator SparkLoop()
        {
            while (_seg0Dark || _seg1Dark)
            {
                if (_seg0Dark) SparkAt(0);
                // Small stagger so the two segments never spark on the same frame.
                if (_seg1Dark)
                {
                    yield return new WaitForSecondsRealtime(0.12f);
                    SparkAt(1);
                }

                float wait = sparkInterval + Random.Range(0f, sparkJitter);
                yield return new WaitForSecondsRealtime(wait);
            }
            _sparkRoutine = null;
        }

        private void SparkAt(int segmentIndex)
        {
            if (VFXDirector.Instance == null)
                return;
            Vector3 pos = SegmentPosition(segmentIndex);
            VFXDirector.Instance.Play(VFXDirector.Effect.ImpactSpark, pos);
        }

        private Vector3 SegmentPosition(int index)
        {
            if (segmentAnchors != null && index < segmentAnchors.Length && segmentAnchors[index] != null)
                return segmentAnchors[index].position;
            // Fallback: around the dome centre.
            Vector3 c = domeRoot != null ? domeRoot.position : transform.position;
            return c + (index == 0 ? new Vector3(0.8f, 0.6f, 0.4f) : new Vector3(-0.8f, 0.6f, -0.4f));
        }

        private void StopSparks()
        {
            if (_sparkRoutine != null)
            {
                StopCoroutine(_sparkRoutine);
                _sparkRoutine = null;
            }
        }

        // ================================================================================
        //  Flicker — the whole structure flickers below 20% (unscaled)
        // ================================================================================

        private void StartFlicker()
        {
            StopFlicker();
            if (isActiveAndEnabled)
                _flickerRoutine = StartCoroutine(FlickerLoop());
        }

        private IEnumerator FlickerLoop()
        {
            // Rapid, irregular dip between amber and a dimmed amber — reads as a
            // failing power feed. Unscaled so it still flickers while paused / at 2×.
            while (_critical)
            {
                SetDomeEmissive(amberEmissive * Random.Range(0.25f, 0.55f));
                yield return new WaitForSecondsRealtime(Random.Range(0.04f, 0.12f));
                SetDomeEmissive(amberEmissive);
                yield return new WaitForSecondsRealtime(Random.Range(0.10f, 0.28f));
            }
            _flickerRoutine = null;
        }

        private void StopFlicker()
        {
            if (_flickerRoutine != null)
            {
                StopCoroutine(_flickerRoutine);
                _flickerRoutine = null;
            }
        }

        // ================================================================================
        //  Emissive helpers (MaterialPropertyBlock — no material instancing)
        // ================================================================================

        private void SetDomeEmissive(Color emissive)
        {
            ApplyEmissive(domeRenderers, emissive);
        }

        /// <summary>
        /// Push an emissive colour onto a set of renderers via a shared property
        /// block. Passing null restores each renderer's captured base emissive.
        /// </summary>
        private void ApplyEmissive(Renderer[] renderers, Color? emissive)
        {
            if (renderers == null)
                return;

            foreach (var r in renderers)
            {
                if (r == null)
                    continue;

                Color target;
                if (emissive.HasValue)
                    target = emissive.Value;
                else if (!_baseEmissive.TryGetValue(r, out target))
                    target = cyanEmissive;

                if (_mpb == null)
                    _mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionColorId, target);
                r.SetPropertyBlock(_mpb);
            }
        }

        // ================================================================================
        //  Setup helpers
        // ================================================================================

        private void ResolveDomeRenderers()
        {
            if (domeRenderers != null && domeRenderers.Length > 0)
                return;

            Transform root = domeRoot != null ? domeRoot : transform;
            domeRenderers = root.GetComponentsInChildren<Renderer>(true);
        }

        private void CaptureBaseEmissive()
        {
            CaptureFrom(domeRenderers);
            CaptureFrom(segment0Renderers);
            CaptureFrom(segment1Renderers);
        }

        private void CaptureFrom(Renderer[] renderers)
        {
            if (renderers == null)
                return;
            foreach (var r in renderers)
            {
                if (r == null || _baseEmissive.ContainsKey(r))
                    continue;
                Color baseCol = cyanEmissive;
                if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(EmissionColorId))
                    baseCol = r.sharedMaterial.GetColor(EmissionColorId);
                _baseEmissive[r] = baseCol;
            }
        }

        /// <summary>
        /// Make sure there are two segment anchors. When none are assigned they are
        /// created as child transforms offset around the dome so the sparks land on
        /// the structure even without hand-authored anchors.
        /// </summary>
        private void EnsureSegmentAnchors()
        {
            if (segmentAnchors == null || segmentAnchors.Length < 2)
                segmentAnchors = new Transform[2];

            Vector3 domeCentre = domeRoot != null ? domeRoot.position : transform.position;
            Vector3[] offsets =
            {
                new Vector3(0.9f, 0.7f, 0.5f),
                new Vector3(-0.9f, 0.7f, -0.5f),
            };

            for (int i = 0; i < 2; i++)
            {
                if (segmentAnchors[i] != null)
                    continue;
                var go = new GameObject($"DomeSegmentAnchor_{i}");
                go.transform.SetParent(transform, true);
                go.transform.position = domeCentre + offsets[i];
                segmentAnchors[i] = go.transform;
            }
        }
    }
}
