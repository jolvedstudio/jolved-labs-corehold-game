using System.Text;
using UnityEditor;
using UnityEngine;
using Corehold.Systems;

/// <summary>
/// One-shot sync: snapshot the active scene's tuned VFXDirector into the shared
/// VFXDirectorConfig asset so the config faithfully mirrors the live component —
/// including the hitscan tracer's Glow and Material fields.
/// </summary>
public static class ConfigureHitscanTracer
{
    public static string Execute()
    {
        var director = Object.FindFirstObjectByType<VFXDirector>(FindObjectsInactive.Include);
        if (director == null)
            return "ERROR: No VFXDirector found in the active scene.";

        var log = new StringBuilder();
        VFXDirectorConfig config = VFXConfigIO.WriteFromDirector(director, log);
        if (config == null)
            return "ERROR: Failed to write VFXDirectorConfig from the director.";

        AssetDatabase.SaveAssets();

        // Read back what landed in the asset so the result is verifiable.
        var so = new SerializedObject(director);
        string dirMat = so.FindProperty("tracerMaterial").objectReferenceValue is Material dm ? dm.name : "(null - runtime built)";
        float dirGlow = so.FindProperty("tracerGlow").floatValue;
        float dirWidth = so.FindProperty("tracerWidth").floatValue;

        string cfgMat = config.tracerMaterial != null ? config.tracerMaterial.name : "(null - runtime built)";

        return "VFXDirectorConfig synced from the live VFXDirector.\n" +
               $"  Director  -> material={dirMat}, glow={dirGlow}, width={dirWidth}\n" +
               $"  Config    -> material={cfgMat}, glow={config.tracerGlow}, width={config.tracerWidth}\n" +
               $"  Asset: {VFXConfigIO.ConfigPath}\n{log}";
    }
}
