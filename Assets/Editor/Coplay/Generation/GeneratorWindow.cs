using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The team-facing surface for level generation: pick a blueprint, see what the
/// seed will draw, see the WHOLE pipeline — every stage and every gate — press
/// Generate, watch the bar, read the transcript, press Play.
///
/// Visibility model (the pipeline is synchronous, so the window cannot repaint
/// mid-run):
///   • BEFORE a run the full stage map renders from
///     <see cref="GenerationPipeline.Stages"/> — pending rows, gates badged —
///     so the process is legible before anyone commits to it.
///   • DURING a run, Unity's cancelable progress bar (driven by
///     <see cref="GenerationProgress"/>) shows stage n/18 plus sub-stage
///     detail (candidate scoring, the balance-model subprocess), and Cancel
///     aborts through the same discard path as a gate failure.
///   • AFTER a run the map becomes the transcript: ✓/–/✗ per stage, per-stage
///     timings, gate verdicts, and a Copy Report button so a failed run can be
///     pasted into chat verbatim.
///
/// This window owns NO pipeline logic — it renders whatever the pipeline
/// declares. When a stage is added THERE, this window updates untouched.
/// </summary>
public class GeneratorWindow : EditorWindow
{
    private LevelBlueprint _blueprint;
    private readonly List<GenerationPipeline.StageRun> _results = new();
    private string _scenePath;
    private bool _ran;
    private double _totalSeconds;
    private bool _utilities;
    private Vector2 _scroll;

    private static readonly Color GateColor = new Color(1f, 0.75f, 0.25f);
    private static readonly Color FailColor = new Color(1f, 0.35f, 0.3f);
    private static readonly Color OkColor = new Color(0.4f, 0.85f, 0.5f);
    private static readonly Color PendingColor = new Color(0.6f, 0.6f, 0.6f);

    [MenuItem("Tools/COREHOLD/Level/Level Generator", false, 0)]
    public static void Open()
    {
        var w = GetWindow<GeneratorWindow>("COREHOLD Generator");
        w.minSize = new Vector2(440f, 520f);
    }

    private void OnEnable()
    {
        if (_blueprint == null)
            _blueprint = GenerateLevel.ResolveBlueprintQuiet();
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawBlueprintSection();
        if (_blueprint != null)
        {
            DrawPads();
            DrawDrawPreview();
            DrawValidation(out bool blocked);
            DrawGenerate(blocked);
            DrawStageMap();
        }
        DrawUtilities();

        EditorGUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------ sections

    private void DrawBlueprintSection()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Blueprint", EditorStyles.boldLabel);

        _blueprint = (LevelBlueprint)EditorGUILayout.ObjectField(
            "Level Blueprint", _blueprint, typeof(LevelBlueprint), false);

        if (_blueprint == null)
        {
            EditorGUILayout.HelpBox(
                "A LevelBlueprint drives everything: seed, field, routes, pads, theme pool. " +
                "Create one per map you want to generate.", MessageType.Info);
            if (GUILayout.Button("Create the shipped-map blueprint (parity target)"))
            {
                GenerateLevel.CreateShippedBlueprint();
                _blueprint = GenerateLevel.ResolveBlueprintQuiet();
            }
            return;
        }

        DrawModeSwitch();

        // Seed edits go through Undo so a designer can back out of them. On a
        // parity blueprint the seed changes nothing, so the field is disabled
        // rather than left inviting a change that does nothing.
        using (new EditorGUI.DisabledScope(_blueprint.parityLayout))
        {
            EditorGUI.BeginChangeCheck();
            int seed = EditorGUILayout.IntField(
                new GUIContent("Seed", "Every random draw derives from this. Same seed = same map, " +
                                       "same theme, same weather — on every machine."),
                _blueprint.randomSeed);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Seed +1", GUILayout.Width(70f))) seed = _blueprint.randomSeed + 1;
                if (GUILayout.Button("Random", GUILayout.Width(70f))) seed = Random.Range(1, 1_000_000);
            }
            if (EditorGUI.EndChangeCheck() && seed != _blueprint.randomSeed)
            {
                Undo.RecordObject(_blueprint, "Change blueprint seed");
                _blueprint.randomSeed = seed;
                EditorUtility.SetDirty(_blueprint);
                _ran = false;                                // stale results would mislead
            }
        }
    }

    /// <summary>
    /// THE mode switch: rebuild the shipped map, or synthesize a new one.
    ///
    /// It lives here rather than only on the asset because it is the single
    /// most consequential choice in the tool — it decides whether the seed and
    /// half the blueprint's fields mean anything — and "untick a bool in the
    /// Inspector" is not a discoverable way to express that.
    /// </summary>
    private void DrawModeSwitch()
    {
        EditorGUILayout.Space(2f);
        int current = _blueprint.parityLayout ? 0 : 1;
        int picked = GUILayout.Toolbar(current, new[]
        {
            new GUIContent("Rebuild shipped map",
                "Parity: reproduce Refinery Delta exactly. The seed varies nothing and the " +
                "gates verify rather than shape. This is the regression test."),
            new GUIContent("Generate new map",
                "Synthesize routes, pads and dressing from the seed. This is what the " +
                "generator is for."),
        }, GUILayout.Height(24f));

        if (picked != current)
        {
            Undo.RecordObject(_blueprint, "Change generation mode");
            _blueprint.parityLayout = picked == 0;
            EditorUtility.SetDirty(_blueprint);
            _ran = false;                                    // the previous run described the other mode
        }

        EditorGUILayout.LabelField(
            _blueprint.parityLayout
                ? "Parity — the shipped layout, seed ignored. Use it to prove the pipeline still " +
                  "reproduces the live map after a change."
                : "Synthesis — routes, hardpoints and dressing come from the seed. Reseed freely; " +
                  "a refused seed costs nothing.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(2f);
    }

    /// <summary>
    /// Pad count and class mix, with the total as a LIVE control rather than a
    /// stored field.
    ///
    /// The blueprint carries the mix only. Typing a total here re-spreads it
    /// (<see cref="LevelBlueprint.PadClassMix.WithTotal"/>) and writes the mix
    /// back, so "10 pads" is a thing you can ask for without a second number
    /// existing to fall out of step with the breakdown — which is what the old
    /// hardpointCount field did, blocking generation over a value nothing read.
    /// </summary>
    private void DrawPads()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Hardpoints", EditorStyles.boldLabel);

        // Parity pads are the shipped set, so the mix is a fixed census there —
        // editing it would only make gate 2 disagree with the map it rebuilt.
        using (new EditorGUI.DisabledScope(_blueprint.parityLayout))
        {
            LevelBlueprint.PadClassMix mix = _blueprint.classMix;

            EditorGUI.BeginChangeCheck();
            int total = EditorGUILayout.IntField(
                new GUIContent("Total pads",
                    "How many pads the map gets. Raising it adds Standard pads; lowering it takes " +
                    "from Standard, then Overwatch, then Rear. Premium never drops below " +
                    LevelBlueprint.PadClassMix.MinPremium + " — the coverage rule needs that many at 4+ spans."),
                mix.Total);
            if (EditorGUI.EndChangeCheck() && total != mix.Total)
            {
                ApplyMix(mix.WithTotal(total), "Change pad count");
                mix = _blueprint.classMix;               // the per-class fields below show the new spread now
            }

            EditorGUI.BeginChangeCheck();
            LevelBlueprint.PadClassMix edited = mix;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("By class",
                    "Premium (4+ covered spans) / Standard (2–3) / Rear (final approach + air leg) / " +
                    "Overwatch (Mortar homes, set back). Classes are assigned from MEASURED coverage — " +
                    "this asks for a count, the generator proves it."));
                float labels = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 18f;
                edited.premium = EditorGUILayout.IntField("P", mix.premium);
                edited.standard = EditorGUILayout.IntField("S", mix.standard);
                edited.rear = EditorGUILayout.IntField("R", mix.rear);
                edited.overwatch = EditorGUILayout.IntField("O", mix.overwatch);
                EditorGUIUtility.labelWidth = labels;
            }
            bool classEdited = EditorGUI.EndChangeCheck() &&
                (edited.premium != mix.premium || edited.standard != mix.standard ||
                 edited.rear != mix.rear || edited.overwatch != mix.overwatch);
            if (classEdited)
                ApplyMix(edited, "Change class mix");
        }

        EditorGUILayout.LabelField(
            _blueprint.parityLayout
                ? "Parity rebuilds the shipped 8 pads (3P/2S/2R/1O); the mix is the census the gate checks."
                : "The mix is the pad count — there is no separate total to keep in sync. More pads only " +
                  "generate if the geometry can host them; if it can't, gate 2 refuses and names the class.",
            EditorStyles.wordWrappedMiniLabel);
    }

    private void ApplyMix(LevelBlueprint.PadClassMix mix, string undoLabel)
    {
        Undo.RecordObject(_blueprint, undoLabel);
        _blueprint.classMix = mix;
        EditorUtility.SetDirty(_blueprint);
        _ran = false;                                        // stale results would describe another map
    }

    private void DrawDrawPreview()
    {
        EnvPack theme = GenerationPipeline.DrawTheme(_blueprint);
        WeatherPreset weather = GenerationPipeline.DrawWeather(_blueprint, theme);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("This seed draws", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField("Theme",
                theme == null ? "— (envPackPool empty; generates undressed)"
                              : string.IsNullOrEmpty(theme.themeName) ? theme.name : theme.themeName);
            EditorGUILayout.TextField("Weather",
                weather == null ? "null preset (authored look)" : weather.name);
            if (theme != null)
                EditorGUILayout.TextField("Ground",
                    theme.groundMaterial != null ? theme.groundMaterial.name : "scene default");
        }
    }

    private void DrawValidation(out bool blocked)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        GenerateLevel.ValidateBlueprint(_blueprint, errors, warnings);
        blocked = errors.Count > 0;

        foreach (string e in errors)
            EditorGUILayout.HelpBox(e, MessageType.Error);
        foreach (string w in warnings)
            EditorGUILayout.HelpBox(w, MessageType.Warning);

        DrawFixes(ref blocked);
    }

    /// <summary>Advisor state, recomputed only when the blueprint actually changes.</summary>
    private int _fixKey;
    private string _diagnosis;
    private List<GenerationAdvisor.Fix> _fixes = new List<GenerationAdvisor.Fix>();

    /// <summary>
    /// The preflight panel: does this blueprint generate, and if not, what one
    /// change would make it?
    ///
    /// It runs the real synthesizer on throwaway copies, which is cheap but not
    /// free, so it recomputes on a CHANGE KEY rather than every repaint — an
    /// editor window repaints on mouse movement, and a search per frame would
    /// make the whole window feel broken.
    /// </summary>
    private void DrawFixes(ref bool blocked)
    {
        int key = FixKey(_blueprint);
        if (key != _fixKey)
        {
            _fixKey = key;
            _fixes = GenerationAdvisor.Suggest(_blueprint, out _diagnosis);
        }

        if (string.IsNullOrEmpty(_diagnosis) && _fixes.Count == 0)
            return;

        if (!string.IsNullOrEmpty(_diagnosis))
        {
            // A structural refusal blocks Generate the same way a validation error
            // does. Letting the button run a pipeline that cannot reach stage 7 is
            // a slower way of saying the same thing.
            blocked = true;
            EditorGUILayout.HelpBox("This blueprint cannot generate on any seed:\n\n" + _diagnosis,
                                    MessageType.Error);
        }

        if (_fixes.Count == 0)
            return;

        EditorGUILayout.LabelField(_fixes.Count == 1 ? "Suggested fix" : "Suggested fixes",
                                   EditorStyles.boldLabel);
        GenerationAdvisor.Fix clicked = null;
        foreach (GenerationAdvisor.Fix fix in _fixes)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(fix.why, EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button(fix.label))
                    clicked = fix;
            }
        }

        // Applied AFTER the loop: mutating the blueprint mid-draw changes what the
        // rest of this pass wants to lay out, and IMGUI does not forgive that.
        if (clicked != null)
        {
            Undo.RecordObject(_blueprint, clicked.label);
            clicked.apply(_blueprint);
            EditorUtility.SetDirty(_blueprint);
            _ran = false;
            _fixKey = 0;                      // re-diagnose against the edited blueprint
            Repaint();
        }
    }

    /// <summary>
    /// Everything the advisor's answer depends on, in one int. Deliberately not
    /// the seed: a structural refusal is the same on every seed, which is the
    /// whole reason the advisor exists.
    /// </summary>
    private static int FixKey(LevelBlueprint b)
    {
        if (b == null)
            return 0;
        unchecked
        {
            int h = b.GetInstanceID();
            h = h * 31 + b.topology.GetHashCode();
            h = h * 31 + b.parityLayout.GetHashCode();
            h = h * 31 + b.playfieldSize.GetHashCode();
            h = h * 31 + b.protectedNormalizedPos.GetHashCode();
            h = h * 31 + b.routeLengthTarget.GetHashCode();
            h = h * 31 + b.foldWidth.GetHashCode();
            h = h * 31 + b.classMix.premium * 7 + b.classMix.standard * 11 +
                         b.classMix.rear * 13 + b.classMix.overwatch * 17;
            return h;
        }
    }

    private void DrawGenerate(bool blocked)
    {
        EditorGUILayout.Space(6f);
        int gates = 0;
        foreach (var s in GenerationPipeline.Stages)
            if (s.gate) gates++;

        using (new EditorGUI.DisabledScope(blocked))
        {
            if (GUILayout.Button($"Generate  ({GenerationPipeline.Stages.Length} stages, {gates} gates — " +
                                 "cancel any time)", GUILayout.Height(32f)))
                Run();
        }
        if (blocked)
            EditorGUILayout.HelpBox("Fix the errors above — the pipeline refuses to start on an " +
                                    "invalid blueprint rather than emit a half-right scene.", MessageType.None);
    }

    private void Run()
    {
        _results.Clear();
        _scenePath = null;
        _ran = true;

        var results = GenerationPipeline.RunAll(_blueprint);
        _results.AddRange(results);

        _totalSeconds = 0;
        foreach (var run in results)
        {
            _totalSeconds += run.seconds;
            if (run.stage.title == "Save scene" && run.result.ok)
                _scenePath = run.result.message.Split(' ')[0];
        }

        Repaint();
    }

    /// <summary>
    /// The pipeline as one always-visible map. Before a run: every stage
    /// pending. After: the transcript in place — a failed run shows exactly
    /// which gate stopped it and which stages never ran.
    /// </summary>
    private void DrawStageMap()
    {
        EditorGUILayout.Space(6f);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(_ran ? $"Pipeline — last run {_totalSeconds:0.0} s" : "Pipeline",
                                       EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_ran && GUILayout.Button("Copy report", GUILayout.Width(90f)))
                EditorGUIUtility.systemCopyBuffer = BuildReport();
        }

        bool failed = false;
        int shown = 0;
        foreach (var run in _results)
        {
            DrawStageRow(run.stage, run.result, run.seconds, ran: true);
            failed |= !run.result.ok;
            if (run.stage.run != null)          // the synthetic Discard row is extra
                shown++;
        }

        // Stages that have not run (or not yet this session) render as pending,
        // so the full process is visible before anyone presses the button.
        for (int i = shown; i < GenerationPipeline.Stages.Length; i++)
            DrawStageRow(GenerationPipeline.Stages[i], default, 0f, ran: false, neverReached: failed);

        if (_ran && !failed && _scenePath != null)
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶ Enter Play Mode", GUILayout.Height(26f)))
                    EditorApplication.EnterPlaymode();
                if (GUILayout.Button("Ping scene", GUILayout.Width(90f), GUILayout.Height(26f)))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(_scenePath));
            }
        }
    }

    private void DrawStageRow(GenerationPipeline.Stage stage, GenerationPipeline.StageResult result,
                              float seconds, bool ran, bool neverReached = false)
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            string icon = !ran ? (neverReached ? "·" : "○")
                        : !result.ok ? "✗"
                        : result.skipped ? "–" : "✓";
            var iconStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = !ran ? PendingColor : !result.ok ? FailColor : OkColor }
            };
            GUILayout.Label(icon, iconStyle, GUILayout.Width(16f));

            using (new EditorGUILayout.VerticalScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var titleStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                    if (stage.gate)
                        titleStyle.normal.textColor = ran && !result.ok ? FailColor : GateColor;
                    else if (!ran)
                        titleStyle.normal.textColor = PendingColor;
                    EditorGUILayout.LabelField((stage.gate ? "⛨ " : "") + $"{stage.title}   ({stage.ticket})",
                                               titleStyle);
                    GUILayout.FlexibleSpace();
                    if (ran && seconds >= 0.05f)
                        GUILayout.Label($"{seconds:0.0} s", EditorStyles.miniLabel, GUILayout.Width(44f));
                }
                if (ran)
                    EditorGUILayout.LabelField(result.message, EditorStyles.wordWrappedMiniLabel);
                else if (neverReached)
                    EditorGUILayout.LabelField("not reached — the run stopped above", EditorStyles.miniLabel);
            }
        }
    }

    /// <summary>The transcript as paste-ready text — the team's bug-report currency.</summary>
    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"COREHOLD generation — blueprint '{_blueprint.name}', seed {_blueprint.randomSeed}, " +
                      $"{_totalSeconds:0.0} s");
        foreach (var run in _results)
        {
            string icon = !run.result.ok ? "✗" : run.result.skipped ? "–" : "✓";
            sb.AppendLine($"{icon} {(run.stage.gate ? "[GATE] " : "")}{run.stage.title} ({run.stage.ticket})" +
                          (run.seconds >= 0.05f ? $" — {run.seconds:0.0} s" : ""));
            sb.AppendLine($"    {run.result.message.Replace("\n", "\n    ")}");
        }
        return sb.ToString();
    }

    private void DrawUtilities()
    {
        EditorGUILayout.Space(8f);
        _utilities = EditorGUILayout.Foldout(_utilities, "Authoring utilities", true);
        if (!_utilities)
            return;

        if (GUILayout.Button("Build Env Packs From Folders  (Authoring/EnvPack/<Theme>/<Category>)"))
            EnvPackTools.BuildFromFolders();
        if (GUILayout.Button("Create Refinery Env Pack  (shipped map's nine props)"))
            EnvPackTools.CreateRefineryPack();
        if (GUILayout.Button("Measure Selected Env Pack"))
            EnvPackTools.MeasureSelected();
        if (GUILayout.Button("Organize Hierarchy  (open scene)"))
            OrganizeHierarchy.Organize();
    }
}
