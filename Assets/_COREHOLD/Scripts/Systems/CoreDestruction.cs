using System.Collections;
using Corehold.Core;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Plays a dramatic multi-stage explosion on the Core (Shield Generator) when the
    /// run is lost (GameState.Defeat). A rolling series of large explosions walks up
    /// the structure, a big camera shake fires, then the dome is hidden — the Core
    /// visibly "goes away with a major explosion".
    ///
    /// Attach to the Core / Shield Generator root, or leave coreTransform unset and it
    /// resolves to this transform.
    /// </summary>
    [DisallowMultipleComponent]
    public class CoreDestruction : MonoBehaviour
    {
        [Tooltip("Centre of the explosion sequence. Defaults to this transform.")]
        [SerializeField] private Transform coreTransform;

        [Tooltip("Roots to hide once the explosion sequence finishes (the dome/head).")]
        [SerializeField] private GameObject[] hideOnDestroy;

        [Tooltip("Number of staged explosions in the sequence.")]
        [SerializeField] private int blastCount = 8;

        [Tooltip("Seconds between staged explosions.")]
        [SerializeField] private float blastInterval = 0.12f;

        [Tooltip("Radius the staged blasts scatter around the core, in metres.")]
        [SerializeField] private float scatter = 3.5f;

        [Tooltip("Height the blasts climb over the sequence, in metres.")]
        [SerializeField] private float climb = 6f;

        private bool _played;
        private GameManager _gm;

        private void OnEnable()
        {
            StartCoroutine(Subscribe());
        }

        private IEnumerator Subscribe()
        {
            // GameManager.Instance may not exist yet at scene load; retry until it does.
            while (_gm == null)
            {
                _gm = GameManager.Instance;
                if (_gm == null)
                    yield return null;
            }
            _gm.OnStateChanged += HandleState;
        }

        private void OnDisable()
        {
            if (_gm != null)
                _gm.OnStateChanged -= HandleState;
        }

        private void HandleState(GameState state)
        {
            if (state == GameState.Defeat && !_played)
            {
                _played = true;
                StartCoroutine(Explode());
            }
        }

        private IEnumerator Explode()
        {
            Vector3 c = coreTransform != null ? coreTransform.position : transform.position;

            // Big shake up front.
            if (CameraShake.Instance != null)
                CameraShake.Instance.ShakeFootfall();

            var vfx = VFXDirector.Instance;
            for (int i = 0; i < blastCount; i++)
            {
                if (vfx != null)
                {
                    float t = i / Mathf.Max(1f, blastCount - 1f);
                    Vector3 offset = new Vector3(
                        Random.Range(-scatter, scatter),
                        climb * t,
                        Random.Range(-scatter, scatter));
                    // Force the LARGE explosion (radius above the large threshold).
                    vfx.PlayExplosion(c + offset, VFXDirector.LargeSplashThreshold + 1f);
                    vfx.PlayCoreHit(c + offset);
                }

                if (CameraShake.Instance != null && i % 2 == 0)
                    CameraShake.Instance.ShakeFootfall();

                yield return new WaitForSecondsRealtime(blastInterval);
            }

            // One final big burst dead centre, then hide the dome.
            if (vfx != null)
            {
                vfx.PlayExplosion(c + Vector3.up * 2f, VFXDirector.LargeSplashThreshold + 4f);
                vfx.PlayExplosion(c + Vector3.up * 4f, VFXDirector.LargeSplashThreshold + 4f);
            }

            yield return new WaitForSecondsRealtime(0.25f);

            if (hideOnDestroy != null)
                foreach (var go in hideOnDestroy)
                    if (go != null) go.SetActive(false);
        }
    }
}
