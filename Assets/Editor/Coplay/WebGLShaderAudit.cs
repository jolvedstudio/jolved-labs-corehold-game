using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
///   • shaders that sample a SCREEN TEXTURE the shipping quality tier does
///     not render (_CameraOpaqueTexture / _CameraDepthTexture). This is the
///     general form of the soft-particle bug below, and it is the single
///     likeliest way a bought URP asset dies in the browser: refraction and
///     depth-fade water, soft foam, scanner effects and heat haze all read
///     one of those two textures, and the Mobile tier renders neither;
///   • shaders whose pipeline stage does not exist on WebGL — geometry
///     shaders and tessellation are not in GLES 3.0 at all, and stylized
///     grass/fur packs reach for both;
///   • compute shaders reachable from shipped content — same reason the VFX
///     Graph had to go;
///   • (documented, not detectable statically) shaders used only through
///     Shader.Find: nothing serialized references them, so builds STRIP
///     them and the editor lies. Keep such shaders in a Resources folder —
///     see ShieldShell.MaterialFor for the incident writeup.
///
/// The last four exist because vendor packs arrive as a pile of shaders that
/// all look perfect in the editor — which runs the PC tier — and this project
/// has already lost days to exactly that asymmetry once.
///
/// The editor compiles shader variants on demand; a build strips. That is
/// why every finding here is worth reading even when the editor looks right.
/// </summary>
public static class WebGLShaderAudit
{
    [MenuItem("Tools/COREHOLD/VFX/WebGL Shader Audit", false, 63)]
    public static void Run()
    {
        string report = Report(out int errs, out _);
        if (errs > 0) Debug.LogError(report); else Debug.Log(report);
    }

    /// <summary>
    /// The audit as DATA, so callers other than the menu can gate on it — the
    /// campaign preflight runs it before declaring a campaign shippable. A check
    /// nobody is obliged to run only protects the person who remembers it.
    /// </summary>
    public static string Report(out int errorCount, out int warningCount)
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
        // EVERY material in the project, not just _COREHOLD's.
        //
        // Scoping this to our own folder is how the audit returned a clean bill
        // of health on a build full of magenta: vendor packs live OUTSIDE
        // _COREHOLD (Vendor/, and whatever a kit unpacks itself into), their
        // materials were never scanned, and those are exactly the materials
        // most likely to carry a shader the target cannot run. An audit that
        // cannot see the risky half is worse than no audit — it is a false
        // all-clear, and it costs a build to discover.
        foreach (string guid in AssetDatabase.FindAssets("t:Material"))
        {
            string mp = AssetDatabase.GUIDToAssetPath(guid);
            // Package materials ship with the engine and are not ours to judge.
            if (mp.StartsWith("Packages/", System.StringComparison.Ordinal))
                continue;
            materialPaths.Add(mp);
        }

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

        // ---- Soft particles sweep --------------------------------------------
        // URP fades a soft particle against the DEPTH TEXTURE. The quality tier a
        // WebGL build runs (Mobile: RequireDepthTexture = 0) has none, so the fade
        // term has nothing to sample and the particle renders fully transparent —
        // INVISIBLE in the build while perfect in the editor, which runs the PC
        // tier. That asymmetry cost this project a long hunt: every effect except
        // the portal (no soft particles) and the tracers (own shader) vanished.
        foreach (string path in materialPaths.OrderBy(p => p, System.StringComparer.Ordinal))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null)
                continue;
            bool soft = mat.IsKeywordEnabled("_SOFTPARTICLES_ON") ||
                        (mat.HasProperty("_SoftParticlesEnabled") && mat.GetFloat("_SoftParticlesEnabled") > 0.5f);
            if (soft)
                errors.Add($"{Path.GetFileName(path)}: SOFT PARTICLES are on — invisible in a WebGL build " +
                           "(the Mobile quality tier it runs has no depth texture). Turn Soft Particles off " +
                           $"on the material, or enable Require Depth Texture on Mobile_RPAsset.  ({path})");
        }

        // ---- VFX Graph sweep -------------------------------------------------
        // The graph runs on COMPUTE shaders; WebGL has none. Its generated
        // shaders all fail in the browser ("Hidden/VFX/... not supported on
        // this GPU") and the effect renders NOTHING — seen live with
        // vfxgraph_MuzzleFlash01. Any VisualEffect in shipped content is an
        // error, full stop: replace it with a Shuriken (ParticleSystem) effect.
        var prefabPaths = new HashSet<string>(
            roots.Count > 0
                ? AssetDatabase.GetDependencies(roots.ToArray(), true)
                    .Where(p => p.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                : System.Linq.Enumerable.Empty<string>());
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_COREHOLD" }))
            prefabPaths.Add(AssetDatabase.GUIDToAssetPath(guid));

        foreach (string path in prefabPaths.OrderBy(p => p, System.StringComparer.Ordinal))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
                continue;
            if (go.GetComponentInChildren<UnityEngine.VFX.VisualEffect>(true) != null)
                errors.Add($"{Path.GetFileName(path)}: contains a VFX GRAPH (VisualEffect) — WebGL has no " +
                           "compute shaders, so the effect renders NOTHING in builds. Replace it with a " +
                           $"Shuriken (ParticleSystem) effect.  ({path})");
        }

        // ---- screen textures and pipeline stages, per target ------------------
        // The general form of the soft-particle bug above, and the single
        // likeliest way a bought URP asset dies in the browser. Reported per
        // TARGET: the same water shader that renders nothing on WebGL is fine
        // on Desktop, and that distinction is a scoping decision worth having
        // rather than a blanket refusal.
        List<TargetTier> tiers = ResolveTiers();
        var notices = new List<string>();

        var shippedShaderPaths = new HashSet<string>();
        foreach (string path in materialPaths)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            string sp = mat != null && mat.shader != null ? AssetDatabase.GetAssetPath(mat.shader) : null;
            if (!string.IsNullOrEmpty(sp) && sp.StartsWith("Assets/", System.StringComparison.Ordinal))
                shippedShaderPaths.Add(sp);
        }

        // The two natures of finding are graded differently ON PURPOSE.
        //
        // A missing pipeline STAGE is provable from the source: #pragma geometry
        // means the shader has a geometry stage, GLES 3.0 has none, and no
        // material setting can change that. Blocking.
        //
        // A screen-TEXTURE reference is not provable. Vendored CFXR mentions
        // both _CameraDepthTexture and _CameraOpaqueTexture inside keyword
        // guards, and whether a given material lights those keywords is a
        // per-material fact this text scan cannot see. Erroring on it would
        // block every ship over a feature nothing enables — so it warns, and
        // the exact per-material soft-particle check above stays the blocking
        // one. A gate that cries wolf gets switched off, and then it protects
        // nobody.
        foreach (string sp in shippedShaderPaths.OrderBy(p => p, System.StringComparer.Ordinal))
        {
            ShaderNeeds needs = NeedsOf(ReadSourceWithIncludes(sp, 2));
            if (!needs.Any)
                continue;

            var stageNeeds = new ShaderNeeds
            { geometry = needs.geometry, tessellation = needs.tessellation, compute = needs.compute };
            var textureNeeds = new ShaderNeeds { depth = needs.depth, opaque = needs.opaque };

            if (stageNeeds.Any)
            {
                List<string> blocked = Blockers(stageNeeds, tiers, out List<string> okOn);
                var ship = blocked.Where(b => ShipTargets.Any(t => b.StartsWith(t, System.StringComparison.Ordinal))).ToList();
                if (ship.Count > 0)
                    errors.Add($"{Path.GetFileName(sp)}: SHIPPED CONTENT USES THIS, and the pipeline stage it " +
                               $"needs does not exist on {string.Join(" / ", ship)}" +
                               (okOn.Count > 0 ? $" — fine on {string.Join(", ", okOn)}" : "") +
                               ". The shader cannot compile there at all; swap the material to one that " +
                               $"renders through the vertex/fragment stages only.  ({sp})");
            }

            if (textureNeeds.Any)
            {
                List<string> blocked = Blockers(textureNeeds, tiers, out List<string> okOn);
                var ship = blocked.Where(b => ShipTargets.Any(t => b.StartsWith(t, System.StringComparison.Ordinal))).ToList();
                if (ship.Count > 0)
                    warns.Add($"{Path.GetFileName(sp)}: shipped content uses this, and it READS A SCREEN TEXTURE " +
                              $"that {string.Join(" / ", ship)} does not render" +
                              (okOn.Count > 0 ? $" (fine on {string.Join(", ", okOn)})" : "") +
                              ". If any material turns that feature on, it renders wrong in the browser and " +
                              "perfect in the editor. Either confirm no material enables it, or turn on " +
                              "Require Depth/Opaque Texture for that tier at a real bandwidth cost.  " +
                              $"({sp})");
            }
        }

        // ---- vendor intake: shaders present but not yet shipped --------------
        // Purchased packs arrive as dozens of shaders that all look perfect in
        // the editor, because the editor runs the PC tier. This section says
        // which of them are safe to build content on BEFORE the authoring time
        // is spent, grouped by pack so the report stays readable.
        var packFindings = new Dictionary<string, List<string>>();
        foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
        {
            string sp = AssetDatabase.GUIDToAssetPath(guid);
            if (shippedShaderPaths.Contains(sp) || sp.StartsWith("Assets/TextMesh Pro/", System.StringComparison.Ordinal))
                continue;
            ShaderNeeds needs = NeedsOf(ReadSourceWithIncludes(sp, 2));
            if (!needs.Any)
                continue;
            List<string> blocked = Blockers(needs, tiers, out _);
            var blockedShip = blocked.Where(b => ShipTargets.Any(t => b.StartsWith(t, System.StringComparison.Ordinal))).ToList();
            if (blockedShip.Count == 0)
                continue;

            // Group by the pack's own top-level folder: "Assets/StylizedWater3".
            string[] parts = sp.Split('/');
            string pack = parts.Length > 1 ? parts[0] + "/" + parts[1] : sp;
            if (!packFindings.TryGetValue(pack, out var list))
                packFindings[pack] = list = new List<string>();
            list.Add($"{Path.GetFileName(sp)} — {string.Join("; ", blockedShip)}");
        }
        foreach (var kv in packFindings.OrderBy(k => k.Key, System.StringComparer.Ordinal))
        {
            notices.Add($"{kv.Key}: {kv.Value.Count} shader(s) cannot run on {string.Join("/", ShipTargets)} — " +
                        "safe on Desktop, so this is a scoping decision, not a broken pack. " +
                        string.Join("  |  ", kv.Value.Take(6)) +
                        (kv.Value.Count > 6 ? $"  |  …and {kv.Value.Count - 6} more" : ""));
        }

        // ---- compute shaders reachable from shipped content ------------------
        // Same reason the VFX Graph had to go: WebGL has no compute stage, so
        // anything driven by one renders nothing in the browser.
        if (roots.Count > 0)
        {
            foreach (string dep in AssetDatabase.GetDependencies(roots.ToArray(), true)
                         .Where(p => p.EndsWith(".compute", System.StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p, System.StringComparer.Ordinal))
                errors.Add($"{Path.GetFileName(dep)}: a COMPUTE shader is reachable from shipped content — " +
                           "WebGL has no compute stage, so whatever it drives renders nothing in the " +
                           $"browser (it is fine on Desktop).  ({dep})");
        }

        // ---- report ---------------------------------------------------------
        var sb = new StringBuilder();
        sb.AppendLine($"=== WebGL SHADER AUDIT — {materialPaths.Count} material(s), {prefabPaths.Count} prefab(s) checked ===");
        foreach (TargetTier t in tiers) sb.AppendLine("  " + t.Describe());
        sb.AppendLine($"  shipping: {string.Join(", ", ShipTargets)}");
        sb.AppendLine($"  OK {ok},  warnings {warns.Count},  errors {errors.Count},  vendor notices {notices.Count}");
        foreach (string e in errors) sb.AppendLine("  ERROR  " + e);
        foreach (string w in warns) sb.AppendLine("  warn   " + w);
        foreach (string n in notices) sb.AppendLine("  intake " + n);
        sb.AppendLine("  Reminder: shaders used only via Shader.Find are invisible to this audit AND to the");
        sb.AppendLine("  build's dependency walk — they must live under a Resources/ folder to ship.");
        errorCount = errors.Count;
        warningCount = warns.Count;
        return sb.ToString();
    }

    // =====================================================================
    //  Per-target capability model
    // =====================================================================
    //
    // A bought URP asset is rarely "broken" — it is broken ON A TARGET. The
    // same Stylized Water that dies in the browser is perfect on Desktop,
    // because the two run different QUALITY TIERS pointing at different render
    // pipeline assets, and because GLES 3.0 is missing whole pipeline stages
    // that D3D11/Metal/Vulkan have.
    //
    // So findings are reported per target rather than as one pass/fail. That
    // turns "this pack does not work" into the far more useful "this pack is a
    // Desktop-tier purchase", which is a scoping decision rather than a bug.

    /// <summary>Targets the project intends to ship. A finding is an ERROR only
    /// when it blocks one of these; anything else is reported for information.
    /// Adding Desktop here later is a one-line change.</summary>
    private static readonly string[] ShipTargets = { "WebGL" };

    private class TargetTier
    {
        public string target;
        public bool resolved;
        public string qualityName = "?";
        public string rpName = "?";
        public bool depthTexture;
        public bool opaqueTexture;

        /// <summary>Pipeline stages the GRAPHICS API has at all — a tier setting
        /// cannot grant these. WebGL 2.0 is GLES 3.0: no geometry stage, no
        /// tessellation, no compute.</summary>
        public bool geometryStage;
        public bool tessellation;
        public bool compute;

        public string Describe() => resolved
            ? $"{target}: quality tier '{qualityName}' → {rpName} " +
              $"(depth {(depthTexture ? "on" : "OFF")}, opaque {(opaqueTexture ? "on" : "OFF")})"
            : $"{target}: tier could not be resolved — screen-texture findings below assume the worst";
    }

    /// <summary>
    /// Which quality tier each target defaults to, and what that tier's render
    /// pipeline asset actually renders. Read through <see cref="SerializedObject"/>
    /// off ProjectSettings, because the per-platform default quality map has no
    /// public API and the render-pipeline fields move between package versions.
    /// </summary>
    private static List<TargetTier> ResolveTiers()
    {
        var tiers = new List<TargetTier>
        {
            new TargetTier { target = "WebGL",     geometryStage = false, tessellation = false, compute = false },
            new TargetTier { target = "Standalone", geometryStage = true, tessellation = true,  compute = true  },
        };

        Object[] loaded = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
        if (loaded == null || loaded.Length == 0 || loaded[0] == null)
            return tiers;

        var so = new SerializedObject(loaded[0]);
        SerializedProperty levels = so.FindProperty("m_QualitySettings");
        SerializedProperty perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
        if (levels == null || !levels.isArray || perPlatform == null || !perPlatform.isArray)
            return tiers;

        foreach (TargetTier tier in tiers)
        {
            int level = -1;
            for (int i = 0; i < perPlatform.arraySize; i++)
            {
                SerializedProperty pair = perPlatform.GetArrayElementAtIndex(i);
                SerializedProperty key = pair.FindPropertyRelative("first");
                SerializedProperty val = pair.FindPropertyRelative("second");
                if (key != null && val != null && key.stringValue == tier.target)
                {
                    level = val.intValue;
                    break;
                }
            }
            if (level < 0 || level >= levels.arraySize)
                continue;

            SerializedProperty lv = levels.GetArrayElementAtIndex(level);
            tier.qualityName = lv.FindPropertyRelative("name")?.stringValue ?? level.ToString();

            var rp = lv.FindPropertyRelative("customRenderPipeline")?.objectReferenceValue as ScriptableObject;
            if (rp == null)
                continue;

            tier.rpName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(rp));
            var rso = new SerializedObject(rp);
            SerializedProperty d = rso.FindProperty("m_RequireDepthTexture");
            SerializedProperty o = rso.FindProperty("m_RequireOpaqueTexture");
            if (d == null || o == null)
                continue;

            tier.depthTexture = d.boolValue;
            tier.opaqueTexture = o.boolValue;
            tier.resolved = true;
        }

        return tiers;
    }

    // ---------------------------------------------------------------- needs

    private struct ShaderNeeds
    {
        public bool depth, opaque, geometry, tessellation, compute;
        public bool Any => depth || opaque || geometry || tessellation || compute;
    }

    /// <summary>What a shader source demands of the platform. Text matching, not
    /// compilation — a false positive costs a sentence in a report, a false
    /// negative costs a shipped build that renders nothing.</summary>
    private static ShaderNeeds NeedsOf(string src)
    {
        var n = new ShaderNeeds();
        if (string.IsNullOrEmpty(src))
            return n;

        // URP exposes the screen textures under both the raw sampler names and
        // the ShaderLibrary helpers; Shader Graph serializes the node type name.
        n.opaque = src.Contains("_CameraOpaqueTexture") || src.Contains("SampleSceneColor") ||
                   src.Contains("SceneColorNode");
        // Only the precise signals. LinearEyeDepth and friends appear in plenty
        // of shaders that never touch the camera depth texture, and a false
        // positive here does not cost a sentence in a report — it blocks a ship.
        n.depth = src.Contains("_CameraDepthTexture") || src.Contains("SampleSceneDepth") ||
                  src.Contains("SceneDepthNode");
        n.geometry = src.Contains("#pragma geometry");
        n.tessellation = src.Contains("#pragma hull") || src.Contains("#pragma domain");
        n.compute = src.Contains("#pragma kernel");
        return n;
    }

    /// <summary>
    /// Shader source plus the sources it includes from inside Assets/. Vendor
    /// packs habitually keep the interesting half in an .hlsl next door, so
    /// reading only the .shader misses exactly the shaders worth catching.
    /// Two levels deep and de-duplicated: enough in practice, and it cannot
    /// loop on a circular include.
    /// </summary>
    private static string ReadSourceWithIncludes(string path, int depth, HashSet<string> seen = null)
    {
        seen ??= new HashSet<string>();
        if (depth < 0 || !seen.Add(path) || !File.Exists(path))
            return string.Empty;

        string src;
        try { src = File.ReadAllText(path); }
        catch { return string.Empty; }
        if (depth == 0)
            return src;

        var sb = new StringBuilder(src);
        string dir = Path.GetDirectoryName(path) ?? "";
        foreach (Match m in Regex.Matches(src, "#include\\s+\"([^\"]+)\""))
        {
            string rel = m.Groups[1].Value;
            if (rel.StartsWith("Packages/", System.StringComparison.Ordinal))
                continue;   // the pipeline's own library — its needs are the target's own
            string resolved = rel.StartsWith("Assets/", System.StringComparison.Ordinal)
                ? rel
                : Path.Combine(dir, rel).Replace('\\', '/');
            sb.Append('\n').Append(ReadSourceWithIncludes(resolved, depth - 1, seen));
        }
        return sb.ToString();
    }

    /// <summary>Which shipping targets a set of needs breaks, and why.</summary>
    private static List<string> Blockers(ShaderNeeds needs, List<TargetTier> tiers, out List<string> okOn)
    {
        var blocked = new List<string>();
        okOn = new List<string>();
        foreach (TargetTier t in tiers)
        {
            var why = new List<string>();
            if (needs.opaque && (!t.opaqueTexture || !t.resolved))
                why.Add($"no opaque texture on {t.rpName}");
            if (needs.depth && (!t.depthTexture || !t.resolved))
                why.Add($"no depth texture on {t.rpName}");
            if (needs.geometry && !t.geometryStage)
                why.Add("no geometry stage");
            if (needs.tessellation && !t.tessellation)
                why.Add("no tessellation");
            if (needs.compute && !t.compute)
                why.Add("no compute shaders");

            if (why.Count > 0)
                blocked.Add($"{t.target} ({string.Join(", ", why)})");
            else
                okOn.Add(t.target);
        }
        return blocked;
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
