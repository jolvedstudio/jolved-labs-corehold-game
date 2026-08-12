using System.Reflection;
using System.Text;
using UnityEngine;
using Corehold.Core;
using Corehold.Towers;
using Corehold.Enemies;
using Corehold.UI;

/// <summary>
/// Build turrets, start a ground wave, teleport a couple of enemies right next to a
/// turret (to prove separation keep-out + bars + tracers), and frame the SceneView
/// camera on that turret. Run in play mode.
/// </summary>
public static class SpawnAndPose
{
    public static string Execute()
    {
        var sb = new StringBuilder();
        if (!Application.isPlaying) return "NOT PLAYING";

        var gm = GameManager.Instance;
        gm.ConfigureRun(Difficulty.Normal);
        gm.SetState(GameState.Build);

        var theme = Object.FindFirstObjectByType<UITheme>();
        Corehold.Data.TowerDefinition autocannon = theme.turrets[0];
        foreach (var t in theme.turrets) if (t != null && t.id == "autocannon") autocannon = t;

        Tower firstTower = null;
        foreach (var pad in Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            if (!pad.IsOccupied && pad.TryBuild(autocannon) && firstTower == null)
                firstTower = pad.Occupant;

        var wm = Object.FindFirstObjectByType<WaveManager>();
        var field = wm.GetType().GetField("_nextWaveIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) field.SetValue(wm, 0);
        wm.StartNextWave();

        sb.AppendLine(firstTower != null ? $"First turret at {firstTower.transform.position}" : "no turret");
        return sb.ToString();
    }
}
