using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scene-scoped component lookup for the generator and its validators.
///
/// <c>Object.FindObjectsByType</c> reaches across EVERY loaded scene — and in the
/// editor that includes a second scene someone left open, an additively loaded
/// scene, and prefab-stage preview scenes. For a validator that is not a detail:
/// a gate must judge the map the pipeline just built, not every object loaded in
/// the editor.
///
/// This bit for real. A parity rebuild placed 8 hardpoints and the coverage gate
/// counted 16 — the shipped map's own 8 pads, still loaded, were being censused
/// as part of the generated map, and the class mix came out at exactly double
/// (6/4/4/2 against a 3/2/2/1 blueprint). The map was correct; the question was
/// wrong. Every scene query in generation goes through here now.
/// </summary>
public static class SceneQuery
{
    /// <summary>
    /// Components of type <typeparamref name="T"/> that live in the ACTIVE scene.
    /// Objects in any other loaded scene — and hidden/DontSave objects, which
    /// belong to no scene — are excluded.
    /// </summary>
    public static T[] InActiveScene<T>() where T : Component
    {
        return InActiveScene<T>(out _);
    }

    /// <summary>
    /// As <see cref="InActiveScene{T}()"/>, reporting how many matches were
    /// DISCARDED for living elsewhere. Validators surface that count rather than
    /// silently ignoring it: "8 pads, 8 ignored in another scene" is a very
    /// different message from "8 pads", and the difference is usually a scene
    /// someone forgot was open.
    /// </summary>
    public static T[] InActiveScene<T>(out int ignoredElsewhere) where T : Component
    {
        Scene active = SceneManager.GetActiveScene();
        T[] all = Object.FindObjectsByType<T>(FindObjectsSortMode.None);

        var kept = new List<T>(all.Length);
        foreach (T c in all)
        {
            if (c == null)
                continue;
            if (c.gameObject.scene == active)
                kept.Add(c);
        }

        ignoredElsewhere = all.Length - kept.Count;
        return kept.ToArray();
    }

    /// <summary>
    /// First component of type <typeparamref name="T"/> in the ACTIVE scene, or
    /// null. The scoping matters more here than in the plural case: wiring an
    /// emitted LevelDefinition into another scene's WaveManager would look like
    /// success and silently produce a generated scene that runs nothing.
    /// </summary>
    public static T FirstInActiveScene<T>() where T : Component
    {
        Scene active = SceneManager.GetActiveScene();
        foreach (T c in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            if (c != null && c.gameObject.scene == active)
                return c;
        return null;
    }

    /// <summary>Names of matches living outside the active scene, for an actionable report.</summary>
    public static List<string> StraysOutsideActiveScene<T>() where T : Component
    {
        Scene active = SceneManager.GetActiveScene();
        var strays = new List<string>();
        foreach (T c in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
        {
            if (c == null || c.gameObject.scene == active)
                continue;
            string where = string.IsNullOrEmpty(c.gameObject.scene.name)
                ? "<no scene — hidden or DontSave object>"
                : c.gameObject.scene.name;
            strays.Add($"{c.name} (in '{where}')");
        }
        return strays;
    }
}
