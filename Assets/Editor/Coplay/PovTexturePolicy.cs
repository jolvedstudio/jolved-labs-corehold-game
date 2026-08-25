using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// POV texture policy (M-d): the project's texture sizes were cut to 512/1024
/// for the WebGL budget, which reads soft the moment a POV camera (turret cam,
/// manual control) stands next to a surface. This tool splits the budget BY
/// PLATFORM instead of globally, touching only the HERO textures a POV camera
/// actually gets close to:
///
///   • WebGL keeps EXACTLY the size each texture has today (written as an
///     explicit WebGL platform override, created only where none exists) —
///     the shipping budget does not move by a byte;
///   • the DEFAULT platform (editor, standalone) rises to 2048 max, so
///     desktop and editor POV shots are crisp;
///   • anisotropic level is raised to 8 and mipmaps ensured — half of POV
///     blur at grazing angles is filtering, not resolution;
///   • project Quality settings get anisotropic filtering FORCED ON across
///     every level.
///
/// Hero set = every texture reachable from tower prefabs (TowerDefinition),
/// enemy prefabs (EnemyDefinition) and theme grounds (EnvPack ground
/// material/prefab). UI atlases and fonts are not touched. Vendor textures
/// are included — their importers exist locally even though the assets are
/// git-ignored, so the policy applies per machine (re-run it after installing
/// a kit on a new machine).
///
/// Idempotent: re-running reports "already conforming" per texture.
/// </summary>
public static class PovTexturePolicy
{
    private const int DesktopMax = 2048;
    private const int AnisoLevel = 8;

    [MenuItem("Tools/COREHOLD/Scene Setup/Apply POV Texture Policy", false, 49)]
    public static void Run()
    {
        var log = new StringBuilder();
        log.AppendLine("=== POV Texture Policy (M-d) ===");

        // ---- collect the hero texture set --------------------------------
        var roots = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:TowerDefinition"))
        {
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (def != null && def.basePrefab != null)
                roots.Add(AssetDatabase.GetAssetPath(def.basePrefab));
        }
        foreach (string guid in AssetDatabase.FindAssets("t:EnemyDefinition"))
        {
            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (def != null && def.prefab != null)
                roots.Add(AssetDatabase.GetAssetPath(def.prefab));
        }
        foreach (string guid in AssetDatabase.FindAssets("t:EnvPack"))
        {
            var pack = AssetDatabase.LoadAssetAtPath<EnvPack>(AssetDatabase.GUIDToAssetPath(guid));
            if (pack == null) continue;
            if (pack.groundMaterial != null) roots.Add(AssetDatabase.GetAssetPath(pack.groundMaterial));
            if (pack.groundPrefab != null) roots.Add(AssetDatabase.GetAssetPath(pack.groundPrefab));
        }
        roots.RemoveAll(string.IsNullOrEmpty);

        var textures = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (string dep in AssetDatabase.GetDependencies(roots.Distinct().ToArray(), true))
        {
            if (AssetDatabase.GetMainAssetTypeAtPath(dep) != typeof(Texture2D))
                continue;
            // Leave UI art and fonts alone — mip/aniso policy is for world surfaces.
            if (dep.Contains("/Art/UI/") || dep.Contains("/TextMesh Pro/") || dep.EndsWith(".asset"))
                continue;
            textures.Add(dep);
        }

        log.AppendLine($"{roots.Distinct().Count()} root prefab/material(s) → {textures.Count} world texture(s).");

        // ---- apply -------------------------------------------------------
        int changed = 0, conforming = 0, skipped = 0;
        foreach (string path in textures)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) { skipped++; continue; }

            bool dirty = false;
            var notes = new List<string>();

            // 1. Freeze today's size as the WebGL budget (only if not already overridden).
            TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
            if (!webgl.overridden)
            {
                webgl.overridden = true;
                webgl.maxTextureSize = importer.maxTextureSize;
                webgl.format = TextureImporterFormat.Automatic;
                importer.SetPlatformTextureSettings(webgl);
                notes.Add($"WebGL pinned @{webgl.maxTextureSize}");
                dirty = true;
            }

            // 2. Default (editor/standalone) rises to the desktop ceiling.
            if (importer.maxTextureSize < DesktopMax)
            {
                notes.Add($"default {importer.maxTextureSize}→{DesktopMax}");
                importer.maxTextureSize = DesktopMax;
                dirty = true;
            }

            // 3. Filtering: aniso + mips (grazing-angle sharpness).
            if (importer.anisoLevel < AnisoLevel)
            {
                notes.Add($"aniso {importer.anisoLevel}→{AnisoLevel}");
                importer.anisoLevel = AnisoLevel;
                dirty = true;
            }
            if (!importer.mipmapEnabled)
            {
                notes.Add("mipmaps on");
                importer.mipmapEnabled = true;
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
                changed++;
                log.AppendLine($"  ~ {path}  ({string.Join(", ", notes)})");
            }
            else
            {
                conforming++;
            }
        }

        // ---- quality settings: force anisotropic filtering ----------------
        string anisoNote = ForceAnisotropic();

        log.AppendLine($"{changed} texture(s) updated, {conforming} already conforming, {skipped} not texture importers.");
        log.AppendLine(anisoNote);
        log.AppendLine("WebGL sizes are untouched (pinned as explicit overrides); desktop/editor now import at " +
                       $"up to {DesktopMax}. Vendor importers are machine-local — re-run this after installing " +
                       "a kit on another machine.");
        Debug.Log(log.ToString());
    }

    /// <summary>Set anisotropic filtering to Forced On in every quality level,
    /// through the serialized asset so it persists like a hand edit.</summary>
    private static string ForceAnisotropic()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
        if (assets == null || assets.Length == 0)
            return "Quality settings asset not reachable — set Anisotropic Textures to 'Forced On' by hand.";

        var so = new SerializedObject(assets[0]);
        SerializedProperty levels = so.FindProperty("m_QualitySettings");
        if (levels == null || !levels.isArray)
            return "Quality settings schema unexpected — set Anisotropic Textures to 'Forced On' by hand.";

        int touched = 0;
        for (int i = 0; i < levels.arraySize; i++)
        {
            SerializedProperty aniso = levels.GetArrayElementAtIndex(i).FindPropertyRelative("anisotropicTextures");
            if (aniso != null && aniso.intValue != 2)
            {
                aniso.intValue = 2; // Forced On
                touched++;
            }
        }
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return touched > 0
            ? $"Anisotropic filtering FORCED ON across {touched} quality level(s)."
            : "Anisotropic filtering already forced on in every quality level.";
    }
}
