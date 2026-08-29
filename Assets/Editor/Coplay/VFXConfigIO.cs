using System.Text;
using Corehold.Systems;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared read/write bridge between a live <see cref="VFXDirector"/> component and
/// the persistent <see cref="VFXDirectorConfig"/> asset.
///
///   • <see cref="WriteFromDirector"/> — snapshot a (tuned) scene director INTO the
///     config asset. Called by the testbed's Apply button so a human's tuning
///     becomes the shared source of truth.
///   • <see cref="ApplyToDirector"/> — push the config asset ONTO a director. Called
///     by <c>SetupVFXDirector</c> during level generation so every new scene gets
///     the tuned wiring.
///
/// Both operate through SerializedObject so they touch the exact serialized fields
/// (effects[], tracerWidth, tracerPrewarm, defaultTracerColor) the director defines.
/// </summary>
public static class VFXConfigIO
{
    public const string ConfigPath = "Assets/_COREHOLD/Data/VFXDirectorConfig.asset";

    /// <summary>Load the shared config asset, or null if it does not exist.</summary>
    public static VFXDirectorConfig Load()
        => AssetDatabase.LoadAssetAtPath<VFXDirectorConfig>(ConfigPath);

    /// <summary>Load the config asset, creating an empty one if absent.</summary>
    public static VFXDirectorConfig LoadOrCreate()
    {
        var config = Load();
        if (config != null)
            return config;

        config = ScriptableObject.CreateInstance<VFXDirectorConfig>();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath));
        AssetDatabase.CreateAsset(config, ConfigPath);
        AssetDatabase.SaveAssets();
        return config;
    }

    /// <summary>
    /// Snapshot the director's serialized wiring into the config asset (creating it
    /// if needed). Returns the config, or null on failure.
    /// </summary>
    public static VFXDirectorConfig WriteFromDirector(VFXDirector director, StringBuilder log = null)
    {
        if (director == null)
            return null;

        var config = LoadOrCreate();
        var so = new SerializedObject(director);

        SerializedProperty effects = so.FindProperty("effects");
        var entries = new VFXDirectorConfig.Entry[effects != null ? effects.arraySize : 0];
        for (int i = 0; i < entries.Length; i++)
        {
            var el = effects.GetArrayElementAtIndex(i);
            entries[i] = new VFXDirectorConfig.Entry
            {
                id = (VFXDirector.Effect)el.FindPropertyRelative("id").enumValueIndex,
                prefab = el.FindPropertyRelative("prefab").objectReferenceValue as GameObject,
                prewarm = el.FindPropertyRelative("prewarm").intValue,
            };
        }

        config.effects = entries;
        config.tracerCoreMaterial = ObjectOf(so, "tracerCoreMaterial", config.tracerCoreMaterial) as Material;
        config.tracerHaloMaterial = ObjectOf(so, "tracerHaloMaterial", config.tracerHaloMaterial) as Material;
        config.tracerHaloWidthScale = FloatOf(so, "tracerHaloWidthScale", config.tracerHaloWidthScale);
        config.tracerCoreGlow = FloatOf(so, "tracerCoreGlow", config.tracerCoreGlow);
        config.tracerPrewarm = IntOf(so, "tracerPrewarm", config.tracerPrewarm);
        config.friendlyTracerWidth = FloatOf(so, "friendlyTracerWidth", config.friendlyTracerWidth);
        config.friendlyTracerGlow = FloatOf(so, "friendlyTracerGlow", config.friendlyTracerGlow);
        config.friendlyTracerColor = ColorOf(so, "friendlyTracerColor", config.friendlyTracerColor);
        config.hostileTracerWidth = FloatOf(so, "hostileTracerWidth", config.hostileTracerWidth);
        config.hostileTracerGlow = FloatOf(so, "hostileTracerGlow", config.hostileTracerGlow);
        config.hostileTracerColor = ColorOf(so, "hostileTracerColor", config.hostileTracerColor);

        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();

        log?.AppendLine($"• VFX config written: {entries.Length} effect slot(s), " +
                        $"friendly {config.friendlyTracerColor} / hostile {config.hostileTracerColor}.");
        return config;
    }

    /// <summary>
    /// Push the config asset's wiring onto a director via SerializedObject. Returns
    /// true when applied. Missing effect prefabs are reported through
    /// <paramref name="missing"/> but do not abort the write.
    /// </summary>
    public static bool ApplyToDirector(VFXDirector director, VFXDirectorConfig config,
        System.Collections.Generic.List<string> missing = null)
    {
        if (director == null || config == null || config.effects == null)
            return false;

        var so = new SerializedObject(director);
        SerializedProperty effects = so.FindProperty("effects");
        effects.arraySize = config.effects.Length;

        for (int i = 0; i < config.effects.Length; i++)
        {
            var entry = config.effects[i];
            var el = effects.GetArrayElementAtIndex(i);
            el.FindPropertyRelative("id").enumValueIndex = (int)entry.id;
            el.FindPropertyRelative("prefab").objectReferenceValue = entry.prefab;
            el.FindPropertyRelative("prewarm").intValue = entry.prewarm;
            if (entry.prefab == null)
                missing?.Add(entry.id.ToString());
        }

        SetObject(so, "tracerCoreMaterial", config.tracerCoreMaterial);
        SetObject(so, "tracerHaloMaterial", config.tracerHaloMaterial);
        SetFloat(so, "tracerHaloWidthScale", config.tracerHaloWidthScale);
        SetFloat(so, "tracerCoreGlow", config.tracerCoreGlow);
        SetInt(so, "tracerPrewarm", config.tracerPrewarm);
        SetFloat(so, "friendlyTracerWidth", config.friendlyTracerWidth);
        SetFloat(so, "friendlyTracerGlow", config.friendlyTracerGlow);
        SetColor(so, "friendlyTracerColor", config.friendlyTracerColor);
        SetFloat(so, "hostileTracerWidth", config.hostileTracerWidth);
        SetFloat(so, "hostileTracerGlow", config.hostileTracerGlow);
        SetColor(so, "hostileTracerColor", config.hostileTracerColor);

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(director);
        return true;
    }

    // ------------------------------------------------------------ helpers
    private static float FloatOf(SerializedObject so, string f, float fallback)
    { var p = so.FindProperty(f); return p != null ? p.floatValue : fallback; }
    private static int IntOf(SerializedObject so, string f, int fallback)
    { var p = so.FindProperty(f); return p != null ? p.intValue : fallback; }
    private static Color ColorOf(SerializedObject so, string f, Color fallback)
    { var p = so.FindProperty(f); return p != null ? p.colorValue : fallback; }
    private static UnityEngine.Object ObjectOf(SerializedObject so, string f, UnityEngine.Object fallback)
    { var p = so.FindProperty(f); return p != null ? p.objectReferenceValue : fallback; }

    private static void SetFloat(SerializedObject so, string f, float v)
    { var p = so.FindProperty(f); if (p != null) p.floatValue = v; }
    private static void SetInt(SerializedObject so, string f, int v)
    { var p = so.FindProperty(f); if (p != null) p.intValue = v; }
    private static void SetColor(SerializedObject so, string f, Color v)
    { var p = so.FindProperty(f); if (p != null) p.colorValue = v; }
    private static void SetObject(SerializedObject so, string f, UnityEngine.Object v)
    { var p = so.FindProperty(f); if (p != null) p.objectReferenceValue = v; }
}
