using UnityEngine;

namespace Corehold.Data
{
    /// <summary>
    /// Tuning for the salvage kill-streak combo (roadmap R2). Rapid consecutive
    /// kills escalate a bonus on top of each kill's bounty, up to a cap; letting
    /// the window lapse resets the streak. All values are [TUNE] — the streak is
    /// an economy term and feeds the balance model (roadmap R22).
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Streak Config", fileName = "StreakConfig")]
    public class StreakConfig : ScriptableObject
    {
        [Tooltip("[TUNE] Extra salvage per streak step, as a fraction of the kill's bounty (0.05 = +5% per step past the first kill).")]
        [Range(0f, 0.5f)] public float perStepBonus = 0.05f;

        [Tooltip("[TUNE] Ceiling on the total streak bonus, as a fraction of the kill's bounty (0.5 = +50%).")]
        [Range(0f, 2f)] public float bonusCap = 0.5f;

        [Tooltip("[TUNE] Seconds of GAME time (scales with the 2x toggle, like kill pacing does) allowed between kills before the streak resets.")]
        [Range(0.25f, 10f)] public float windowSeconds = 2f;
    }
}
