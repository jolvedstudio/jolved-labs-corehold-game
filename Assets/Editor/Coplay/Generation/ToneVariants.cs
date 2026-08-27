using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared tone-variant materials for dressing variation (R28).
///
/// Why materials and not MaterialPropertyBlocks: a generated scene is BAKED —
/// property blocks do not serialize, so a tint applied through one would
/// vanish on the next scene load. And per-instance material copies would embed
/// a material into the scene for every prop, bloating the WebGL build and
/// breaking batching. Instead each source material gets at most FOUR variant
/// ASSETS (tone steps −2..+2, step 0 is the source itself), generated once
/// under <see cref="Folder"/> and shared by every prop that draws that step —
/// deterministic file names, so regeneration reuses instead of multiplying.
///
/// The tint is weathering, not disco: brighter steps lose a little saturation
/// (sun-bleached), darker steps gain a little (damp), with a hair of hue
/// drift. Strength is the pack's toneVariation knob, quantized into the file
/// name so two packs at different strengths never share a wrongly-tinted
/// variant.
///
/// Variants SNAPSHOT the source material at creation time. If a source
/// material is later edited (recolored, detail-injected), delete the
/// ToneVariants folder — the next generation rebuilds fresh copies.
/// Variants of git-ignored vendor materials carry vendor GUID references,
/// same standing policy as EnvPacks that reference vendor prefabs.
/// </summary>
public static class ToneVariants
{
    private const string Folder = "Assets/_COREHOLD/Art/Generated/ToneVariants";

    // Per tone step × strength: value (brightness) swing, opposing saturation
    // swing, and a small hue drift. [TUNE]
    private const float ValuePerStep = 0.09f;
    private const float SaturationPerStep = 0.05f;
    private const float HuePerStep = 0.004f;

    private static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

    /// <summary>
    /// Retint <paramref name="go"/>'s renderers to tone <paramref name="step"/>
    /// (−2..+2; 0 or strength 0 is a no-op) by swapping their shared materials
    /// for the shared variants. Returns how many renderers changed. Only
    /// materials exposing _BaseColor (the URP family) participate; anything
    /// else is left exactly as authored.
    /// </summary>
    public static int Apply(GameObject go, int step, float strength)
    {
        if (step == 0 || strength <= 0f)
            return 0;

        int touched = 0;
        foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material variant = GetVariant(mats[i], step, strength);
                if (variant != null && variant != mats[i])
                {
                    mats[i] = variant;
                    changed = true;
                }
            }
            if (changed)
            {
                // On a prefab instance this records a per-instance override —
                // the prefab asset itself is never touched.
                r.sharedMaterials = mats;
                touched++;
            }
        }
        return touched;
    }

    private static Material GetVariant(Material src, int step, float strength)
    {
        if (src == null || !src.HasProperty("_BaseColor"))
            return null;

        string srcPath = AssetDatabase.GetAssetPath(src);
        if (string.IsNullOrEmpty(srcPath))
            return null;   // scene-embedded or generated-at-runtime — leave it
        string guid = AssetDatabase.AssetPathToGUID(srcPath);
        if (string.IsNullOrEmpty(guid))
            return null;

        int tv = Mathf.RoundToInt(Mathf.Clamp01(strength) * 100f);
        string key = $"{Sanitize(src.name)}_{guid.Substring(0, 8)}_" +
                     $"{(step < 0 ? "m" : "p")}{Mathf.Abs(step)}_tv{tv}";
        if (Cache.TryGetValue(key, out Material cached) && cached != null)
            return cached;

        string path = $"{Folder}/{key}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            Cache[key] = existing;
            return existing;
        }

        EnsureFolder();
        var variant = new Material(src) { name = key };
        Color c = variant.GetColor("_BaseColor");
        Color.RGBToHSV(c, out float h, out float s, out float v);
        v = Mathf.Clamp01(v * (1f + ValuePerStep * step * strength));
        s = Mathf.Clamp01(s * (1f - SaturationPerStep * step * strength));
        h = Mathf.Repeat(h + HuePerStep * step * strength, 1f);
        Color tinted = Color.HSVToRGB(h, s, v);
        tinted.a = c.a;
        variant.SetColor("_BaseColor", tinted);
        AssetDatabase.CreateAsset(variant, path);

        Cache[key] = variant;
        return variant;
    }

    private static void EnsureFolder()
    {
        if (AssetDatabase.IsValidFolder(Folder))
            return;
        if (!AssetDatabase.IsValidFolder("Assets/_COREHOLD/Art/Generated"))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Art", "Generated");
        AssetDatabase.CreateFolder("Assets/_COREHOLD/Art/Generated", "ToneVariants");
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "Material";
    }
}
