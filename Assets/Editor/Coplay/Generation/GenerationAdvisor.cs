using System.Collections.Generic;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns a refusal into a fix the designer can click (R29 follow-on).
///
/// The generator's constraints are real — 4.5 m between lane bands because two
/// enemy bodies are that wide, 3.75 m around a route because a pad plus a body
/// is that wide, three Premium pads because the coverage rule says so. None of
/// them are negotiable and none of them are interesting to a level designer.
/// What IS interesting is which blueprint field to move, and by how much, and
/// R29's doctrine left that entirely to the reader: "reseed rather than repair"
/// is right for a seed-dependent failure and useless for a structural one,
/// where every seed fails the same way.
///
/// So this preflights the blueprint and, when it cannot generate, searches for
/// the SMALLEST edit that makes it. The search runs the real
/// <see cref="RouteSynthesizer"/> on throwaway copies rather than a model of it,
/// so a suggestion that says "this works" has actually been generated once
/// already — a second implementation of the geometry is exactly the drift the
/// whole project keeps refusing to introduce.
///
/// It never applies anything on its own. R29 forbids the pipeline repairing a
/// blueprint mid-run, and that still holds; this proposes, names the cost in a
/// designer's vocabulary, and waits to be clicked.
/// </summary>
public static class GenerationAdvisor
{
    /// <summary>One proposed blueprint edit, with the reason it helps.</summary>
    public sealed class Fix
    {
        /// <summary>Button text — the action, short.</summary>
        public string label;

        /// <summary>Why this is the problem and what the change costs, in plain terms.</summary>
        public string why;

        /// <summary>Applies the edit. Undo is recorded by the caller.</summary>
        public System.Action<LevelBlueprint> apply;
    }

    /// <summary>Length search step and reach, in metres.</summary>
    private const float TargetStep = 2f;
    private const float TargetReach = 40f;

    /// <summary>
    /// What is wrong and what would fix it.
    /// <paramref name="diagnosis"/> is null when the blueprint already generates.
    /// </summary>
    public static List<Fix> Suggest(LevelBlueprint b, out string diagnosis)
    {
        diagnosis = null;
        var fixes = new List<Fix>();
        if (b == null)
            return fixes;

        // Parity rebuilds a layout that is already known to work; the only thing
        // that can be wrong is the blueprint claiming to be something else.
        if (b.parityLayout)
        {
            if (b.topology != LevelBlueprint.ApproachTopology.Corridor)
            {
                diagnosis = "This blueprint rebuilds the shipped map, which is a Corridor.";
                fixes.Add(new Fix
                {
                    label = "Set topology to Corridor",
                    why = "Parity mode reproduces Refinery Delta exactly, and Refinery Delta is a corridor " +
                          "map. Any other topology contradicts what parity means.",
                    apply = bp => bp.topology = LevelBlueprint.ApproachTopology.Corridor,
                });
            }
            return fixes;
        }

        // ---- things that block before geometry is even attempted --------------
        if (b.classMix.premium < LevelBlueprint.PadClassMix.MinPremium)
        {
            int need = LevelBlueprint.PadClassMix.MinPremium;
            diagnosis = $"The pad mix asks for {b.classMix.premium} Premium pad(s).";
            fixes.Add(new Fix
            {
                label = $"Set Premium to {need}",
                why = $"A map has to have {need} pads that cover four route segments each — that is the " +
                      "rule the coverage gate checks, so a mix below it can never pass no matter how the " +
                      "map comes out.",
                apply = bp =>
                {
                    LevelBlueprint.PadClassMix m = bp.classMix;
                    m.premium = need;
                    bp.classMix = m;
                },
            });
        }

        if (!b.IsSiege && b.classMix.overwatch > 0 && b.foldWidth < 12f)
        {
            fixes.Add(new Fix
            {
                label = "Widen folds to 12 m",
                why = "The mix asks for an Overwatch pad, which is a Siege Mortar's home, and a Mortar " +
                      $"cannot shoot inside 6 m. In a {b.foldWidth:0.#} m pocket both lanes are inside that " +
                      "dead zone, so the pad gets pushed out of the folds where it is worth less.",
                apply = bp => bp.foldWidth = 12f,
            });
            fixes.Add(new Fix
            {
                label = "Drop the Overwatch pad",
                why = "The other way round: keep the narrow folds and spend that pad on a Standard turret " +
                      "instead, which has no minimum range.",
                apply = bp =>
                {
                    LevelBlueprint.PadClassMix m = bp.classMix;
                    m.standard += m.overwatch;
                    m.overwatch = 0;
                    bp.classMix = m;
                },
            });
        }

        TryThemeFixes(b, fixes);

        // ---- can it actually synthesize? --------------------------------------
        if (TrySynthesize(b, out string refusal))
            return fixes;                       // geometry is fine; anything above still stands

        // Is this seed's geometry unlucky, or is the blueprint impossible? The
        // question has to be ANSWERED rather than assumed: siege draws its entry
        // bearings from the seed, and the bearings set the lead-in, the fitted
        // sweep and therefore the separation. A refusal is genuinely seed-dependent
        // more often than it looks, and telling a designer "this cannot work" when
        // Seed +1 would have fixed it is the worse of the two mistakes.
        int worked = CountWorkingSeeds(b, out int workingSeed);
        if (worked > 0)
        {
            diagnosis = $"Seed {b.randomSeed} does not work here — {refusal}\n\n" +
                        $"This is bad luck rather than a bad blueprint: {worked} of {SeedSweep} nearby seeds " +
                        "do work.";
            fixes.Add(new Fix
            {
                label = $"Use seed {workingSeed}",
                why = $"The nearest seed that generates. Nothing else about your map changes — the seed only " +
                      "decides where the approaches enter and how the dressing falls.",
                apply = bp => bp.randomSeed = workingSeed,
            });
            return fixes;
        }

        diagnosis = refusal + $"\n\nNone of {SeedSweep} nearby seeds worked either, so this is the blueprint " +
                              "rather than the draw — reseeding will not help.";

        // Ordered by how little design intent each one spends. Centring the Core
        // on a map whose whole premise is "attackers surround the Core" costs
        // nothing; shortening every route changes how the map plays; dropping an
        // approach changes what the map IS.
        if (b.IsSiege)
            TryCentreCore(b, fixes);

        TryReachableLength(b, fixes);
        TrySimplerTopology(b, fixes);

        if (fixes.Count == 0)
            diagnosis += "\n\nNo single-field fix found within the ranges searched — the field itself is " +
                         "probably too small for this combination. Try a larger playfieldSize.";
        return fixes;
    }

    /// <summary>Seeds tried when deciding whether a refusal is bad luck or a bad blueprint.</summary>
    private const int SeedSweep = 16;

    /// <summary>
    /// How many of the next <see cref="SeedSweep"/> seeds synthesize, and the
    /// first one that does. Walks upward from the current seed because that is
    /// what Seed +1 does — the answer should match the button the designer would
    /// otherwise press by hand, sixteen times.
    /// </summary>
    private static int CountWorkingSeeds(LevelBlueprint b, out int firstWorking)
    {
        firstWorking = b.randomSeed;
        int worked = 0;
        for (int i = 1; i <= SeedSweep; i++)
        {
            int candidate = b.randomSeed + i;
            if (!Works(b, bp => bp.randomSeed = candidate))
                continue;
            if (worked == 0)
                firstWorking = candidate;
            worked++;
        }
        return worked;
    }

    // ------------------------------------------------------------- candidates

    /// <summary>
    /// Theme problems, which are the other half of what stops a map being made —
    /// and unlike geometry, most of them are one call to an existing tool.
    /// </summary>
    private static void TryThemeFixes(LevelBlueprint b, List<Fix> fixes)
    {
        if (b.envPackPool == null || b.envPackPool.Length == 0)
        {
            // A project with exactly one theme has an unambiguous answer.
            string[] guids = AssetDatabase.FindAssets("t:EnvPack");
            if (guids.Length == 1)
            {
                var only = AssetDatabase.LoadAssetAtPath<EnvPack>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (only != null)
                    fixes.Add(new Fix
                    {
                        label = $"Use theme '{only.name}'",
                        why = "This map has no theme, so it would generate as untextured greybox. " +
                              $"'{only.name}' is the only theme in the project.",
                        apply = bp => bp.envPackPool = new[] { only },
                    });
            }
            return;
        }

        foreach (EnvPack pack in b.envPackPool)
        {
            if (pack == null)
                continue;

            if (pack.CountInvalid() > 0)
            {
                EnvPack captured = pack;
                fixes.Add(new Fix
                {
                    label = $"Measure '{pack.name}'",
                    why = $"{pack.CountInvalid()} prop(s) in this theme have no measured size or no role. The " +
                          "generator needs both to keep props off the routes and out of sight lines, so it " +
                          "refuses rather than place them blind. Measuring fills the numbers in from the " +
                          "prefabs themselves.",
                    apply = _ =>
                    {
                        Selection.activeObject = captured;   // the tool reads the selection
                        EnvPackTools.MeasureSelected();
                    },
                });
            }

            if (pack.CountInRole(EnvPack.PropRole.Silhouette) == 0 &&
                pack.CountInRole(EnvPack.PropRole.Landmark) > 0)
            {
                EnvPack captured = pack;
                fixes.Add(new Fix
                {
                    label = $"Borrow silhouettes from '{pack.name}' landmarks",
                    why = "The far band behind the playfield has nothing to put in it, so the horizon will be " +
                          "bare. The two tallest landmarks in this same theme are copied into the silhouette " +
                          "role — they stay landmarks too, and it is the theme's own art rather than another " +
                          "theme's, which is what would make a desert map look like a refinery.",
                    apply = _ => PromoteTallestToSilhouette(captured, 2),
                });
            }
        }
    }

    /// <summary>
    /// Copies the tallest Landmark entries into the Silhouette role. Copies rather
    /// than moves: a landmark is still wanted on the playfield, and the far band
    /// only needs the same shape at distance.
    /// </summary>
    private static void PromoteTallestToSilhouette(EnvPack pack, int count)
    {
        if (pack?.entries == null)
            return;

        var landmarks = new List<EnvPack.Entry>();
        foreach (EnvPack.Entry e in pack.entries)
            if (e.prefab != null && e.role == EnvPack.PropRole.Landmark)
                landmarks.Add(e);
        if (landmarks.Count == 0)
            return;

        landmarks.Sort((x, y) => y.height.CompareTo(x.height));

        var grown = new List<EnvPack.Entry>(pack.entries);
        for (int i = 0; i < Mathf.Min(count, landmarks.Count); i++)
        {
            EnvPack.Entry copy = landmarks[i];
            copy.role = EnvPack.PropRole.Silhouette;
            grown.Add(copy);
        }

        Undo.RecordObject(pack, "Borrow silhouettes");
        pack.entries = grown.ToArray();
        EditorUtility.SetDirty(pack);
        AssetDatabase.SaveAssets();
    }

    private static void TryCentreCore(LevelBlueprint b, List<Fix> fixes)
    {
        var centred = new Vector2(0.5f, 0.5f);
        if (Vector2.Distance(b.protectedNormalizedPos, centred) < 0.01f)
            return;

        float before = ApproachRing(b, b.protectedNormalizedPos);
        float after = ApproachRing(b, centred);
        if (after <= before + 0.5f || !Works(b, bp => bp.protectedNormalizedPos = centred))
            return;

        fixes.Add(new Fix
        {
            label = "Centre the Core",
            why = $"On a siege map the attackers spiral in through the ring of ground around the Core, and " +
                  $"that ring is only as wide as the NEAREST field edge allows. Where the Core sits now it " +
                  $"is {before:0.#} m across; centred it is {after:0.#} m. The extra room is what lets each " +
                  "approach reach its length without wrapping so tightly that it crowds its neighbours.",
            apply = bp => bp.protectedNormalizedPos = centred,
        });
    }

    private static void TryReachableLength(LevelBlueprint b, List<Fix> fixes)
    {
        // Nearest first, both directions — a target that is too LONG for the ring
        // is the common case, but too short is possible on a big field.
        for (float delta = TargetStep; delta <= TargetReach; delta += TargetStep)
        {
            foreach (float signed in new[] { -delta, delta })
            {
                float candidate = Mathf.Round(b.routeLengthTarget + signed);
                if (candidate < 40f)
                    continue;
                if (!Works(b, bp => bp.routeLengthTarget = candidate))
                    continue;

                bool shorter = candidate < b.routeLengthTarget;
                fixes.Add(new Fix
                {
                    label = $"Set route length to {candidate:0} m",
                    why = $"{b.routeLengthTarget:0} m does not fit this field and topology; {candidate:0} m is " +
                          "the closest that does. " +
                          (shorter
                              ? "Shorter routes give your turrets less time on each enemy, but the balance " +
                                "model re-solves enemy health growth against the map it actually gets, so " +
                                "the difficulty curve comes out in band either way."
                              : "Longer routes give your turrets more time on each enemy; the balance model " +
                                "re-solves against the map it actually gets, so the curve stays in band."),
                    apply = bp => bp.routeLengthTarget = candidate,
                });
                return;
            }
        }
    }

    private static void TrySimplerTopology(LevelBlueprint b, List<Fix> fixes)
    {
        LevelBlueprint.ApproachTopology[] ladder = Simpler(b.topology);
        foreach (LevelBlueprint.ApproachTopology candidate in ladder)
        {
            if (!Works(b, bp => bp.topology = candidate))
                continue;

            int from = b.SiegeSectors > 0 ? b.SiegeSectors : b.GroundLegs;
            fixes.Add(new Fix
            {
                label = $"Use {candidate} instead",
                why = $"{b.topology} puts {from} approaches on this field and they cannot all stay a lane " +
                      $"apart on the way in. {candidate} asks for fewer, which leaves each one room to " +
                      "wrap. The map is still attacked from more than one side.",
                apply = bp => bp.topology = candidate,
            });
            return;
        }
    }

    private static LevelBlueprint.ApproachTopology[] Simpler(LevelBlueprint.ApproachTopology t)
    {
        switch (t)
        {
            case LevelBlueprint.ApproachTopology.Encirclement:
                return new[] { LevelBlueprint.ApproachTopology.Siege, LevelBlueprint.ApproachTopology.Pincer };
            case LevelBlueprint.ApproachTopology.Siege:
                return new[] { LevelBlueprint.ApproachTopology.Pincer };
            case LevelBlueprint.ApproachTopology.Pincer:
                return new[] { LevelBlueprint.ApproachTopology.Corridor };
            case LevelBlueprint.ApproachTopology.Corridor:
                return new[] { LevelBlueprint.ApproachTopology.SingleLane };
            default:
                return new LevelBlueprint.ApproachTopology[0];
        }
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Width of the ground ring a siege map has to spiral through.</summary>
    private static float ApproachRing(LevelBlueprint b, Vector2 normalizedCore)
    {
        Vector3 core = LevelLayout.FromNormalized(normalizedCore, b.playfieldSize);
        float toEdge = Mathf.Min(
            Mathf.Min(b.playfieldSize.x * 0.5f - core.x, b.playfieldSize.x * 0.5f + core.x),
            Mathf.Min(b.playfieldSize.y * 0.5f - core.z, b.playfieldSize.y * 0.5f + core.z));
        return toEdge - 4f;
    }

    /// <summary>
    /// Does this candidate edit generate? Takes a throwaway copy, tries it, and
    /// destroys it — the copy exists only to be measured.
    /// </summary>
    private static bool Works(LevelBlueprint b, System.Action<LevelBlueprint> edit)
    {
        LevelBlueprint copy = Object.Instantiate(b);
        try
        {
            edit(copy);
            return TrySynthesize(copy, out _);
        }
        finally
        {
            Object.DestroyImmediate(copy);
        }
    }

    /// <summary>
    /// Runs the REAL synthesizer. Every suggestion this class makes has therefore
    /// been generated once before it is offered — a suggestion derived from a
    /// separate estimate of the geometry would eventually recommend something the
    /// generator then refuses, which is worse than saying nothing.
    /// </summary>
    private static bool TrySynthesize(LevelBlueprint b, out string refusal)
    {
        refusal = null;
        try
        {
            LevelLayout layout = RouteSynthesizer.Synthesize(b, out string report);
            if (layout == null)
                refusal = FirstLine(report);
            return layout != null;
        }
        catch (System.Exception e)
        {
            refusal = e.Message;
            return false;
        }
    }

    private static string FirstLine(string report)
    {
        if (string.IsNullOrEmpty(report))
            return "synthesis refused without a reason (report was empty)";
        // Fit logs are prefixed [fit]; the refusal is the part without a prefix.
        foreach (string line in report.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("[fit]", System.StringComparison.Ordinal))
                return t;
        }
        return report.Trim();
    }
}
