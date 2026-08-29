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
    private const int MinCascadesForLongDistance = 2;

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
                cascades != null && cascades.intValue < MinCascadesForLongDistance)
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

        sb.AppendLine($"  OK, errors {errors.Count}, warnings {warns.Count}");
        foreach (string e in errors) sb.AppendLine("  ERROR  " + e);
        foreach (string w in warns) sb.AppendLine("  warn   " + w);

        errorCount = errors.Count;
        warningCount = warns.Count;
        return sb.ToString();
    }
}
