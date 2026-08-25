using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Prop detail injection (M-d, companion to the POV texture policy): sweep
/// every MATERIAL the hero prefabs use — towers, enemies, theme grounds and
/// the EnvPack prop pool — and fill URP Lit's built-in Detail Inputs with a
/// high-frequency noise tile, so surfaces stay readable when a POV camera
/// stands next to them despite the 512/1024 texture budget.
///
/// Rules of engagement:
///   • only URP "Lit"/"Complex Lit" materials — they are the ones with the
///     detail slots; other shaders are counted and named, never touched;
///   • a material that ALREADY has a detail albedo keeps it — an author's
///     choice is never overwritten; re-running is idempotent;
///   • the tile is a generated 128 px seamless noise imported LINEAR
///     (detail albedo is overlay-×2: 0.5 must stay 0.5, and an sRGB import
///     would decode it to 0.21 and darken everything);
///   • vendor materials are machine-local (the kits are git-ignored), so the
///     injection applies per machine — like the texture policy, re-run after
///     installing a kit elsewhere.
/// </summary>
public static class PropDetailInjector
{
    private const string DetailTexPath = "Assets/_COREHOLD/Art/Textures/DetailNoise.png";

    /// <summary>[TUNE] Detail tiling relative to each material's own UVs.
    /// Props have arbitrary UV density, so this is a taste constant — 4 reads
    /// as fine surface grain on metre-scale props.</summary>
    private const float DetailTiling = 4f;

    [MenuItem("Tools/COREHOLD/Scene Setup/Add Detail Maps To Hero Materials", false, 50)]
    public static void Run()
    {
        var log = new StringBuilder();
        log.AppendLine("=== Add Detail Maps To Hero Materials (M-d) ===");

        Texture2D detail = EnsureDetailAsset(log);
        if (detail == null)
        {
            Debug.Log(log.ToString());
            return;
        }

        // ---- hero roots: everything a POV camera gets close to --------------
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
            if (pack.entries == null) continue;
            foreach (EnvPack.Entry e in pack.entries)
                if (e.prefab != null)
                    roots.Add(AssetDatabase.GetAssetPath(e.prefab));
        }
        roots.RemoveAll(string.IsNullOrEmpty);

        var materials = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (string dep in AssetDatabase.GetDependencies(roots.Distinct().ToArray(), true))
            if (AssetDatabase.GetMainAssetTypeAtPath(dep) == typeof(Material))
                materials.Add(dep);

        log.AppendLine($"{roots.Distinct().Count()} hero root(s) → {materials.Count} material(s).");

        // ---- inject ----------------------------------------------------------
        int injected = 0, kept = 0, wrongShader = 0;
        var shaderNames = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (string path in materials)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null)
                continue;

            string shaderName = mat.shader.name;
            bool isLit = shaderName == "Universal Render Pipeline/Lit" ||
                         shaderName == "Universal Render Pipeline/Complex Lit";
            if (!isLit || !mat.HasProperty("_DetailAlbedoMap"))
            {
                wrongShader++;
                shaderNames.Add(shaderName);
                continue;
            }

            if (mat.GetTexture("_DetailAlbedoMap") != null)
            {
                kept++; // authored detail wins, always
                continue;
            }

            mat.SetTexture("_DetailAlbedoMap", detail);
            mat.SetTextureScale("_DetailAlbedoMap", new Vector2(DetailTiling, DetailTiling));
            if (mat.HasProperty("_DetailAlbedoMapScale"))
                mat.SetFloat("_DetailAlbedoMapScale", 1f);
            // URP Lit's detail path is keyword-gated; ×1 scale uses _DETAIL_MULX2.
            mat.EnableKeyword("_DETAIL_MULX2");
            EditorUtility.SetDirty(mat);
            injected++;
            log.AppendLine($"  + {path}");
        }

        AssetDatabase.SaveAssets();

        log.AppendLine($"{injected} material(s) injected, {kept} kept their authored detail, " +
                       $"{wrongShader} on shaders without detail slots" +
                       (shaderNames.Count > 0 ? $" ({string.Join(", ", shaderNames)})" : "") + ".");
        log.AppendLine("Vendor materials are machine-local — re-run after installing a kit on another " +
                       "machine. Undo by clearing the material's Detail Albedo slot; re-running never " +
                       "overwrites an assigned one.");
        Debug.Log(log.ToString());
    }

    /// <summary>The shared detail tile as a real PROJECT asset (materials must
    /// reference an asset, not a runtime texture): generated on first run from
    /// the terrain stage's seamless noise, imported linear/repeat/aniso.</summary>
    private static Texture2D EnsureDetailAsset(StringBuilder log)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(DetailTexPath);
        if (existing != null)
            return existing;

        if (!AssetDatabase.IsValidFolder("Assets/_COREHOLD/Art/Textures"))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Art", "Textures");

        Texture2D noise = TerrainStage.BuildDetailNoise();
        System.IO.File.WriteAllBytes(DetailTexPath, noise.EncodeToPNG());
        Object.DestroyImmediate(noise);
        AssetDatabase.ImportAsset(DetailTexPath);

        var importer = AssetImporter.GetAtPath(DetailTexPath) as TextureImporter;
        if (importer == null)
        {
            log.AppendLine($"FAILED to import {DetailTexPath} — no texture importer.");
            return null;
        }
        importer.sRGBTexture = false;   // 0.5 must STAY 0.5 — sRGB would darken the overlay
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.mipmapEnabled = true;
        importer.anisoLevel = 8;
        importer.SaveAndReimport();

        log.AppendLine($"Detail tile created → {DetailTexPath} (128 px seamless noise, linear import).");
        return AssetDatabase.LoadAssetAtPath<Texture2D>(DetailTexPath);
    }
}
