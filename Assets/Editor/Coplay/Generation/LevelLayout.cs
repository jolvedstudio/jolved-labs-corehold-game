using Corehold.Data;
using UnityEngine;
using Kind = Corehold.Towers.HardpointCoverageGizmo.TurretKind;
using Cls = Corehold.Towers.HardpointCoverageGizmo.PadClass;

/// <summary>
/// The geometric content of one map, as data: core position, ground routes, air
/// spawn, and (when the layout supplies them — the parity path does, synthesis
/// does not) the pad set. This is the seam between R26 and R27/R28: the parity
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

    /// <summary>Pads, when the layout dictates them (parity). Null ⇒ R28 selects.
    /// Internal because <see cref="RefineryDeltaBlockout.HP"/> is internal — a public
    /// member may not expose a less-accessible type (CS0052), and the pad spec is
    /// generator plumbing, not API.</summary>
    internal RefineryDeltaBlockout.HP[] pads;

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

/// <summary>
/// The shipped Refinery Delta map as a layout — R26's parity target.
///
/// Routes and core come from the blockout's own constants. The PAD SET does
/// not: the scene's pads were hand-moved after the blockout ran (the clearance
/// pass), so the live scene is ground truth, and these are the live positions —
/// the same set docs/balance_model.py carries as PADS.
///
/// ONE deliberate divergence from the live scene: HP_Premium_2 sits at the
/// scene's (7.5, 1.5) covering only 3 spans — the standing violation the
/// roadmap documents (clearance and coverage were satisfied sequentially, and
/// the second pass silently broke the first). A parity rebuild through the R29
/// gate cannot emit a map that fails its own coverage rule, so this layout
/// adopts the documented fix, (7.5, 13): 5 spans, 5.0 m clearance. It is the
/// one place the rebuild is deliberately better than the scene it mirrors.
/// </summary>
public static class ShippedLayout
{
    public static LevelLayout Get(LevelBlueprint blueprint)
    {
        return new LevelLayout
        {
            corePos = RefineryDeltaBlockout.ShippedCorePos,
            groundRoutes = new[]
            {
                RefineryDeltaBlockout.ShippedWestRoute,
                RefineryDeltaBlockout.ShippedNorthRoute,
            },
            routeNames = new[] { "Route_West", "Route_North" },
            airSpawn = RefineryDeltaBlockout.ShippedAirSpawn,
            pads = new[]
            {
                RefineryDeltaBlockout.MakeHP("HP_Premium_1",  new Vector3(-3.488f, 0f, 4.5f),    Kind.Missile,    Cls.Premium),
                RefineryDeltaBlockout.MakeHP("HP_Premium_2",  new Vector3(7.5f,    0f, 13f),     Kind.Autocannon, Cls.Premium),
                RefineryDeltaBlockout.MakeHP("HP_Premium_3",  new Vector3(18.024f, 0f, 1.5f),    Kind.ArcNode,    Cls.Premium),
                RefineryDeltaBlockout.MakeHP("HP_Standard_1", new Vector3(-25.767f, 0f, 11.231f), Kind.Autocannon, Cls.Standard),
                RefineryDeltaBlockout.MakeHP("HP_Standard_2", new Vector3(-13f,    0f, 2.5f),    Kind.Missile,    Cls.Standard),
                RefineryDeltaBlockout.MakeHP("HP_Rear_1",     new Vector3(32.579f, 0f, 5.542f),  Kind.Autocannon, Cls.Rear),
                RefineryDeltaBlockout.MakeHP("HP_Rear_2",     new Vector3(22.551f, 0f, -3.379f), Kind.ArcNode,    Cls.Rear),
                RefineryDeltaBlockout.MakeHP("HP_Overwatch",  new Vector3(24f,     0f, -8f),     Kind.Mortar,     Cls.Overwatch),
            },
        };
    }
}
