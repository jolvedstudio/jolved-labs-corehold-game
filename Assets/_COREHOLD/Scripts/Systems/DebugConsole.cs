#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Corehold.Core;
using Corehold.Enemies;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Corehold.Systems
{
    /// <summary>
    /// Editor / development-build only debug console (GDD §12.4). Costs nothing
    /// in the ship build because the entire class body is compiled out.
    ///
    /// Uses the new Input System (the project's active input handler) so it works
    /// under the Input System package. Reads <see cref="Keyboard.current"/> each
    /// frame.
    ///
    /// Key bindings:
    ///   ]      skip to / start next wave
    ///   [      previous wave (index only)
    ///   0      jump straight to wave 9 (Ticket 29 draw-call check)
    ///   M      grant 1000 salvage
    ///   I      toggle core invulnerability
    ///   K      kill all live enemies
    ///   S      stun all live enemies 3 s (R18 test — Colossus resists 25%)
    ///   L      slow all live enemies 50% for 3 s (R18 test)
    ///   T      cycle forced wave mutators for waves started AFTER the press
    ///          (R20 test: None → Storm → Convoy → Overcharge → Blackout → All)
    ///   1/2/3  set difficulty (Normal / Veteran / Nightmare)
    ///   F1     toggle the OnGUI overlay
    /// </summary>
    [DisallowMultipleComponent]
    public class DebugConsole : MonoBehaviour
    {
        private bool _showOverlay;
        private float _frameMs;

        private void Update()
        {
            // Smoothed frame time in milliseconds.
            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.1f);

            var kb = Keyboard.current;
            if (kb == null)
                return;

            if (kb.rightBracketKey.wasPressedThisFrame)
                NextWave();
            if (kb.leftBracketKey.wasPressedThisFrame)
                PreviousWave();
            if (kb.digit0Key.wasPressedThisFrame)
                JumpToWave(9);
            if (kb.mKey.wasPressedThisFrame)
                GrantSalvage(1000);
            if (kb.iKey.wasPressedThisFrame)
                ToggleCoreInvulnerability();
            if (kb.kKey.wasPressedThisFrame)
                KillAllEnemies();
            if (kb.sKey.wasPressedThisFrame)
                StunAllEnemies();
            if (kb.lKey.wasPressedThisFrame)
                SlowAllEnemies();
            if (kb.tKey.wasPressedThisFrame)
                CycleForcedMutators();
            if (kb.digit1Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Normal);
            if (kb.digit2Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Veteran);
            if (kb.digit3Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Nightmare);
            if (kb.f1Key.wasPressedThisFrame)
                _showOverlay = !_showOverlay;
        }

        // ----- Commands -----

        private void NextWave()
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm != null && wm.StartNextWave())
            {
                Debug.Log($"[DebugConsole] Started wave (next index now {wm.NextWaveIndex}).");
                return;
            }

            var gm = GameManager.Instance;
            if (gm != null)
                gm.WaveIndex++;
            Debug.Log($"[DebugConsole] Next wave (WaveIndex now {gm?.WaveIndex}).");
        }

        private void PreviousWave()
        {
            // WaveManager has no rewind (waves are one-shot); this steps the
            // GameManager index back for tuning purposes only.
            var gm = GameManager.Instance;
            if (gm != null)
                gm.WaveIndex = Mathf.Max(0, gm.WaveIndex - 1);
            Debug.Log($"[DebugConsole] Previous wave (WaveIndex now {gm?.WaveIndex}).");
        }

        /// <summary>
        /// Fast-forward straight to the given 1-based wave for tuning and the
        /// draw-call check (GDD §12.4, Ticket 29). Advances the WaveManager's
        /// next-wave pointer without spawning the intervening waves, then starts it.
        /// </summary>
        private void JumpToWave(int waveNumber)
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogWarning("[DebugConsole] No WaveManager to jump with.");
                return;
            }

            int target = Mathf.Clamp(waveNumber, 1, wm.WaveCount);
            wm.JumpToWave(target);
            Debug.Log($"[DebugConsole] Jumped to wave {target} and started it.");
        }

        private void GrantSalvage(int amount)
        {
            var gm = GameManager.Instance;
            if (gm != null)
                gm.AddSalvage(amount);
            Debug.Log($"[DebugConsole] Granted {amount} salvage.");
        }

        private void ToggleCoreInvulnerability()
        {
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.CoreInvulnerable = !gm.CoreInvulnerable;
                Debug.Log($"[DebugConsole] Core invulnerable: {gm.CoreInvulnerable}.");
            }
        }

        private void KillAllEnemies()
        {
            var enemies = FindObjectsByType<Enemy>(FindObjectsSortMode.None);
            int killed = 0;
            foreach (var e in enemies)
            {
                if (e != null && e.IsAlive)
                {
                    e.TakeDamage(e.CurrentHealth);
                    killed++;
                }
            }
            Debug.Log($"[DebugConsole] Killed {killed} live enemies.");
        }

        private void StunAllEnemies()
        {
            int hit = 0;
            for (int i = 0; i < Enemy.Live.Count; i++)
            {
                var e = Enemy.Live[i];
                if (e != null && e.IsAlive) { e.ApplyStun(3f); hit++; }
            }
            Debug.Log($"[DebugConsole] Stunned {hit} live enemies for 3 s (R18; Colossus takes 2.25 s).");
        }

        private void SlowAllEnemies()
        {
            int hit = 0;
            for (int i = 0; i < Enemy.Live.Count; i++)
            {
                var e = Enemy.Live[i];
                if (e != null && e.IsAlive) { e.ApplySlow(3f, 0.5f); hit++; }
            }
            Debug.Log($"[DebugConsole] Slowed {hit} live enemies 50% for 3 s (R18).");
        }

        /// <summary>
        /// R20 test: cycle a forced mutator set, OR-ed into every wave started
        /// after the press (already-spawned units keep their stamps).
        /// </summary>
        private void CycleForcedMutators()
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogWarning("[DebugConsole] No WaveManager to force mutators on.");
                return;
            }

            var cycle = new[]
            {
                Corehold.Data.WaveMutator.None,
                Corehold.Data.WaveMutator.Storm,
                Corehold.Data.WaveMutator.Convoy,
                Corehold.Data.WaveMutator.Overcharge,
                Corehold.Data.WaveMutator.Blackout,
                Corehold.Data.WaveMutator.Storm | Corehold.Data.WaveMutator.Convoy |
                Corehold.Data.WaveMutator.Overcharge | Corehold.Data.WaveMutator.Blackout,
            };

            int at = System.Array.IndexOf(cycle, wm.DebugForceMutators);
            wm.DebugForceMutators = cycle[(at + 1) % cycle.Length];
            Debug.Log($"[DebugConsole] Forced wave mutators: {wm.DebugForceMutators} " +
                      "(applies to waves started from now; T again to cycle).");
        }

        private void SetDifficulty(Difficulty d)
        {
            var gm = GameManager.Instance;
            if (gm != null)
                gm.Difficulty = d;
            Debug.Log($"[DebugConsole] Difficulty set to {d}.");
        }

        private int LiveEnemyCount()
        {
            var enemies = FindObjectsByType<Enemy>(FindObjectsSortMode.None);
            int count = 0;
            foreach (var e in enemies)
            {
                if (e != null && e.IsAlive)
                    count++;
            }
            return count;
        }

        // ----- Overlay -----

        private void OnGUI()
        {
            if (!_showOverlay)
                return;

            var gm = GameManager.Instance;
            var wm = FindFirstObjectByType<WaveManager>();

            int liveEnemies = LiveEnemyCount();
            int wave = wm != null ? wm.NextWaveIndex : (gm != null ? gm.WaveIndex : 0);
            int salvage = gm != null ? gm.Salvage : 0;
            int integrity = gm != null ? gm.Integrity : 0;

            // Draw call count if reachable.
            long drawCalls = -1;
#if UNITY_EDITOR
            drawCalls = UnityEditor.UnityStats.drawCalls;
#endif

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                padding = new RectOffset(10, 10, 10, 10)
            };
            style.normal.textColor = Color.white;

            string text =
                "COREHOLD DEBUG\n" +
                $"Live enemies : {liveEnemies}\n" +
                $"Wave         : {wave}\n" +
                $"Salvage      : {salvage}\n" +
                $"Integrity    : {integrity}\n" +
                $"Frame time   : {_frameMs:0.0} ms ({(_frameMs > 0f ? (1000f / _frameMs) : 0f):0} fps)\n" +
                $"Draw calls   : {(drawCalls >= 0 ? drawCalls.ToString() : "n/a")}";

            GUI.Box(new Rect(10, 10, 260, 160), text, style);
        }
    }
}
#endif
