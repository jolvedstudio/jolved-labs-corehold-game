using System;

/// <summary>
/// "Am I a standalone menu action, or a step inside the generation pipeline?"
///
/// The scene-setup tools were written as menu items and behave accordingly:
/// each one OPENS the shipped Game.unity if that is not the active scene, and
/// SAVES the scene when it finishes. Both are the right convenience for a human
/// clicking a menu from anywhere in the project. Both are catastrophic inside
/// the pipeline:
///
///   • The open <b>replaced the freshly created scene with Game.unity</b>, so
///     generation carried on building into the shipped map — which already has
///     eight hardpoints. Gate 2 then counted sixteen pads with duplicate names
///     and refused the map. The generator was, quietly, editing the live scene.
///   • The save fires <c>SaveScene</c> on an untitled scene, which opens a
///     modal Save dialog mid-run and blocks the pipeline on a human.
///
/// So the tools need to know which caller they have. A scoped flag beats
/// threading a bool through six tools and two behaviours each: one place to
/// reason about, and the <see cref="IDisposable"/> scope guarantees the flag is
/// cleared even when a stage throws.
///
/// Usage:
/// <code>
/// using (GenerationDriven.Scope())
/// {
///     // every setup tool called in here operates on the ACTIVE scene and
///     // leaves saving to the pipeline's own save stage
/// }
/// </code>
/// </summary>
public static class GenerationDriven
{
    private static int _depth;

    /// <summary>True while the generation pipeline is driving the setup tools.</summary>
    public static bool Active => _depth > 0;

    /// <summary>
    /// Enter the pipeline-driven scope. Re-entrant (counted), so a nested tool
    /// call cannot clear the flag early.
    /// </summary>
    public static IDisposable Scope() => new Handle();

    private sealed class Handle : IDisposable
    {
        private bool _disposed;

        public Handle() => _depth++;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _depth = _depth > 0 ? _depth - 1 : 0;
        }
    }
}
