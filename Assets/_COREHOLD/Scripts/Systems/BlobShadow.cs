using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Keeps a blob-shadow quad flat on the ground directly beneath its owner,
    /// regardless of the owner's height or rotation (GDD §5.5).
    ///
    /// The quad is a child of the unit so it moves with it for free, but a child
    /// inherits the parent's Y and rotation — wrong for a ground shadow, and badly
    /// wrong for air units flying at 4 m. This component pins the quad's world
    /// position to (owner.x, groundY, owner.z) and forces it flat (facing up), so
    /// one shared quad + one shared material works for walking and flying units and
    /// for turrets alike, and they all batch together (effectively one draw call).
    ///
    /// Runs in LateUpdate so it sees the final transform for the frame. Executes in
    /// edit mode too, so shadows sit correctly while authoring the scene.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class BlobShadow : MonoBehaviour
    {
        /// <summary>
        /// Global switch, OFF since real sun shadows replaced these fakes (the RP
        /// assets had the shadow pass disabled and a 50 m shadow distance behind a
        /// camera 130-150 m back, so blobs were the ONLY ground contact there was).
        /// Flipping it back at runtime restores every quad — the check is live, not
        /// a one-shot at spawn.
        ///
        /// Only acts in PLAY: toggling a renderer in edit mode would serialise
        /// "hidden" into prefabs and scenes, which is a mess to undo.
        /// </summary>
        public static bool Enabled = false;

        private Renderer _renderer;
        [Tooltip("World-space ground height the shadow sits at.")]
        [SerializeField] private float groundY = 0.05f;

        [Tooltip("Diameter of the shadow in metres (uniform quad scale).")]
        [SerializeField] private float diameter = 4f;

        private Transform _t;
        private Transform _owner;

        private void OnEnable()
        {
            _t = transform;
            _owner = _t.parent;
            Apply();
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (_t == null) _t = transform;
            if (_owner == null) _owner = _t.parent;

            if (Application.isPlaying)
            {
                if (_renderer == null) _renderer = GetComponent<Renderer>();
                if (_renderer != null && _renderer.enabled != Enabled)
                    _renderer.enabled = Enabled;
                if (!Enabled)
                    return;   // hidden: no need to keep pinning it to the ground
            }

            Vector3 basePos = _owner != null ? _owner.position : _t.position;
            _t.position = new Vector3(basePos.x, groundY, basePos.z);
            // Lie flat, quad facing straight up.
            _t.rotation = Quaternion.Euler(90f, 0f, 0f);
            _t.localScale = new Vector3(diameter, diameter, 1f);
        }

        /// <summary>Set the shadow diameter (used by the editor setup pass).</summary>
        public void SetDiameter(float d) => diameter = Mathf.Max(0.01f, d);

        /// <summary>Set the ground height the shadow rests at.</summary>
        public void SetGroundY(float y) => groundY = y;
    }
}
