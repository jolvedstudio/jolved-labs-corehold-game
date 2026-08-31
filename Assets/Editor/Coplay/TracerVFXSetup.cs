using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Corehold.Systems;

/// <summary>
/// One-shot editor setup for the reworked hitscan tracer (core+halo) approach.
///
///  • Authors the two tracer materials as ASSETS from the Corehold/VFXTracer
///    shader (additive core + alpha-blend halo) so their blend state is reliable
///    and no longer depends on runtime SetFloat calls.
///  • Wires them onto the scene's _Directors/VFXDirector.
///  • Reports every Volume/profile in the open scene with its current Tonemapping
///    mode and Bloom threshold/intensity so we tune the RIGHT profile.
/// </summary>
public static class TracerVFXSetup
{
    private const string MatDir = "Assets/_COREHOLD/VFX/Materials";
    private const string CorePath = MatDir + "/VFX_Tracer_Core_Additive.mat";
    private const string HaloPath = MatDir + "/VFX_Tracer_Halo_AlphaBlend.mat";

    public static string Execute()
    {
        var sb = new StringBuilder();

        Shader shader = Shader.Find("Corehold/VFXTracer");
        if (shader == null)
            return "ERROR: Corehold/VFXTracer shader not found (compile/import may still be pending).";

        if (!AssetDatabase.IsValidFolder(MatDir))
        {
            Directory_CreateRecursive(MatDir);
        }

        // --- Core (additive) ---
        Material core = AssetDatabase.LoadAssetAtPath<Material>(CorePath);
        if (core == null)
        {
            core = new Material(shader) { name = "VFX_Tracer_Core_Additive" };
            AssetDatabase.CreateAsset(core, CorePath);
        }
        core.shader = shader;
        core.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        core.SetFloat("_DstBlend", (float)BlendMode.One); // additive
        core.SetColor("_Color", Color.white);
        core.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(core);

        // --- Halo (alpha-blend, hue preserving) ---
        Material halo = AssetDatabase.LoadAssetAtPath<Material>(HaloPath);
        if (halo == null)
        {
            halo = new Material(shader) { name = "VFX_Tracer_Halo_AlphaBlend" };
            AssetDatabase.CreateAsset(halo, HaloPath);
        }
        halo.shader = shader;
        halo.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        halo.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); // alpha blend
        halo.SetColor("_Color", Color.white);
        halo.renderQueue = (int)RenderQueue.Transparent - 1; // just under the core
        EditorUtility.SetDirty(halo);

        AssetDatabase.SaveAssets();
        sb.AppendLine($"Materials authored: {CorePath}, {HaloPath}");

        // --- Wire into the scene's VFXDirector ---
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director != null)
        {
            var so = new SerializedObject(director);
            var coreProp = so.FindProperty("tracerCoreMaterial");
            var haloProp = so.FindProperty("tracerHaloMaterial");
            if (coreProp != null) coreProp.objectReferenceValue = core;
            if (haloProp != null) haloProp.objectReferenceValue = halo;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(director);
            sb.AppendLine($"Wired materials onto VFXDirector '{director.name}'.");
        }
        else
        {
            sb.AppendLine("WARNING: no VFXDirector found in the open scene to wire.");
        }

        // --- Report volumes/profiles so we tune the correct one ---
        sb.AppendLine("\n--- Volumes in open scene ---");
        foreach (var vol in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
        {
            var profile = vol.sharedProfile;
            sb.AppendLine($"Volume '{vol.name}' global={vol.isGlobal} priority={vol.priority} profile={(profile != null ? profile.name : "null")}");
            if (profile == null) continue;
            if (profile.TryGet(out Tonemapping tm))
                sb.AppendLine($"   Tonemapping active={tm.active} mode={tm.mode.value} (override={tm.mode.overrideState})");
            else
                sb.AppendLine("   Tonemapping: (none)");
            if (profile.TryGet(out Bloom bloom))
                sb.AppendLine($"   Bloom active={bloom.active} threshold={bloom.threshold.value} intensity={bloom.intensity.value} scatter={bloom.scatter.value}");
            else
                sb.AppendLine("   Bloom: (none)");
        }

        return sb.ToString();
    }

    private static void Directory_CreateRecursive(string path)
    {
        string[] parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}
