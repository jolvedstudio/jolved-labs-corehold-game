using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Rolls the treads of a tracked enemy (the Shrike) without a skeletal rig.
    /// The track geometry is a plain MeshRenderer sharing the vendor pack's atlas
    /// material, so this scrolls the base-map UV via a per-renderer
    /// MaterialPropertyBlock — the shared material asset is never touched, so no
    /// other unit using the same material is affected.
    ///
    /// Scroll speed follows the unit's actual ground speed (from EnemyMover when
    /// present, else a constant fallback), so the treads visually match travel and
    /// stop when the tank is stunned/halted.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrackScroll : MonoBehaviour
    {
        [Tooltip("Track renderers to scroll (left/right tread meshes).")]
        [SerializeField] private Renderer[] trackRenderers;

        [Tooltip("Metres of travel to one full UV scroll. Smaller = faster apparent roll.")]
        [SerializeField] private float metresPerLoop = 1.5f;

        [Tooltip("Scroll direction along the UV (V by default for tread atlases).")]
        [SerializeField] private Vector2 uvAxis = new Vector2(0f, 1f);

        [Tooltip("Constant fallback speed (m/s) used when there is no EnemyMover.")]
        [SerializeField] private float fallbackSpeed = 4.5f;

        // Base-map property ids: cover both URP Lit (_BaseMap) and Standard (_MainTex).
        private static readonly int BaseMapST = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private EnemyMover _mover;
        private MaterialPropertyBlock _mpb;
        private float _offset;

        private void Awake()
        {
            _mover = GetComponent<EnemyMover>();
            _mpb = new MaterialPropertyBlock();
            if (trackRenderers == null || trackRenderers.Length == 0)
                AutoFindTracks();
        }

        private void OnEnable()
        {
            _offset = 0f;
        }

        private void Update()
        {
            if (trackRenderers == null || trackRenderers.Length == 0 || metresPerLoop <= 0.0001f)
                return;

            float speed = _mover != null ? _mover.Velocity.magnitude : fallbackSpeed;
            _offset += (speed / metresPerLoop) * Time.deltaTime;
            _offset %= 1f;

            Vector2 off = uvAxis * _offset;

            foreach (var r in trackRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                // Tiling 1, offset scrolls. Write both common ST vectors so it
                // works whichever base map the material exposes.
                var st = new Vector4(1f, 1f, off.x, off.y);
                _mpb.SetVector(BaseMapST, st);
                _mpb.SetVector(MainTexST, st);
                r.SetPropertyBlock(_mpb);
            }
        }

        private void AutoFindTracks()
        {
            var found = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name.IndexOf("Track", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(r);
            }
            trackRenderers = found.ToArray();
        }

        /// <summary>Assign the track renderers (used by the editor wiring).</summary>
        public void SetTracks(Renderer[] renderers) => trackRenderers = renderers;
    }
}
