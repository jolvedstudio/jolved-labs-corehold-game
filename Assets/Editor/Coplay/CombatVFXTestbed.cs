using System.Collections;
using Corehold.Data;
using Corehold.Systems;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A visual, play-mode testbed for the tower→enemy combat VFX (R22 — the counter
/// system's readability). Where <see cref="VerifyVFXDirector"/> proves the pools do
/// not leak, this tool proves the effects READ correctly: it lines up one firing
/// point per <see cref="ArmourType"/> and repeatedly fires each <see cref="DamageType"/>
/// at it, driving the exact <see cref="VFXDirector.PlayImpactEffective"/> path the
/// live game uses — so a human (or a scene capture) can confirm that a countered hit
/// looks powerful, a resisted hit looks like a deflection, and a shielded hit ripples.
///
/// It needs a live <see cref="VFXDirector"/> (it creates a bare one if the scene has
/// none) and the project's DamageTable to pick effects from the real multipliers.
/// Nothing is spawned via Instantiate on a gameplay path — the effects come straight
/// from the pooled director, exactly as in a real wave.
/// </summary>
public static class CombatVFXTestbed
{
    private const string DamageTablePath = "Assets/_COREHOLD/Data/DamageTable.asset";

    // Three station positions, one per armour type, spread along X so each reaction
    // fires in its own column and reads independently.
    private static readonly Vector3[] Stations =
    {
        new Vector3(-6f, 1.5f, 0f), // Unarmoured
        new Vector3(0f, 1.5f, 0f),  // Plated
        new Vector3(6f, 1.5f, 0f),  // Shielded
    };

    [MenuItem("Tools/COREHOLD/Validate/Combat VFX Testbed (Play)", false, 28)]
    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
            EditorApplication.update += WaitForPlay;
        }
        else
        {
            Begin();
        }
    }

    private static void WaitForPlay()
    {
        if (!Application.isPlaying)
            return;
        EditorApplication.update -= WaitForPlay;
        Begin();
    }

    private static void Begin()
    {
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
        {
            var go = new GameObject("VFXDirector (Testbed)");
            director = go.AddComponent<VFXDirector>();
            Debug.LogWarning("[COREHOLD] Combat VFX Testbed: no VFXDirector in scene — created a bare one. " +
                             "Run 'Tools/COREHOLD/Scene Setup/VFX Director' first for the authored prefabs.");
        }

        DamageTable table = AssetDatabase.LoadAssetAtPath<DamageTable>(DamageTablePath);
        if (table == null)
            Debug.LogWarning($"[COREHOLD] Combat VFX Testbed: no DamageTable at {DamageTablePath} — using neutral multipliers.");

        director.StartCoroutine(Cycle(director, table));
    }

    private static IEnumerator Cycle(VFXDirector d, DamageTable table)
    {
        var armours = new[] { ArmourType.Unarmoured, ArmourType.Plated, ArmourType.Shielded };
        var damages = new[] { DamageType.Kinetic, DamageType.Energy, DamageType.Explosive };

        Debug.Log("[COREHOLD] Combat VFX Testbed running: columns = Unarmoured / Plated / Shielded, " +
                  "cycling Kinetic → Energy → Explosive. Watch which impacts read as strong / weak / shield.");

        // Run a fixed number of full sweeps so the tool is self-terminating.
        for (int sweep = 0; sweep < 6; sweep++)
        {
            foreach (var dmg in damages)
            {
                for (int a = 0; a < armours.Length; a++)
                {
                    Vector3 pos = Stations[a];
                    float mult = table != null ? table.Multiplier(dmg, armours[a]) : 1f;

                    // Muzzle + tracer from a fixed emitter to mimic a real turret shot.
                    Vector3 muzzle = pos + new Vector3(0f, 2.5f, -8f);
                    d.PlayMuzzle(dmg, muzzle, (pos - muzzle).normalized);
                    d.DrawTracer(muzzle, pos);
                    d.PlayImpactEffective(pos, mult, armours[a]);

                    Debug.Log($"[COREHOLD] {dmg} vs {armours[a]}: x{mult:0.##} " +
                              $"({(mult >= VFXDirector.StrongHitThreshold ? "STRONG" : mult <= VFXDirector.WeakHitThreshold ? "WEAK/SHIELD" : "neutral")})");
                }
                yield return new WaitForSeconds(0.8f);
            }
        }

        Debug.Log("[COREHOLD] Combat VFX Testbed complete.");
        EditorApplication.isPlaying = false;
    }
}
