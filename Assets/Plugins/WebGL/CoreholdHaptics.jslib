// WebGL haptics bridge (VFX plan Tier 4 / review blocker B).
// Unity's Gamepad.SetMotorSpeeds and Handheld.Vibrate are silent no-ops in a
// browser build, so rumble goes straight to the two channels browsers expose:
//   - navigator.vibrate       : phones/tablets (Android Chrome; iOS has none),
//   - gamepad.vibrationActuator: controllers (Chrome/Edge dual-rumble).
// Fire-and-forget; every path degrades to a harmless no-op when unsupported.
mergeInto(LibraryManager.library, {
  Corehold_Vibrate: function (durationMs, strong, weak) {
    try {
      if (navigator.vibrate) navigator.vibrate(durationMs);
      var pads = (navigator.getGamepads && navigator.getGamepads()) || [];
      for (var i = 0; i < pads.length; i++) {
        var p = pads[i];
        if (p && p.vibrationActuator && p.vibrationActuator.playEffect) {
          p.vibrationActuator.playEffect("dual-rumble", {
            duration: durationMs,
            strongMagnitude: strong,
            weakMagnitude: weak
          });
        }
      }
    } catch (e) { /* haptics are never worth an exception */ }
  }
});
