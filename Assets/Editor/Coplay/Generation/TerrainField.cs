using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The analytic heightfield behind terrain generation (M-b). Two bands with
/// different jobs, both deterministic from the blueprint seed:
///
///   • BASE — gentle rolling (low amplitude, long wavelength) that everything
///     gameplay-relevant sits on: routes take their knot heights from it, pads
///     and the core sit on it, the high-ground bonus measures against it.
///   • RELIEF — real hills, masked to ZERO anywhere near routes, pads or the
///     core, so drama lives outside the play corridor and the balance model's
///     planar range math stays honest by construction.
///
/// The field is ANALYTIC — no Terrain asset, no colliders, no physics. The
/// generation gates sample it directly (LOS checks are height comparisons
/// along a line), the mesh builder rasterises it once, and the runtime never
/// needs it at all: heights are baked into spline knots, transforms and the
/// relief mesh; movement grade comes from spline tangents.
/// </summary>
public class TerrainField
{
    // ---- [TUNE] the shape of the world ----
    public const float BaseAmplitude = 1.2f;      // m — rolling the routes ride
    public const float BaseWavelength = 34f;      // m
    public const float ReliefAmplitude = 6.5f;    // m — hills outside the corridor
    public const float ReliefWavelength = 46f;    // m
    public const float CorridorClear = 6f;        // m — relief mask fully 0 inside
    public const float CorridorFade = 14f;        // m — …and fully 1 beyond this
    public const float CoreBasinRadius = 14f;     // m — base blends flat at the core

    private readonly int _seed;
    private readonly List<Vector3[]> _routes;
    private readonly Vector3[] _pads;
    private readonly Vector3 _core;
    private readonly float _coreShift;

    public TerrainField(int seed, List<Vector3[]> routePolylines, Vector3[] padPositions, Vector3 core)
    {
        _seed = seed;
        _routes = routePolylines ?? new List<Vector3[]>();
        _pads = padPositions ?? new Vector3[0];
        _core = core;
        // Shift the whole field so it reads EXACTLY 0 at the core: the
        // protected structure (and everything authored around it) never moves,
        // and the basin blend below pins its surroundings flat.
        _coreShift = BaseRaw(core.x, core.z);
    }

    /// <summary>The gameplay band: rolling base, pinned to exactly 0 at the
    /// core with a smooth basin around it — so the Core platform keeps its
    /// authored height and the merge zone barely moves (tangent pins hold).</summary>
    public float Base(float x, float z)
    {
        float h = BaseRaw(x, z) - _coreShift;
        float dCore = Dist2D(x, z, _core);
        if (dCore < CoreBasinRadius)
            h *= Mathf.SmoothStep(0f, 1f, dCore / CoreBasinRadius);
        return h;
    }

    /// <summary>The full visual height: base + corridor-masked relief.</summary>
    public float Height(float x, float z)
    {
        return Base(x, z) + Relief(x, z);
    }

    public float Relief(float x, float z)
    {
        float mask = CorridorMask(x, z);
        if (mask <= 0f) return 0f;
        float n = Fbm(x / ReliefWavelength, z / ReliefWavelength, _seed * 7 + 5, 3);
        // Signed → mostly-positive hills: valleys outside the corridor read as
        // holes against the flat play band, so bias upward.
        return Mathf.Max(0f, n) * ReliefAmplitude * mask;
    }

    /// <summary>0 inside the play corridor (routes/pads/core + clear), 1 well outside.</summary>
    public float CorridorMask(float x, float z)
    {
        float d = DistanceToCorridor(x, z);
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CorridorClear, CorridorFade, d));
    }

    public float DistanceToCorridor(float x, float z)
    {
        float best = Dist2D(x, z, _core);
        foreach (var pad in _pads)
            best = Mathf.Min(best, Dist2D(x, z, pad));
        foreach (var poly in _routes)
        {
            for (int i = 0; i + 1 < poly.Length; i++)
                best = Mathf.Min(best, PointSegment2D(x, z, poly[i], poly[i + 1]));
        }
        return best;
    }

    // ------------------------------------------------------------------ noise

    private float BaseRaw(float x, float z)
    {
        // Centred noise so heights roll around 0 rather than lifting the map.
        float n = Fbm(x / BaseWavelength, z / BaseWavelength, _seed * 3 + 1, 2);
        return n * BaseAmplitude;
    }

    /// <summary>Deterministic value-noise fBm in [-1,1] — hash-based, no
    /// UnityEngine.Random, no Mathf.PerlinNoise (whose tables are not a
    /// contract across platforms). Same doctrine as the pipeline's FNV draws.</summary>
    private static float Fbm(float x, float z, int seed, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += Value(x, z, seed + o * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            x *= 2.03f; z *= 2.03f;
        }
        return sum / norm;
    }

    private static float Value(float x, float z, int seed)
    {
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        float fx = x - x0, fz = z - z0;
        float sx = fx * fx * (3f - 2f * fx);
        float sz = fz * fz * (3f - 2f * fz);

        float a = Hash01(x0, z0, seed);
        float b = Hash01(x0 + 1, z0, seed);
        float c = Hash01(x0, z0 + 1, seed);
        float d = Hash01(x0 + 1, z0 + 1, seed);
        float v = Mathf.Lerp(Mathf.Lerp(a, b, sx), Mathf.Lerp(c, d, sx), sz);
        return v * 2f - 1f;
    }

    private static float Hash01(int x, int z, int seed)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h = (h ^ (uint)seed) * 16777619u;
            h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    // --------------------------------------------------------------- geometry

    private static float Dist2D(float x, float z, Vector3 p)
    {
        float dx = x - p.x, dz = z - p.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float PointSegment2D(float x, float z, Vector3 a, Vector3 b)
    {
        float abx = b.x - a.x, abz = b.z - a.z;
        float apx = x - a.x, apz = z - a.z;
        float len = abx * abx + abz * abz;
        float t = len > 0.0001f ? Mathf.Clamp01((apx * abx + apz * abz) / len) : 0f;
        float cx = a.x + abx * t, cz = a.z + abz * t;
        float dx = x - cx, dz = z - cz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
