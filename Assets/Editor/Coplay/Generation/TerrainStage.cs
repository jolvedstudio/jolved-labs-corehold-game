using System.Collections.Generic;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Terrain stage (M-b). Runs after Weather and BEFORE hierarchy/emission, so
/// the re-measured 3-D spline lengths flow into the balance-model export with
/// no extra plumbing.
///
/// Doctrine: THE MODEL STAYS PLANAR. Terrain is a generation-time constraint,
/// solved and gated here, then BAKED — into spline knots, transforms and one
/// relief mesh. The runtime never reads a heightfield: movement grade comes
/// from spline tangents the mover already samples.
///
///   T1  cosmetic relief outside the play corridor (masked to zero near
///       routes, pads, the Core and the air lane — see TerrainField),
///   T2  the corridor itself rides a gentle rolling band: route knots, pads,
///       ground spawners and dressing props are lifted onto it and the routes
///       re-baked (grade then drives EnemyMover's small symmetric speed term),
///   T3  an analytic sight-line gate — a hill may not hide too much of the
///       nearby route from any pad — plus a per-pad high-ground damage bonus
///       written into TowerHardpoint and EXPORTED to the model, so certified
///       margins include it.
///
/// What the model does NOT see, and why that stays honest:
///   • grade speed modulation — symmetric about 1 and clamped; over a route
///     that starts and ends on the same band the linear term cancels, leaving
///     a sub-1% NET slowdown: live play is marginally EASIER than certified,
///     so the model remains a lower bound;
///   • 3-D range vs the model's 2-D range — pads and routes ride the same
///     ±1.2 m band, so worst-case range loss is Δy²/2r (centimetres on a
///     10-20 m ring), far inside the gate-3 margins;
///   • hills never physically block fire (no terrain collider, by design) —
///     the T3 gate bounds how often a shot could even LOOK like it passes
///     through rock, and discards seeds beyond the bound.
///
/// Shared-tail safety: knot heights are a pure function of (x,z), so the
/// duplicated merge-tail knots on corridor maps get identical heights, and the
/// R7 tangent pins keep the shared curves identical exactly as on flat maps.
/// </summary>
public static class TerrainStage
{
    /// <summary>[TUNE] Socket disc radius under each lifted pad, metres —
    /// just past the 1.0 m pad visual so the slope seam hides under art.</summary>
    private const float SocketRadius = 1.15f;

    /// <summary>[TUNE] Relief mesh resolution in cells per side. 96 → 9,409
    /// vertices, inside the 16-bit index budget with headroom.</summary>
    private const int MeshCells = 96;

    /// <summary>[TUNE] T3 gate: fraction of a pad's nearby route samples the
    /// terrain may hide before the seed is discarded (R29).</summary>
    private const float MaxBlockedFraction = 0.35f;

    /// <summary>[TUNE] T3: how far around a pad "nearby route" reaches,
    /// metres — the Mortar's 20 m ring plus lane slack.</summary>
    private const float LosRadius = 22f;

    /// <summary>[TUNE] High ground: damage-bonus fraction per metre of height
    /// over the nearby lane, and its cap. 3 m up ≈ +4.5%, never above +10%.</summary>
    private const float HgPerMetre = 0.015f;
    private const float HgCap = 0.10f;

    /// <summary>[TUNE] Sight-line sampling: step along each line, the sampled
    /// interior span, and the clearance below the line that already counts as
    /// blocked (compensates the discrete sampling missing a peak).</summary>
    private const float LosStep = 2f;
    private const float LosSpanMin = 0.15f, LosSpanMax = 0.85f;
    private const float LosClearance = 0.25f;

    public static GenerationPipeline.StageResult Run(GenerationPipeline.Context ctx)
    {
        if (ctx.blueprint == null || !ctx.blueprint.terrainRelief)
            return GenerationPipeline.StageResult.Skip(
                "terrainRelief off on the blueprint — classic flat map");
        if (ctx.routes == null || ctx.routes.Count == 0)
            return GenerationPipeline.StageResult.Fail("no routes to sculpt terrain around");

        GameObject floor = SceneQuery.FindGround();
        if (floor == null)
            return GenerationPipeline.StageResult.Fail(
                "no ground to sculpt — the floor stage left no Floor");
        var floorRenderers = floor.GetComponentsInChildren<Renderer>();
        if (floorRenderers.Length == 0)
            return GenerationPipeline.StageResult.Fail("the ground has no renderer to measure");
        Bounds bounds = floorRenderers[0].bounds;
        for (int i = 1; i < floorRenderers.Length; i++)
            bounds.Encapsulate(floorRenderers[i].bounds);

        // ---- the field: corridor polylines sampled FLAT (pre-lift) ----------
        var polylines = new List<Vector3[]>();
        foreach (var route in ctx.routes)
            polylines.Add(SampleRoute(route, 2f));
        // The air lane flies level at ~4 m. A relief hill under it would read
        // as a collision nobody coded, so the lane is masked flat like a route.
        if (ctx.blueprint.airCorridor)
            polylines.Add(new[] { ctx.layout.airSpawn, ctx.coreTarget.position });

        var padGizmos = SceneQuery.InActiveScene<HardpointCoverageGizmo>();
        var padPositions = new Vector3[padGizmos.Length];
        for (int i = 0; i < padGizmos.Length; i++)
            padPositions[i] = padGizmos[i].transform.position;

        int seed = unchecked((int)GenerationPipeline.Fnv1a(ctx.blueprint.randomSeed, "terrain"));
        var field = new TerrainField(seed, polylines, padPositions, ctx.coreTarget.position);

        // ---- T2: lift route knots onto the band, re-bake the splines --------
        int knots = 0;
        float loY = float.MaxValue, hiY = float.MinValue;
        foreach (var route in ctx.routes)
        {
            var so = new SerializedObject(route);
            SerializedProperty wps = so.FindProperty("waypoints");
            if (wps == null || !wps.isArray)
                return GenerationPipeline.StageResult.Fail(
                    $"'{route.name}' has no waypoints array — PathRoute contract changed?");
            for (int i = 0; i < wps.arraySize; i++)
            {
                var t = wps.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                if (t == null)
                    continue;
                Vector3 p = t.position;
                p.y += field.Base(p.x, p.z);
                t.position = p;
                knots++;
                loY = Mathf.Min(loY, p.y);
                hiY = Mathf.Max(hiY, p.y);
            }
            // The hash guard sees the moved waypoints and re-bakes the spline;
            // Length is 3-D from here on, and StEmitLevel exports THAT.
            route.RecomputeNow();
        }

        // ---- T2: pads onto the band, with a socket burying each seam --------
        Material groundMat = floorRenderers[0].sharedMaterial;
        foreach (var g in padGizmos)
        {
            Vector3 p = g.transform.position;
            p.y += field.Base(p.x, p.z);
            g.transform.position = p;

            var socket = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            socket.name = $"Socket_{g.name}";
            Object.DestroyImmediate(socket.GetComponent<Collider>()); // taps belong to the pad's sphere
            socket.transform.SetParent(g.transform, true);
            socket.transform.position = new Vector3(p.x, p.y + 0.02f - 0.55f, p.z);
            socket.transform.localScale = new Vector3(SocketRadius * 2f, 0.55f, SocketRadius * 2f);
            if (groundMat != null)
                socket.GetComponent<MeshRenderer>().sharedMaterial = groundMat;
            Undo.RegisterCreatedObjectUndo(socket, "Generate Level");
        }

        // ---- T2: ground spawners follow their route starts ------------------
        int spawners = 0;
        foreach (var sp in SceneQuery.InActiveScene<Corehold.Core.Spawner>())
        {
            if (sp.name == "Spawner_Air")
                continue; // air keeps its authored altitude — flight is level by design
            Vector3 p = sp.transform.position;
            p.y += field.Base(p.x, p.z);
            sp.transform.position = p;
            spawners++;
        }

        // ---- T2: dressing props ride the FULL height (they live in relief) --
        int props = 0;
        if (ctx.levelContainer != null)
        {
            foreach (var prop in ctx.levelContainer.GetComponentsInChildren<Corehold.Systems.PlacedProp>())
            {
                Vector3 p = prop.transform.position;
                p.y += field.Height(p.x, p.z);
                prop.transform.position = p;
                props++;
            }
        }

        // ---- T1: the relief mesh replaces the flat floor visually -----------
        float uvPerMetre = ctx.theme != null && ctx.theme.groundTilingPerMetre > 0f
            ? ctx.theme.groundTilingPerMetre
            : 0.08f;
        var meshGo = new GameObject("TerrainRelief");
        meshGo.transform.SetParent(SceneContainers.Ensure("_Level"), false);
        Mesh reliefMesh = BuildMesh(field, bounds, uvPerMetre);
        meshGo.AddComponent<MeshFilter>().sharedMesh = reliefMesh;
        // CAMERA OCCLUSION ONLY: the Cinemachine deoccluder on the turret-cam
        // rigs needs the hills to exist physically or it cannot push the camera
        // clear of them. Nothing gameplay-side raycasts the Default layer (taps
        // are Hardpoint-masked, combat is pure math), so this collider changes
        // no behaviour beyond the cameras.
        meshGo.AddComponent<MeshCollider>().sharedMesh = reliefMesh;
        var mr = meshGo.AddComponent<MeshRenderer>();

        // M-d terrain shading: the mesh bakes valley/slope tint into vertex
        // colours, which URP Lit ignores — so the relief wears the project's
        // vertex-colour-aware terrain shader, seeded with the theme ground's
        // texture and tint. Missing shader (or texture-less ground) falls back
        // to the flat ground material: worse-looking, never broken.
        Shader terrainShader = Shader.Find("COREHOLD/Terrain Lit");
        if (terrainShader != null)
        {
            var tMat = new Material(terrainShader) { name = "TerrainRelief(Baked)" };
            if (groundMat != null)
            {
                Texture baseTex =
                    groundMat.HasProperty("_BaseMap") && groundMat.GetTexture("_BaseMap") != null
                        ? groundMat.GetTexture("_BaseMap")
                        : groundMat.HasProperty("_MainTex") ? groundMat.GetTexture("_MainTex") : null;
                if (baseTex != null)
                    tMat.SetTexture("_BaseMap", baseTex);
                if (groundMat.HasProperty("_BaseColor"))
                    tMat.SetColor("_BaseColor", groundMat.GetColor("_BaseColor"));
                else if (groundMat.HasProperty("_Color"))
                    tMat.SetColor("_BaseColor", groundMat.GetColor("_Color"));
            }

            // Near-field detail (M-d/POV): the theme's texture, or a generated
            // seamless noise so it works with zero assets. The texture is
            // scene-embedded like the mesh and material.
            float detailStrength = ctx.theme != null ? ctx.theme.groundDetailStrength : 0.35f;
            float detailScale = ctx.theme != null ? ctx.theme.groundDetailScale : 9f;
            Texture2D detail = ctx.theme != null && ctx.theme.groundDetail != null
                ? ctx.theme.groundDetail
                : BuildDetailNoise();
            tMat.SetTexture("_DetailMap", detail);
            tMat.SetFloat("_DetailScale", Mathf.Max(1f, detailScale));
            tMat.SetFloat("_DetailStrength", Mathf.Clamp01(detailStrength));

            mr.sharedMaterial = tMat;
        }
        else if (groundMat != null)
        {
            mr.sharedMaterial = groundMat;
        }
        Undo.RegisterCreatedObjectUndo(meshGo, "Generate Level");

        // The flat floor becomes the void-catcher: dropped below the deepest
        // valley so it never pokes through the mesh, kept so any seam at the
        // mesh edge shows ground rather than skybox.
        Vector3 fp = floor.transform.position;
        fp.y -= 3f;
        floor.transform.position = fp;

        // ---- T3: sight-line gate + high-ground bonus ------------------------
        var samples3 = new List<Vector3>();
        foreach (var route in ctx.routes)
            samples3.AddRange(SampleRoute(route, 2f)); // post-lift: 3-D now

        float worstBlocked = 0f;
        string worstPad = "—";
        float hgMax = 0f;
        int hgAwarded = 0;
        foreach (var g in padGizmos)
        {
            Vector3 p = g.transform.position;
            var muzzle = new Vector3(p.x, p.y + HardpointCoverageGizmo.MuzzleHeight, p.z);

            int near = 0, blocked = 0;
            float laneYSum = 0f;
            foreach (var s in samples3)
            {
                float dx = s.x - p.x, dz = s.z - p.z;
                if (dx * dx + dz * dz > LosRadius * LosRadius)
                    continue;
                near++;
                laneYSum += s.y;
                if (SightBlocked(field, muzzle, s + Vector3.up * HardpointCoverageGizmo.TargetHeight))
                    blocked++;
            }

            float frac = near > 0 ? blocked / (float)near : 0f;
            if (frac > worstBlocked)
            {
                worstBlocked = frac;
                worstPad = g.name;
            }
            if (frac > MaxBlockedFraction)
                return GenerationPipeline.StageResult.Fail(
                    $"terrain hides {frac:P0} of the route within {LosRadius:0} m of {g.name} " +
                    $"(limit {MaxBlockedFraction:P0}) — reseed (R29)");

            float hg = near > 0
                ? Mathf.Clamp((p.y - laneYSum / near) * HgPerMetre, 0f, HgCap)
                : 0f;
            var pad = g.GetComponent<TowerHardpoint>();
            if (pad != null)
            {
                var padSo = new SerializedObject(pad);
                SerializedProperty hgProp = padSo.FindProperty("highGroundBonus");
                if (hgProp == null)
                    return GenerationPipeline.StageResult.Fail(
                        "TowerHardpoint has no highGroundBonus field — M-b contract changed?");
                hgProp.floatValue = hg;
                padSo.ApplyModifiedPropertiesWithoutUndo();
                if (hg > 0f)
                {
                    hgAwarded++;
                    hgMax = Mathf.Max(hgMax, hg);
                }
            }
        }

        return GenerationPipeline.StageResult.Ok(
            $"{knots} knots onto the band (y {loY:0.0#}..{hiY:0.0#} m), {padGizmos.Length} pads " +
            $"socketed, {spawners} spawner(s) and {props} prop(s) re-projected; relief mesh " +
            $"{MeshCells}×{MeshCells} over {bounds.size.x:0}×{bounds.size.z:0} m; worst sight loss " +
            $"{worstBlocked:P0} at {worstPad} (limit {MaxBlockedFraction:P0}); high ground on " +
            $"{hgAwarded} pad(s), max +{hgMax * 100f:0.#}% dmg (exported to the model)");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Centreline samples every ~<paramref name="step"/> metres,
    /// endpoints included. Flat before the lift, 3-D after — same call.</summary>
    private static Vector3[] SampleRoute(Corehold.Core.PathRoute route, float step)
    {
        int n = Mathf.Max(2, Mathf.CeilToInt(route.Length / step) + 1);
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
            pts[i] = route.SamplePosition(route.Length * i / (n - 1f), out _);
        return pts;
    }

    /// <summary>
    /// True when the terrain rises into the muzzle→target line. Interior span
    /// only (endpoints sit ON the field by construction), stepped at LosStep,
    /// counting anything within LosClearance of the line as blocked so a peak
    /// between samples cannot slip through.
    /// </summary>
    private static bool SightBlocked(TerrainField field, Vector3 from, Vector3 to)
    {
        float dx = to.x - from.x, dz = to.z - from.z;
        float horiz = Mathf.Sqrt(dx * dx + dz * dz);
        if (horiz < 0.5f)
            return false;
        int steps = Mathf.Max(2, Mathf.CeilToInt(horiz / LosStep));
        for (int i = 0; i <= steps; i++)
        {
            float t = Mathf.Lerp(LosSpanMin, LosSpanMax, i / (float)steps);
            float x = from.x + dx * t;
            float z = from.z + dz * t;
            float lineY = Mathf.Lerp(from.y, to.y, t);
            if (field.Height(x, z) > lineY - LosClearance)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Rasterise the field once over the floor bounds. World-space vertices
    /// (the parent container is pinned at the origin), UVs in world metres ×
    /// the theme's tiles-per-metre so texel density matches the flat floor it
    /// replaces.
    /// </summary>
    private static Mesh BuildMesh(TerrainField field, Bounds bounds, float uvPerMetre)
    {
        int n = MeshCells;
        var verts = new Vector3[(n + 1) * (n + 1)];
        var uvs = new Vector2[verts.Length];
        var colors = new Color[verts.Length];
        var tris = new int[n * n * 6];

        Vector3 min = bounds.min;
        float sx = bounds.size.x / n, sz = bounds.size.z / n;
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                float x = min.x + i * sx;
                float z = min.z + j * sz;
                int v = j * (n + 1) + i;
                float h = field.Height(x, z);
                verts[v] = new Vector3(x, h, z);
                uvs[v] = new Vector2(x * uvPerMetre, z * uvPerMetre);
                colors[v] = TintAt(field, x, z, h);
            }
        }

        int t = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * (n + 1) + i;
                tris[t++] = v;
                tris[t++] = v + n + 1;
                tris[t++] = v + 1;
                tris[t++] = v + 1;
                tris[t++] = v + n + 1;
                tris[t++] = v + n + 2;
            }
        }

        var mesh = new Mesh { name = "TerrainRelief" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.colors = colors;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Generated seamless grayscale noise for the shader's near-field detail
    /// slot — works with zero assets, 16 KB, scene-embedded. Fixed lattice and
    /// seed (content-identical on every map): wrap-around value noise, two
    /// octaves, centred on 0.5 so the overlay-multiply is neutral on average.
    /// </summary>
    private static Texture2D BuildDetailNoise()
    {
        const int size = 128;
        const int cells = 16;   // lattice period — wrapping makes the tile seamless
        var tex = new Texture2D(size, size, TextureFormat.RGB24, true, true)
        {
            name = "TerrainDetail(Noise)",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };

        float Hash(int x, int y, int s)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)(x & (cells - 1))) * 16777619u;
                h = (h ^ (uint)(y & (cells - 1))) * 16777619u;
                h = (h ^ (uint)s) * 16777619u;
                h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
                return (h & 0xFFFF) / 65535f;
            }
        }

        float Value(float fx, float fy, int s)
        {
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(x0, y0, s), b = Hash(x0 + 1, y0, s);
            float c = Hash(x0, y0 + 1, s), d = Hash(x0 + 1, y0 + 1, s);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x * (cells / (float)size);
                float v = y * (cells / (float)size);
                float n = Value(u, v, 11) * 0.65f + Value(u * 2f, v * 2f, 29) * 0.35f;
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + (n - 0.5f) * 0.9f) * 255f), 0, 255);
                pixels[y * size + x] = new Color32(g, g, g, 255);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(true, false);
        return tex;
    }

    /// <summary>
    /// The baked terrain tint (M-d), computed from the analytic field so it is
    /// deterministic and free: valleys of the rolling band darken slightly
    /// (moisture/shadow), steep relief faces darken and desaturate toward rock,
    /// crests keep near-full albedo. Multiplied over the ground texture by the
    /// terrain shader — a flat map has no relief mesh, so nothing else changes.
    /// </summary>
    private static Color TintAt(TerrainField field, float x, float z, float h)
    {
        const float e = 0.75f;
        float dhx = field.Height(x + e, z) - field.Height(x - e, z);
        float dhz = field.Height(x, z + e) - field.Height(x, z - e);
        float ny = 2f * e / Mathf.Sqrt(dhx * dhx + dhz * dhz + 4f * e * e);
        float steep = Mathf.Clamp01((1f - ny) * 2.2f);

        float baseY = field.Base(x, z);
        float valley = Mathf.Clamp01(-baseY / TerrainField.BaseAmplitude);
        float crest = Mathf.Clamp01((h - baseY) / TerrainField.ReliefAmplitude);

        Color c = Color.white;
        c = Color.Lerp(c, new Color(0.72f, 0.74f, 0.78f), valley * 0.8f);
        c = Color.Lerp(c, new Color(0.55f, 0.53f, 0.50f), steep);
        c = Color.Lerp(c, Color.white, crest * 0.25f);
        return c;
    }
}
