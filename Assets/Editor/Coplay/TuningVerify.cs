using System.Reflection;
using System.Text;
using UnityEngine;
using Corehold.Core;
using Corehold.Towers;
using Corehold.Enemies;
using Corehold.UI;

public static class TuningVerify
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
            if (built >= 4) break;
            if (!pad.IsOccupied && pad.TryBuild(autocannon))
            {
                built++;
                var hb = pad.Occupant.GetComponentInChildren<WorldHealthBar>();
                var recoil = pad.Occupant.GetComponentInChildren<TurretBarrelSpin>();
                sb.AppendLine($"Built on {pad.name}: WorldHealthBar={(hb!=null)} Recoil={(recoil!=null)}");
            }
        }

        // Advance to wave 5 (drones) so we can see air + return fire.
        var wm = Object.FindFirstObjectByType<WaveManager>();
        var field = wm.GetType().GetField("_nextWaveIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) field.SetValue(wm, 4);
        wm.StartNextWave();

        sb.AppendLine($"Started Wave_05 (drones). State={gm.State}");
        return sb.ToString();
    }
}
