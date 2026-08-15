using Corehold.Data;
using UnityEngine;
using Kind = Corehold.Towers.HardpointCoverageGizmo.TurretKind;
using Cls = Corehold.Towers.HardpointCoverageGizmo.PadClass;

/// <summary>
/// The geometric content of one map, as data: core position, ground routes, air
/// spawn. This is the seam between R26 and R27/R28: the synthesis
/// layout returns the shipped map, the synthesizer returns a seeded one, and
/// the pipeline builds whichever it is handed without caring which.
/// </summary>
public class LevelLayout
{
    public Vector3 corePos;

    /// <summary>Full waypoint list per ground route, spawn → core. One or two.</summary>
    public Vector3[][] groundRoutes;

    /// <summary>Display names for the ground routes, index-matched.</summary>
    public string[] routeNames;

    public Vector3 airSpawn;

    /// <summary>
    /// Whether the ground routes MERGE onto one shared tail. True for the shipped
    /// map and for corridor synthesis with two legs — those need R7's world-space
    /// tangent pin, without which AutoSmooth diverges the duplicated knots. Siege
    /// approaches never merge; they only converge at the Core, so pinning them
    /// would be pinning a join that does not exist.
    /// </summary>
    public bool sharedTail;

    /// <summary>
    /// World position for a normalized playfield position (from the south-west
    /// corner), on a field centred at the origin. The shipped Core's normalized
    /// (0.765, 0.413) on 130×75 lands at (34.45, −6.525) — the stored (34.5, −6.5)
    /// to authoring precision.
    /// </summary>
    public static Vector3 FromNormalized(Vector2 normalized, Vector2 fieldSize)
    {
        return new Vector3((normalized.x - 0.5f) * fieldSize.x, 0f,
                           (normalized.y - 0.5f) * fieldSize.y);
    }
}
