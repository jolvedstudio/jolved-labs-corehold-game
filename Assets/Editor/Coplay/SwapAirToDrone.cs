using System.IO;
using System.Text;
using UnityEditor;

/// <summary>
/// Replaces the Wasp air enemy with the new Drone in every wave's air group
/// (item g). Air enemies become drones; ground waves are untouched.
/// </summary>
public static class SwapAirToDrone
{
    private const string WaspGuid = "4c6008c65aacdf54da499676a421088d";
    private const string DroneGuid = "ddd6ac25d0683a64b9443acde334269e";

    private static readonly string[] Waves =
    {
        "Assets/_COREHOLD/Data/Waves/Wave_03.asset",
        "Assets/_COREHOLD/Data/Waves/Wave_05.asset",
        "Assets/_COREHOLD/Data/Waves/Wave_06.asset",
        "Assets/_COREHOLD/Data/Waves/Wave_08.asset",
        "Assets/_COREHOLD/Data/Waves/Wave_09.asset",
        "Assets/_COREHOLD/Data/Waves/Wave_10.asset",
    };

    public static string Execute()
    {
        var sb = new StringBuilder();
        foreach (var path in Waves)
        {
            if (!File.Exists(path)) { sb.AppendLine($"MISS {path}"); continue; }
            string text = File.ReadAllText(path);
            if (text.Contains(WaspGuid))
            {
                text = text.Replace(WaspGuid, DroneGuid);
                File.WriteAllText(path, text);
                sb.AppendLine($"OK   {path}");
            }
            else
            {
                sb.AppendLine($"skip {path} (no wasp guid)");
            }
        }
        AssetDatabase.Refresh();
        return sb.ToString();
    }
}
