using System.Reflection;
using Corehold.Core;
using Corehold.Systems;
using UnityEngine;

namespace CoplayEditor
{
    /// <summary>
    /// Play-mode verification for Ticket 37. Must be run while the game is playing.
    /// Drives the Core integrity down through the 66% / 33% / 20% bands and reports
    /// the resulting CoreDamageState flags, then fires two camera-shake requests back
    /// to back to confirm the 1.5 s cooldown refuses the second.
    /// </summary>
    public static class Ticket37Verify
    {
        public static string Execute()
        {
            if (!Application.isPlaying)
                return "NOT PLAYING — enter play mode first.";

            var log = new System.Text.StringBuilder();
            var gm = GameManager.Instance;
            if (gm == null) return "No GameManager.";

            // Make sure we are in a run with full integrity.
            gm.ConfigureRun(Difficulty.Normal);
            int max = GameManager.StartingIntegrityFor(gm.Difficulty);
            log.AppendLine($"Difficulty Normal, max integrity {max}, current {gm.Integrity}.");

            var cds = Object.FindFirstObjectByType<CoreDamageState>();
            if (cds == null) return "No CoreDamageState in scene.";

            var t = typeof(CoreDamageState);
            FieldInfo fSeg0 = t.GetField("_seg0Dark", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fSeg1 = t.GetField("_seg1Dark", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fCrit = t.GetField("_critical", BindingFlags.NonPublic | BindingFlags.Instance);

            void Report(string band)
            {
                bool s0 = (bool)fSeg0.GetValue(cds);
                bool s1 = (bool)fSeg1.GetValue(cds);
                bool cr = (bool)fCrit.GetValue(cds);
                float frac = (float)gm.Integrity / max;
                log.AppendLine($"{band}: integrity {gm.Integrity}/{max} ({frac:P0}) -> seg0Dark={s0} seg1Dark={s1} critical={cr}");
            }

            // Full.
            Report("FULL");

            // Drop to ~65% (below 66%).
            gm.DamageCore(Mathf.CeilToInt(max * 0.36f));
            Report("~64%");

            // Drop to ~32% (below 33%).
            gm.DamageCore(Mathf.CeilToInt(max * 0.32f));
            Report("~32%");

            // Drop to ~15% (below 20% critical).
            gm.DamageCore(Mathf.CeilToInt(max * 0.17f));
            Report("~15%");

            // ---- Camera shake cooldown ----
            var shake = CameraShake.Instance;
            if (shake != null)
            {
                bool first = shake.Shake();
                bool second = shake.Shake(); // immediate — should be refused
                log.AppendLine($"CameraShake: first={first} (expect True), immediate-second={second} (expect False, cooldown), remaining={shake.CooldownRemaining:F2}s.");
            }
            else
            {
                log.AppendLine("WARNING: no CameraShake.Instance.");
            }

            // Restore for continued play.
            gm.ConfigureRun(Difficulty.Normal);

            return log.ToString();
        }
    }
}
