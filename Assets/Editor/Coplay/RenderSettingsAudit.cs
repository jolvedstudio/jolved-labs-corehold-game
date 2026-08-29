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
///   • <b>Main light shadows were OFF</b> in every RP asset. Nothing cast a
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

        string[] guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
        var sb = new StringBuilder();
        sb.AppendLine($"=== RENDER SETTINGS AUDIT — {guids.Length} render-pipeline asset(s) ===");

        if (guids.Length == 0)
        {
            warns.Add("no UniversalRenderPipelineAsset found — cannot verify shadows.");
        }

        foreach (string guid in guids.OrderBy(g => g, System.StringComparer.Ordinal))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
                continue;

            var so = new SerializedObject(asset);
            var shadowsOn = so.FindProperty("m_MainLightShadowsSupported");
            var distance = so.FindProperty("m_ShadowDistance");
            var cascades = so.FindProperty("m_ShadowCascadeCount");
            var soft = so.FindProperty("m_SoftShadowsSupported");
            string name = System.IO.Path.GetFileNameWithoutExtension(path);

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

            sb.AppendLine($"  {name}: shadows {(shadowsOn.boolValue ? "ON" : "OFF")}, " +
                          $"distance {distance.floatValue:0} m" +
                          (cascades != null ? $", {cascades.intValue} cascade(s)" : "") +
                          (soft != null ? $", soft {(soft.boolValue ? "on" : "off")}" : ""));
        }

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

        foreach (string guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
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
                log.AppendLine($"  {System.IO.Path.GetFileNameWithoutExtension(path)}: {string.Join(", ", before)}");
            }
        }

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
