using System.Collections;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Core;
using Corehold.Enemies;

namespace CoreholdEditor
{
    /// <summary>
    /// Play-mode diagnostic: enters play, starts wave 1, samples enemy positions
    /// twice ~1s apart, and reports whether they moved, their routes' point counts,
    /// and whether any turret exists. Runs entirely from the editor coroutine.
    /// </summary>
    public static class RuntimeDiag
    {
        public static string Run()
        {
            if (!Application.isPlaying)
            {
                EditorApplication.EnterPlaymode();
                return "Entering play mode — run again once playing.";
            }

            var sb = new StringBuilder();
            var wm = Object.FindFirstObjectByType<WaveManager>();
            sb.AppendLine($"WaveManager: {(wm != null ? "found" : "NULL")}  WaveCount={wm?.WaveCount}");

            // Report routes.
            foreach (var r in Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None))
                sb.AppendLine($"Route '{r.name}': points={r.PointCount}, length={r.Length:0.0}m, p0={r.GetPoint(0)}");

            // Start a wave if none in progress.
            if (wm != null && !wm.WaveInProgress && wm.HasNextWave)
            {
                wm.StartNextWave();
                sb.AppendLine("Started next wave.");
            }
            else sb.AppendLine($"Wave already in progress={wm?.WaveInProgress}, hasNext={wm?.HasNextWave}");

            // Sample enemies right now.
            var enemies = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
            sb.AppendLine($"Enemies live now: {enemies.Length}");
            foreach (var e in enemies.Take(6))
            {
                var mv = e.GetComponent<EnemyMover>();
                sb.AppendLine($"  {e.name} pos={e.transform.position} isAir={mv?.IsAir} baseSpd={mv?.BaseSpeed} frac={mv?.PathFraction:0.00}");
            }

            var towers = Object.FindObjectsByType<Corehold.Towers.Tower>(FindObjectsSortMode.None);
            sb.AppendLine($"Towers in scene: {towers.Length}");

            Debug.Log("[COREHOLD DIAG] " + sb);
            return sb.ToString();
        }
    }
}
