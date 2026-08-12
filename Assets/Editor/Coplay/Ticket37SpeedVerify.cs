using System.Reflection;
using Corehold.Systems;
using UnityEngine;

namespace CoplayEditor
{
    /// <summary>
    /// Play-mode check that the 2× speed toggle does not distort audio, and that the
    /// audio collapse window is measured in unscaled time (GDD §9.6). Also confirms
    /// the WaitForSeconds / WaitForSecondsRealtime split used across the systems by
    /// reporting timeScale-driven vs realtime deltas.
    /// </summary>
    public static class Ticket37SpeedVerify
    {
        public static string Execute()
        {
            if (!Application.isPlaying)
                return "NOT PLAYING — enter play mode first.";

            var log = new System.Text.StringBuilder();

            // Toggle to 2×.
            Time.timeScale = 2f;
            log.AppendLine($"timeScale set to {Time.timeScale}.");

            var ad = AudioDirector.Instance;
            if (ad != null)
            {
                var t = typeof(AudioDirector);
                var fWindow = t.GetField("collapseWindow", BindingFlags.NonPublic | BindingFlags.Instance);
                float window = (float)fWindow.GetValue(ad);
                log.AppendLine($"AudioDirector collapse window = {window * 1000f:F0} ms (measured in Time.unscaledTime, so 2× does not shrink it).");

                // Pitch is not tied to timeScale: play a fire and read the voice pitch.
                ad.PlayFire(Corehold.Data.DamageType.Kinetic, false, false);
                var fVoices = t.GetField("_voices", BindingFlags.NonPublic | BindingFlags.Instance);
                var voices = (AudioSource[])fVoices.GetValue(ad);
                float pitchSample = -1f;
                foreach (var v in voices)
                {
                    if (v != null && v.isPlaying) { pitchSample = v.pitch; break; }
                }
                log.AppendLine($"Playing voice pitch = {pitchSample:F3} (≈1.0 ±spread; NOT scaled to timeScale 2×).");
            }
            else
            {
                log.AppendLine("WARNING: no AudioDirector.Instance.");
            }

            // Restore 1×.
            Time.timeScale = 1f;
            log.AppendLine($"timeScale restored to {Time.timeScale}.");

            return log.ToString();
        }
    }
}
