using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;                      // LevelBlueprint
using Corehold.Systems;
using Corehold.Towers;
using CoreholdEditor;                     // HardpointMarkers, HardpointAura
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Mode b — the scene-adapt intake (docs/generator_intake_modes.md): certify
/// an AUTHORED scene against the same gates the procedural pipeline runs,
/// changing the author's dressing as little as possible and saying exactly
/// what changed.
///
/// The contract with the author:
///   • the scene carries a CORE (the protected-structure stack) and PADS
///     (HardpointCoverageGizmo objects) — those are gameplay anchors and are
///     never moved by this tool; failures they cause refuse honestly;
///   • dressing lives under a container named "Dressing" — everything in it
///     may be NUDGED (preferred) or REMOVED (last resort), each intervention
///     logged and one Undo step, up to a budget past which the run refuses
///     with the full conflict list;
///   • spawners and routes are SYNTHESIZED, converging on the authored Core —
///     you place the defense, the generator brings the assault (v1; authored
///     spawner positions are a later extension);
///   • the scene's own ground, lighting and weather are kept — an authored
///     scene owns its look. The scene is saved IN PLACE, and only after every
///     gate has passed.
/// </summary>
public static class SceneAdapt
{
    /// <summary>[TUNE] Interventions (moves + removals) before the run refuses:
    /// past this, the scene is fighting the gates and the AUTHOR decides.</summary>
    private const int InterventionBudget = 14;

    /// <summary>Nudge search: radii × the 8 compass directions, in this fixed
    /// order — determinism comes from the order, not from a seed.</summary>
    private static readonly float[] NudgeRadii = { 1.5f, 3f, 4.5f, 6f, 8f };

    private const string DressingContainer = "Dressing";

    // ------------------------------------------------------------------ menu

    [MenuItem("Tools/COREHOLD/Level/Adapt Open Scene (certify authored dressing)", false, 21)]
    public static void AdaptMenu()
    {
        var blueprint = Selection.activeObject as LevelBlueprint;
        if (blueprint == null)
        {
            string guid = AssetDatabase.FindAssets("t:LevelBlueprint").FirstOrDefault();
            if (guid != null)
                blueprint = AssetDatabase.LoadAssetAtPath<LevelBlueprint>(
                    AssetDatabase.GUIDToAssetPath(guid));
        }
        if (blueprint == null)
        {
            Debug.LogError("[Adapt] No LevelBlueprint selected or found.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        if (!EditorUtility.DisplayDialog("Adapt Open Scene",
                $"Certify '{scene.name}' against the generation gates using blueprint " +
                $"'{blueprint.name}'.\n\nAuthored props under '{DressingContainer}' may be " +
                $"MOVED or REMOVED (each is one Undo step, all are listed, budget " +
                $"{InterventionBudget}). Routes and spawners are synthesized toward your " +
                "Core. The scene is saved in place only if every gate passes.\n\nProceed?",
                "Adapt", "Cancel"))
            return;

        List<GenerationPipeline.StageRun> results = GenerationPipeline.RunAdapt(blueprint);

        var sb = new StringBuilder();
        sb.AppendLine($"=== ADAPT — '{scene.name}' × blueprint '{blueprint.name}' ===");
        bool ok = true;
        foreach (var r in results)
        {
            sb.AppendLine($"  [{(r.result.ok ? (r.result.skipped ? "SKIP" : " OK ") : "FAIL")}] " +
                          $"{r.stage.title} — {r.result.message}");
            ok &= r.result.ok;
        }
        if (ok) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
    }

    [MenuItem("Tools/COREHOLD/Level/Stamp Adapt Anchors Into Open Scene", false, 22)]
    public static void StampAnchors()
    {
        var blueprint = Selection.activeObject as LevelBlueprint;
        var root = new GameObject("AdaptAnchors");
        Undo.RegisterCreatedObjectUndo(root, "Stamp Adapt Anchors");

        // The same Core the generator builds — authored means "placed by you",
        // not "different prefab". South-of-centre default; move it freely.
        RefineryDeltaBlockout.BuildCore(root.transform, new Vector3(0f, 0f, -24f),
                                        blueprint != null ? blueprint.protectedPrefab : null);

        if (GameObject.Find(DressingContainer) == null)
            new GameObject(DressingContainer);

        Debug.Log("[Adapt] Stamped a Core at (0, -24) and ensured a 'Dressing' container.\n" +
                  "  • Move the Core where you want the defense to stand.\n" +
                  "  • Put your props under 'Dressing' — that is what Adapt may touch.\n" +
                  "  • PADS: copy HP_* pads from any generated scene (Clone Level helps), or " +
                  "place them by hand — Adapt refuses without at least one, on purpose.\n" +
                  "  • Then: Tools → COREHOLD → Level → Adapt Open Scene.");
    }

    // ------------------------------------------------------------------ stages

    /// <summary>Verify the authored scene and inventory what it offers. The
    /// blueprint is CLONED with the authored Core reverse-mapped into
    /// protectedNormalizedPos, so the untouched RouteSynthesizer converges on
    /// the author's Core; terrain relief is forced off (authored ground).</summary>
    public static GenerationPipeline.StageResult StIntake(GenerationPipeline.Context ctx)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
            return GenerationPipeline.StageResult.Fail(
                "the open scene has never been saved — save it first (adapt saves IN PLACE, " +
                "and an unsaved scene has no place).");

        // ---- the authored Core ------------------------------------------------
        var coreState = SceneQuery.FirstInActiveScene<CoreDamageState>();
        Transform core = coreState != null ? coreState.transform.root : null;
        if (core == null)
        {
            var named = GameObject.Find("Core");
            core = named != null ? named.transform : null;
        }
        if (core == null)
            return GenerationPipeline.StageResult.Fail(
                "no authored Core found (no CoreDamageState in the scene). Run " +
                "'Stamp Adapt Anchors Into Open Scene', place the Core, and re-run.");

        // ---- dressing inventory ----------------------------------------------
        var dressing = GameObject.Find(DressingContainer);
        if (dressing == null)
            return GenerationPipeline.StageResult.Fail(
                $"no '{DressingContainer}' container — adapt only ever touches props under it, " +
                "so it must exist (empty is fine). Stamp Adapt Anchors creates one.");

        ctx.adaptProps = new List<PlacedProp>();
        int stamped = 0, unmeasurable = 0;
        foreach (Transform child in dressing.transform)
        {
            var marker = child.GetComponent<PlacedProp>();
            if (marker == null)
            {
                if (!EnvPackTools.TryMeasure(child.gameObject, out EnvPackTools.Measurement m)
                    || m.height <= 0f)
                {
                    unmeasurable++;
                    continue;   // no mesh — a light, a sound source; not dressing's problem
                }
                marker = Undo.AddComponent<PlacedProp>(child.gameObject);
                Vector3 s = child.lossyScale;
                marker.placedFootprintRadius = m.radius * Mathf.Max(s.x, s.z);
                marker.placedHeight = m.height * s.y;
                marker.role = "Authored";
                stamped++;
            }
            ctx.adaptProps.Add(marker);
        }

        // ---- clone the blueprint around the authored Core --------------------
        // Reverse of LevelLayout.FromNormalized: n = pos/field + 0.5. The clone
        // is what every later stage sees, so the synthesizer, the gates and the
        // emit all agree the Core is where the author put it.
        var bp = ScriptableObject.CreateInstance<LevelBlueprint>();
        EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(ctx.blueprint), bp);
        bp.name = ctx.blueprint.name;
        bp.hideFlags = HideFlags.DontSave;
        bp.protectedNormalizedPos = new Vector2(
            core.position.x / bp.playfieldSize.x + 0.5f,
            core.position.z / bp.playfieldSize.y + 0.5f);
        bp.terrainRelief = false;   // authored ground is kept as-is (v1)
        ctx.blueprint = bp;
        ctx.coreTarget = core;

        // ---- containers, without a new scene ---------------------------------
        Transform levelRoot = SceneContainers.Ensure("_Level");
        var container = new GameObject($"Level_{scene.name}_Adapt");
        Undo.RegisterCreatedObjectUndo(container, "Adapt");
        container.transform.SetParent(levelRoot, false);
        ctx.levelContainer = container.transform;
        ctx.scenePath = scene.path;

        return GenerationPipeline.StageResult.Ok(
            $"authored Core at ({core.position.x:0.#}, {core.position.z:0.#}) → normalized " +
            $"({bp.protectedNormalizedPos.x:0.###}, {bp.protectedNormalizedPos.y:0.###}); " +
            $"{ctx.adaptProps.Count} authored prop(s) ({stamped} newly measured+stamped, " +
            $"{unmeasurable} meshless ignored); terrain relief off (authored ground kept)");
    }

    /// <summary>Layout via the untouched synthesizer, on the Core-mapped clone.
    /// A mismatch between what it produced and where the author's Core stands
    /// is an anchor problem and refuses honestly — moving props cannot fix it.</summary>
    public static GenerationPipeline.StageResult StLayout(GenerationPipeline.Context ctx)
    {
        ctx.layout = RouteSynthesizer.Synthesize(ctx.blueprint, out string report);
        if (ctx.layout == null)
            return GenerationPipeline.StageResult.Fail(
                "route synthesis refused this blueprint at the authored Core position:\n" + report +
                "\nThis is an ANCHOR problem — move the Core (or adjust the blueprint), not the props.");
        if (report != null)
            Debug.Log("[MODE-B] " + report);

        float drift = Vector3.Distance(
            new Vector3(ctx.layout.corePos.x, 0f, ctx.layout.corePos.z),
            new Vector3(ctx.coreTarget.position.x, 0f, ctx.coreTarget.position.z));
        if (drift > 1.5f)
            return GenerationPipeline.StageResult.Fail(
                $"the synthesizer needs the Core {drift:0.0} m away from where it stands — the " +
                "authored position violates a layout constraint (see its report above). Move the " +
                "authored Core, not the props.");

        return GenerationPipeline.StageResult.Ok(
            $"layout converges on the authored Core (drift {drift:0.00} m), " +
            $"{ctx.layout.groundRoutes.Length} route(s) synthesized toward it");
    }

    /// <summary>Authored pads adopted as-is: never moved, never added. Their
    /// adequacy is GATE 2's judgement, and a shortfall refuses with "move
    /// pads, not props" — which is exactly what the gate's message says.</summary>
    public static GenerationPipeline.StageResult StPadsAdopt(GenerationPipeline.Context ctx)
    {
        var pads = SceneQuery.InActiveScene<HardpointCoverageGizmo>();
        if (pads.Length == 0)
            return GenerationPipeline.StageResult.Fail(
                "no authored pads (HardpointCoverageGizmo) in the scene. Place them — copying " +
                "HP_* pads from any generated scene works — and re-run. Refusing is deliberate: " +
                "inventing pad positions would be the generator authoring YOUR defense.");

        foreach (var pad in pads)
            if (pad.transform.root != ctx.levelContainer.root ||
                !pad.transform.IsChildOf(ctx.levelContainer))
                Undo.SetTransformParent(pad.transform, ctx.levelContainer, "Adapt");

        // Same finishing passes the procedural pad stage runs — markers so the
        // player can SEE the pads, auras so empty ones breathe.
        HardpointMarkers.Run();
        HardpointAura.Run();

        int premium = pads.Count(p => p.padClass == HardpointCoverageGizmo.PadClass.Premium);
        return GenerationPipeline.StageResult.Ok(
            $"{pads.Length} authored pad(s) adopted ({premium} premium); markers + auras applied");
    }

    /// <summary>
    /// THE adapt loop. Enforces the same constraints PropPlacer enforces at
    /// placement time — route clearance, pad and core keep-outs, the camera's
    /// sight line to every pad, the turret coverage spans, the route
    /// visibility budget — over props that already exist. Repair is nudge
    /// first (fixed ring, deterministic), remove last, every intervention
    /// logged and Undo-able, and past the budget the run refuses with what
    /// remains. Authored overlaps between props are deliberately NOT policed:
    /// two rocks intersecting is a composition choice, not a gameplay bug.
    /// </summary>
    public static GenerationPipeline.StageResult StDress(GenerationPipeline.Context ctx)
    {
        var log = new List<string>();
        var live = ctx.adaptProps.Where(p => p != null).ToList();

        // Route samples, flat, every 0.5 m — PropPlacer's own sampling.
        var routeSamples = new List<Vector3>();
        foreach (PathRoute route in ctx.routes)
            for (float d = 0f; d <= route.Length; d += 0.5f)
            {
                Vector3 s = route.SamplePosition(d, out _);
                s.y = 0f;
                routeSamples.Add(s);
            }

        var pads = SceneQuery.InActiveScene<HardpointCoverageGizmo>()
            .OrderBy(p => p.name, System.StringComparer.Ordinal).ToArray();
        Camera cam = SceneQuery.FirstInActiveScene<Camera>();
        Vector3 corePos = ctx.coreTarget.position;
        float halfW = ctx.blueprint.playfieldSize.x * 0.5f + 25f;
        float halfD = ctx.blueprint.playfieldSize.y * 0.5f + 30f;

        // A candidate position is CLEAR when it violates nothing the gates
        // will later check. One function, used by both the violation scan and
        // the nudge search, so they can never disagree.
        string Violation(PlacedProp p, Vector3 pos)
        {
            float r = p.placedFootprintRadius;
            float needRoute = PropPlacer.LaneHalfWidth + PropPlacer.MaxBodyRadius + r + PropPlacer.Margin;
            foreach (Vector3 s in routeSamples)
            {
                float dx = pos.x - s.x, dz = pos.z - s.z;
                if (dx * dx + dz * dz < needRoute * needRoute)
                    return $"inside lane clearance ({needRoute:0.0} m)";
            }
            foreach (var pad in pads)
            {
                Vector3 pp = pad.transform.position;
                float dx = pos.x - pp.x, dz = pos.z - pp.z;
                float need = PropPlacer.PadKeepOut + r;
                if (dx * dx + dz * dz < need * need)
                    return $"inside {pad.name}'s keep-out";
            }
            {
                float dx = pos.x - corePos.x, dz = pos.z - corePos.z;
                float need = 10f + r;
                if (dx * dx + dz * dz < need * need)
                    return "inside the Core keep-out";
            }
            if (Mathf.Abs(pos.x) > halfW || pos.z < -halfD || pos.z > halfD + 30f)
                return "outside the field";
            if (cam != null)
            {
                var probe = new List<HardpointCoverageGizmo.Occluder>
                {
                    new HardpointCoverageGizmo.Occluder
                    { position = pos, radius = r, height = p.placedHeight },
                };
                foreach (var pad in pads)
                {
                    Vector3 padPoint = pad.transform.position +
                                       Vector3.up * HardpointCoverageGizmo.PadVisibleHeight;
                    if (HardpointCoverageGizmo.LineBlocked(cam.transform.position, padPoint, probe))
                        return $"hides {pad.name} from the camera";
                }
            }
            return null;
        }

        int moved = 0, removed = 0;

        // Nudge-then-remove, shared by every phase. False = budget exhausted.
        bool Intervene(PlacedProp p, string why)
        {
            if (ctx.adaptInterventions >= InterventionBudget)
                return false;
            Vector3 from = p.transform.position;
            foreach (float radius in NudgeRadii)
                for (int dir = 0; dir < 8; dir++)
                {
                    float a = dir * Mathf.PI / 4f;
                    var to = new Vector3(from.x + Mathf.Cos(a) * radius, from.y,
                                         from.z + Mathf.Sin(a) * radius);
                    if (Violation(p, to) != null)
                        continue;
                    Undo.RecordObject(p.transform, "Adapt");
                    p.transform.position = to;
                    log.Add($"moved '{p.name}' {radius:0.#} m " +
                            $"({DirName(dir)}) — {why}");
                    ctx.adaptInterventions++;
                    moved++;
                    return true;
                }
            log.Add($"removed '{p.name}' — {why}; no clear position within {NudgeRadii.Last()} m");
            live.Remove(p);
            Undo.DestroyObjectImmediate(p.gameObject);
            ctx.adaptInterventions++;
            removed++;
            return true;
        }

        // ---- phase 1: geometry + camera sight, prop by prop ------------------
        foreach (PlacedProp p in live.OrderBy(p => p.name, System.StringComparer.Ordinal).ToList())
        {
            string why = Violation(p, p.transform.position);
            if (why != null && !Intervene(p, why))
                return Refuse(ctx, live, Violation, log);
        }

        // ---- phase 2: turret coverage spans (GATE 2b's first half) -----------
        List<HardpointCoverageGizmo.Occluder> Occluders() => live
            .Where(p => p != null)
            .Select(p => new HardpointCoverageGizmo.Occluder
            {
                position = p.transform.position,
                radius = p.placedFootprintRadius,
                height = p.placedHeight,
            }).ToList();

        for (int pass = 0; pass < 10; pass++)
        {
            var occ = Occluders();
            HardpointCoverageGizmo shortPad = null;
            int need = 0;
            foreach (var pad in pads)
            {
                int wants = pad.padClass == HardpointCoverageGizmo.PadClass.Premium ? 4 : 2;
                if (pad.CountCoveredSpansOnCurve(occ) < wants) { shortPad = pad; need = wants; break; }
            }
            if (shortPad == null)
                break;

            // The prop whose absence recovers the most spans — PropPlacer's own
            // occlusion-repair heuristic, with nudge tried before removal.
            PlacedProp best = null;
            int bestRecovered = -1;
            foreach (PlacedProp p in live.Where(p => p != null))
            {
                var without = Occluders().Where(o =>
                    (o.position - p.transform.position).sqrMagnitude > 0.01f).ToList();
                int rec = shortPad.CountCoveredSpansOnCurve(without);
                if (rec > bestRecovered) { bestRecovered = rec; best = p; }
            }
            if (best == null || bestRecovered < need)
                return GenerationPipeline.StageResult.Fail(
                    $"{shortPad.name} cannot reach {need} covered spans even with dressing removed — " +
                    "an ANCHOR problem: move that pad closer to a route.");
            if (!Intervene(best, $"blocked {shortPad.name}'s turret sight lines"))
                return Refuse(ctx, live, Violation, log);
        }

        // ---- phase 3: route visibility budget --------------------------------
        if (cam != null && ctx.routes.Count > 0)
        {
            List<Vector3> visSamples = RouteVisibility.SampleRoutes(ctx.routes);
            float budget = RouteVisibility.BudgetMetres(visSamples);
            for (int pass = 0; pass < 10; pass++)
            {
                if (RouteVisibility.HiddenMetres(visSamples, cam, Occluders()) <= budget)
                    break;
                PlacedProp worst = null;
                float worstLeft = float.MaxValue;
                foreach (PlacedProp p in live.Where(p => p != null))
                {
                    var without = Occluders().Where(o =>
                        (o.position - p.transform.position).sqrMagnitude > 0.01f).ToList();
                    float left = RouteVisibility.HiddenMetres(visSamples, cam, without);
                    if (left < worstLeft) { worstLeft = left; worst = p; }
                }
                if (worst == null || !Intervene(worst, "route visibility budget exceeded"))
                    return Refuse(ctx, live, Violation, log);
            }
        }

        ctx.adaptProps = live;
        if (log.Count > 0)
            Debug.Log("[MODE-B] Interventions:\n  • " + string.Join("\n  • ", log));
        return GenerationPipeline.StageResult.Ok(
            $"{moved} moved, {removed} removed of {ctx.adaptProps.Count + removed} authored prop(s) " +
            $"(budget {ctx.adaptInterventions}/{InterventionBudget}); every intervention logged and Undo-able");
    }

    private static GenerationPipeline.StageResult Refuse(
        GenerationPipeline.Context ctx, List<PlacedProp> live,
        System.Func<PlacedProp, Vector3, string> violation, List<string> log)
    {
        var remaining = live.Where(p => p != null)
            .Select(p => new { p, why = violation(p, p.transform.position) })
            .Where(x => x.why != null)
            .Select(x => $"{x.p.name}: {x.why}")
            .ToList();
        if (log.Count > 0)
            Debug.Log("[MODE-B] Interventions before refusal:\n  • " + string.Join("\n  • ", log));
        return GenerationPipeline.StageResult.Fail(
            $"intervention budget ({InterventionBudget}) exhausted — this scene fights the gates. " +
            $"Remaining conflicts:\n  • " + string.Join("\n  • ", remaining.DefaultIfEmpty("(see phases above)")) +
            "\nThe author decides from here: thin the dressing near the lanes, or move the anchors.");
    }

    /// <summary>Save the authored scene at ITS OWN path — only reached when
    /// every gate has passed — and register it for builds.</summary>
    public static GenerationPipeline.StageResult StSaveInPlace(GenerationPipeline.Context ctx)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!EditorSceneManager.SaveScene(scene))
            return GenerationPipeline.StageResult.Fail("Unity refused to save the scene.");
        ctx.sceneSaved = true;

        if (!EditorBuildSettings.scenes.Any(s => s.path == scene.path))
            EditorBuildSettings.scenes = EditorBuildSettings.scenes
                .Concat(new[] { new EditorBuildSettingsScene(scene.path, true) }).ToArray();

        return GenerationPipeline.StageResult.Ok(
            $"saved in place ({scene.path}) and registered in Build Settings; " +
            $"{ctx.adaptInterventions} intervention(s) total — the scene remains yours");
    }

    private static string DirName(int dir) =>
        new[] { "E", "NE", "N", "NW", "W", "SW", "S", "SE" }[dir];
}
