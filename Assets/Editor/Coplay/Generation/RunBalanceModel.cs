using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor
{
    /// <summary>
    /// Run the balance model against the OPEN scene, as Gate 3 does during
    /// generation (roadmap R30).
    ///
    /// Generated levels are model-certified when they are made; a level that is
    /// then hand-edited — a forked level from Clone Level, a moved pad, a
    /// retuned wave table, an edited hpGrowthPerWave — carries a certificate
    /// for geometry and rules that no longer exist. This is how you re-earn it
    /// without regenerating (which would discard the hand edits).
    ///
    /// Reads the scene's real geometry (built routes, spawners, core) and its
    /// wired LevelDefinition's rules, exactly like the pipeline's gate — no
    /// second source of truth.
    /// </summary>
    public static class RunBalanceModel
    {
        [MenuItem("Tools/COREHOLD/Validate/Run Balance Model (open scene)", false, 1)]
        public static void Run()
        {
            var routes = Object.FindObjectsByType<PathRoute>(FindObjectsSortMode.None).ToList();
            if (routes.Count == 0)
            {
                Report("No PathRoute in the open scene — this checks playable LEVELS.");
                return;
            }

            var spawners = Object.FindObjectsByType<Spawner>(FindObjectsSortMode.None);
            var withCore = spawners.FirstOrDefault(s => s.CoreTarget != null);
            if (withCore == null)
            {
                Report("No spawner with a wired CoreTarget — the model needs the core position.");
                return;
            }

            var wm = Object.FindFirstObjectByType<WaveManager>();
            LevelDefinition def = wm != null
                ? new SerializedObject(wm).FindProperty("level").objectReferenceValue as LevelDefinition
                : null;
            if (def == null)
            {
                Report("No WaveManager with a wired LevelDefinition — the model needs the level's rules " +
                       "(hpGrowthPerWave, maxLiveEnemies).");
                return;
            }

            var air = spawners.FirstOrDefault(s => s.name.Contains("Air"));
            Vector3 airSpawn = air != null ? air.transform.position : withCore.transform.position;

            var result = BalanceModelRunner.Run(routes, airSpawn, withCore.CoreTarget.position,
                                                solveGrowth: false,
                                                hpGrowth: def.hpGrowthPerWave,
                                                maxLive: def.maxLiveEnemies,
                                                out string error);
            if (result == null)
            {
                Report($"The model did not run — {error}");
                return;
            }

            var sb = new StringBuilder();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            sb.AppendLine($"[BalanceModel] {scene.name}  (rules: {def.name})");
            sb.AppendLine($"  difficulty {result.difficulty}, hpGrowth {result.hp_growth_used:0.###}, " +
                          $"maxLive {result.max_live}, routes {routes.Count}");
            sb.AppendLine(result.in_band
                ? "  IN BAND — every wave's margin sits inside the shipped envelope."
                : "  OUT OF BAND — the waves flagged below fall outside the shipped envelope.");

            if (result.rows != null)
                foreach (var r in result.rows)
                {
                    string flags = r.flags != null && r.flags.Length > 0 ? string.Join(",", r.flags) : "-";
                    sb.AppendLine($"    w{r.wave,-2} margin {r.margin,5:0.00}  worst {r.worst_group,-18} {flags}");
                }

            if (!result.in_band)
                sb.AppendLine("  Fix by hand (pads, wave counts, hpGrowthPerWave on the LevelDefinition) and " +
                              "re-run — or, for an unedited generated level, regenerate it, which solves " +
                              "growth automatically.");

            Debug.Log(sb.ToString());
            EditorUtility.DisplayDialog("Balance Model",
                (result.in_band ? "IN BAND" : "OUT OF BAND") +
                $"\n\n{scene.name} at hpGrowth {result.hp_growth_used:0.###}, maxLive {result.max_live}." +
                "\n\nPer-wave margins are in the Console.", "OK");
        }

        private static void Report(string message)
        {
            Debug.LogWarning("[BalanceModel] " + message);
            EditorUtility.DisplayDialog("Balance Model", message, "OK");
        }
    }
}
