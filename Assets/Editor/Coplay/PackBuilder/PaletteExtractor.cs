using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The one piece of image reading allowed in-editor (EnvPack Builder L4):
/// dominant-color extraction is arithmetic, not semantics, so it needs no
/// vision model. Everything semantic about a reference image (what the shapes
/// ARE, what counts they demand) stays in the ArtTarget's authored values.
///
/// Deterministic on purpose: k-means with centroids seeded at fixed luminance
/// percentiles, a fixed iteration count, and no random draws — the same
/// references always yield the same palette, so re-running the extractor can
/// never silently reshuffle a target someone reviewed.
///
/// Reads textures by blitting through a temporary RenderTexture, which works
/// regardless of each texture's Read/Write import flag.
///
/// Writes sunColor / fogColor / groundTint / rockTint as STARTING points for a
/// human eye — fogDensity and sunAngles stay authored, because haze depth and
/// sun direction are not reliably readable from a photograph.
/// </summary>
public static class PaletteExtractor
{
    private const int Sample = 64;    // per-image downsample
    private const int K = 6;
    private const int Iterations = 12;

    [MenuItem("Tools/COREHOLD/Level/Env Pack Builder/Extract Palette From References", false, 85)]
    public static void ExtractMenu()
    {
        var target = Selection.activeObject as ArtTarget;
        if (target == null)
        {
            Debug.LogError("[Palette] Select an ArtTarget asset first.");
            return;
        }
        if (target.referenceImages == null || target.referenceImages.All(t => t == null))
        {
            Debug.LogError("[Palette] The ArtTarget has no reference images assigned.");
            return;
        }
        Debug.Log(Extract(target));
    }

    public static string Extract(ArtTarget target)
    {
        // ---- gather pixels, remembering which vertical band each came from --
        var pixels = new List<Color>();
        var rows = new List<float>();   // 0 = bottom of image, 1 = top
        foreach (Texture2D tex in target.referenceImages)
        {
            if (tex == null)
                continue;
            Color[] px = ReadPixels(tex, Sample, Sample);
            for (int y = 0; y < Sample; y++)
                for (int x = 0; x < Sample; x++)
                {
                    pixels.Add(px[y * Sample + x]);
                    rows.Add(y / (float)(Sample - 1));
                }
        }
        if (pixels.Count == 0)
            return "[Palette] No readable reference pixels.";

        // ---- deterministic k-means -----------------------------------------
        var byLum = Enumerable.Range(0, pixels.Count)
            .OrderBy(i => Lum(pixels[i])).ToArray();
        var centroids = new Color[K];
        float[] percentiles = { 0.05f, 0.25f, 0.45f, 0.65f, 0.85f, 0.97f };
        for (int k = 0; k < K; k++)
            centroids[k] = pixels[byLum[(int)((byLum.Length - 1) * percentiles[k])]];

        var assign = new int[pixels.Count];
        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < pixels.Count; i++)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int k = 0; k < K; k++)
                {
                    float d = Dist(pixels[i], centroids[k]);
                    if (d < bestD) { bestD = d; best = k; }
                }
                assign[i] = best;
            }
            var sum = new Vector3[K];
            var count = new int[K];
            for (int i = 0; i < pixels.Count; i++)
            {
                Color c = pixels[i];
                sum[assign[i]] += new Vector3(c.r, c.g, c.b);
                count[assign[i]]++;
            }
            for (int k = 0; k < K; k++)
                if (count[k] > 0)
                    centroids[k] = new Color(sum[k].x / count[k], sum[k].y / count[k], sum[k].z / count[k]);
        }

        // ---- classify: sky from the top rows, ground from the bottom --------
        int sky = MajorityCluster(assign, rows, r => r > 0.75f);
        int ground = MajorityCluster(assign, rows, r => r < 0.30f);
        int sun = Enumerable.Range(0, K).OrderByDescending(k => Lum(centroids[k])).First();
        int rock = Enumerable.Range(0, K)
            .Where(k => k != sky && k != ground)
            .OrderByDescending(k => assign.Where((a, i) => a == k &&
                rows[i] >= 0.30f && rows[i] <= 0.75f).Count())
            .DefaultIfEmpty(ground)
            .First();

        Undo.RecordObject(target, "Extract Palette");
        target.fogColor = centroids[sky];
        target.groundTint = centroids[ground];
        target.rockTint = centroids[rock];
        target.sunColor = centroids[sun];
        EditorUtility.SetDirty(target);
        AssetDatabase.SaveAssets();

        var log = new StringBuilder();
        log.AppendLine($"=== PALETTE — {pixels.Count / (Sample * Sample)} reference(s) ===");
        for (int k = 0; k < K; k++)
            log.AppendLine($"  cluster {k}: {Fmt(centroids[k])}" +
                           (k == sky ? "  ← sky → fogColor" : "") +
                           (k == ground ? "  ← ground → groundTint" : "") +
                           (k == rock ? "  ← mid-band → rockTint" : "") +
                           (k == sun ? "  ← brightest → sunColor" : ""));
        log.AppendLine("  Written to the ArtTarget as STARTING points — review by eye. " +
                       "fogDensity and sunAngles were left authored on purpose.");
        return log.ToString();
    }

    // --------------------------------------------------------------- helpers

    private static Color[] ReadPixels(Texture2D tex, int w, int h)
    {
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0);
        RenderTexture prev = RenderTexture.active;
        var tmp = new Texture2D(w, h, TextureFormat.RGB24, false);
        try
        {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply();
            return tmp.GetPixels();
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Object.DestroyImmediate(tmp);
        }
    }

    private static int MajorityCluster(int[] assign, List<float> rows,
                                       System.Func<float, bool> inBand)
    {
        var count = new int[K];
        for (int i = 0; i < assign.Length; i++)
            if (inBand(rows[i]))
                count[assign[i]]++;
        int best = 0;
        for (int k = 1; k < K; k++)
            if (count[k] > count[best])
                best = k;
        return best;
    }

    private static float Lum(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

    private static float Dist(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return dr * dr + dg * dg + db * db;
    }

    private static string Fmt(Color c) => $"({c.r:0.00}, {c.g:0.00}, {c.b:0.00})";
}
