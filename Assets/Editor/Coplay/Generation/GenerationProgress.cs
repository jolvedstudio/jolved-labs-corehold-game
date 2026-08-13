using UnityEditor;

/// <summary>
/// Live progress for a generation run. The pipeline is synchronous — the
/// editor cannot repaint a window mid-run — so visibility during a run comes
/// from Unity's cancelable progress bar, which paints from inside blocking
/// code. This is that bar, plus the cancellation flag long stages poll.
///
/// Two layers of granularity:
///   • <see cref="Stage"/> — the pipeline announces each stage (coarse ticks).
///   • <see cref="Detail"/> — long stages announce progress WITHIN themselves
///     (the candidate grid, the balance-model subprocess), so the bar keeps
///     moving during work that takes seconds rather than milliseconds.
///
/// Cancellation is cooperative: pressing Cancel sets <see cref="Cancelled"/>,
/// the polling stage returns a failure, and the pipeline's normal
/// discard-on-fail path runs — a cancelled run leaves nothing behind, same as
/// a gate failure (R29).
/// </summary>
public static class GenerationProgress
{
    private static bool _active;
    private static int _stageCount = 1;
    private static int _stageIndex;
    private static string _stageTitle = "";

    public static bool Cancelled { get; private set; }

    public static void Begin(int stageCount)
    {
        _active = true;
        _stageCount = stageCount < 1 ? 1 : stageCount;
        _stageIndex = 0;
        _stageTitle = "";
        Cancelled = false;
    }

    /// <summary>Announce a stage. Returns false when the user has cancelled.</summary>
    public static bool Stage(int index, string title)
    {
        _stageIndex = index;
        _stageTitle = title;
        return Detail(title, 0f);
    }

    /// <summary>
    /// Announce progress inside the current stage (0..1). Returns false when
    /// the user has cancelled — callers should stop work and return a failure.
    /// </summary>
    public static bool Detail(string detail, float subFraction)
    {
        if (!_active)
            return true;
        if (Cancelled)
            return false;

        float fraction = (_stageIndex + (subFraction < 0f ? 0f : subFraction > 1f ? 1f : subFraction))
                         / _stageCount;
        if (EditorUtility.DisplayCancelableProgressBar(
                $"COREHOLD Generator — stage {_stageIndex + 1}/{_stageCount}",
                string.IsNullOrEmpty(detail) ? _stageTitle : detail,
                fraction))
        {
            Cancelled = true;
            return false;
        }
        return true;
    }

    public static void End()
    {
        if (_active)
            EditorUtility.ClearProgressBar();
        _active = false;
        Cancelled = false;
    }
}
