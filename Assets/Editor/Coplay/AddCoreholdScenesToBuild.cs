using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class AddCoreholdScenesToBuild
{
    public static string Execute()
    {
        string[] wanted =
        {
            "Assets/_COREHOLD/Scenes/Boot.unity",
            "Assets/_COREHOLD/Scenes/Game.unity",
        };

        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        var existing = new HashSet<string>(scenes.Select(s => s.path));
        int added = 0;

        foreach (var path in wanted)
        {
            if (existing.Contains(path))
                continue;
            scenes.Add(new EditorBuildSettingsScene(path, true));
            existing.Add(path);
            added++;
        }

        EditorBuildSettings.scenes = scenes.ToArray();

        var summary = string.Join("\n", EditorBuildSettings.scenes.Select((s, i) => $"  [{i}] {(s.enabled ? "on " : "off")} {s.path}"));
        return $"Added {added} scene(s) to build settings. Current build scenes:\n{summary}";
    }
}
