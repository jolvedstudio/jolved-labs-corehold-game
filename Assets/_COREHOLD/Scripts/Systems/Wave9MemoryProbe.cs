#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.Towers;
using UnityEngine;
using UnityEngine.Profiling;

namespace Corehold.Systems
{
    /// <summary>
    /// Ticket 38 — Memory and texture measurement (GDD Platform block).
    ///
    /// Drives the scene into a realistic wave-9 steady state and samples the two
    /// figures the Memory Profiler reports:
    ///   • Steady-state managed heap  — Profiler.GetMonoUsedSizeLong() after a
    ///     collect, so the number is resident working set, not transient garbage.
    ///   • Total GPU texture memory   — Texture.currentTextureMemory / the
    ///     "Texture Memory" profiler counter (this is the on-device figure for the
    ///     active build target's compression format; WebGL = DXT/S3TC).
    ///
    /// It builds a turret on every hardpoint first so all turret prefabs and their
    /// textures are resident (the on-device working set), then jumps straight to
    /// wave 9 and lets the field fill to the concurrency cap before sampling.
    ///
    /// Development-build / editor only — compiled out of the ship build.
    /// </summary>
    [DisallowMultipleComponent]
    public class Wave9MemoryProbe : MonoBehaviour
    {
        [SerializeField] private float settleSeconds = 8f;
        [SerializeField] private int sampleFrames = 120;

        public static string LastReport { get; private set; }

        private IEnumerator Start()
        {
            yield return null; // let all Awake/Start run

            var gm = GameManager.Instance;
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogError("[Wave9MemoryProbe] No WaveManager found.");
                yield break;
            }

            // Enter Build and grant plenty of salvage so every pad can be filled.
            if (gm != null)
            {
                gm.ConfigureRun(Corehold.Core.Difficulty.Normal);
                gm.SetState(GameState.Build);
                gm.CoreInvulnerable = true;
                gm.AddSalvage(100000);
            }

            yield return null;

            // Fill every hardpoint with a turret so all turret prefabs/textures are
            // resident (worst realistic working set). Round-robin the definitions.
            var defs = Resources.FindObjectsOfTypeAll<TowerDefinition>();
            int builtCount = 0;
            if (defs != null && defs.Length > 0)
            {
                var pads = FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None);
                for (int i = 0; i < pads.Length; i++)
                {
                    var def = defs[i % defs.Length];
                    if (pads[i].TryBuild(def))
                        builtCount++;
                }
            }
            Debug.Log($"[Wave9MemoryProbe] Built {builtCount} turrets across hardpoints (defs available: {(defs != null ? defs.Length : 0)}).");

            yield return null;

            // Jump straight to wave 9 and start it.
            bool started = wm.JumpToWave(9);
            Debug.Log($"[Wave9MemoryProbe] JumpToWave(9) started={started}, waveCount={wm.WaveCount}.");

            // Let the field fill to the concurrency cap and reach steady state.
            float t = 0f;
            int peakLive = 0;
            while (t < settleSeconds)
            {
                peakLive = Mathf.Max(peakLive, wm.LiveCount);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            // Sample over several frames for a stable read.
            long texMemPeak = 0;
            long monoPeak = 0;
            for (int i = 0; i < sampleFrames; i++)
            {
                peakLive = Mathf.Max(peakLive, wm.LiveCount);
                texMemPeak = System.Math.Max(texMemPeak, (long)Texture.currentTextureMemory);
                monoPeak = System.Math.Max(monoPeak, Profiler.GetMonoUsedSizeLong());
                yield return null;
            }

            // Steady-state managed heap: collect first so the number is resident
            // working set, then read reserved + used.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
            yield return null;

            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long monoReserved = Profiler.GetMonoHeapSizeLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long gfxDriver = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long texMemNow = (long)Texture.currentTextureMemory;
            long texDesired = (long)Texture.desiredTextureMemory;
            long texTarget = (long)Texture.targetTextureMemory;
            long texNonStreaming = (long)Texture.nonStreamingTextureMemory;

            const double MB = 1024.0 * 1024.0;
            var sb = new StringBuilder();
            sb.AppendLine("========== TICKET 38 — WAVE 9 MEMORY MEASUREMENT ==========");
            sb.AppendLine($"Build target        : {Application.platform} (probe target = {UnityEngine.Application.platform})");
            sb.AppendLine($"Peak live enemies   : {peakLive}");
            sb.AppendLine($"Turrets built       : {builtCount}");
            sb.AppendLine("--- Managed (Mono) heap ---");
            sb.AppendLine($"  Mono used  (steady): {monoUsed / MB:0.0} MB");
            sb.AppendLine($"  Mono used  (peak)  : {monoPeak / MB:0.0} MB");
            sb.AppendLine($"  Mono heap reserved : {monoReserved / MB:0.0} MB");
            sb.AppendLine("--- GPU texture memory ---");
            sb.AppendLine($"  Texture memory now : {texMemNow / MB:0.0} MB");
            sb.AppendLine($"  Texture memory peak: {texMemPeak / MB:0.0} MB");
            sb.AppendLine($"  Non-streaming tex  : {texNonStreaming / MB:0.0} MB");
            sb.AppendLine($"  Desired / Target   : {texDesired / MB:0.0} / {texTarget / MB:0.0} MB");
            sb.AppendLine("--- Totals (context) ---");
            sb.AppendLine($"  Total reserved     : {totalReserved / MB:0.0} MB");
            sb.AppendLine($"  Total allocated    : {totalAllocated / MB:0.0} MB");
            sb.AppendLine($"  Gfx driver alloc   : {gfxDriver / MB:0.0} MB");
            sb.AppendLine("===========================================================");

            LastReport = sb.ToString();
            Debug.Log(LastReport);
        }
    }
}
#endif
