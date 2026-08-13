using System.Collections.Generic;
using System.IO;
using System.Text;
using Corehold.Core;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The P2 spline gate, in one run (roadmap R9).
///
/// R9 is a validation ticket: it ships no feature, it decides whether the spline
/// backbone is allowed to become the default. This tool produces the evidence for
/// every mechanical stage of that decision and writes it to
/// <c>docs/spline_gate_report.txt</c> so "filed" means a file, not a console
/// scrollback:
///
///   1. CLEARANCE  — <see cref="ValidateRouteClearance"/>, report-only. Where it
///                   flags a conflict a HUMAN moves the named knots; nothing here
///                   nudges geometry, because route geometry is balance-load-bearing.
///   2. COVERAGE   — every pad counted BOTH ways (chords vs the walked route) so
///                   the before/after table R8 owes is a like-for-like comparison,
///                   with any pad whose pass/fail flips called out for explicit
///                   acceptance.
///   3. DIVERGENCE — R7's shared-tail check.
///   4. LENGTH     — per-route polyline vs curve length and the delta %, which is
///                   the number R10 re-baselines the balance model against.
///
/// Two things this tool deliberately does NOT do, because they are the human's
/// call: flip <c>useSpline</c> on by default, and capture the 1920×1080 screenshot
/// sign-off set across the three framing aspects.
/// </summary>
public static class SplineGateReport
{
    private const string ReportPath = "docs/spline_gate_report.txt";

    [MenuItem("Tools/COREHOLD/Validate/Run Spline Gate (R9)", false, 21)]
    public static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("COREHOLD — P2 spline gate report (roadmap R9)");
        sb.AppendLine($"scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        sb.AppendLine();

        bool clearanceClean = AppendClearance(sb);
        bool coverageHolds = AppendCoverage(sb, out int flippedPads);
        bool divergenceOk = AppendDivergence(sb);
        AppendLengths(sb);

        sb.AppendLine("=== GATE SUMMARY ===");
        sb.AppendLine($"  clearance clean        : {YesNo(clearanceClean)}");
        sb.AppendLine($"  coverage rule holds    : {YesNo(coverageHolds)}");
        sb.AppendLine($"  coverage classes moved : {flippedPads} pad(s) — each needs EXPLICIT acceptance");
        sb.AppendLine($"  shared-tail divergence : {YesNo(divergenceOk)}");
        sb.AppendLine();
        sb.AppendLine("  Still owed by a human before flipping useSpline default ON:");
        sb.AppendLine("    • move any knots the clearance stage named (never auto-nudged)");
        sb.AppendLine("    • accept any coverage class change above, in writing");
        sb.AppendLine("    • capture the 1920×1080 sign-off set at 16:9, 16:10 and 20:9");
        sb.AppendLine("    • re-run docs/balance_model.py on the new Lengths (R10)");

        string report = sb.ToString();
        Debug.Log(report);
        WriteReport(report);
    }

    // ---------------------------------------------------------------- stages

    private static bool AppendClearance(StringBuilder sb)
    {
        sb.AppendLine("=== 1. ROUTE CLEARANCE (report-only) ===");
        string result = ValidateRouteClearance.Execute();
        sb.AppendLine(result);
        sb.AppendLine();
        return result.Contains("Route clearance OK");
    }

    private static bool AppendCoverage(StringBuilder sb, out int flippedPads)
    {
        sb.AppendLine("=== 2. HARDPOINT COVERAGE — chords (before) vs walked route (after) ===");

        var pads = new List<HardpointCoverageGizmo>(
            Object.FindObjectsByType<HardpointCoverageGizmo>(FindObjectsSortMode.None));
        pads.Sort((x, y) => string.CompareOrdinal(x.name, y.name));

        flippedPads = 0;
        if (pads.Count == 0)
        {
            sb.AppendLine("  no hardpoints found in the scene.");
            sb.AppendLine();
            return false;
        }

        sb.AppendLine($"  {"pad",-16} {"turret",-11} {"class",-10} {"need",4} {"chord",5} {"curve",5} {"delta",5}  verdict");

        bool allPass = true;
        int premiumWithFour = 0;

        foreach (var pad in pads)
        {
            int need = pad.padClass == HardpointCoverageGizmo.PadClass.Premium ? 4 : 2;
            int chord = pad.CountCoveredSegmentsLinear();
            int curve = pad.CountCoveredSpansOnCurve();

            bool passBefore = chord >= need;
            bool passAfter = curve >= need;
            if (!passAfter) allPass = false;
            if (pad.padClass == HardpointCoverageGizmo.PadClass.Premium && curve >= 4)
                premiumWithFour++;

            string verdict = passAfter ? "PASS" : "**FAIL**";
            if (passBefore != passAfter)
            {
                flippedPads++;
                verdict += passAfter ? "  <-- GAINED (accept explicitly)" : "  <-- LOST (accept explicitly)";
            }
            else if (chord != curve)
            {
                verdict += "  (count moved, class unchanged)";
            }

            sb.AppendLine($"  {pad.name,-16} {pad.intendedTurret,-11} {pad.padClass,-10} " +
                          $"{need,4} {chord,5} {curve,5} {curve - chord,5:+#;-#;0}  {verdict}");
        }

        bool rule = allPass && premiumWithFour >= 3;
        sb.AppendLine($"  Premium pads covering 4+: {premiumWithFour} (need >= 3)");
        sb.AppendLine($"  COVERAGE RULE: {(rule ? "SATISFIED" : "**NOT MET**")}");
        sb.AppendLine();
        return rule;
    }

    private static bool AppendDivergence(StringBuilder sb)
    {
        sb.AppendLine("=== 3. SHARED-TAIL DIVERGENCE (R7) ===");
        string result = MergeKnotPinning.DivergenceReport();
        sb.AppendLine(result);
        sb.AppendLine();
        return result.Contains("PASS");
    }

    private static void AppendLengths(StringBuilder sb)
    {
        sb.AppendLine("=== 4. ROUTE LENGTH DELTA (input to R10) ===");

        var routes = new List<PathRoute>(Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None));
        routes.RemoveAll(r => r == null || r.PointCount < 2);
        routes.Sort((x, y) => string.CompareOrdinal(x.name, y.name));

        sb.AppendLine($"  {"route",-14} {"spline?",8} {"polyline",10} {"current",10} {"delta",9}");
        foreach (var route in routes)
        {
            // The polyline length is computed from the knots directly, so it is the
            // pre-spline baseline regardless of which mode the route is in now.
            float polyline = 0f;
            for (int i = 1; i < route.PointCount; i++)
                polyline += Vector3.Distance(route.GetPoint(i - 1), route.GetPoint(i));

            float current = route.Length;
            float delta = polyline > 0.001f ? (current - polyline) / polyline : 0f;

            sb.AppendLine($"  {route.name,-14} {route.SplineReady,8} {polyline,10:0.###} " +
                          $"{current,10:0.###} {delta,8:P2}");
        }
        sb.AppendLine("  Longer curved routes give enemies more time-in-range, which EASES margins.");
        sb.AppendLine("  Feed these lengths to docs/balance_model.py and report any wave whose margin moves >0.15 (R10).");
        sb.AppendLine();
    }

    // ---------------------------------------------------------------- output

    private static void WriteReport(string report)
    {
        try
        {
            // Application.dataPath is <project>/Assets; the report lives beside the
            // balance-model baseline in <project>/docs.
            string root = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(root, ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, report);
            Debug.Log($"[R9] gate report written to {ReportPath}");
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[R9] could not write {ReportPath}: {e.Message}");
        }
    }

    private static string YesNo(bool ok) => ok ? "YES" : "NO";
}
