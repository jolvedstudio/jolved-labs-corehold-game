using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Corehold.Systems
{
    /// <summary>
    /// WebGL haptics (VFX plan Tier 4 / proposal-review blocker B). Unity's own
    /// APIs are dead on this target — <c>Gamepad.SetMotorSpeeds</c> and
    /// <c>Handheld.Vibrate</c> are silent no-ops in a browser — so pulses go
    /// through a .jslib (<c>Plugins/WebGL/CoreholdHaptics.jslib</c>) to the two
    /// channels browsers actually expose: <c>navigator.vibrate</c> for
    /// phones/tablets and the gamepad <c>vibrationActuator</c> for controllers.
    ///
    /// Design rules:
    ///   • COSMETIC punctuation only — explosions and Core hits, never a
    ///     continuous rumble and never gameplay information.
    ///   • Callers pass intensity ALREADY scaled by their accessibility gate
    ///     (CameraShake's effectScale), so "reduce effects" quiets rumble too.
    ///   • Rate-limited: repeated playEffect calls CANCEL the running effect,
    ///     so spamming reads as stutter rather than more rumble.
    ///   • No-op in the editor, on desktop without a pad, and on iOS — safe to
    ///     call unconditionally from any platform path.
    /// </summary>
    public static class Haptics
    {
        /// <summary>Master switch (for the Settings screen; default on — browsers
        /// without vibration support simply do nothing).</summary>
        public static bool Enabled = true;

        private const float MinInterval = 0.06f;
        private static float _lastTime = -10f;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void Corehold_Vibrate(int durationMs, float strong, float weak);
#endif

        /// <summary>One rumble pulse: <paramref name="seconds"/> long (clamped
        /// 10–400 ms) at <paramref name="intensity"/> 0..1. Zero intensity no-ops.</summary>
        public static void Pulse(float seconds, float intensity)
        {
            if (!Enabled || intensity <= 0f)
                return;
            float now = Time.unscaledTime;
            if (now - _lastTime < MinInterval)
                return;
            _lastTime = now;
#if UNITY_WEBGL && !UNITY_EDITOR
            Corehold_Vibrate(
                Mathf.Clamp(Mathf.RoundToInt(seconds * 1000f), 10, 400),
                Mathf.Clamp01(intensity),
                Mathf.Clamp01(intensity * 0.6f));
#else
            _ = seconds;   // intentionally inert off-target; the call sites stay identical
#endif
        }
    }
}
