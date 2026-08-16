using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// The Campaign Builder (plan v2 §A.3): orchestrates existing pipelines and
    /// owns no generation logic. Per stage it clones the blueprint (never
    /// mutates the authored asset), derives the seed from the campaign's master
    /// seed, runs <see cref="GenerationPipeline.RunAll"/> with the bounded
    /// auto-reseed retry, then RELOCATES the outputs from the git-ignored
    /// Scenes/Generated into the committed campaign folders (decision D1) and
    /// deep-clones the stage's wave tables so shipped WaveDefinition assets
    /// stay read-only. "Register Campaign" rewrites Build Settings wholesale:
    /// Welcome first, levels in order, Closing, then the surviving singles.
    /// </summary>
    public class CampaignBuilderWindow : EditorWindow
    {
        private const int MaxAutoSeeds = 6; // same bound as the Level Generator window

        private CampaignAuthoring _authoring;
        private Vector2 _scroll;
        private string _report = "";

        [MenuItem("Tools/COREHOLD/Campaign/Campaign Builder", false, 1)]
        public static void Open()
        {
            var w = GetWindow<CampaignBuilderWindow>("Campaign Builder");
            w.minSize = new Vector2(560, 480);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawAuthoringPicker();
            if (_authoring == null)
            {
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawHeaderFields();
            DrawStages();
            DrawActions();
            DrawReport();

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------- picker

        private void DrawAuthoringPicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _authoring = (CampaignAuthoring)EditorGUILayout.ObjectField(
                    "Campaign", _authoring, typeof(CampaignAuthoring), false);

                if (GUILayout.Button("Create New", GUILayout.Width(90)))
                {
                    string path = EditorUtility.SaveFilePanelInProject(
                        "New Campaign Authoring", "CampaignAuthoring_main", "asset",
                        "Editor-side campaign asset (never ships).",
                        "Assets/_COREHOLD/Data");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var a = CreateInstance<CampaignAuthoring>();
                        AssetDatabase.CreateAsset(a, path);
                        AssetDatabase.SaveAssets();
                        _authoring = a;
                    }
                }
            }

            if (_authoring == null)
                EditorGUILayout.HelpBox(
                    "Pick or create a CampaignAuthoring asset. It is editor-only: blueprints and " +
                    "generation bookkeeping live here; the runtime gets an emitted CampaignManifest.",
                    MessageType.Info);
        }

        private void DrawHeaderFields()
        {
            EditorGUI.BeginChangeCheck();
            _authoring.campaignId = EditorGUILayout.TextField("Campaign id", _authoring.campaignId);
            _authoring.displayName = EditorGUILayout.TextField("Display name", _authoring.displayName);
            _authoring.masterSeed = EditorGUILayout.IntField("Master seed", _authoring.masterSeed);
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(_authoring);
        }

        // ------------------------------------------------------------- stages

        private void DrawStages()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Levels ({_authoring.stages.Count})", EditorStyles.boldLabel);

            for (int i = 0; i < _authoring.stages.Count; i++)
            {
                var s = _authoring.stages[i];
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUI.BeginChangeCheck();

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Level {i + 1}", EditorStyles.boldLabel, GUILayout.Width(60));
                        s.title = EditorGUILayout.TextField(s.title);

                        GUI.enabled = i > 0;
                        if (GUILayout.Button("▲", GUILayout.Width(24))) { Swap(i, i - 1); GUI.enabled = true; break; }
                        GUI.enabled = i < _authoring.stages.Count - 1;
                        if (GUILayout.Button("▼", GUILayout.Width(24))) { Swap(i, i + 1); GUI.enabled = true; break; }
                        GUI.enabled = true;
                        if (GUILayout.Button("✕", GUILayout.Width(24)))
                        {
                            _authoring.stages.RemoveAt(i);
                            EditorUtility.SetDirty(_authoring);
                            break;
                        }
                    }

                    s.blueprint = (LevelBlueprint)EditorGUILayout.ObjectField(
                        "Blueprint", s.blueprint, typeof(LevelBlueprint), false);
                    s.briefing = EditorGUILayout.TextField("Briefing", s.briefing);
                    s.seedOverride = EditorGUILayout.IntField(
                        new GUIContent("Seed override", "0 = derive from master seed. Use a contact-sheet pick to choose by eye."),
                        s.seedOverride);

                    if (EditorGUI.EndChangeCheck())
                        EditorUtility.SetDirty(_authoring);

                    bool generated = !string.IsNullOrEmpty(s.scenePath);
                    string status = generated
                        ? (System.IO.File.Exists(s.scenePath)
                            ? $"seed {s.acceptedSeed} → {s.scenePath}"
                            : $"STALE — recorded scene missing ({s.scenePath}); regenerate")
                        : "not generated yet";
                    EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUI.enabled = s.blueprint != null;
                        if (GUILayout.Button("Contact sheet (pick a seed by eye)"))
                        {
                            // ContactSheet works on the selected blueprint; the
                            // PNG it writes shows 9 passing seeds to choose from.
                            Selection.activeObject = s.blueprint;
                            ContactSheet.Run();
                            _report = "Contact sheet rendering — check the console for the PNG path, " +
                                      "then put the seed you like into this stage's Seed override.";
                        }
                        if (GUILayout.Button(generated ? "Regenerate" : "Generate"))
                        {
                            GenerateStage(i);
                            GUI.enabled = true;
                            break;
                        }
                        GUI.enabled = true;
                    }
                }
            }

            if (GUILayout.Button("+ Add level"))
            {
                _authoring.stages.Add(new CampaignAuthoring.AuthoredStage
                {
                    title = $"Operation {_authoring.stages.Count + 1}",
                });
                EditorUtility.SetDirty(_authoring);
            }
        }

        private void Swap(int a, int b)
        {
            (_authoring.stages[a], _authoring.stages[b]) = (_authoring.stages[b], _authoring.stages[a]);
            EditorUtility.SetDirty(_authoring);
        }

        // ------------------------------------------------------------ actions

        private void DrawActions()
        {
            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _authoring.stages.Count > 0 && _authoring.stages.All(s => s.blueprint != null);
                if (GUILayout.Button("Generate ALL levels", GUILayout.Height(28)))
                    GenerateAll();
                GUI.enabled = true;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build menu scenes (stub)"))
                    BuildCampaignScenes.BuildBoth();

                GUI.enabled = _authoring.stages.Any(s => !string.IsNullOrEmpty(s.scenePath));
                if (GUILayout.Button("Emit manifest + wire Welcome"))
                    EmitManifest();
                if (GUILayout.Button("Register Campaign (Build Settings)"))
                    RegisterCampaign();
                GUI.enabled = true;
            }
        }

        private void DrawReport()
        {
            if (string.IsNullOrEmpty(_report)) return;
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
                if (GUILayout.Button("Copy", GUILayout.Width(60)))
                    EditorGUIUtility.systemCopyBuffer = _report;
            }
            EditorGUILayout.TextArea(_report, GUILayout.MinHeight(140));
        }

        // --------------------------------------------------------- generation

        private void GenerateAll()
        {
            var log = new StringBuilder();
            int passed = 0;
            for (int i = 0; i < _authoring.stages.Count; i++)
            {
                if (GenerateStageInternal(i, log)) passed++;
                else break; // a failed stage stops the batch — read the transcript, fix, resume
            }
            log.AppendLine($"\n{passed}/{_authoring.stages.Count} levels generated.");
            if (passed == _authoring.stages.Count)
            {
                EmitManifest();
                RegisterCampaign();
                log.AppendLine("Manifest emitted and Build Settings registered — open the Welcome scene and press Play.");
            }
            _report = log.ToString();
        }

        private void GenerateStage(int index)
        {
            var log = new StringBuilder();
            if (GenerateStageInternal(index, log))
            {
                EmitManifest();
                RegisterCampaign();
                log.AppendLine("Manifest + Build Settings refreshed for the regenerated stage.");
            }
            _report = log.ToString();
        }

        private bool GenerateStageInternal(int index, StringBuilder log)
        {
            var stage = _authoring.stages[index];
            if (stage.blueprint == null)
            {
                log.AppendLine($"Level {index + 1}: no blueprint assigned.");
                return false;
            }

            DeleteStageOutputs(stage, log);

            // Blueprint originals have been observed to die across pipeline runs
            // (asset-database churn) — same defence as the contact sheet: snapshot
            // to JSON once, rebuild a throwaway clone per attempt.
            string bpJson = EditorJsonUtility.ToJson(stage.blueprint);
            string bpName = stage.blueprint.name;

            int baseSeed = stage.seedOverride != 0
                ? stage.seedOverride
                : (int)GenerationPipeline.Fnv1a(_authoring.masterSeed, "level_" + index);

            List<GenerationPipeline.StageRun> results = null;
            int usedSeed = 0;
            bool passed = false;

            for (int attempt = 0; attempt < MaxAutoSeeds && !passed; attempt++)
            {
                var clone = ScriptableObject.CreateInstance<LevelBlueprint>();
                try
                {
                    EditorJsonUtility.FromJsonOverwrite(bpJson, clone);
                    clone.name = bpName;
                    clone.hideFlags = HideFlags.DontSave;
                    usedSeed = baseSeed + attempt;
                    clone.randomSeed = usedSeed;

                    results = GenerationPipeline.RunAll(clone);
                    passed = results.Count > 0 && results.All(r => r.result.ok);
                }
                finally
                {
                    Object.DestroyImmediate(clone);
                }

                if (!passed && attempt < MaxAutoSeeds - 1)
                    log.AppendLine($"Level {index + 1}: seed {usedSeed} failed at " +
                                   $"'{FirstFailure(results)}' — reseeding.");
            }

            log.AppendLine($"— Level {index + 1} '{stage.title}' ({bpName}) —");
            if (results != null)
                foreach (var r in results)
                    log.AppendLine($"  {(r.result.ok ? (r.result.skipped ? "SKIP" : "ok ") : "FAIL")}  {r.stage.title}" +
                                   (r.result.ok ? "" : $" — {r.result.message}"));

            if (!passed)
            {
                log.AppendLine($"Level {index + 1}: no passing seed within {MaxAutoSeeds} attempts — " +
                               "this blueprint needs the Level Generator's fix panel, not more seeds.");
                return false;
            }

            // Outputs: scene path from the save stage's message; the emitted
            // LevelDefinition via the open scene's WaveManager (both proven
            // patterns — GeneratorWindow and ContactSheet respectively).
            string generatedScene = results
                .Where(r => r.stage.title == "Save scene" && r.result.ok)
                .Select(r => r.result.message.Split(' ')[0])
                .FirstOrDefault();
            if (string.IsNullOrEmpty(generatedScene))
                generatedScene = EditorSceneManager.GetActiveScene().path;

            var wm = Object.FindFirstObjectByType<WaveManager>();
            string generatedDef = null;
            LevelDefinition def = null;
            if (wm != null)
            {
                var so = new SerializedObject(wm);
                def = so.FindProperty("level").objectReferenceValue as LevelDefinition;
                if (def != null) generatedDef = AssetDatabase.GetAssetPath(def);
            }
            if (def == null)
            {
                log.AppendLine($"Level {index + 1}: generated scene has no wired LevelDefinition — aborting relocation.");
                return false;
            }

            if (!RelocateOutputs(stage, index, usedSeed, generatedScene, generatedDef, def, log))
                return false;

            stage.acceptedSeed = usedSeed;
            EditorUtility.SetDirty(_authoring);
            AssetDatabase.SaveAssets();
            log.AppendLine($"  seed {usedSeed} accepted → {stage.scenePath}");
            return true;
        }

        private static string FirstFailure(List<GenerationPipeline.StageRun> results)
        {
            if (results == null) return "startup";
            foreach (var r in results)
                if (!r.result.ok) return r.stage.title;
            return "unknown";
        }

        // --------------------------------------------------------- relocation

        /// <summary>
        /// Move the generated scene + LevelDefinition from the git-ignored
        /// Generated folders into the committed campaign home (decision D1), and
        /// deep-clone the wave tables so the shipped WaveDefinition assets are
        /// never shared into a campaign stage. MoveAsset preserves GUIDs, so the
        /// scene's wired references survive.
        /// </summary>
        private bool RelocateOutputs(CampaignAuthoring.AuthoredStage stage, int index, int seed,
                                     string scenePath, string defPath, LevelDefinition def, StringBuilder log)
        {
            EnsureFolder(_authoring.SceneFolder);
            EnsureFolder(_authoring.DataFolder);

            string tag = $"L{index + 1:00}";
            string sceneDest = $"{_authoring.SceneFolder}/{tag}_{System.IO.Path.GetFileName(scenePath)}";
            string defDest = $"{_authoring.DataFolder}/{tag}_{System.IO.Path.GetFileName(defPath)}";

            AssetDatabase.DeleteAsset(sceneDest);
            AssetDatabase.DeleteAsset(defDest);

            string err = AssetDatabase.MoveAsset(scenePath, sceneDest);
            if (!string.IsNullOrEmpty(err)) { log.AppendLine($"  scene move failed: {err}"); return false; }
            err = AssetDatabase.MoveAsset(defPath, defDest);
            if (!string.IsNullOrEmpty(err)) { log.AppendLine($"  definition move failed: {err}"); return false; }

            // Deep-clone the wave tables (plan v2 §A.3): the emitted definition
            // is a shallow clone still pointing at the SHARED shipped
            // WaveDefinition assets — per-stage edits or roster swaps would
            // otherwise change every level at once, shipped map included.
            string wavesFolder = $"{_authoring.DataFolder}/{tag}_Waves";
            AssetDatabase.DeleteAsset(wavesFolder);
            EnsureFolder(wavesFolder);

            var defSo = new SerializedObject(def);
            var wavesProp = defSo.FindProperty("waves");
            int cloned = 0;
            for (int w = 0; w < wavesProp.arraySize; w++)
            {
                var element = wavesProp.GetArrayElementAtIndex(w);
                var shared = element.objectReferenceValue as WaveDefinition;
                if (shared == null) continue;
                var copy = Object.Instantiate(shared);
                copy.name = shared.name;
                AssetDatabase.CreateAsset(copy, $"{wavesFolder}/{shared.name}.asset");
                element.objectReferenceValue = copy;
                cloned++;
            }
            defSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();

            stage.scenePath = sceneDest;
            stage.levelDefPath = defDest;
            stage.wavesFolder = wavesFolder;
            log.AppendLine($"  relocated to campaign folders; {cloned} wave tables deep-cloned (shipped assets untouched).");
            return true;
        }

        private void DeleteStageOutputs(CampaignAuthoring.AuthoredStage stage, StringBuilder log)
        {
            // Regeneration hygiene (plan v2 §A.3): superseded outputs die BEFORE
            // new ones are written, or every regeneration ships a stale scene.
            int removed = 0;
            if (!string.IsNullOrEmpty(stage.scenePath) && AssetDatabase.DeleteAsset(stage.scenePath)) removed++;
            if (!string.IsNullOrEmpty(stage.levelDefPath) && AssetDatabase.DeleteAsset(stage.levelDefPath)) removed++;
            if (!string.IsNullOrEmpty(stage.wavesFolder) && AssetDatabase.DeleteAsset(stage.wavesFolder)) removed++;
            if (removed > 0) log.AppendLine($"  deleted {removed} superseded output(s).");
            stage.scenePath = stage.levelDefPath = stage.wavesFolder = null;
            stage.acceptedSeed = 0;
        }

        // ----------------------------------------------------------- manifest

        private void EmitManifest()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(_authoring.ManifestAssetPath);
            bool created = manifest == null;
            if (created) manifest = CreateInstance<CampaignManifest>();

            manifest.campaignId = _authoring.campaignId;
            manifest.displayName = _authoring.displayName;
            manifest.progression = _authoring.progression;
            manifest.stages = new List<CampaignStageInfo>
            {
                new CampaignStageInfo { kind = CampaignStageKind.Welcome, title = "Welcome",
                                        scenePath = _authoring.welcomeScenePath },
            };
            for (int i = 0; i < _authoring.stages.Count; i++)
            {
                var s = _authoring.stages[i];
                if (string.IsNullOrEmpty(s.scenePath)) continue; // ungenerated stages stay out of the manifest
                manifest.stages.Add(new CampaignStageInfo
                {
                    kind = CampaignStageKind.Level,
                    title = s.title,
                    briefing = s.briefing,
                    scenePath = s.scenePath,
                    seed = s.acceptedSeed,
                });
            }
            manifest.stages.Add(new CampaignStageInfo { kind = CampaignStageKind.Closing, title = "Debrief",
                                                        scenePath = _authoring.closingScenePath });

            EnsureFolder(BuildCampaignScenes.ManifestDir);
            if (created) AssetDatabase.CreateAsset(manifest, _authoring.ManifestAssetPath);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();

            BuildCampaignScenes.WireManifestIntoWelcome(manifest);
        }

        // ------------------------------------------------------ build settings

        /// <summary>
        /// Rewrite Build Settings wholesale (plan v2 §A.3): Welcome at index 0 —
        /// the shipped build boots into the campaign — then levels in campaign
        /// order, Closing, then every other enabled scene (Game.unity keeps
        /// single-map play). The pipeline's own registration is append-only and
        /// its prune only watches Scenes/Generated, so ordering lives here.
        /// </summary>
        private void RegisterCampaign()
        {
            var campaignPaths = new List<string> { _authoring.welcomeScenePath };
            campaignPaths.AddRange(_authoring.stages
                .Where(s => !string.IsNullOrEmpty(s.scenePath))
                .Select(s => s.scenePath));
            campaignPaths.Add(_authoring.closingScenePath);

            var list = new List<EditorBuildSettingsScene>();
            foreach (var p in campaignPaths)
            {
                if (!System.IO.File.Exists(p))
                {
                    Debug.LogWarning($"[Campaign] Build Settings: '{p}' does not exist yet (build the menu scenes / generate levels first).");
                    continue;
                }
                list.Add(new EditorBuildSettingsScene(p, true));
            }

            foreach (var existing in EditorBuildSettings.scenes)
            {
                if (campaignPaths.Contains(existing.path)) continue;
                if (!existing.enabled) continue;
                if (!System.IO.File.Exists(existing.path)) continue;
                list.Add(existing);
            }

            EditorBuildSettings.scenes = list.ToArray();
            Debug.Log($"[Campaign] Build Settings: {list.Count} scenes, campaign first (Welcome at index 0).");
        }

        // -------------------------------------------------------------- utils

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
