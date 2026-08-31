using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Render-pipeline audit: the settings that decide whether the game looks like
/// a 3-D scene or a flat diorama, checked automatically instead of remembered.
///
/// It exists because of two failures found in the field, both invisible from
/// inside the editor's Game view:
///
///   • <b>Main light shadows were OFF</b> in the shipped RP assets. Nothing cast a
///     shadow at all; the only ground contact came from the fake
///     <c>BlobShadow</c> decals, which is exactly the "too flat" read. No
///     amount of sun tuning fixes a disabled shadow pass.
///   • <b>Shadow distance was 50 m</b> while the gameplay camera sits 130-150 m
///     back to frame a wide map. Shadow distance is measured FROM THE CAMERA,
///     so even with shadows enabled, every shadow in the play area fell outside
///     the range and simply was not drawn.
///
/// Both are one number in an asset nobody opens twice a year, and both look
/// perfectly fine until someone compares against a build that has them right.
/// The campaign preflight runs this, so a regression blocks a ship.
///
/// Fields are read through <see cref="SerializedObject"/> rather than the URP
/// types so this compiles regardless of which render-pipeline package version
/// (or assembly layout) the project carries.
///
/// SCOPE: only the render-pipeline assets the game actually renders through —
/// Graphics → Default Render Pipeline, plus each quality level's override. A
/// project accumulates RP assets (package defaults, abandoned experiments) that
/// nothing references, and auditing those produces errors nobody can act on
/// while their settings flatter the report: a stray asset with shadows
/// correctly ON reads as a pass to anyone skimming, while the one the build
/// ships has them off. Skipped assets are NAMED in the report so the silence is
/// never mistaken for an oversight.
/// </summary>
public static class RenderSettingsAudit
{
    /// <summary>Shadow distance (m) every RP asset must reach. The gameplay
    /// camera pulls back to 130-150 m on a wide map and the far edge sits
    /// further still, so anything under this leaves the field unshadowed.</summary>
    public const float MinShadowDistance = 500f;

    /// <summary>Below this cascade count a long shadow distance spreads the
    /// shadow map so thin that contact shadows turn to mush.</summary>
    public const int MinCascades = 2;

    /// <summary>Unit prefabs whose bodies MUST cast: blob shadows are retired, so
    /// a unit that casts nothing has no ground contact at all. Blocking.</summary>
    private static readonly string[] UnitFolders =
    {
        "Assets/_COREHOLD/Prefabs/Enemies",
        "Assets/_COREHOLD/Prefabs/Towers",
    };

    /// <summary>Dressing that should also cast — rocks and props with no shadow
    /// are what make a field read as decals on a plane. Reported, not blocking.</summary>
    private static readonly string[] PropFolders =
    {
        "Assets/_COREHOLD/Authoring/EnvPack",
        "Assets/_COREHOLD/Prefabs/Structures",
    };

    /// <summary>Ground planes RECEIVE shadows; casting from them buys nothing and
    /// costs a shadow-map draw, so they are exempt by folder.</summary>
    private static bool IsGround(string path) => path.Contains("/Ground/");

    // ------------------------------------------------- which pipelines matter

    /// <summary>One render-pipeline asset that is actually WIRED, and what
    /// wires it — the sentence a reader needs to judge whether a warning is
    /// worth acting on.</summary>
    private readonly struct PipelineRef
    {
        public readonly Object Asset;
        public readonly string Path;
        public readonly string Usage;
        public readonly bool ShipsOnWebGL;

        public PipelineRef(Object asset, string path, string usage, bool shipsOnWebGL)
        {
            Asset = asset; Path = path; Usage = usage; ShipsOnWebGL = shipsOnWebGL;
        }
    }

    /// <summary>
    /// The render-pipeline assets the GAME actually renders through: whatever
    /// Graphics → Default Render Pipeline points at, plus each quality level's
    /// own override.
    ///
    /// This scoping is the whole point. An RP asset that nothing references
    /// changes nothing at runtime, so auditing it produces errors that cannot
    /// be acted on — and, worse, its settings can flatter the report: a stray
    /// asset with shadows correctly ON reads as a pass to anyone skimming,
    /// while the one the build ships has them off. The audit must only ever
    /// speak about what ships.
    /// </summary>
    private static List<PipelineRef> ResolveActivePipelines(List<string> notes)
    {
        var found = new List<PipelineRef>();
        var seen = new HashSet<Object>();
        int webglQuality = WebGLDefaultQualityIndex();

        void Add(Object asset, string usage, bool webgl)
        {
            if (asset == null || !seen.Add(asset))
                return;
            found.Add(new PipelineRef(asset, AssetDatabase.GetAssetPath(asset), usage, webgl));
        }

        Add(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline,
            "Graphics → Default Render Pipeline", webglQuality < 0);

        string[] names = QualitySettings.names;
        for (int i = 0; i < names.Length; i++)
        {
            Object rp = QualitySettings.GetRenderPipelineAssetAt(i);
            if (rp == null)
                continue;
            bool webgl = i == webglQuality;
            Add(rp, $"quality level {i} '{names[i]}'" +
                    (webgl ? " — THIS IS WHAT WEBGL SHIPS" : ""), webgl);
        }

        // Name what was skipped. Silence here would look like the audit had
        // simply missed them, and someone would "fix" a dead asset.
        var active = new HashSet<string>(found.Select(f => f.Path));
        var idle = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => !active.Contains(p))
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToList();
        if (idle.Count > 0)
            notes.Add($"{idle.Count} render-pipeline asset(s) NOT audited — nothing references them, " +
                      "so their settings change nothing at runtime: " + string.Join(", ", idle));

        if (webglQuality < 0)
            notes.Add("could not read the WebGL default quality level — the WebGL row below is a guess. " +
                      "Check Project Settings → Quality → the platform grid by hand.");

        return found;
    }

    /// <summary>
    /// The quality level WebGL builds start on.
    ///
    /// Worth singling out because it is the classic trap in this project: the
    /// editor runs one quality level and the WebGL build ships another, so a
    /// setting can look right all day in the Game view and be wrong in the
    /// thing players load. <c>m_PerPlatformDefaultQuality</c> has no public
    /// API, and reading the one line is more honest than reflecting into it.
    /// </summary>
    private static int WebGLDefaultQualityIndex()
    {
        const string path = "ProjectSettings/QualitySettings.asset";
        try
        {
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (!t.StartsWith("WebGL:"))
                    continue;
                return int.TryParse(t.Substring("WebGL:".Length).Trim(), out int v) ? v : -1;
            }
        }
        catch (System.IO.IOException) { }
        return -1;
    }

    [MenuItem("Tools/COREHOLD/Look/Render Settings Audit", false, 62)]
    public static void Run()
    {
        string report = Report(out int errors, out _);
        if (errors > 0) Debug.LogError(report); else Debug.Log(report);
    }

    /// <summary>The audit as data, so the campaign preflight can gate on it.</summary>
    public static string Report(out int errorCount, out int warningCount)
    {
        var errors = new List<string>();
        var warns = new List<string>();

        var notes = new List<string>();
        List<PipelineRef> pipelines = ResolveActivePipelines(notes);

        var sb = new StringBuilder();
        sb.AppendLine($"=== RENDER SETTINGS AUDIT — {pipelines.Count} render-pipeline asset(s) IN USE ===");

        if (pipelines.Count == 0)
        {
            warns.Add("no render-pipeline asset is referenced by Graphics or Quality settings — " +
                      "the project is rendering on the built-in pipeline, or the references were lost.");
        }

        foreach (PipelineRef pipe in pipelines)
        {
            string path = pipe.Path;
            Object asset = pipe.Asset;
            if (asset == null)
                continue;

            var so = new SerializedObject(asset);
            var shadowsOn = so.FindProperty("m_MainLightShadowsSupported");
            var distance = so.FindProperty("m_ShadowDistance");
            var cascades = so.FindProperty("m_ShadowCascadeCount");
            var soft = so.FindProperty("m_SoftShadowsSupported");
            string name = System.IO.Path.GetFileNameWithoutExtension(path) +
                          $" [{pipe.Usage}]";

            if (shadowsOn == null || distance == null)
            {
                warns.Add($"{name}: shadow fields not found — a render-pipeline version this audit " +
                          $"does not know. Check its Shadows section by hand.  ({path})");
                continue;
            }

            if (!shadowsOn.boolValue)
                errors.Add($"{name}: MAIN LIGHT SHADOWS ARE OFF — nothing in the scene casts a shadow, " +
                           "which reads as a flat diorama however the sun is tuned. Enable Cast Shadows " +
                           $"under Lighting → Main Light.  ({path})");

            if (distance.floatValue < MinShadowDistance)
                errors.Add($"{name}: shadow distance {distance.floatValue:0} m is under the " +
                           $"{MinShadowDistance:0} m standard. Distance is measured FROM THE CAMERA, and " +
                           "the gameplay camera sits 130-150 m back to frame a wide map — shorter than " +
                           $"this and the play area is simply not shadowed.  ({path})");

            if (shadowsOn.boolValue && distance.floatValue >= MinShadowDistance &&
                cascades != null && cascades.intValue < MinCascades)
                warns.Add($"{name}: {distance.floatValue:0} m of shadow over {cascades.intValue} cascade — " +
                          "the shadow map is stretched thin, so contact shadows will read soft and blocky. " +
                          $"2+ cascades sharpen the near field.  ({path})");

            if (shadowsOn.boolValue && soft != null && !soft.boolValue)
                warns.Add($"{name}: soft shadows off — edges will alias, which is loud at this camera " +
                          $"distance.  ({path})");

            sb.AppendLine($"  {(pipe.ShipsOnWebGL ? "▶ " : "  ")}{name}: " +
                          $"shadows {(shadowsOn.boolValue ? "ON" : "OFF")}, " +
                          $"distance {distance.floatValue:0} m" +
                          (cascades != null ? $", {cascades.intValue} cascade(s)" : "") +
                          (soft != null ? $", soft {(soft.boolValue ? "on" : "off")}" : ""));
        }

        foreach (string n in notes)
            sb.AppendLine("  note   " + n);

        // ---- everything on the field must cast, now that blob shadows are off ----
        int mutePropCount = 0;
        foreach (var (folders, blocking) in new[] { (UnitFolders, true), (PropFolders, false) })
        {
            foreach (string folder in folders)
            {
                if (!AssetDatabase.IsValidFolder(folder))
                    continue;
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (IsGround(path))
                        continue;
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go == null || !HasMeshRenderer(go) || CountCasters(go) > 0)
                        continue;

                    if (blocking)
                        errors.Add($"{System.IO.Path.GetFileNameWithoutExtension(path)}: nothing casts a shadow " +
                                   "— with blob shadows retired this unit has NO ground contact at all. Run " +
                                   "Tools → COREHOLD → Look → Fix Shadow Standard.  (" + path + ")");
                    else
                        mutePropCount++;
                }
            }
        }
        if (mutePropCount > 0)
            warns.Add($"{mutePropCount} dressing prefab(s) cast no shadow — they read as decals painted on the " +
                      "ground. Fix Shadow Standard turns them on; regenerate the map to place the fixed prefabs.");

        sb.AppendLine($"  OK, errors {errors.Count}, warnings {warns.Count}");
        foreach (string e in errors) sb.AppendLine("  ERROR  " + e);
        foreach (string w in warns) sb.AppendLine("  warn   " + w);

        errorCount = errors.Count;
        warningCount = warns.Count;
        return sb.ToString();
    }

    /// <summary>True when the prefab has any mesh at all — a pure logic or VFX
    /// prefab has nothing to cast and must not be reported as broken.</summary>
    private static bool HasMeshRenderer(GameObject root)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            if ((r is MeshRenderer || r is SkinnedMeshRenderer) && r.GetComponent("BlobShadow") == null)
                return true;
        return false;
    }

    /// <summary>Body renderers that cast — blob-shadow quads and non-mesh
    /// renderers (particles, lines, trails) never count.</summary>
    private static int CountCasters(GameObject root)
    {
        int n = 0;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer))
                continue;
            if (r.GetComponent("BlobShadow") != null)
                continue;
            if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ fixer

    /// <summary>
    /// Apply the shadow standard everywhere, in one pass — the companion to the
    /// audit above, so nobody hand-edits an RP asset field again (and so two
    /// machines editing the same asset do not collide over it).
    ///
    ///   • every RP asset: main light shadows ON, shadow distance at least
    ///     <see cref="MinShadowDistance"/>, cascades at least
    ///     <see cref="MinCascades"/> — a long distance on one cascade is what
    ///     turns contact shadows to mush.
    ///   • every enemy/turret prefab: body renderers cast again. Five turrets
    ///     shipped with casting switched off entirely, which was invisible while
    ///     blob shadows covered for them.
    ///
    /// Values already ABOVE the standard are left alone: this raises a floor, it
    /// does not overwrite deliberate tuning.
    /// </summary>
    [MenuItem("Tools/COREHOLD/Look/Fix Shadow Standard", false, 63)]
    public static void FixShadowStandard()
    {
        var log = new StringBuilder();
        log.AppendLine("=== FIX SHADOW STANDARD ===");
        int assets = 0, prefabs = 0;

        // Only the pipelines the game renders through. Writing a fix into an
        // asset nothing references is worse than doing nothing: it edits a file
        // for no runtime effect, and it leaves the project with two assets that
        // disagree and no way to tell which one was meant.
        var fixNotes = new List<string>();
        foreach (PipelineRef pipe in ResolveActivePipelines(fixNotes))
        {
            string path = pipe.Path;
            Object asset = pipe.Asset;
            if (asset == null) continue;

            var so = new SerializedObject(asset);
            var shadowsOn = so.FindProperty("m_MainLightShadowsSupported");
            var distance = so.FindProperty("m_ShadowDistance");
            var cascades = so.FindProperty("m_ShadowCascadeCount");
            if (shadowsOn == null || distance == null) continue;

            var before = new List<string>();
            if (!shadowsOn.boolValue) { before.Add("shadows OFF→ON"); shadowsOn.boolValue = true; }
            if (distance.floatValue < MinShadowDistance)
            { before.Add($"distance {distance.floatValue:0}→{MinShadowDistance:0}"); distance.floatValue = MinShadowDistance; }
            if (cascades != null && cascades.intValue < MinCascades)
            { before.Add($"cascades {cascades.intValue}→{MinCascades}"); cascades.intValue = MinCascades; }

            if (before.Count > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                assets++;
                log.AppendLine($"  {System.IO.Path.GetFileNameWithoutExtension(path)} " +
                               $"[{pipe.Usage}]: {string.Join(", ", before)}");
            }
        }

        foreach (string n in fixNotes)
            log.AppendLine("  note   " + n);

        foreach (string folder in UnitFolders.Concat(PropFolders))
        {
            if (!AssetDatabase.IsValidFolder(folder)) continue;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsGround(path)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null || CountCasters(go) > 0) continue;

                int turned = 0;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (r.GetComponent("BlobShadow") != null) continue;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    turned++;
                }
                if (turned > 0)
                {
                    PrefabUtility.SavePrefabAsset(go);
                    prefabs++;
                    log.AppendLine($"  {System.IO.Path.GetFileNameWithoutExtension(path)}: {turned} renderer(s) now cast");
                }
            }
        }

        AssetDatabase.SaveAssets();
        log.AppendLine($"  {assets} render-pipeline asset(s) and {prefabs} unit prefab(s) updated.");
        if (assets == 0 && prefabs == 0)
            log.AppendLine("  (nothing to change — the standard already holds)");
        Debug.Log(log.ToString());
    }
}
