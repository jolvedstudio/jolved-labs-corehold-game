using UnityEngine;

namespace Corehold.Enemies
{
    /// <summary>
    /// Animation-free walk for procedural units (built for the Colossus, generic
    /// on purpose): body heave/sway plus leg swings, driven by DISTANCE TRAVELLED
    /// (<see cref="EnemyMover.Frontness"/>), never by time — so the cadence stays
    /// honest at crawl speed, under R18 slows, and through the enrage boost, and
    /// a 6 m stride lands its dip exactly on the mover's 6 m footfall shake.
    ///
    /// Everything is applied as LOCAL offsets over cached base poses in
    /// LateUpdate (after the mover has placed the root), and eased back to base
    /// when the unit stops — pooling-safe, no allocation, no Animator.
    /// </summary>
    [DisallowMultipleComponent]
    public class ProceduralGait : MonoBehaviour
    {
        [Header("Rig (wired by the setup tool)")]
        [Tooltip("The body group that heaves and sways (legs excluded).")]
        [SerializeField] private Transform body;

        [Tooltip("Leg pivot roots, rotated around X to swing.")]
        [SerializeField] private Transform[] legs;

        [Tooltip("Gait phase per leg in radians (diagonal pairs share a phase for a trot).")]
        [SerializeField] private float[] legPhases;

        [Header("[TUNE] Gait")]
        [Tooltip("Metres per full gait cycle. 6 matches the Colossus footfall-shake stride.")]
        [SerializeField] private float strideLength = 6f;

        [Tooltip("Vertical body heave in metres.")]
        [SerializeField] private float bodyBob = 0.10f;

        [Tooltip("Side-to-side roll in degrees as weight shifts between diagonal pairs.")]
        [SerializeField] private float bodyRollDegrees = 1.6f;

        [Tooltip("Fore-aft pitch in degrees.")]
        [SerializeField] private float bodyPitchDegrees = 0.9f;

        [Tooltip("Leg swing amplitude in degrees around the hip.")]
        [SerializeField] private float legSwingDegrees = 7f;

        [Tooltip("How quickly the gait blends in/out when the unit starts/stops (1/s).")]
        [SerializeField] private float settleSpeed = 4f;

        private EnemyMover _mover;
        private Vector3 _bodyBasePos;
        private Quaternion _bodyBaseRot;
        private Quaternion[] _legBaseRot;
        private float _weight;

        private void Awake()
        {
            _mover = GetComponent<EnemyMover>();
            if (body != null)
            {
                _bodyBasePos = body.localPosition;
                _bodyBaseRot = body.localRotation;
            }
            if (legs != null)
            {
                _legBaseRot = new Quaternion[legs.Length];
                for (int i = 0; i < legs.Length; i++)
                    _legBaseRot[i] = legs[i] != null ? legs[i].localRotation : Quaternion.identity;
            }
        }

        private void LateUpdate()
        {
            if (_mover == null || body == null)
                return;

            bool moving = _mover.Velocity.sqrMagnitude > 0.0025f;
            _weight = Mathf.MoveTowards(_weight, moving ? 1f : 0f, settleSpeed * Time.deltaTime);
            if (_weight <= 0.0001f)
                return; // settled on base pose; nothing to write

            // Distance-driven phase: one full cycle per stride. A trot has two
            // beats per cycle, so body terms run at 2× where a beat lands.
            float phase = _mover.Frontness / Mathf.Max(0.5f, strideLength) * (Mathf.PI * 2f);

            float heave = -Mathf.Abs(Mathf.Sin(phase)) * bodyBob;       // drops INTO each step
            float roll = Mathf.Sin(phase) * bodyRollDegrees;            // weight shift per pair
            float pitch = Mathf.Sin(phase * 2f + 0.6f) * bodyPitchDegrees;

            body.localPosition = _bodyBasePos + Vector3.up * (heave * _weight);
            body.localRotation = _bodyBaseRot * Quaternion.Euler(pitch * _weight, 0f, roll * _weight);

            if (legs == null)
                return;
            for (int i = 0; i < legs.Length; i++)
            {
                if (legs[i] == null)
                    continue;
                float lp = legPhases != null && i < legPhases.Length ? legPhases[i] : 0f;
                float swing = Mathf.Sin(phase + lp) * legSwingDegrees * _weight;
                legs[i].localRotation = _legBaseRot[i] * Quaternion.Euler(swing, 0f, 0f);
            }
        }
    }
}
