using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Stamped onto every prop the level generator places (roadmap R28). Carries
    /// the PLACED dimensions — the EnvPack metadata multiplied by the applied
    /// scale — so the sight-line occlusion test and any later validator measure
    /// what is actually standing in the scene, not the asset's scale-1 numbers.
    ///
    /// This is scene data, not tooling: a generated scene must stay verifiable
    /// after the generator is gone (R12's "re-run the gate after dressing" reads
    /// these), which is why it is a runtime component rather than editor state.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlacedProp : MonoBehaviour
    {
        [Tooltip("Horizontal keep-out radius in metres AT THE PLACED SCALE, about the pivot.")]
        public float placedFootprintRadius;

        [Tooltip("Height above the pivot in metres AT THE PLACED SCALE. The sight-line test compares crossing heights against this.")]
        public float placedHeight;

        [Tooltip("The EnvPack role this prop was placed as (Landmark/MidField/Clutter/Silhouette), recorded for audits.")]
        public string role;
    }
}
