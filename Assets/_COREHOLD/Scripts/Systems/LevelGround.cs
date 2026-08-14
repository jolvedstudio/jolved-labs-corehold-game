using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Marks THE ground plane of a level — the single object R11's frustum fit
    /// sizes and a theme's ground material is applied to.
    ///
    /// This exists because the ground was previously identified by the NAME
    /// "Floor", and a name search across the scene is not safe: vendor art
    /// carries meshes called "Floor" too. In a generated scene the pads (a
    /// vendor floor-cache prefab) and the Core platform are built before the
    /// ground stage, so the lookup matched a 43 m prop inside one of them,
    /// "reused" it as the ground, and painted the terrain material onto it —
    /// while the actual ground was never created at all.
    ///
    /// A marker component cannot collide with someone else's naming. Lookup
    /// order is: this marker, then a root-level object called "Floor" (which is
    /// what the hand-built Game.unity has), never an arbitrary descendant.
    /// </summary>
    [DisallowMultipleComponent]
    public class LevelGround : MonoBehaviour
    {
        [Tooltip("How this ground was produced, for audits: the built-in plane, or a theme's groundPrefab.")]
        public string source;
    }
}
