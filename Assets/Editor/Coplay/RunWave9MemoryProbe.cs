using Corehold.Systems;
using UnityEditor;
using UnityEngine;

public static class RunWave9MemoryProbe
{
    public static string Execute()
    {
        // Ensure a probe object exists in the open scene.
        var existing = Object.FindFirstObjectByType<Wave9MemoryProbe>();
        if (existing == null)
        {
            var go = new GameObject("Wave9MemoryProbe");
            go.AddComponent<Wave9MemoryProbe>();
            Debug.Log("[RunWave9MemoryProbe] Added Wave9MemoryProbe to scene.");
        }
        else
        {
            Debug.Log("[RunWave9MemoryProbe] Wave9MemoryProbe already present.");
        }

        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
            return "Entering play mode. Wait ~15s then read logs for the TICKET 38 report.";
        }

        return "Already in play mode.";
    }

    public static string Report()
    {
        return string.IsNullOrEmpty(Wave9MemoryProbe.LastReport)
            ? "No report yet — probe still settling."
            : Wave9MemoryProbe.LastReport;
    }

    public static string Stop()
    {
        EditorApplication.isPlaying = false;
        return "Exiting play mode.";
    }
}
