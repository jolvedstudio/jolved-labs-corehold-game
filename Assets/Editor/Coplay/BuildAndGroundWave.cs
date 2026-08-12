using System.Reflection;
using System.Text;
using UnityEngine;
using Corehold.Core;
using Corehold.Towers;
using Corehold.UI;

public static class BuildAndGroundWave
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

        int built = 0;
        foreach (var pad in Object.FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
        {
            if (!pad.IsOccupied && pad.TryBuild(autocannon)) built++;
        }

        // Wave 1 (ground swarm) — many close-packed ground enemies to see separation.
        var wm = Object.FindFirstObjectByType<WaveManager>();
        var field = wm.GetType().GetField("_nextWaveIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) field.SetValue(wm, 0);
        wm.StartNextWave();

        sb.AppendLine($"Built {built} turrets (HP now 220). Started Wave_01 (ground). State={gm.State}");
        return sb.ToString();
    }
}
