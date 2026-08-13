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
        public Row[] rows;
    }

    /// <summary>
    /// Σ PathRoute.TotalCapacity(largest radius) over the ground routes plus
    /// the air allowance — the same math RouteTraffic.DerivedCapacity runs on
    /// live tracks in play (which cannot be called here: it reads registered
    /// movers, and an unplayed scene has none).
    /// </summary>
    public static int DeriveMaxLive(List<PathRoute> routes, bool airCorridor)
    {
        int total = 0;
        foreach (PathRoute route in routes)
            total += route.TotalCapacity(LargestBodyRadius);
        if (airCorridor)
            total += AirCapacityAllowance;
        return Mathf.Max(1, total);
    }

    /// <summary>
    /// Run the model against the scene's generated geometry. Pass
    /// <paramref name="solveGrowth"/> to bisect hpGrowthPerWave (generated
    /// maps); otherwise <paramref name="hpGrowth"/> is verified as-is (parity —
    /// solving would un-parity the shipped rules). Returns null with an
    /// actionable <paramref name="error"/> on any failure.
    /// </summary>
    public static Result Run(List<PathRoute> routes, Vector3 airSpawn, Vector3 coreTarget,
                             bool solveGrowth, float hpGrowth, int maxLive, out string error)
    {
        error = null;

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

        var pads = UnityEngine.Object.FindObjectsByType<HardpointCoverageGizmo>(FindObjectsSortMode.None)
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
            sb.Append($"{{\"name\":\"{route.name}\",\"spawner\":{r},\"measured_length\":");
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
                      $"\"tower\":\"{TowerId(g.intendedTurret)}\",\"cls\":\"{g.padClass}\"}}");
        }
        sb.Append("],\"build_priority\":[");
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
                };
                using (var proc = Process.Start(psi))
                {
                    string so = proc.StandardOutput.ReadToEnd();
                    string se = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(120_000))
                    {
                        proc.Kill();
                        attempts.Add($"{exe}: timed out after 120 s");
                        continue;
                    }
                    stdout = so;
                    stderr = se;
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
