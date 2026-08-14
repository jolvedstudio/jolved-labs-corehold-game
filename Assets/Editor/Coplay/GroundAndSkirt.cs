using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ground fit, distance fog and the silhouette band (roadmap R11, rescoped).
///
/// R11 was written to kill a black void with a ring of skirt geometry. Measured
/// against the live camera that premise no longer holds, and the ticket was
/// rescoped around three facts:
///
///   • The horizon is never in frame. The camera pitches 38° with a 17.5°
///     half-FOV, so the TOP of the frustum is 20.5° BELOW horizontal — the top
///     of the screen is ground, not sky. A skybox cannot help (one is already
///     assigned and simply never seen).
///   • What removes the void is ground extent, and the fix is free: the same
///     plane, scaled. No draw calls, no assets.
///   • What makes that extent read as DEPTH rather than as endless flat ground
///     is distance fog plus a few silhouettes — not a ring of geometry.
///
/// The floor extent is computed from the camera frustum rather than the design
/// box, which is the part that matters for the generator: every generated map
/// gets its own camera solve, so a design-box floor is wrong by a different
/// amount each time (roadmap R26's stage order).
/// </summary>
public static class GroundAndSkirt
{
    /// <summary>Aspects the framing is verified against (CameraFramingSetup).</summary>
    private static readonly float[] Aspects = { 16f / 9f, 16f / 10f, 20f / 9f };

    /// <summary>Metres of ground kept beyond the widest frustum corner.</summary>
    private const float EdgeMargin = 10f;

    /// <summary>A Unity Plane spans 10 m at scale 1, so half-extent = scale × 5.</summary>
    private const float PlaneHalfExtentPerScale = 5f;

    /// <summary>Fog transmittance wanted at the FAR edge of the visible ground (0 = opaque).</summary>
    private const float FarEdgeTransmittance = 0.55f;

    /// <summary>Dark blue-slate: fog should read as the field receding, not as grey haze.</summary>
    private static readonly Color FogTint = new Color(0.10f, 0.12f, 0.16f, 1f);

    [MenuItem("Tools/COREHOLD/Look/Fit Ground + Fog (R11)", false, 62)]
    public static void FitGroundAndFog()
    {
        var log = new StringBuilder();
        log.AppendLine("=== R11 ground fit + distance fog ===");

        Camera cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            Debug.LogWarning("[R11] no camera in the scene — cannot fit the ground.");
            return;
        }

        FitFloor(cam, log);
        TuneFog(cam, log);

        MarkSceneDirty(cam);
        Debug.Log(log.ToString());
    }

    // ---------------------------------------------------------------- ground

    /// <summary>
    /// Ground half-extent (X, Z) needed so the camera never sees past the plane
    /// at any verified aspect. Intersects each aspect's four frustum corner rays
    /// with y = 0 and takes the widest result, plus a margin.
    ///
    /// Public because the generator needs exactly this: fit the floor to the
    /// camera AFTER framing, per map (R26).
    /// </summary>
    public static Vector2 RequiredHalfExtent(Camera cam, out bool sawHorizon)
    {
        sawHorizon = false;
        float maxX = 0f, maxZ = 0f;
        float vHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        Vector3 pos = cam.transform.position;
        Quaternion rot = cam.transform.rotation;

        foreach (float ar in Aspects)
        {
            float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * ar);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    Vector3 dir = rot * new Vector3(sx * Mathf.Tan(hHalf), sy * Mathf.Tan(vHalf), 1f);
                    if (dir.y >= -0.0001f)
                    {
                        // This corner points at or above the horizon: it never hits
                        // the ground, so no plane can cover it. Only reachable if the
                        // camera is pitched shallower than half its FOV.
                        sawHorizon = true;
                        continue;
                    }
                    float t = -pos.y / dir.y;
                    Vector3 hit = pos + dir * t;
                    maxX = Mathf.Max(maxX, Mathf.Abs(hit.x));
                    maxZ = Mathf.Max(maxZ, Mathf.Abs(hit.z));
                }
            }
        }
        return new Vector2(maxX + EdgeMargin, maxZ + EdgeMargin);
    }

    /// <summary>
    /// Floor localScale that covers the camera's ground footprint. Scale is kept
    /// UNIFORM: a Unity Plane's UVs span 0..1 across its whole surface, so a
    /// non-uniform scale stretches the ground texture unevenly.
    /// <paramref name="fallback"/> is returned when there is no camera to fit to.
    /// </summary>
    public static Vector3 FloorScaleForCamera(Camera cam, Vector3 fallback)
    {
        if (cam == null)
            return fallback;
        Vector2 half = RequiredHalfExtent(cam, out _);
        float scale = Mathf.Ceil(Mathf.Max(half.x, half.y) / PlaneHalfExtentPerScale);
        return new Vector3(scale, 1f, scale);
    }

    private static void FitFloor(Camera cam, StringBuilder log)
    {
        // NOT a name search: vendor prefabs contain meshes called "Floor", and
        // matching one makes a prop "the ground" (see SceneQuery.FindGround).
        var floor = SceneQuery.FindGround();
        if (floor == null)
        {
            log.AppendLine("[warn] no ground found (no LevelGround marker, no root 'Floor').");
            return;
        }
        log.AppendLine($"Ground object: '{floor.name}'");

        Vector2 half = RequiredHalfExtent(cam, out bool sawHorizon);
        if (sawHorizon)
        {
            log.AppendLine("[warn] part of the frustum points at or above the horizon — " +
                           "no ground plane can cover it. A skybox becomes load-bearing here " +
                           "(this is what R15's flyover would introduce).");
        }

        Vector3 before = floor.transform.localScale;
        Vector3 after = FloorScaleForCamera(cam, before);
        floor.transform.position = Vector3.zero;
        floor.transform.localScale = after;

        log.AppendLine($"Frustum ground footprint: |x| <= {half.x - EdgeMargin:0.0} m, " +
                       $"|z| <= {half.y - EdgeMargin:0.0} m (+{EdgeMargin:0} m margin)");
        log.AppendLine($"Floor scale {before.x:0.##} -> {after.x:0.##} " +
                       $"(half-extent {before.x * PlaneHalfExtentPerScale:0.0} m -> " +
                       $"{after.x * PlaneHalfExtentPerScale:0.0} m)");
        if (!Mathf.Approximately(before.x, after.x) && before.x > 0.01f)
        {
            log.AppendLine($"[note] the plane grew ×{after.x / before.x:0.###}; if the ground " +
                           "material relies on UV tiling, raise its tiling by the same factor " +
                           "to hold texel density.");
        }
        EditorUtility.SetDirty(floor);
    }

    // ------------------------------------------------------------------- fog

    /// <summary>
    /// Retune the distance fog so the far ground recedes instead of hazing.
    ///
    /// Density is solved from the camera rather than hard-coded, so the same
    /// perceptual gradient survives a re-framed or generated map: with
    /// ExponentialSquared, transmittance is exp(-(ρ·d)²), so for a target
    /// transmittance T at the far ground distance d_far, ρ = √(-ln T) / d_far.
    /// </summary>
    private static void TuneFog(Camera cam, StringBuilder log)
    {
        float vHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        Vector3 pos = cam.transform.position;
        Quaternion rot = cam.transform.rotation;

        float nearDist = GroundDistance(pos, rot * new Vector3(0f, -Mathf.Tan(vHalf), 1f));
        float farDist = GroundDistance(pos, rot * new Vector3(0f, Mathf.Tan(vHalf), 1f));
        if (farDist <= 0.01f)
        {
            log.AppendLine("[warn] frustum does not reach the ground — fog left untouched.");
            return;
        }

        float density = Mathf.Sqrt(-Mathf.Log(FarEdgeTransmittance)) / farDist;

        Color beforeColor = RenderSettings.fogColor;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = FogTint;
        RenderSettings.fogDensity = density;

        log.AppendLine($"Visible ground spans {nearDist:0.0} m -> {farDist:0.0} m from the camera");
        log.AppendLine($"Fog colour {ColorText(beforeColor)} -> {ColorText(FogTint)}, " +
                       $"ExponentialSquared density {density:0.#####} " +
                       $"({(1f - FarEdgeTransmittance) * 100f:0}% fogged at the far edge)");
        log.AppendLine("[tune] colour and target transmittance are starting values — " +
                       "eyeball them against the field and adjust in Lighting > Environment.");
    }

    private static float GroundDistance(Vector3 pos, Vector3 dir)
    {
        if (dir.y >= -0.0001f)
            return 0f;
        return (pos + dir * (-pos.y / dir.y) - pos).magnitude;
    }

    private static string ColorText(Color c) => $"({c.r:0.##}, {c.g:0.##}, {c.b:0.##})";

    // ------------------------------------------------------- silhouette band

    private const string CreepyRoot = "Assets/Vendor/Creepy_Cat/3D Scifi Kit Vol 4/Prefabs/";

    /// <summary>
    /// Large shapes placed beyond the playfield purely to break the far horizon.
    /// Normalized Z (0 = near frustum edge, 1 = far edge) so the band lands
    /// correctly on any camera, generated maps included.
    /// </summary>
    private static readonly (string[] candidates, float normX, float normZ, float scale)[] Band =
    {
        (new[] { CreepyRoot + "Props/Machine/P_Wind_Turbine_01.prefab" },            -0.55f, 0.88f, 3.0f),
        (new[] { CreepyRoot + "Props/Machine/P_Solar_Power_01.prefab" },             -0.15f, 0.96f, 3.5f),
        (new[] { CreepyRoot + "Props/Container & Crate/P_Tank_Cistern_01.prefab" },   0.25f, 0.90f, 2.5f),
        (new[] { CreepyRoot + "Props/Machine/P_Pumping_Station_01.prefab" },          0.60f, 0.94f, 1.2f),
    };

    [MenuItem("Tools/COREHOLD/Look/Build Silhouette Band (R11)", false, 63)]
    public static void BuildSilhouetteBand()
    {
        Camera cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            Debug.LogWarning("[R11] no camera in the scene — cannot place the silhouette band.");
            return;
        }

        var log = new StringBuilder();
        log.AppendLine("=== R11 silhouette band ===");

        var prior = SceneLookup.Find("SilhouetteBand");
        if (prior != null)
            Object.DestroyImmediate(prior);

        var root = new GameObject("SilhouetteBand");
        Undo.RegisterCreatedObjectUndo(root, "Build Silhouette Band");

        // Ground footprint, so the band sits just inside the far edge.
        float vHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        Vector3 pos = cam.transform.position;
        Quaternion rot = cam.transform.rotation;
        Vector3 nearHit = GroundHit(pos, rot * new Vector3(0f, -Mathf.Tan(vHalf), 1f));
        Vector3 farHit = GroundHit(pos, rot * new Vector3(0f, Mathf.Tan(vHalf), 1f));
        float widestHalf = RequiredHalfExtent(cam, out _).x - EdgeMargin;

        int placed = 0;
        foreach (var (candidates, normX, normZ, scale) in Band)
        {
            float z = Mathf.Lerp(nearHit.z, farHit.z, normZ);
            float x = pos.x + normX * widestHalf;
            GameObject go = PlaceFirstAvailable(candidates, root.transform,
                                                new Vector3(x, 0f, z), scale, log);
            if (go == null)
                continue;
            RecedeIntoBackground(go);
            placed++;
        }

        log.AppendLine($"Placed {placed}/{Band.Length} silhouettes between z {Mathf.Lerp(nearHit.z, farHit.z, 0.88f):0.0} " +
                       $"and {Mathf.Lerp(nearHit.z, farHit.z, 0.96f):0.0}");
        log.AppendLine("Each is lightmap-excluded, shadow-free and darkened via a property block " +
                       "(no vendor material is edited).");

        MarkSceneDirty(cam);
        Debug.Log(log.ToString());
    }

    private static Vector3 GroundHit(Vector3 pos, Vector3 dir)
    {
        if (dir.y >= -0.0001f)
            return pos;
        return pos + dir * (-pos.y / dir.y);
    }

    private static GameObject PlaceFirstAvailable(string[] candidates, Transform parent,
                                                  Vector3 position, float scale, StringBuilder log)
    {
        foreach (string path in candidates)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            log.AppendLine($"[ok] {System.IO.Path.GetFileNameWithoutExtension(path)} at " +
                           $"({position.x:0.0}, {position.z:0.0}) ×{scale}");
            return go;
        }
        log.AppendLine($"[MANUAL] none of {candidates.Length} candidate prefab(s) resolved " +
                       $"(Assets/Vendor is git-ignored) — place a silhouette here by hand.");
        return null;
    }

    /// <summary>
    /// Push a prop into the background without touching shared vendor materials:
    /// no lightmap contribution, no shadows, and a dark tint through a property
    /// block so it reads behind the playfield.
    /// </summary>
    private static void RecedeIntoBackground(GameObject go)
    {
        GameObjectUtility.SetStaticEditorFlags(go, 0);
        var block = new MaterialPropertyBlock();
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.GetPropertyBlock(block);
            block.SetColor("_BaseColor", new Color(0.35f, 0.38f, 0.45f, 1f));
            block.SetColor("_Color", new Color(0.35f, 0.38f, 0.45f, 1f));
            r.SetPropertyBlock(block);
        }
    }

    private static void MarkSceneDirty(Camera cam)
    {
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
    }
}
