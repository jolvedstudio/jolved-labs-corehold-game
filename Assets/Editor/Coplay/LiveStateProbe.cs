using System.Text;
using UnityEngine;
using Corehold.Towers;
using Corehold.Enemies;
using Corehold.UI;

public static class LiveStateProbe
{
    public static string Execute()
    {
        var sb = new StringBuilder();
        int barsEnemies = 0, barsTowers = 0;
        foreach (var hb in Object.FindObjectsByType<WorldHealthBar>(FindObjectsSortMode.None))
        {
            if (hb.GetComponent<Tower>() != null) barsTowers++;
            else barsEnemies++;
        }
        sb.AppendLine($"WorldHealthBars: towers={barsTowers} enemies={barsEnemies}");
        sb.AppendLine($"Enemies live={Enemy.Live.Count}  Towers live={Tower.Live.Count}");
        foreach (var t in Tower.Live)
        {
            if (t == null) continue;
            var h = t.GetComponent<TowerHealth>();
            var tgt = t.GetComponent<TowerTargeting>()?.CurrentTarget;
            sb.AppendLine($"  {t.name}: hp={(h!=null?$"{h.CurrentHealth:0}/{h.MaxHealth:0}":"none")} target={(tgt!=null?tgt.name:"idle-scan")}");
        }
        return sb.ToString();
    }
}
