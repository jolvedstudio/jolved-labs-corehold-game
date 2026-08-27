using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Towers;
using UnityEngine;
using Kind = Corehold.Towers.HardpointCoverageGizmo.TurretKind;
using Cls = Corehold.Towers.HardpointCoverageGizmo.PadClass;

/// <summary>
/// The bridge to <c>docs/balance_model.py</c> (roadmap R30).
///
/// The margin math is NEVER ported to C#: two implementations of the gate
/// drift, and the whole point of R1 is that there is exactly one. Generation
/// is editor-time, so this shells out — write the generated geometry as JSON,
/// run the model as a subprocess (solving hpGrowthPerWave or verifying a given
/// one), read back <c>--json</c>.
///
/// Python 3 is therefore a dev-machine dependency FOR GENERATION ONLY — the
/// shipped game never runs it. When the interpreter cannot be found, the
/// failure message says exactly that, per the roadmap.
/// </summary>
public static class BalanceModelRunner
{
    /// <summary>
    /// The largest spawnable body radius (the Colossus class), matching the
    /// clearance envelope's maxBodyRadius. Used for the derived live cap. [TUNE]
    /// </summary>
    public const float LargestBodyRadius = 1.35f;

    /// <summary>Air-corridor concurrency allowance — RouteTraffic.DerivedCapacity's own estimate.</summary>
    public const int AirCapacityAllowance = 8;

    [Serializable]
    public class Row
    {
        public int wave;
        public float margin;
        public string[] flags;
        public string worst_group;
        // Pads whose turret died to return fire this wave (tower-loss term).
        // Informational — the margin already carries the cost.
        public string[] towers_lost;
        // The model's fix suggestions for this wave (empty when unflagged):
        // which knob, which enemy, how much. Display verbatim.
        public string[] advice;
    }

    /// <summary>
    /// One entry of the model's counts-only tune (emitted whenever a run is
    /// flagged): set wave[wave-1].groups[group].count from prev to next
    /// (next 0 = delete the group). Indices address the level's waves array
    /// in its exported order; enemy is the id sanity check at apply time.
    /// </summary>
    [Serializable]
    public class SuggestedChange
    {
        public int wave;     // 1-based
        public int group;    // index into that wave's groups, pre-edit order
        public string enemy;
        public int prev;
        public int next;
    }

    [Serializable]
    public class Result
    {
        public string difficulty;
        public float hp_growth_used;
        public bool solved;
        public float solved_hp_growth;
        public int max_live;
        public bool in_band;
        public SuggestedChange[] suggested_changes;
        // Defender package (level-scoped, runtime-honoured): the FINAL
        // multipliers/bank the tune settled on. 1 / 0 = untouched.
        public float suggested_tower_dps_mult = 1f;
        public float suggested_tower_range_mult = 1f;
        public int suggested_starting_salvage;
        public bool suggested_in_band;
        public string suggested_note;
        public Row[] rows;
    }

    /// <summary>True when the tune proposes anything at all — a defender
    /// package, wave count changes, or both.</summary>
    public static bool HasSuggestedTune(Result r)
    {
        if (r == null)
            return false;
        return (r.suggested_changes != null && r.suggested_changes.Length > 0) ||
               r.suggested_tower_dps_mult > 1.0001f ||
               r.suggested_tower_range_mult > 1.0001f ||
               r.suggested_starting_salvage > 0;
    }

    /// <summary>
    /// Human-readable block for a flagged run's suggested tune; empty when
    /// the run carried none. Display-only — the numbers are the model's.
    /// </summary>
    public static string DescribeSuggestedTune(Result r)
    {
        if (!HasSuggestedTune(r))
            return "";
        var sb = new StringBuilder();
        sb.AppendLine(r.suggested_in_band
            ? "suggested tune that passes the gate:"
            : "suggested tune (best effort — does not fully converge):");
        string pkg = DescribeDefenderPackage(r);
        if (pkg.Length > 0)
            sb.AppendLine($"  defender package (level-scoped, certified): {pkg}");
        if (r.suggested_changes != null)
            foreach (SuggestedChange c in r.suggested_changes)
                sb.AppendLine($"  wave {c.wave}: {c.enemy} {c.prev}→{c.next}" +
                              (c.next == 0 ? " (drop the group)" : ""));
        if (!string.IsNullOrEmpty(r.suggested_note))
            sb.AppendLine($"  {r.suggested_note}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>"starting salvage → 600, turret damage ×1.50, …" or "".</summary>
    public static string DescribeDefenderPackage(Result r)
    {
        if (r == null)
            return "";
        var parts = new List<string>();
        if (r.suggested_starting_salvage > 0)
            parts.Add($"starting salvage → {r.suggested_starting_salvage}");
        if (r.suggested_tower_dps_mult > 1.0001f)
            parts.Add($"turret damage ×{r.suggested_tower_dps_mult:0.00}");
        if (r.suggested_tower_range_mult > 1.0001f)
            parts.Add($"turret range ×{r.suggested_tower_range_mult:0.00}");
        return string.Join(", ", parts);
    }

    /// <summary>Live cap the shipped map is tuned and framed around.</summary>
    public const int ShippedMaxLive = 14;

    /// <summary>Shipped ground route length, both legs summed (153.7 + 154.5 m).</summary>
    public const float ShippedGroundMetres = 308.2f;

    /// <summary>Below this a wave stops reading as a wave.</summary>
    public const int MinMaxLive = 6;

    /// <summary>
    /// How many enemies may be alive at once.
    ///
    /// Σ PathRoute.TotalCapacity is how many bodies PHYSICALLY FIT nose-to-tail
    /// — several lanes × ~48 units per 154 m lane, so a two-route map computes
    /// out around 200. That is a ceiling, not a cap, and using it as the cap is
    /// what produced a solid wall of overlapping enemies on generated maps while
    /// the shipped map sits at 14. Packing capacity answers "can they fit"; the
    /// cap has to answer "how many should be on screen at once", and the shipped
    /// map is the only measured answer to that we have.
    ///
    /// So: scale the shipped 14 by how much more path this map has, and clamp to
    /// what physically fits. A map with the shipped route length reproduces 14
    /// exactly, which keeps the model's inputs honest. The cap feeds the balance
    /// model, so tightening it re-solves hpGrowthPerWave against the pressure the
    /// player will actually face rather than against a wall that cannot happen.
    /// </summary>
    public static int DeriveMaxLive(List<PathRoute> routes, bool airCorridor)
    {
        int packing = 0;
        float metres = 0f;
        foreach (PathRoute route in routes)
        {
            packing += route.TotalCapacity(LargestBodyRadius);
            metres += route.Length;
        }
        if (airCorridor)
            packing += AirCapacityAllowance;

        packing = Mathf.Max(1, packing);
        int presentation = Mathf.RoundToInt(ShippedMaxLive * (metres / ShippedGroundMetres));
        return Mathf.Clamp(Mathf.Min(packing, presentation), Mathf.Min(MinMaxLive, packing), packing);
    }

    /// <summary>
    /// Run the model against the scene's generated geometry. Pass
    /// <paramref name="solveGrowth"/> to bisect hpGrowthPerWave (generated
    /// maps); otherwise <paramref name="hpGrowth"/> is verified as-is (a fixed-rules run —
    /// solving would un-parity the shipped rules). Returns null with an
    /// actionable <paramref name="error"/> on any failure.
    /// </summary>
    public static Result Run(List<PathRoute> routes, Vector3 airSpawn, Vector3 coreTarget,
                             bool solveGrowth, float hpGrowth, int maxLive, out string error,
                             int startingSalvage = -1, string wavesJsonPath = null,
                             float towerDpsMult = 1f, float towerRangeMult = 1f)
    {
        error = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        string geometryPath = WriteGeometryJson(routes, airSpawn, coreTarget, out string geomError);
        if (geometryPath == null)
        {
            error = geomError;
            return null;
        }

        string outPath = Path.Combine(Path.GetTempPath(), $"corehold_model_{Guid.NewGuid():N}.json");
        var args = new StringBuilder();
        args.Append($"docs/balance_model.py --geometry \"{geometryPath}\" --json \"{outPath}\" ");
        args.Append($"--max-live {maxLive} ");
        // A2 campaign carry: an ABSOLUTE entry bank (the model skips its own
        // difficulty economy multiplier for it). -1 = the model's default.
        if (startingSalvage >= 0)
            args.Append($"--starting-salvage {startingSalvage} ");
        // Certified per-level turret tuning (LevelDefinition multipliers) —
        // the model must judge the level with the same buffs the runtime
        // (TowerTuning) will apply, or "certified" is fiction.
        if (towerDpsMult > 0f && Mathf.Abs(towerDpsMult - 1f) > 0.0001f)
            args.Append($"--tower-dps-mult {towerDpsMult.ToString("0.####", inv)} ");
        if (towerRangeMult > 0f && Mathf.Abs(towerRangeMult - 1f) > 0.0001f)
            args.Append($"--tower-range-mult {towerRangeMult.ToString("0.####", inv)} ");
        // Live certification: the level's actual wave tables + enemy stats
        // (WaveTableExporter) replace the model's embedded copies for this run,
        // so the gate certifies the assets the level will really play.
        if (!string.IsNullOrEmpty(wavesJsonPath))
            args.Append($"--waves \"{wavesJsonPath}\" ");
        args.Append(solveGrowth
            ? "--solve-hp-growth"
            : $"--hp-growth {hpGrowth.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}");

        try
        {
            if (!TryRunPython(args.ToString(), out string stdout, out string stderr, out string howRun))
            {
                error = "Could not start Python. Generation needs Python 3 on this machine (the balance " +
                        "model is the gate, and it is deliberately not reimplemented in C#) — install " +
                        "python3 / ensure it is on PATH. The shipped game itself never needs it.\n" + howRun;
                return null;
            }

            if (!File.Exists(outPath))
            {
                error = $"balance model produced no JSON output.\nstdout tail:\n{Tail(stdout)}\nstderr:\n{Tail(stderr)}";
                return null;
            }

            var result = JsonUtility.FromJson<Result>(File.ReadAllText(outPath));
            if (result == null || result.rows == null || result.rows.Length == 0)
            {
                error = "balance model JSON parsed empty — model/runner contract drift?";
                return null;
            }
            return result;
        }
        finally
        {
            SafeDelete(geometryPath);
            SafeDelete(outPath);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Serialize the scene's ACTUAL geometry: route knots + measured spline
    /// lengths from the built PathRoutes, pads from the placed gizmos (never
    /// from any intermediate list — the scene is what ships), build priority
    /// ordered Premium → Standard → Rear → Overwatch as the sim expects.
    /// </summary>
    private static string WriteGeometryJson(List<PathRoute> routes, Vector3 airSpawn, Vector3 coreTarget,
                                            out string error)
    {
        error = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Active scene only — the model must describe the map being generated,
        // not pads belonging to some other scene the editor happens to have open.
        var pads = SceneQuery.InActiveScene<HardpointCoverageGizmo>()
            .OrderBy(g => g.name, StringComparer.Ordinal).ToArray();
        if (pads.Length == 0)
        {
            error = "no hardpoints in the scene to model";
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("{\"routes\":[");
        for (int r = 0; r < routes.Count; r++)
        {
            PathRoute route = routes[r];
            if (r > 0) sb.Append(',');
            // The REAL spawner index, not the loop counter: siege approaches step
            // over the air index, and the model keys routes by whatever the wave
            // tables address. Writing the counter would have the model simulate a
            // different route assignment than the game runs.
            sb.Append($"{{\"name\":\"{route.name}\",\"spawner\":{GenerationPipeline.GroundSpawnerIndex(r)}," +
                      "\"measured_length\":");
            sb.Append(route.Length.ToString("0.###", inv));
            sb.Append(",\"points\":[");
            for (int i = 0; i < route.PointCount; i++)
            {
                Vector3 p = route.GetPoint(i);
                if (i > 0) sb.Append(',');
                sb.Append($"[{p.x.ToString("0.###", inv)},{p.z.ToString("0.###", inv)}]");
            }
            sb.Append("]}");
        }
        sb.Append("],\"air_spawn\":[");
        sb.Append($"{airSpawn.x.ToString("0.###", inv)},{airSpawn.z.ToString("0.###", inv)}");
        sb.Append("],\"air_target\":[");
        sb.Append($"{coreTarget.x.ToString("0.###", inv)},{coreTarget.z.ToString("0.###", inv)}");
        sb.Append("],\"pads\":[");
        for (int i = 0; i < pads.Length; i++)
        {
            var g = pads[i];
            if (i > 0) sb.Append(',');
            Vector3 p = g.transform.position;
            sb.Append($"{{\"name\":\"{g.name}\",\"x\":{p.x.ToString("0.###", inv)}," +
                      $"\"z\":{p.z.ToString("0.###", inv)}," +
                      $"\"tower\":\"{TowerId(g.intendedTurret)}\",\"cls\":\"{g.padClass}\"");
            // High ground (M-b): the pad's terrain damage bonus, written by the
            // terrain stage. Emitted only when nonzero so flat-map exports stay
            // byte-identical to the pre-terrain format.
            var hardpoint = g.GetComponent<Corehold.Towers.TowerHardpoint>();
            if (hardpoint != null && hardpoint.HighGroundBonus > 0f)
                sb.Append($",\"hg\":{hardpoint.HighGroundBonus.ToString("0.####", inv)}");
            sb.Append('}');
        }
        sb.Append($"],\"spread_ground_groups\":{(routes.Count > 2 ? "true" : "false")},\"build_priority\":[");
        var ordered = pads.OrderBy(g => ClassOrder(g.padClass))
                          .ThenBy(g => g.name, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"{ordered[i].name}\"");
        }
        sb.Append("]}");

        string path = Path.Combine(Path.GetTempPath(), $"corehold_geom_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static int ClassOrder(Cls cls)
    {
        switch (cls)
        {
            case Cls.Premium: return 0;
            case Cls.Standard: return 1;
            case Cls.Rear: return 2;
            default: return 3;
        }
    }

    private static string TowerId(Kind kind)
    {
        switch (kind)
        {
            case Kind.Autocannon: return "autocannon";
            case Kind.Missile: return "missile_battery";
            case Kind.ArcNode: return "arc_node";
            case Kind.Mortar: return "siege_mortar";
            default: return "autocannon";
        }
    }

    /// <summary>Try python3 first (Linux/macOS), then python (Windows installs).</summary>
    private static bool TryRunPython(string arguments, out string stdout, out string stderr, out string detail)
    {
        stdout = stderr = null;
        var attempts = new List<string>();
        foreach (string exe in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    WorkingDirectory = Directory.GetCurrentDirectory(),   // the project root
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // Windows pipes default to the ANSI codepage (cp1252),
                    // which cannot carry the report's arrows — the model
                    // forces UTF-8 on its side (sys.stdout.reconfigure), this
                    // is the matching read side plus a belt-and-braces env
                    // override for any Python that predates reconfigure.
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (var proc = Process.Start(psi))
                {
                    // Drain the pipes on tasks so the wait loop below can poll
                    // for exit/cancel without the classic full-pipe deadlock.
                    var soTask = proc.StandardOutput.ReadToEndAsync();
                    var seTask = proc.StandardError.ReadToEndAsync();

                    var sw = Stopwatch.StartNew();
                    bool cancelled = false;
                    bool timedOut = false;
                    while (!proc.HasExited)
                    {
                        if (sw.ElapsedMilliseconds > 120_000)
                        {
                            proc.Kill();
                            timedOut = true;
                            attempts.Add($"{exe}: timed out after 120 s");
                            break;
                        }
                        if (!GenerationProgress.Detail(
                                $"balance model running ({sw.Elapsed.TotalSeconds:0} s)…",
                                Mathf.Clamp01((float)sw.Elapsed.TotalSeconds / 30f)))
                        {
                            proc.Kill();
                            cancelled = true;
                            break;
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                    if (cancelled)
                    {
                        attempts.Add($"{exe}: cancelled by user");
                        detail = string.Join("\n", attempts);
                        return false;
                    }
                    if (timedOut)
                        continue;
                    stdout = soTask.GetAwaiter().GetResult();
                    stderr = seTask.GetAwaiter().GetResult();
                    detail = $"ran '{exe}' (exit {proc.ExitCode})";
                    // A non-zero exit is a MODEL verdict (flagged waves), not a
                    // launch failure — the JSON carries the details either way.
                    return true;
                }
            }
            catch (Exception ex)
            {
                attempts.Add($"{exe}: {ex.GetType().Name} — {ex.Message}");
            }
        }
        detail = string.Join("\n", attempts);
        return false;
    }

    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "(empty)";
        return s.Length <= 800 ? s : s.Substring(s.Length - 800);
    }

    private static void SafeDelete(string path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); }
        catch { /* temp files; the OS will get them eventually */ }
    }
}
