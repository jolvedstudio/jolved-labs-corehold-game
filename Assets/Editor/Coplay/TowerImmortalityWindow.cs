using System.Linq;
using Corehold.Data;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Clickable front-end for <see cref="TowerImmortality"/> — the same per-type
/// battleplay cheat the debug console drives with G / ⇧G, as a list you can
/// see and tick.
///
/// It deliberately edits RUNTIME state, never an asset. The obvious
/// alternative — a serialized "immortal" bool on TowerDefinition — was
/// rejected: those are shipped data assets, so the flag would be saved,
/// committed and built, and one forgotten tick would make a real player's
/// turrets invincible on every machine. A runtime registry cannot leak that
/// way: it dies with the play session.
///
/// The direct consequence is that this window only works IN PLAY MODE (Unity
/// reloads the domain when you press Play, which clears every static). That is
/// the honest trade for a cheat that cannot escape into the build, and the
/// window says so rather than offering dead checkboxes.
/// </summary>
public class TowerImmortalityWindow : EditorWindow
{
    private Vector2 _scroll;

    [MenuItem("Tools/COREHOLD/Debug/Turret Immortality", false, 60)]
    public static void Open()
    {
        var w = GetWindow<TowerImmortalityWindow>("Immortality");
        w.minSize = new Vector2(320f, 260f);
        w.Show();
    }

    private void OnInspectorUpdate()
    {
        // Live-follow the console: G / ⇧G and this list drive one registry.
        if (Application.isPlaying)
            Repaint();
    }

    private void OnGUI()
    {
        GUILayout.Label("Turret immortality — by type", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Testing aid. Immortal turrets are still acquired, aimed at and hit — only the health " +
            "subtraction is skipped, so enemy behaviour and DPS-on-target stay exactly as in a real " +
            "run. Turning a type ON heals its live turrets.",
            MessageType.Info);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Enter PLAY MODE to use this. The state is runtime-only by design — it is never " +
                "written to a TowerDefinition asset, so it can never be committed or shipped. " +
                "Entering play reloads the domain and clears it.",
                MessageType.Warning);
            return;
        }

        TowerDefinition[] roster = RosterInPlay();
        if (roster.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "No turret roster in the running scene (UITheme.turrets is empty). Run " +
                "Tools → COREHOLD → Scene Setup → Build Real UI on this scene.",
                MessageType.Warning);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("All immortal"))
                TowerImmortality.SetAll(roster, true);
            if (GUILayout.Button("None"))
                TowerImmortality.Clear();
        }

        EditorGUILayout.Space(4f);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (TowerDefinition def in roster)
        {
            if (def == null)
                continue;
            bool on = TowerImmortality.IsImmortal(def);
            string label = string.IsNullOrEmpty(def.displayName) ? def.id : def.displayName;
            bool now = EditorGUILayout.ToggleLeft(new GUIContent($"{label}   ({def.id})", def.description),
                                                  on);
            if (now != on)
                TowerImmortality.Toggle(def);   // heals live turrets when switched on
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.LabelField("Immortal now", TowerImmortality.Describe(), EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.LabelField(
            "A red banner shows in-game while any type is immortal — a cheat that changes what " +
            "survives a wave must never be forgettable while judging balance.",
            EditorStyles.wordWrappedMiniLabel);
    }

    /// <summary>The running scene's roster (the runtime mirror of RosterRegistry),
    /// falling back to whatever definitions live turrets carry.</summary>
    private static TowerDefinition[] RosterInPlay()
    {
        var theme = Corehold.UI.UITheme.Instance;
        if (theme != null && theme.turrets != null && theme.turrets.Length > 0)
            return theme.turrets;

        return Tower.Live
            .Where(t => t != null && t.Definition != null)
            .Select(t => t.Definition)
            .Distinct()
            .OrderBy(d => d.menuOrder)
            .ThenBy(d => d.name, System.StringComparer.Ordinal)
            .ToArray();
    }
}
