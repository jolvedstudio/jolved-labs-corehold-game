using System.Text;
using UnityEngine;
using Corehold.Enemies;
using Corehold.Towers;

public static class HealthBugProbe
{
    public static string Execute()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Enemies live={Enemy.Live.Count}");
        foreach (var e in Enemy.Live)
        {
            if (e == null) continue;
            float frac = e.MaxHealth > 0f ? e.CurrentHealth / e.MaxHealth : 0f;
            sb.AppendLine($"  {e.name}: hp={e.CurrentHealth:0.##}/{e.MaxHealth:0.##} frac={frac:0.###} alive={e.IsAlive}");
        }
        sb.AppendLine($"Towers live={Tower.Live.Count}");
        foreach (var t in Tower.Live)
        {
            if (t == null) continue;
            var h = t.GetComponent<TowerHealth>();
            sb.AppendLine($"  {t.name}: hp={(h != null ? $"{h.CurrentHealth:0.##}/{h.MaxHealth:0.##} frac={h.HealthFraction:0.###}" : "none")}");
        }
        return sb.ToString();
    }
}
