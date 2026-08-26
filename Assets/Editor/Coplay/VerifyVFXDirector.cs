using System.Collections;
using System.Text;
using Corehold.Systems;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Play-mode smoke test for the VFXDirector (GDD §11 done-condition: firing every
/// effect for a while leaves the pool counts stable). Enters play mode, fires every
/// pooled effect and tracers repeatedly for a few seconds, then reports the pool
/// TotalCount before/after — a stable total means no per-effect allocation.
/// </summary>
public static class VerifyVFXDirector
{
    [MenuItem("Tools/COREHOLD/Validate/Verify VFX Director (Play)", false, 27)]
    public static void Verify()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
            EditorApplication.update += WaitForPlay;
        }
        else
        {
            RunOnDirector();
        }
    }

    private static void WaitForPlay()
    {
        if (!EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode == false)
            return;

        if (Application.isPlaying)
        {
            EditorApplication.update -= WaitForPlay;
            RunOnDirector();
        }
    }

    private static void RunOnDirector()
    {
        var director = Object.FindFirstObjectByType<VFXDirector>();
        if (director == null)
        {
            Debug.LogError("[COREHOLD] Verify: no VFXDirector in the scene.");
            return;
        }

        director.StartCoroutine(Run(director));
    }

    private static IEnumerator Run(VFXDirector d)
    {
        // Prewarm baseline.
        yield return null;

        var effects = (VFXDirector.Effect[])System.Enum.GetValues(typeof(VFXDirector.Effect));

        // Warm-up at a realistic rate so pools grow to steady state.
        yield return Fire(d, effects, 4f);

        // Let everything finish and return to the pool.
        yield return new WaitForSeconds(4f);

        var before = new StringBuilder();
        int[] baseTotals = new int[effects.Length];
        for (int i = 0; i < effects.Length; i++)
        {
            baseTotals[i] = d.TotalCount(effects[i]);
            before.Append($"{effects[i]}={baseTotals[i]} ");
        }
        int baseTracers = d.TracerTotalCount;

        // Realistic sustained firing: ~8 turrets firing a couple of times a second
        // plus periodic deaths/impacts, staggered rather than every-effect-every-frame
        // (which would just measure particle lifetime, not a leak). Run TWO identical
        // windows: convergence between them (window B adds no growth) proves the pool
        // reached steady state and is not leaking.
        yield return Fire(d, effects, 8f);
        yield return new WaitForSeconds(4f);

        var mid = new StringBuilder();
        int[] midTotals = new int[effects.Length];
        for (int i = 0; i < effects.Length; i++)
        {
            midTotals[i] = d.TotalCount(effects[i]);
            mid.Append($"{effects[i]}={midTotals[i]} ");
        }
        int midTracers = d.TracerTotalCount;

        yield return Fire(d, effects, 8f);
        yield return new WaitForSeconds(4f);

        var after = new StringBuilder();
        bool converged = true;
        for (int i = 0; i < effects.Length; i++)
        {
            int now = d.TotalCount(effects[i]);
            after.Append($"{effects[i]}={now} ");
            if (now > midTotals[i]) converged = false; // grew in the 2nd window => not steady
        }
        if (d.TracerTotalCount > midTracers) converged = false;

        Debug.Log($"[COREHOLD] VFX pool baseline : {before}");
        Debug.Log($"[COREHOLD] VFX pool window A : {mid} tracers={midTracers}");
        Debug.Log($"[COREHOLD] VFX pool window B : {after} tracers={d.TracerTotalCount}");
        Debug.Log($"[COREHOLD] Active now (~0): muzzleK={d.ActiveCount(VFXDirector.Effect.MuzzleKinetic)} tracers={d.TracerActiveCount}");

        Debug.Log(converged
            ? "[COREHOLD] VFXDirector VERIFY PASS — pools converged to steady state; window B added no allocations, tracers stable."
            : "[COREHOLD] VFXDirector VERIFY WARN — a pool grew in the second window; inspect counts above.");

        EditorApplication.isPlaying = false;
    }

    // Fire effects at a realistic, staggered rate for `seconds`.
    private static IEnumerator Fire(VFXDirector d, VFXDirector.Effect[] effects, float seconds)
    {
        float t = 0f;
        float fireEvery = 0.12f;   // ~8 muzzle/impact events per second (8 turrets)
        float acc = 0f;
        int tick = 0;
        while (t < seconds)
        {
            acc += Time.deltaTime;
            while (acc >= fireEvery)
            {
                acc -= fireEvery;
                tick++;
                // Deterministic cadence so window A and B fire identically — any growth
                // between them is then a real leak, not random-fire jitter.
                d.Play(VFXDirector.Effect.MuzzleKinetic, RandomPoint());
                d.DrawTracer(RandomPoint(), RandomPoint());
                d.Play(VFXDirector.Effect.ImpactSpark, RandomPoint());
                // Counter-readable impacts (R22) fire on the same path as the neutral
                // spark, so they must be exercised here to prove they don't leak.
                if (tick % 2 == 0) d.Play(VFXDirector.Effect.ImpactStrong, RandomPoint());
                if (tick % 2 == 1) d.Play(VFXDirector.Effect.ImpactWeak, RandomPoint());
                if (tick % 3 == 0) d.Play(VFXDirector.Effect.ShieldHit, RandomPoint());
                if (tick % 3 == 0) d.Play(VFXDirector.Effect.MuzzleEnergy, RandomPoint());
                if (tick % 4 == 0) d.Play(VFXDirector.Effect.MuzzleExplosive, RandomPoint());
                if (tick % 5 == 0) d.Play(VFXDirector.Effect.ExplosionSmall, RandomPoint());
                if (tick % 7 == 0) d.Play(VFXDirector.Effect.ExplosionLarge, RandomPoint());
                if (tick % 4 == 0) d.Play(VFXDirector.Effect.EnemyDeath, RandomPoint());
                if (tick % 11 == 0) d.Play(VFXDirector.Effect.CoreHit, RandomPoint());
                if (tick % 13 == 0) d.Play(VFXDirector.Effect.BuildPuff, RandomPoint());
            }
            t += Time.deltaTime;
            yield return null;
        }
    }

    private static Vector3 RandomPoint() =>
        new Vector3(Random.Range(-10f, 10f), Random.Range(0f, 3f), Random.Range(-10f, 10f));
}
