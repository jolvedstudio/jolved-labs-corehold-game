using System.Collections.Generic;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Towers;
using UnityEngine;
using Kind = Corehold.Towers.HardpointCoverageGizmo.TurretKind;
using Cls = Corehold.Towers.HardpointCoverageGizmo.PadClass;

/// <summary>
/// Hardpoint candidate scoring and selection (R28).
///
/// The ticket's whole point, encoded structurally: <b>clearance is a
/// precondition of candidacy and coverage is the score over the survivors</b>,
/// so both hold jointly by construction — a pad is Premium *because* it
/// measured 4+, never because it was named one. (The shipped map shows the
/// failure this prevents: HP_Premium_2 was declared Premium, positioned
/// separately, moved for clearance, and silently dropped to 3 spans.)
///
/// Coverage is scored by the REAL validator: one temporary
/// <see cref="HardpointCoverageGizmo"/> is moved across the candidate grid and
/// asked <c>CountCoveredSegments()</c> per turret kind — the identical code
/// path gate 2 will judge the final pads with, so the search cannot disagree
/// with the gate it feeds.
///
/// Selection is FULLY deterministic with no random draws at all: the grid is
/// walked in fixed order and every tie breaks on (spans desc, then x, then z).
/// The seed shapes the routes; the pads follow from the geometry.
/// </summary>
public static class HardpointSelector
{
    /// <summary>Clearance envelope: laneHalfWidth 0.9 + maxBodyRadius 1.35 + padRadius 1.5.</summary>
    private const float ClearanceEnvelope = 3.75f;

    /// <summary>Keep-out around the protected structure (its platform footprint).</summary>
    private const float CoreKeepOut = 8f;

    /// <summary>Minimum spacing between selected pads (shipped minimum is 4.8 m).</summary>
    private const float PadSpacing = 5f;

    /// <summary>Candidate grid step. 2 m balances fidelity against scoring cost.</summary>
    private const float GridStep = 2f;

    /// <summary>Rear pads live on the final approach: within this of the Core.</summary>
    private const float RearRadius = 14f;

    /// <summary>Overwatch (Mortar home) sits set back but still near the Core.</summary>
    private const float OverwatchRadius = 25f;

    // Turret assignment cycles per class, in the shipped map's order.
    private static readonly Kind[] PremiumKinds = { Kind.Missile, Kind.Autocannon, Kind.ArcNode };
    private static readonly Kind[] StandardKinds = { Kind.Autocannon, Kind.Missile };
    private static readonly Kind[] RearKinds = { Kind.Autocannon, Kind.ArcNode };

    private class Candidate
    {
        public Vector3 pos;
        public float routeDist;                       // nearest approach to any route curve
        public float coreDist;
        public readonly Dictionary<Kind, int> spans = new Dictionary<Kind, int>();
    }

    /// <summary>
    /// Select the blueprint's pad mix on the given routes. Returns null with the
    /// reason in <paramref name="report"/> when the geometry cannot satisfy the
    /// mix — which is a RESEED signal (R29), not something to patch here.
    /// Internal because the HP spec it returns is internal (CS0050).
    /// </summary>
    internal static RefineryDeltaBlockout.HP[] Select(
        LevelBlueprint blueprint, List<PathRoute> routes, Vector3 corePos, out string report)
    {
        var log = new StringBuilder();

        // ---- route curve samples, once --------------------------------------
        var routeSamples = new List<Vector3>();
        foreach (PathRoute route in routes)
            for (float d = 0f; d <= route.Length; d += 0.5f)
            {
                Vector3 p = route.SamplePosition(d, out _);
                p.y = 0f;
                routeSamples.Add(p);
            }

        // ---- candidacy: the grid, clearance-filtered -------------------------
        float halfW = blueprint.playfieldSize.x * 0.5f - 4f;
        float halfD = blueprint.playfieldSize.y * 0.5f - 4f;

        var candidates = new List<Candidate>();
        for (float x = -halfW; x <= halfW; x += GridStep)
            for (float z = -halfD; z <= halfD; z += GridStep)
            {
                var pos = new Vector3(x, 0f, z);
                float routeDist = MinDistance(pos, routeSamples);
                if (routeDist < ClearanceEnvelope)
                    continue;                                   // clearance IS candidacy
                float coreDist = Flat(pos - corePos).magnitude;
                if (coreDist < CoreKeepOut)
                    continue;
                candidates.Add(new Candidate { pos = pos, routeDist = routeDist, coreDist = coreDist });
            }

        if (candidates.Count == 0)
        {
            report = "no clearance-satisfying candidate positions exist on this geometry";
            return null;
        }

        // ---- scoring: the real validator, moved across the grid --------------
        // Only candidates near enough to possibly cover anything are scored —
        // the Mortar's 20 m ring is the widest reach any kind has.
        var scorer = new GameObject("~HP_Scorer") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var gz = scorer.AddComponent<HardpointCoverageGizmo>();
            gz.routes = new[] { routes[0] };            // shipped convention: the primary
                                                        // route carries every shared span
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                Candidate c = candidates[ci];
                if ((ci & 63) == 0 &&
                    !GenerationProgress.Detail($"scoring hardpoint candidates {ci}/{candidates.Count}",
                                               ci / (float)candidates.Count))
                {
                    report = "cancelled by user during candidate scoring";
                    return null;
                }
                if (c.routeDist > HardpointCoverageGizmo.RangeFor(Kind.Mortar))
                    continue;
                scorer.transform.position = c.pos;
                foreach (Kind kind in new[] { Kind.Autocannon, Kind.Missile, Kind.ArcNode, Kind.Mortar })
                {
                    gz.intendedTurret = kind;
                    c.spans[kind] = gz.CountCoveredSegments();
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(scorer);
        }

        // ---- selection: deterministic greedy, hardest class first ------------
        var mix = blueprint.classMix;
        var picked = new List<(Candidate cand, Kind kind, Cls cls, string name)>();

        bool ok =
            PickClass(candidates, picked, mix.premium, Cls.Premium, PremiumKinds, 4,
                      c => true, log) &&
            PickClass(candidates, picked, mix.overwatch, Cls.Overwatch, new[] { Kind.Mortar }, 2,
                      c => c.coreDist <= OverwatchRadius, log) &&
            PickClass(candidates, picked, mix.rear, Cls.Rear, RearKinds, 2,
                      c => c.coreDist <= RearRadius, log) &&
            PickClass(candidates, picked, mix.standard, Cls.Standard, StandardKinds, 2,
                      c => true, log);

        if (!ok)
        {
            report = "hardpoint selection could not satisfy the class mix:\n" + log.ToString().TrimEnd() +
                     "\nThis geometry cannot host the blueprint's pads — reseed (R29).";
            return null;
        }

        // Names are per-class ordinals, matching the shipped naming.
        var result = new List<RefineryDeltaBlockout.HP>();
        var ordinals = new Dictionary<Cls, int>();
        foreach (var (cand, kind, cls, _) in picked)
        {
            ordinals.TryGetValue(cls, out int n);
            ordinals[cls] = ++n;
            string name = cls == Cls.Overwatch && mix.overwatch == 1
                ? "HP_Overwatch"
                : $"HP_{cls}_{n}";
            result.Add(RefineryDeltaBlockout.MakeHP(name, cand.pos, kind, cls));
            log.AppendLine($"  {name,-14} {kind,-11} {cls,-9} at ({cand.pos.x:0.#}, {cand.pos.z:0.#})  " +
                           $"spans {cand.spans[kind]}, clearance {cand.routeDist:0.##} m");
        }

        report = log.ToString();
        return result.ToArray();
    }

    /// <summary>
    /// Pick <paramref name="count"/> pads of one class. For each slot the turret
    /// kind cycles through the class's list, and the best remaining candidate BY
    /// THAT KIND'S measured spans is taken — spans desc, then routeDist desc
    /// (prefer breathing room), then x, then z, so ties are stable. Spacing
    /// against already-picked pads is enforced during the scan.
    /// </summary>
    private static bool PickClass(List<Candidate> candidates,
                                  List<(Candidate cand, Kind kind, Cls cls, string name)> picked,
                                  int count, Cls cls, Kind[] kinds, int minSpans,
                                  System.Func<Candidate, bool> extraFilter, StringBuilder log)
    {
        for (int slot = 0; slot < count; slot++)
        {
            Kind kind = kinds[slot % kinds.Length];
            Candidate best = null;
            int bestSpans = -1;

            foreach (Candidate c in candidates)
            {
                if (!c.spans.TryGetValue(kind, out int spans) || spans < minSpans)
                    continue;
                if (!extraFilter(c))
                    continue;
                if (TooClose(c, picked))
                    continue;

                // Standard pads should MEASURE standard: prefer 2–3 spans over
                // an accidental premium-grade spot, so premium spots stay
                // available and the census reads like the design intends.
                int score = cls == Cls.Standard && spans > 3 ? 3 : spans;

                if (best == null || score > bestSpans ||
                    (score == bestSpans && Tiebreak(c, best)))
                {
                    best = c;
                    bestSpans = score;
                }
            }

            if (best == null)
            {
                log.AppendLine($"  • no candidate for {cls} slot {slot + 1} ({kind}, ≥{minSpans} spans" +
                               (cls == Cls.Rear ? $", ≤{RearRadius:0} m of Core" :
                                cls == Cls.Overwatch ? $", ≤{OverwatchRadius:0} m of Core" : "") +
                               ") with spacing held");
                return false;
            }
            picked.Add((best, kind, cls, null));
        }
        return true;
    }

    private static bool Tiebreak(Candidate a, Candidate b)
    {
        if (!Mathf.Approximately(a.routeDist, b.routeDist))
            return a.routeDist > b.routeDist;
        if (!Mathf.Approximately(a.pos.x, b.pos.x))
            return a.pos.x < b.pos.x;
        return a.pos.z < b.pos.z;
    }

    private static bool TooClose(Candidate c, List<(Candidate cand, Kind kind, Cls cls, string name)> picked)
    {
        foreach (var (other, _, _, _) in picked)
            if (Flat(c.pos - other.pos).magnitude < PadSpacing)
                return true;
        return false;
    }

    private static float MinDistance(Vector3 pos, List<Vector3> samples)
    {
        float best = float.MaxValue;
        for (int i = 0; i < samples.Count; i++)
        {
            float d = Flat(pos - samples[i]).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
