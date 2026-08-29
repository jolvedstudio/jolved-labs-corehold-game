using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Look &amp; lighting stage (M-d): the visual-quality pass every generated map
/// gets, after terrain and before hierarchy/emission.
///
///   1. POST — the camera gets post-processing + SMAA, and a global Volume
///      (priority 1, the BASE layer) carries the shared profile: ACES
///      tonemapping, bloom, vignette and a light grade. The pack may override
///      the whole profile; weather grades (R13) still layer ABOVE either at
///      the applier's higher priority, exactly as before.
///   2. ROADWAYS — a translucent worn-path ribbon along every route, sampled
///      off the final (terrain-following) splines, so approaches read at a
///      glance on any ground texture. Colour per theme; alpha 0 disables.
///   3. THEME LIGHTING — sun angle/colour/intensity and base scene fog from
///      the EnvPack, both fully inert at their 0 defaults so every existing
///      pack renders exactly as it did. Fog baked here is the BASE look the
///      WeatherApplier captures and restores around presets (R11/R13
///      authority unchanged).
///
/// Purely visual: no gate reads anything this stage writes, and the balance
/// model export is untouched.
/// </summary>
public static class LookStage
{
    /// <summary>Shared base profile — the same asset the Game scene's bloom
    /// setup authors, extended here with vignette + grade. One asset, one look.</summary>
    private const string SharedProfilePath = "Assets/_COREHOLD/Settings/COREHOLD_PostFX.asset";

    /// <summary>[TUNE] Roadway band: half-width beyond the lane band, metres,
    /// lift above the ground, and sampling step along the spline.</summary>
    private const float RoadMarginBeyondLanes = 0.55f;
    private const float RoadLift = 0.05f;
    private const float RoadStep = 1.5f;

    /// <summary>Roadway colour for undressed maps (no theme drawn).</summary>
    private static readonly Color DefaultRoadColor = new Color(0.03f, 0.03f, 0.04f, 0.35f);

    public static GenerationPipeline.StageResult Run(GenerationPipeline.Context ctx)
    {
        var notes = new List<string>();

        // ---- 1. camera post + base volume ----------------------------------
        Camera cam = SceneQuery.FirstInActiveScene<Camera>();
        if (cam != null)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null)
                data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            notes.Add("camera post+SMAA on");
        }
        else
        {
            notes.Add("no camera found — post skipped");
        }

        // The BASE volume profile must own the scene's Bloom + Tonemapping (the
        // tracer glow lives here). A theme MAY supply its own full base profile,
        // but a weather GRADE profile (colour adjustments only, no Bloom/Tonemapping)
        // must never be used as the base — doing so leaves the scene with no glow.
        // Guard against that misconfiguration: accept theme.postProfile only if it
        // actually is a base profile, else fall back to the canonical shared one.
        VolumeProfile profile;
        if (ctx.theme != null && ctx.theme.postProfile != null && IsBaseProfile(ctx.theme.postProfile))
        {
            profile = ctx.theme.postProfile;
        }
        else
        {
            if (ctx.theme != null && ctx.theme.postProfile != null)
                notes.Add($"theme profile '{ctx.theme.postProfile.name}' has no Bloom/Tonemapping — treating as a weather grade and using the shared base instead");
            profile = EnsureSharedProfile();
        }
        Volume volume = SceneQuery.FirstInActiveScene<Volume>();
        if (volume == null)
        {
            var go = new GameObject("Global Volume");
            go.transform.SetParent(SceneContainers.Ensure("_Rendering"), false);
            volume = go.AddComponent<Volume>();
            Undo.RegisterCreatedObjectUndo(go, "Generate Level");
        }
        volume.isGlobal = true;
        volume.priority = 1f; // BASE layer — the weather applier's grade volume sits above
        volume.sharedProfile = profile;
        notes.Add($"base volume profile '{profile.name}'");

        // ---- 2. roadways ----------------------------------------------------
        Color road = ctx.theme != null ? ctx.theme.roadwayColor : DefaultRoadColor;
        if (road.a > 0.01f && ctx.routes != null && ctx.routes.Count > 0)
        {
            var roadsRoot = new GameObject("Roadways");
            roadsRoot.transform.SetParent(
                ctx.levelContainer != null ? ctx.levelContainer : SceneContainers.Ensure("_Level"), false);
            Undo.RegisterCreatedObjectUndo(roadsRoot, "Generate Level");

            Material roadMat = MakeRoadMaterial(road);
            int ribbons = 0;
            foreach (var route in ctx.routes)
            {
                if (BuildRibbon(route, roadsRoot.transform, roadMat))
                    ribbons++;
            }
            notes.Add($"{ribbons} roadway ribbon(s)");
        }
        else
        {
            notes.Add("roadways off (alpha 0 or no routes)");
        }

        // ---- 3. theme lighting + base fog ----------------------------------
        if (ctx.theme != null && ctx.theme.sunIntensity > 0f)
        {
            Light sun = FindSun();
            if (sun != null)
            {
                sun.transform.rotation = Quaternion.Euler(ctx.theme.sunAngles.x, ctx.theme.sunAngles.y, 0f);
                sun.color = ctx.theme.sunColor;
                sun.intensity = ctx.theme.sunIntensity;
                notes.Add($"sun {ctx.theme.sunAngles.x:0}°/{ctx.theme.sunAngles.y:0}° ×{ctx.theme.sunIntensity:0.##}");
            }
            else
            {
                notes.Add("theme sun set but no directional light found");
            }
        }

        if (ctx.theme != null && ctx.theme.fogDensity > 0f)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = ctx.theme.fogColor;
            RenderSettings.fogDensity = ctx.theme.fogDensity;
            notes.Add($"base fog ρ{ctx.theme.fogDensity:0.####} (weather still layers over it)");
        }

        // Per-theme skybox: RenderSettings IS the scene's Lighting-settings
        // skybox, and it is per-scene state — setting it here and saving the
        // scene gives each level its own sky with nothing global overridden.
        // The WeatherApplier never touches the skybox, so it survives presets.
        if (ctx.theme != null && ctx.theme.skyboxMaterial != null)
        {
            RenderSettings.skybox = ctx.theme.skyboxMaterial;
            // The ambient probe derives from the sky — refresh it so ambient
            // light matches the new horizon rather than the default's.
            DynamicGI.UpdateEnvironment();
            if (cam != null)
                cam.clearFlags = CameraClearFlags.Skybox; // a solid-colour camera would hide the sky
            notes.Add($"skybox '{ctx.theme.skyboxMaterial.name}' (+ambient refresh)");
        }

        return GenerationPipeline.StageResult.Ok(string.Join(", ", notes));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The shared base profile, extended in place: the Game scene's bloom
    /// setup already authors Bloom + ACES here, so those values are respected
    /// (created only when absent) and only the M-d additions — vignette and a
    /// light grade — are ensured on top. One asset serves every scene.
    /// </summary>
    /// <summary>
    /// A profile qualifies as a scene BASE (rather than a weather grade) when it
    /// carries Bloom and/or Tonemapping — the components that define the tracer
    /// glow. Weather grades declare only colour adjustments / white balance /
    /// vignette, so they fail this test and are never accepted as the base.
    /// </summary>
    private static bool IsBaseProfile(VolumeProfile profile)
    {
        return profile != null &&
               (profile.Has<Bloom>() || profile.Has<Tonemapping>());
    }

    private static VolumeProfile EnsureSharedProfile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(SharedProfilePath);
        if (profile == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/_COREHOLD/Settings"))
                AssetDatabase.CreateFolder("Assets/_COREHOLD", "Settings");
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, SharedProfilePath);
        }

        if (!profile.TryGet(out Bloom bloom))
        {
            bloom = profile.Add<Bloom>(true);
            bloom.threshold.overrideState = true; bloom.threshold.value = 1.1f;
            bloom.intensity.overrideState = true; bloom.intensity.value = 0.9f;
            bloom.scatter.overrideState = true; bloom.scatter.value = 0.6f;
        }
        bloom.active = true;

        if (!profile.TryGet(out Tonemapping tone))
        {
            tone = profile.Add<Tonemapping>(true);
            tone.mode.overrideState = true; tone.mode.value = TonemappingMode.ACES;
        }
        tone.active = true;

        if (!profile.TryGet(out Vignette vignette))
        {
            vignette = profile.Add<Vignette>(true);
            vignette.intensity.overrideState = true; vignette.intensity.value = 0.22f;
            vignette.smoothness.overrideState = true; vignette.smoothness.value = 0.42f;
        }
        vignette.active = true;

        if (!profile.TryGet(out ColorAdjustments grade))
        {
            grade = profile.Add<ColorAdjustments>(true);
            grade.contrast.overrideState = true; grade.contrast.value = 8f;
            grade.saturation.overrideState = true; grade.saturation.value = 6f;
        }
        grade.active = true;

        EditorUtility.SetDirty(profile);
        return profile;
    }

    /// <summary>URP/Unlit configured transparent — the documented property/keyword
    /// set BaseShaderGUI uses, so the material survives serialization.</summary>
    private static Material MakeRoadMaterial(Color color)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Roadway(Baked)" };
        mat.SetFloat("_Surface", 1f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.SetColor("_BaseColor", color);
        return mat;
    }

    /// <summary>
    /// One route's worn-path band: centreline samples every ~1.5 m, spanning
    /// the lane band plus a margin, floating just above the ground. The spline
    /// is already terrain-following (M-b bakes knot heights), so the ribbon is
    /// too — and on a flat map it simply lies flat.
    /// </summary>
    private static bool BuildRibbon(Corehold.Core.PathRoute route, Transform parent, Material mat)
    {
        if (route == null || route.Length < 2f)
            return false;

        int samples = Mathf.Max(2, Mathf.CeilToInt(route.Length / RoadStep) + 1);
        float half = route.LaneHalfWidth + RoadMarginBeyondLanes;

        var verts = new Vector3[samples * 2];
        var tris = new int[(samples - 1) * 6];

        for (int i = 0; i < samples; i++)
        {
            float d = route.Length * i / (samples - 1f);
            Vector3 centre = route.SamplePosition(d, out Vector3 tangent);
            Vector3 right = Corehold.Core.PathRoute.HorizontalRight(tangent);
            centre.y += RoadLift;
            verts[i * 2] = centre - right * half;
            verts[i * 2 + 1] = centre + right * half;
        }

        int t = 0;
        for (int i = 0; i < samples - 1; i++)
        {
            int v = i * 2;
            tris[t++] = v;
            tris[t++] = v + 2;
            tris[t++] = v + 1;
            tris[t++] = v + 1;
            tris[t++] = v + 2;
            tris[t++] = v + 3;
        }

        var mesh = new Mesh { name = $"Roadway_{route.name}" };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject($"Roadway_{route.name}");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        Undo.RegisterCreatedObjectUndo(go, "Generate Level");
        return true;
    }

    private static Light FindSun()
    {
        foreach (var light in SceneQuery.InActiveScene<Light>())
            if (light.type == LightType.Directional)
                return light;
        return null;
    }
}
