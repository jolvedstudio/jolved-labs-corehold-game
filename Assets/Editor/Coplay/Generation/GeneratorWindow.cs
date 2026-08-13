using System.Collections.Generic;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The team-facing surface for level generation: pick a blueprint, see what the
/// seed will draw, press Generate, watch the stages, press Play.
///
/// This window owns NO pipeline logic — it renders <see cref="GenerationPipeline.Stages"/>
/// and calls <see cref="GenerationPipeline.RunAll"/>, the same engine the headless
/// menu item drives. When R27–R30 replace stages in the pipeline, this window
/// updates without being touched. Anyone adding a stage should add it THERE.
/// </summary>
public class GeneratorWindow : EditorWindow
{
    private LevelBlueprint _blueprint;
    private readonly List<(GenerationPipeline.Stage stage, GenerationPipeline.StageResult result)> _results = new();
    private string _scenePath;
    private bool _ran;
    private bool _utilities;
    private Vector2 _scroll;

    [MenuItem("Tools/COREHOLD/Level/Level Generator", false, 0)]
    public static void Open()
    {
        var w = GetWindow<GeneratorWindow>("COREHOLD Generator");
        w.minSize = new Vector2(420f, 480f);
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
            DrawDrawPreview();
            DrawValidation(out bool blocked);
            DrawGenerate(blocked);
            DrawResults();
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

        // Seed edits go through Undo so a designer can back out of them.
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
            _ran = false;                                    // stale results would mislead
        }
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
    }

    private void DrawGenerate(bool blocked)
    {
        EditorGUILayout.Space(6f);
        using (new EditorGUI.DisabledScope(blocked))
        {
            if (GUILayout.Button("Generate Level", GUILayout.Height(32f)))
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

        foreach (var (stage, result) in results)
            if (stage.title == "Save scene" && result.ok)
                _scenePath = result.message.Split(' ')[0];

        Repaint();
    }

    private void DrawResults()
    {
        if (!_ran)
            return;

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Stages", EditorStyles.boldLabel);

        bool allOk = true;
        foreach (var (stage, result) in _results)
        {
            allOk &= result.ok;
            string icon = !result.ok ? "✗" : result.skipped ? "–" : "✓";
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(icon, GUILayout.Width(16f));
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField($"{stage.title}   ({stage.ticket})", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(result.message, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        int remaining = GenerationPipeline.Stages.Length - _results.Count;
        if (remaining > 0)
            EditorGUILayout.LabelField($"{remaining} stage(s) not reached.", EditorStyles.miniLabel);

        if (allOk && _scenePath != null)
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
