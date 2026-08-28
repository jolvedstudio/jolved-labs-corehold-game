using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Report-only WebGL/URP shader-compatibility audit for the effect surface
/// (the "why is this effect pink or invisible in the browser but fine in the
/// editor" class of bug). Walks every material reachable from the VFX
/// director config, the weather presets, and everything under _COREHOLD
/// (vendored copies included), resolves each material's shader, and flags:
///
///   • MISSING shader — renders magenta or not at all;
///   • legacy BUILT-IN shaders (unity_builtin_extra) — magenta under URP,
///     except the SRP-compatible Sprites/UI families;
///   • custom CGPROGRAM shaders with no URP pass — magenta in URP builds;
///   • (documented, not detectable statically) shaders used only through
///     Shader.Find: nothing serialized references them, so builds STRIP
///     them and the editor lies. Keep such shaders in a Resources folder —
///     see ShieldShell.MaterialFor for the incident writeup.
///
/// The editor compiles shader variants on demand; a build strips. That is
/// why every finding here is worth reading even when the editor looks right.
/// </summary>
public static class WebGLShaderAudit
{
    [MenuItem("Tools/COREHOLD/VFX/WebGL Shader Audit", false, 63)]
    public static void Run()
    {
        // ---- gather the material set: config + weather + all of _COREHOLD ----
        var roots = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:VFXDirectorConfig"))
            roots.Add(AssetDatabase.GUIDToAssetPath(guid));
        foreach (string guid in AssetDatabase.FindAssets("t:WeatherPreset"))
            roots.Add(AssetDatabase.GUIDToAssetPath(guid));

        var materialPaths = new HashSet<string>(
            roots.Count > 0
                ? AssetDatabase.GetDependencies(roots.ToArray(), true)
                    .Where(p => p.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                : System.Linq.Enumerable.Empty<string>());
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/_COREHOLD" }))
            materialPaths.Add(AssetDatabase.GUIDToAssetPath(guid));

        // ---- classify each material's shader --------------------------------
        var errors = new List<string>();
        var warns = new List<string>();
        int ok = 0;
        var shaderVerdicts = new Dictionary<Shader, string>();

        foreach (string path in materialPaths.OrderBy(p => p, System.StringComparer.Ordinal))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                continue;
            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
            {
                errors.Add($"{Path.GetFileName(path)}: shader MISSING — magenta everywhere.  ({path})");
                continue;
            }
            string verdict = VerdictFor(mat.shader, shaderVerdicts);
            if (verdict == null) { ok++; continue; }
            if (verdict.StartsWith("ERROR"))
                errors.Add($"{Path.GetFileName(path)} → '{mat.shader.name}': {verdict.Substring(6)}  ({path})");
            else
                warns.Add($"{Path.GetFileName(path)} → '{mat.shader.name}': {verdict.Substring(5)}  ({path})");
        }

        // ---- report ---------------------------------------------------------
        var sb = new StringBuilder();
        sb.AppendLine($"=== WebGL SHADER AUDIT — {materialPaths.Count} material(s) checked ===");
        sb.AppendLine($"  OK {ok},  warnings {warns.Count},  errors {errors.Count}");
        foreach (string e in errors) sb.AppendLine("  ERROR  " + e);
        foreach (string w in warns) sb.AppendLine("  warn   " + w);
        sb.AppendLine("  Reminder: shaders used only via Shader.Find are invisible to this audit AND to the");
        sb.AppendLine("  build's dependency walk — they must live under a Resources/ folder to ship.");
        if (errors.Count > 0)
            Debug.LogError(sb.ToString());
        else
            Debug.Log(sb.ToString());
    }

    /// <summary>null = fine; "ERROR ..." / "warn ..." otherwise. Cached per shader.</summary>
    private static string VerdictFor(Shader shader, Dictionary<Shader, string> cache)
    {
        if (cache.TryGetValue(shader, out string cached))
            return cached;

        string verdict = null;
        string path = AssetDatabase.GetAssetPath(shader);
        string name = shader.name;

        if (name.StartsWith("TextMeshPro/"))
        {
            verdict = null;   // TMP's CG shaders are SRP-agnostic — URP renders them
        }
        else if (string.IsNullOrEmpty(path) || path == "Resources/unity_builtin_extra" || path == "Library/unity default resources")
        {
            // Built-in shaders: the sprite/UI families and the skybox family
            // are SRP-compatible (URP renders skyboxes with the built-ins).
            if (!(name.StartsWith("Sprites/") || name.StartsWith("UI/") || name.StartsWith("Skybox/")))
                verdict = "ERROR legacy built-in shader — renders MAGENTA under URP (editor included; " +
                          "swap the material to a Universal Render Pipeline/Particles shader).";
        }
        else if (path.StartsWith("Packages/", System.StringComparison.Ordinal))
        {
            verdict = null;   // URP/TMP package shaders — the target's own content
        }
        else if (path.EndsWith(".shadergraph", System.StringComparison.OrdinalIgnoreCase))
        {
            verdict = null;   // authored against the active pipeline
        }
        else if (path.EndsWith(".shader", System.StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            string src = File.ReadAllText(path);
            bool urpTagged = src.Contains("UniversalPipeline") || src.Contains("CFXR_URP");
            bool hlsl = src.Contains("HLSLPROGRAM");
            bool cg = src.Contains("CGPROGRAM");
            if (!urpTagged && !hlsl && cg)
                verdict = "warn built-in-pipeline CG shader with no URP pass — verify it renders in a " +
                          "URP BUILD (the editor can mask it); if it shows magenta, swap the material " +
                          "to a URP particle shader.";
            if (src.Contains("GrabPass"))
                verdict = "ERROR uses GrabPass — unsupported on URP/WebGL.";
        }

        cache[shader] = verdict;
        return verdict;
    }
}
