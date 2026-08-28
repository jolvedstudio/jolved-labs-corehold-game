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

        // The builder is a SEQUENCE — the same visual contract as the Level
        // Generator's stage list. Each step shows its state up front (✓ done,
        // → do this next, · not reached), and a live Issues panel says exactly
        // what is blocking, so "it failed" always has a visible why.
        private struct StepState
        {
            public bool done;
            public string status;
        }

        private readonly List<string> _issues = new List<string>();

        /// <summary>
        /// Suggested tunes the last Verify collected for failing NON-RECIPE
        /// stages, keyed by stage index — what the "Apply suggested tune"
        /// button applies: the defender package (level-scoped multipliers)
        /// plus any wave count changes. Cleared on every Verify; count
        /// changes carry the count they expect to find (prev), so a stale
        /// tune refuses itself.
        /// </summary>
        private readonly Dictionary<int, BalanceModelRunner.Result> _pendingTunes =
            new Dictionary<int, BalanceModelRunner.Result>();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Campaign Builder — work top to bottom", EditorStyles.boldLabel);

            StepState[] steps = ComputeSteps();
            int next = 0;
            while (next < steps.Length && steps[next].done)
                next++;

            DrawStepHeader(1, "Campaign asset", steps, next);
            DrawAuthoringPicker();

            if (_authoring != null)
            {
                DrawStepHeader(2, "Identity & rules", steps, next);
                DrawHeaderFields();

                DrawStepHeader(3, "Levels", steps, next);
                DrawStages();

                DrawStepHeader(4, "Generate scenes", steps, next);
                DrawGenerateStep();

                DrawStepHeader(5, "Menu scenes (Welcome + Closing)", steps, next);
                DrawMenuScenesStep();

                DrawStepHeader(6, "Verify economy & emit manifest", steps, next);
                DrawManifestStep();

                DrawStepHeader(7, "Register Build Settings", steps, next);
                DrawRegisterStep();

                DrawStepHeader(8, "Preflight & ship", steps, next);
                DrawShipStep();
            }

            DrawIssues();
            DrawReport();

            EditorGUILayout.EndScrollView();
        }

        // -------------------------------------------------------- step status

        /// <summary>Cheap, per-repaint state of every step — file existence and
        /// asset lookups only, never a model run or a scene open.</summary>
        private StepState[] ComputeSteps()
        {
            _issues.Clear();
            var s = new StepState[8];

            // 1 — asset
            s[0].done = _authoring != null;
            s[0].status = _authoring != null ? _authoring.name : "pick or create";
            if (_authoring == null)
            {
                _issues.Add("Step 1: no CampaignAuthoring asset selected — pick one or Create New.");
                return s;
            }

            // 2 — identity
            bool idOk = !string.IsNullOrWhiteSpace(_authoring.campaignId);
            s[1].done = idOk;
            s[1].status = idOk ? $"id '{_authoring.campaignId}', seed {_authoring.masterSeed}" : "campaign id required";
            if (!idOk)
                _issues.Add("Step 2: campaign id is empty — it names the save keys, folders and manifest.");

            // 3 — levels resolvable
            int unresolved = _authoring.stages.Count(st => st.blueprint == null && string.IsNullOrEmpty(st.scenePath));
            s[2].done = _authoring.stages.Count > 0 && unresolved == 0;
            s[2].status = _authoring.stages.Count == 0
                ? "add at least one level"
                : unresolved == 0 ? $"{_authoring.stages.Count} level(s)" : $"{unresolved} level(s) unresolved";
            if (_authoring.stages.Count == 0)
                _issues.Add("Step 3: the campaign has no levels — Add level, then assign a blueprint or an existing scene.");
            for (int i = 0; i < _authoring.stages.Count; i++)
            {
                var st = _authoring.stages[i];
                if (st.blueprint == null && string.IsNullOrEmpty(st.scenePath))
                    _issues.Add($"Step 3: Level {i + 1} '{st.title}' has neither a blueprint nor an existing scene.");
                else if (st.blueprint == null && st.scenePath.Contains("/Scenes/Generated/"))
                    _issues.Add($"Step 3: Level {i + 1} '{st.title}' references the git-ignored Scenes/Generated — " +
                                "preflight will refuse it. Press its 'Adopt into campaign' button (copies the " +
                                "scene + rules into the campaign's folders, byte-identical), or assign its " +
                                "blueprint + seed override and regenerate.");
            }

            // 4 — generated scenes on disk
            int expected = _authoring.stages.Count;
            int generated = 0, stale = 0;
            for (int i = 0; i < _authoring.stages.Count; i++)
            {
                var st = _authoring.stages[i];
                if (string.IsNullOrEmpty(st.scenePath))
                    continue;
                if (System.IO.File.Exists(st.scenePath)) generated++;
                else
                {
                    stale++;
                    _issues.Add($"Step 4: Level {i + 1} '{st.title}' records a scene that is missing on disk — " +
                                (st.blueprint == null ? "re-pick or restore the manual scene." : "regenerate it."));
                }
            }
            s[3].done = expected > 0 && generated == expected && stale == 0;
            s[3].status = $"{generated}/{expected} on disk" + (stale > 0 ? $", {stale} stale" : "");
            if (expected > 0 && generated < expected && s[2].done)
                _issues.Add($"Step 4: {expected - generated} level(s) not generated yet — Generate ALL (or per-level Generate).");

            // 5 — menu scenes
            bool welcome = System.IO.File.Exists(_authoring.welcomeScenePath);
            bool closing = System.IO.File.Exists(_authoring.closingScenePath);
            s[4].done = welcome && closing;
            s[4].status = welcome && closing ? "built" : $"welcome {(welcome ? "ok" : "MISSING")}, closing {(closing ? "ok" : "MISSING")}";
            if (!welcome || !closing)
                _issues.Add("Step 5: Welcome/Closing scenes not built — the campaign cannot boot without its Welcome scene at index 0.");

            // 6 — manifest, and whether the Welcome scene actually references it
            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(_authoring.ManifestAssetPath);
            int manifestLevels = manifest != null ? manifest.stages.Count(m => m.kind == CampaignStageKind.Level) : 0;
            bool wired = manifest != null && WelcomeWiredToManifest();
            s[5].done = manifest != null && manifestLevels == generated && generated > 0 && wired;
            s[5].status = manifest == null
                ? "not emitted"
                : (manifestLevels == generated ? $"{manifestLevels} level(s) in manifest" : $"manifest has {manifestLevels}, disk has {generated} — re-emit")
                  + (wired ? ", Welcome wired" : ", Welcome NOT wired");
            if (manifest == null && s[3].done)
                _issues.Add("Step 6: no manifest emitted — Verify carry economy, then Emit manifest + wire Welcome.");
            if (manifest != null && manifestLevels != generated)
                _issues.Add("Step 6: the manifest is out of date with the generated levels — re-emit it.");
            if (manifest != null && !wired)
                _issues.Add("Step 6: the Welcome scene does not reference this campaign's manifest — the campaign " +
                            "would boot to a dead menu. Build the menu scenes (step 5) and/or Emit manifest again.");

            // 7 — build settings
            var scenes = EditorBuildSettings.scenes;
            bool welcomeFirst = scenes.Length > 0 && scenes[0].path == _authoring.welcomeScenePath && scenes[0].enabled;
            var inBuild = new HashSet<string>(scenes.Where(b => b.enabled).Select(b => b.path));
            int missing = _authoring.stages.Count(st => !string.IsNullOrEmpty(st.scenePath) &&
                                                        System.IO.File.Exists(st.scenePath) && !inBuild.Contains(st.scenePath));
            s[6].done = welcomeFirst && missing == 0 && s[4].done;
            s[6].status = welcomeFirst
                ? (missing == 0 ? "campaign first, all levels in" : $"{missing} level(s) not registered")
                : "Welcome is not Build Settings index 0";
            if (s[3].done && (!welcomeFirst || missing > 0))
                _issues.Add("Step 7: Build Settings are stale — Register Campaign rewrites them (Welcome first, levels in order).");

            // 8 — ship (never "done": preflight is a report, not a state)
            s[7].done = false;
            s[7].status = "preflight tells the truth";
            return s;
        }

        private static void DrawStepHeader(int number, string title, StepState[] steps, int next)
        {
            EditorGUILayout.Space(number == 1 ? 4 : 12);
            StepState st = steps[number - 1];
            string marker = st.done ? "✓" : (number - 1 == next ? "→" : "·");
            GUILayout.Label($"{marker}  Step {number} — {title}     [{st.status}]", EditorStyles.boldLabel);
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
            _authoring.uiSkin = (UISkin)EditorGUILayout.ObjectField(
                new GUIContent("UI skin", "Optional. Bakes into every scene this builder generates — palette, fonts, sprites, proportions."),
                _authoring.uiSkin, typeof(UISkin), false);
            _authoring.waveRecipe = (WaveRecipe)EditorGUILayout.ObjectField(
                new GUIContent("Wave recipe", "Optional (Part C). SYNTHESIZES each stage's waves from a roster + curve " +
                    "instead of cloning the shipped tables, escalating by stage position; growth is re-solved and " +
                    "certified per stage. Any stage can override it with its own bespoke recipe in its box below."),
                _authoring.waveRecipe, typeof(WaveRecipe), false);
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(_authoring);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Carry rules (economy/integrity) are edited on the asset:", EditorStyles.miniLabel);
                if (GUILayout.Button("Select asset", GUILayout.Width(90)))
                    Selection.activeObject = _authoring;
            }
        }

        // ------------------------------------------------------------- stages

        private void DrawStages()
        {
            EditorGUILayout.LabelField($"Levels ({_authoring.stages.Count})", EditorStyles.miniLabel);

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

                    // No blueprint → the stage can instead USE an existing scene
                    // (a Clone Level fork or any hand-authored level) as-is.
                    if (s.blueprint == null)
                    {
                        var current = string.IsNullOrEmpty(s.scenePath)
                            ? null
                            : AssetDatabase.LoadAssetAtPath<SceneAsset>(s.scenePath);
                        var picked = (SceneAsset)EditorGUILayout.ObjectField(
                            new GUIContent("…or existing scene", "Manual stage: a forked or hand-authored level, used as-is."),
                            current, typeof(SceneAsset), false);
                        if (picked != current)
                        {
                            // Switching a GENERATED stage to manual: its old
                            // campaign-owned outputs are deleted (no orphans);
                            // a previously picked manual scene is only released.
                            var cleanup = new StringBuilder();
                            DeleteStageOutputs(s, cleanup);
                            if (cleanup.Length > 0)
                                Debug.Log("[Campaign] Stage switched to a manual scene:\n" + cleanup.ToString().TrimEnd());
                            s.scenePath = picked != null ? AssetDatabase.GetAssetPath(picked) : null;
                        }
                    }

                    s.briefing = EditorGUILayout.TextField("Briefing", s.briefing);
                    if (s.blueprint != null)
                    {
                        s.seedOverride = EditorGUILayout.IntField(
                            new GUIContent("Seed override", "0 = derive from master seed. Use a contact-sheet pick to choose by eye."),
                            s.seedOverride);
                        s.waveRecipe = (WaveRecipe)EditorGUILayout.ObjectField(
                            new GUIContent("Wave recipe (override)",
                                "Optional, THIS stage only. Empty = the campaign's recipe (step 2). A stage " +
                                "override is BESPOKE: evaluated exactly as authored, no per-stage escalation — " +
                                "the campaign recipe keeps its escalating programme. Re-certified either way."),
                            s.waveRecipe, typeof(WaveRecipe), false);
                    }

                    if (EditorGUI.EndChangeCheck())
                        EditorUtility.SetDirty(_authoring);

                    bool generated = !string.IsNullOrEmpty(s.scenePath);
                    bool isManual = s.blueprint == null && generated;
                    string status = generated
                        ? (System.IO.File.Exists(s.scenePath)
                            ? (isManual ? $"manual — {s.scenePath}" : $"seed {s.acceptedSeed} → {s.scenePath}")
                            : (isManual ? $"MISSING manual scene ({s.scenePath}) — re-pick or restore it"
                                        : $"STALE — recorded scene missing ({s.scenePath}); regenerate"))
                        : "not generated yet";
                    EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool manual = s.blueprint == null && !string.IsNullOrEmpty(s.scenePath);
                        if (manual)
                            EditorGUILayout.LabelField("manual stage — used as-is, nothing to generate",
                                                       EditorStyles.miniLabel);
                        // A manual scene in the git-ignored Generated folders can
                        // never ship — one click copies it (byte-identical) plus
                        // its rules into the campaign's own folders.
                        if (manual && s.scenePath.Contains("/Scenes/Generated/") &&
                            GUILayout.Button("Adopt into campaign", GUILayout.Width(150)))
                        {
                            var alog = new StringBuilder();
                            if (AdoptManualStage(i, alog))
                            {
                                bool wired = EmitManifest();
                                RegisterCampaign();
                                alog.AppendLine(wired
                                    ? "Manifest + Build Settings refreshed."
                                    : "Manifest refreshed — Welcome NOT wired (see console).");
                            }
                            ShowReport(alog.ToString());
                            break;
                        }
                        GUI.enabled = s.blueprint != null;
                        if (!manual && GUILayout.Button("Contact sheet (pick a seed by eye)"))
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

        // ---------------------------------------------------- per-step actions

        private void DrawGenerateStep()
        {
            EditorGUILayout.LabelField(
                "Generates every blueprint stage (bounded auto-reseed), relocates outputs into the " +
                "committed campaign folders, clones/synthesizes waves, then verifies, emits and " +
                "registers in one run. Manual stages are kept as-is.", EditorStyles.miniLabel);
            GUI.enabled = _authoring.stages.Count > 0 &&
                          _authoring.stages.All(s => s.blueprint != null || !string.IsNullOrEmpty(s.scenePath));
            if (GUILayout.Button("Generate ALL levels", GUILayout.Height(26), GUILayout.Width(280)))
                GenerateAll();
            GUI.enabled = true;
        }

        private void DrawMenuScenesStep()
        {
            EditorGUILayout.LabelField(
                "Builds the Welcome (difficulty gate, CONTINUE RUN) and Closing (stars, totals) scenes " +
                "into THIS campaign's own folder and wires its manifest when one exists. " +
                "Re-run after changing the UI skin.", EditorStyles.miniLabel);
            if (GUILayout.Button("Build menu scenes", GUILayout.Width(280)))
            {
                UISkin.Active = _authoring.uiSkin;
                string built;
                try { built = BuildCampaignScenes.BuildBoth(_authoring); }
                finally { UISkin.Active = null; }
                ShowReport(built);
            }
        }

        private void DrawManifestStep()
        {
            EditorGUILayout.LabelField(
                "Verify re-runs the balance model per stage at the WORST-CASE entry bank the carry " +
                "rules allow; Emit writes the runtime manifest and wires it into the Welcome scene. " +
                "Generate ALL already does both — these are for reruns after edits.", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _authoring.stages.Any(s => !string.IsNullOrEmpty(s.scenePath));
                if (GUILayout.Button("Verify carry economy", GUILayout.Width(180)))
                {
                    var vlog = new StringBuilder();
                    bool ok = VerifyCarryEconomy(vlog);
                    vlog.AppendLine(ok ? "\nAll stages hold at the worst-case entry."
                                       : "\nAt least one stage fails at the worst-case entry — see rows above.");
                    ShowReport(vlog.ToString());
                }
                if (GUILayout.Button("Emit manifest + wire Welcome", GUILayout.Width(220)))
                {
                    bool wired = EmitManifest();
                    ShowReport(wired
                        ? $"Manifest emitted → {_authoring.ManifestAssetPath} and wired into " +
                          $"'{_authoring.welcomeScenePath}'."
                        : $"Manifest emitted → {_authoring.ManifestAssetPath}, but NOT wired into the " +
                          "Welcome scene — build the menu scenes (step 5), then emit again.");
                }
                GUI.enabled = true;
            }

            // The model's suggested tunes from the last Verify — one click
            // applies them (defender-first) and re-verifies, so "it fails"
            // comes with "and this fixes it".
            if (_pendingTunes.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{_pendingTunes.Count} stage(s) failed Verify with a suggested tune available " +
                    "(details in the report below). Applying writes the DEFENDER package (level-scoped " +
                    "turret damage/range multipliers — tower assets stay untouched) into the stage's " +
                    "definition and any wave count changes into its own wave tables, then re-verifies.",
                    MessageType.Info);
                if (GUILayout.Button($"Apply suggested tune ({_pendingTunes.Count} stage(s))",
                                     GUILayout.Width(280)))
                    ApplyPendingTunes();
            }
        }

        /// <summary>
        /// Apply the model's counts-only tune to each pending stage's wave
        /// assets, then re-run Verify so the report shows the new verdict.
        /// Ownership-guarded (only assets under this campaign's data folder are
        /// edited) and stale-guarded (each change names the count it expects to
        /// find; a mismatch skips the stage and asks for a fresh Verify).
        /// </summary>
        private void ApplyPendingTunes()
        {
            var log = new StringBuilder();
            log.AppendLine("Suggested tune (defender-first):");
            var pending = new Dictionary<int, BalanceModelRunner.Result>(_pendingTunes);
            _pendingTunes.Clear();

            foreach (var kv in pending)
            {
                if (kv.Key < 0 || kv.Key >= _authoring.stages.Count) continue;
                var stage = _authoring.stages[kv.Key];
                var def = string.IsNullOrEmpty(stage.levelDefPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<LevelDefinition>(stage.levelDefPath);
                if (def == null || def.waves == null)
                {
                    log.AppendLine($"  Level {kv.Key + 1}: SKIPPED — its LevelDefinition is not reachable any more.");
                    continue;
                }

                var tune = kv.Value;
                var changes = tune.suggested_changes ?? new BalanceModelRunner.SuggestedChange[0];

                // Ownership: every wave asset the count changes would touch must
                // live in THIS campaign's data folder. Shipped or foreign tables
                // are never edited — the same doctrine as DeleteOwned.
                bool foreign = false;
                foreach (var c in changes)
                {
                    string p = c.wave - 1 >= 0 && c.wave - 1 < def.waves.Length && def.waves[c.wave - 1] != null
                        ? AssetDatabase.GetAssetPath(def.waves[c.wave - 1])
                        : null;
                    if (string.IsNullOrEmpty(p) || !p.StartsWith(_authoring.DataFolder + "/", System.StringComparison.Ordinal))
                    {
                        log.AppendLine($"  Level {kv.Key + 1}: REFUSED — wave {c.wave}'s table " +
                                       $"({p ?? "missing"}) is not campaign-owned. Regenerate or Adopt the " +
                                       "stage so it owns its waves, then Verify again.");
                        foreign = true;
                        break;
                    }
                }
                if (foreign) continue;

                // Defender package first — level-scoped multipliers on the
                // stage's own definition (the runtime honours them; the next
                // Verify certifies with them). The campaign floor test never
                // suggests a salvage change, so only the multipliers apply here.
                string package = BalanceModelRunner.DescribeDefenderPackage(tune);
                if (tune.suggested_tower_dps_mult > def.towerDamageMultiplier)
                    def.towerDamageMultiplier = tune.suggested_tower_dps_mult;
                if (tune.suggested_tower_range_mult > def.towerRangeMultiplier)
                    def.towerRangeMultiplier = tune.suggested_tower_range_mult;
                if (package.Length > 0)
                {
                    EditorUtility.SetDirty(def);
                    log.AppendLine($"  Level {kv.Key + 1} package: {package}");
                }

                if (changes.Length > 0 && !ApplyTuneToStage(kv.Key, def, changes, log))
                    continue;
            }

            AssetDatabase.SaveAssets();
            log.AppendLine();
            bool ok = VerifyCarryEconomy(log);
            log.AppendLine(ok ? "\nRe-verify: all stages hold at the worst-case entry."
                              : "\nRe-verify: still failing — what remains is beyond the tune's caps (see rows above).");
            ShowReport(log.ToString());
        }

        private bool ApplyTuneToStage(int stageIndex, LevelDefinition def,
                                      BalanceModelRunner.SuggestedChange[] changes, StringBuilder log)
        {
            // One shared applier (WaveTuneApplier) serves this button and the
            // Level Generator's adopt offer — two appliers of one tune would
            // be exactly the drift the doctrine forbids. Validate-all-then-
            // apply and the stale refusal live there.
            return WaveTuneApplier.Apply(def, changes, $"Level {stageIndex + 1}", log);
        }

        private void DrawRegisterStep()
        {
            EditorGUILayout.LabelField(
                "Rewrites Build Settings wholesale: Welcome at index 0, levels in campaign order, " +
                "Closing, then the surviving singles (Game.unity keeps single-map play).", EditorStyles.miniLabel);
            if (GUILayout.Button("Register Campaign (Build Settings)", GUILayout.Width(280)))
            {
                RegisterCampaign();
                ShowReport("Build Settings rewritten — Welcome first, campaign in order. Open the Welcome scene and press Play.");
            }
        }

        private void DrawShipStep()
        {
            // Never disabled: preflight on an incomplete campaign is the POINT —
            // its report says exactly what is missing. (A greyed-out button that
            // explains nothing is how this tool once "did nothing".)
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preflight (shippable?)", GUILayout.Width(180)))
                    ShowReport(CampaignShipTool.PreflightReport(_authoring, out _));
                if (GUILayout.Button("Localize vendor assets (all stages)", GUILayout.Width(230)))
                {
                    // Repair path for campaigns BUILT BEFORE self-containment:
                    // copies every vendor dependency of every stage into the
                    // committed Vendored folder and remaps. Newly generated or
                    // adopted stages do this automatically.
                    var llog = new StringBuilder();
                    llog.AppendLine("Vendor localization — all stages:");
                    var covered = new HashSet<string>();
                    foreach (var st in _authoring.stages)
                    {
                        if (string.IsNullOrEmpty(st.scenePath)) continue;
                        covered.Add(st.scenePath);
                        llog.AppendLine($"  {System.IO.Path.GetFileNameWithoutExtension(st.scenePath)}:");
                        try { LocalizeStage(st, llog); }
                        catch (System.Exception ex)
                        { llog.AppendLine($"    FAILED — {ex.GetType().Name}: {ex.Message}"); }
                    }
                    // Preflight judges the MANIFEST's scenes — welcome/closing
                    // and any stage whose emitted path is not what the
                    // authoring rows recorded. Localize those too, or the
                    // button can "succeed" while preflight stays red.
                    var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(_authoring.ManifestAssetPath);
                    if (manifest != null)
                    {
                        foreach (var ms in manifest.stages)
                        {
                            if (string.IsNullOrEmpty(ms.scenePath) || covered.Contains(ms.scenePath) ||
                                !System.IO.File.Exists(ms.scenePath))
                                continue;
                            llog.AppendLine($"  {System.IO.Path.GetFileNameWithoutExtension(ms.scenePath)} (manifest):");
                            try { VendorLocalizer.Localize(new[] { ms.scenePath }, llog); }
                            catch (System.Exception ex)
                            { llog.AppendLine($"    FAILED — {ex.GetType().Name}: {ex.Message}"); }
                        }
                    }
                    AssetDatabase.SaveAssets();
                    llog.AppendLine("\nRe-run Preflight — external-reference errors should be gone. " +
                                    "Commit Assets/_COREHOLD/Vendored with the remapped scenes.");
                    ShowReport(llog.ToString());
                }
                if (GUILayout.Button("BUILD shippable game (WebGL)", GUILayout.Height(24), GUILayout.Width(220)))
                {
                    // Preflight runs inside and aborts on errors; the report
                    // lands in the console either way.
                    string built = CampaignShipTool.BuildCampaign(_authoring, null);
                    ShowReport(built != null
                        ? $"Build succeeded → {built}\nServe it (python3 -m http.server) — WebGL does not run from file://."
                        : "Build did not run or failed — the console has the preflight/build report.");
                }
            }
        }

        // ---------------------------------------------------- issues & report

        private void DrawIssues()
        {
            EditorGUILayout.Space(14);
            GUILayout.Label("Issues", EditorStyles.boldLabel);
            if (_issues.Count == 0)
                EditorGUILayout.HelpBox("Nothing blocking — run Preflight for the shippability truth, then BUILD.",
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(string.Join("\n", _issues), MessageType.Warning);
        }

        /// <summary>Set the report AND scroll to it — it renders at the bottom of
        /// the window, below the fold once a few stages exist.</summary>
        private void ShowReport(string text)
        {
            _report = text;
            _scroll.y = float.MaxValue;
            Repaint();
        }

        private void DrawReport()
        {
            if (string.IsNullOrEmpty(_report)) return;
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Last action report", EditorStyles.boldLabel);
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

            // The campaign's home folders exist from the FIRST action — they
            // used to appear only mid-relocation, so an early stage failure
            // left no campaign footprint on disk at all.
            BuildCampaignScenes.EnsureFolder(_authoring.SceneFolder);
            BuildCampaignScenes.EnsureFolder(_authoring.DataFolder);
            BuildCampaignScenes.EnsureFolder(BuildCampaignScenes.ManifestDir);

            UISkin.Active = _authoring.uiSkin; // the skin bakes in at build time (BuildRealUI reads it)
            try
            {
                for (int i = 0; i < _authoring.stages.Count; i++)
                {
                    if (GenerateStageInternal(i, log)) passed++;
                    else break; // a failed stage stops the batch — read the transcript, fix, resume
                }
            }
            finally
            {
                UISkin.Active = null;
            }
            log.AppendLine($"\n{passed}/{_authoring.stages.Count} levels generated.");
            if (passed == _authoring.stages.Count)
            {
                // Carry economics change what the gates certified (A2): every
                // generated stage must also hold at the campaign's entry floor,
                // or the manifest is withheld until the rules and maps agree.
                if (!VerifyCarryEconomy(log))
                {
                    log.AppendLine("\nMANIFEST WITHHELD — the carry rules and the generated maps disagree (rows " +
                                   "above). Raise baseSalvagePerLevel, soften the rules, or regenerate; " +
                                   "'Emit manifest' remains available as a manual override.");
                }
                else
                {
                    bool wired = EmitManifest();
                    RegisterCampaign();
                    log.AppendLine(wired
                        ? "Manifest emitted (wired into the Welcome scene) and Build Settings registered — " +
                          "open the Welcome scene and press Play."
                        : "Manifest emitted and Build Settings registered — but the Welcome scene is NOT " +
                          "wired yet (build the menu scenes, step 5, then Emit manifest again).");
                }
            }
            ShowReport(log.ToString());
        }

        /// <summary>
        /// A2 economy verify: re-run the model per stage at the campaign's
        /// WORST-CASE entry bank — the base floor, what a player who arrives
        /// broke actually gets. The generation gates certified the difficulty
        /// default; a floor below it can make a certified map unwinnable, and a
        /// floor above it trivializes it — both are findings. Reset campaigns
        /// with no floor have nothing to verify (entry == what gates ran).
        /// </summary>
        private bool VerifyCarryEconomy(StringBuilder log)
        {
            var rules = _authoring.progression;
            int worstEntry = rules.baseSalvagePerLevel;
            bool carryMode = rules.economyCarry != ProgressionRules.EconomyCarry.ResetPerLevel;

            if (worstEntry <= 0)
            {
                if (carryMode)
                    log.AppendLine("\nCarry verify: no baseSalvagePerLevel floor is authored — a broke arrival " +
                                   "falls back to the difficulty default, which the gates already certified. " +
                                   "Consider authoring an explicit floor so the worst case is a design choice.");
                return true;
            }

            log.AppendLine($"\nCarry verify — worst-case entry {worstEntry} salvage per stage:");
            bool allOk = true;
            _pendingTunes.Clear();
            foreach (var stage in _authoring.stages)
            {
                if (string.IsNullOrEmpty(stage.scenePath)) continue;

                var scene = EditorSceneManager.OpenScene(stage.scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                var routes = new List<Corehold.Core.PathRoute>(
                    Object.FindObjectsByType<Corehold.Core.PathRoute>(FindObjectsSortMode.None));
                var spawners = Object.FindObjectsByType<Spawner>(FindObjectsSortMode.None);
                var air = spawners.FirstOrDefault(s => s.name.Contains("Air"));
                var anySpawner = spawners.FirstOrDefault(s => s.CoreTarget != null);
                var def = string.IsNullOrEmpty(stage.levelDefPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<LevelDefinition>(stage.levelDefPath);
                if (def == null)
                {
                    // Manual/forked stages carry no tracked def path — read the
                    // rules straight off the opened scene's WaveManager instead.
                    var sceneWm = Object.FindFirstObjectByType<WaveManager>();
                    if (sceneWm != null)
                        def = new SerializedObject(sceneWm).FindProperty("level")
                                  .objectReferenceValue as LevelDefinition;
                }

                if (routes.Count == 0 || anySpawner == null || def == null)
                {
                    log.AppendLine($"  {scene.name}: verify SKIPPED — scene wiring incomplete " +
                                   $"(routes {routes.Count}, core {(anySpawner != null ? "ok" : "missing")}, " +
                                   $"definition {(def != null ? "ok" : "missing")}).");
                    allOk = false;
                    continue;
                }

                // Live certification: verify against the wave/enemy assets this
                // stage ACTUALLY plays (synthesized, adopted or hand-edited) —
                // an edit to any of them changes this verdict on the next click.
                string wavesJson = WaveTableExporter.Export(def, out string waveErr);
                if (wavesJson == null)
                {
                    log.AppendLine($"  {scene.name}: verify FAILED — wave export: {waveErr}");
                    allOk = false;
                    continue;
                }

                Vector3 airSpawn = air != null ? air.transform.position : anySpawner.transform.position;
                BalanceModelRunner.Result result;
                try
                {
                    result = BalanceModelRunner.Run(routes, airSpawn, anySpawner.CoreTarget.position,
                                                    solveGrowth: false, hpGrowth: def.hpGrowthPerWave,
                                                    maxLive: def.maxLiveEnemies, out string err,
                                                    startingSalvage: worstEntry,
                                                    wavesJsonPath: wavesJson,
                                                    towerDpsMult: def.towerDamageMultiplier,
                                                    towerRangeMult: def.towerRangeMultiplier);
                    if (result == null)
                    {
                        log.AppendLine($"  {scene.name}: verify FAILED to run — {err}");
                        allOk = false;
                    }
                }
                finally
                {
                    try { System.IO.File.Delete(wavesJson); } catch { /* temp file */ }
                }
                if (result == null)
                    continue;

                if (!result.in_band)
                {
                    log.AppendLine($"  {scene.name}: OUT OF BAND at entry {worstEntry} " +
                                   $"(growth {def.hpGrowthPerWave:0.###}, live waves) — the floor " +
                                   "cannot hold this map as its waves stand today.");
                    allOk = false;

                    // The model's fix suggestions: per-wave advice lines, then
                    // its counts-only tune. Recipe stages never get the tune
                    // applied (regeneration would overwrite it and the seed
                    // would no longer reproduce the waves) — their knob is the
                    // recipe itself, which the advice translates to.
                    foreach (var row in result.rows)
                        if (row.advice != null)
                            foreach (string a in row.advice)
                                log.AppendLine($"      wave {row.wave}: {a}");
                    string tune = BalanceModelRunner.DescribeSuggestedTune(result);
                    if (tune.Length > 0)
                    {
                        foreach (string line in tune.Split('\n'))
                            log.AppendLine("      " + line);
                        if (RecipeFor(stage) != null)
                        {
                            log.AppendLine("      synthesized stage — apply the intent to the RECIPE " +
                                           "(budgetBase / budgetGrowthPerWave / escalationPerStage / roster) " +
                                           "and regenerate; a direct tune would not survive regeneration.");
                        }
                        else if (BalanceModelRunner.HasSuggestedTune(result))
                        {
                            _pendingTunes[_authoring.stages.IndexOf(stage)] = result;
                            log.AppendLine("      → 'Apply suggested tune' under step 6 writes the defender " +
                                           "package into the stage's definition and the count changes into " +
                                           "its own wave assets.");
                        }
                    }
                }
                else
                {
                    log.AppendLine($"  {scene.name}: in band at entry {worstEntry} (live waves).");
                }
            }
            return allOk;
        }

        private void GenerateStage(int index)
        {
            var log = new StringBuilder();
            bool ok;
            UISkin.Active = _authoring.uiSkin;
            try { ok = GenerateStageInternal(index, log); }
            finally { UISkin.Active = null; }
            if (ok)
            {
                bool wired = EmitManifest();
                RegisterCampaign();
                log.AppendLine("Manifest + Build Settings refreshed for the regenerated stage." +
                               (wired ? "" : " (Welcome scene NOT wired — build the menu scenes, then emit again.)"));
            }
            ShowReport(log.ToString());
        }

        private bool GenerateStageInternal(int index, StringBuilder log)
        {
            var stage = _authoring.stages[index];

            // Manual stage: a hand-authored or forked scene (Clone Level) used
            // as-is — no blueprint, nothing to generate, nothing to relocate.
            // Linear incremental campaigns are built from exactly these.
            if (stage.blueprint == null && !string.IsNullOrEmpty(stage.scenePath))
            {
                if (!System.IO.File.Exists(stage.scenePath))
                {
                    log.AppendLine($"Level {index + 1} '{stage.title}': manual scene missing on disk — {stage.scenePath}");
                    return false;
                }
                log.AppendLine($"— Level {index + 1} '{stage.title}' — manual scene, kept as-is: {stage.scenePath}");
                return true;
            }

            if (stage.blueprint == null)
            {
                log.AppendLine($"Level {index + 1}: no blueprint assigned (assign one, or pick an existing scene " +
                               "to use the stage as a manual/forked level).");
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

            // Synthesized waves invalidate the growth the pipeline solved
            // against the emitted definition's original tables — re-solve
            // against the waves this level will actually play, on the same open
            // scene's real geometry, and bake the result into the stage's
            // LevelDefinition. Exported fresh from the relocated assets (the
            // definition is already rewired to them), so what is certified is
            // what is on disk — no temp-file twin to go stale.
            if (RecipeFor(stage) != null)
            {
                var routes = new List<Corehold.Core.PathRoute>(
                    Object.FindObjectsByType<Corehold.Core.PathRoute>(FindObjectsSortMode.None));
                var spawners = Object.FindObjectsByType<Spawner>(FindObjectsSortMode.None);
                var airSp = spawners.FirstOrDefault(s => s.name.Contains("Air"));
                var coreSp = spawners.FirstOrDefault(s => s.CoreTarget != null);
                if (routes.Count == 0 || coreSp == null)
                {
                    log.AppendLine("  wave re-solve SKIPPED — scene geometry not reachable; the stage's " +
                                   "growth still describes the pre-synthesis tables. Run Validate/Run Balance Model.");
                    return false;
                }

                string wavesJson = WaveTableExporter.Export(def, out string waveErr);
                if (wavesJson == null)
                {
                    log.AppendLine($"  wave re-solve FAILED — wave export: {waveErr}");
                    return false;
                }

                BalanceModelRunner.Result model;
                try
                {
                    model = BalanceModelRunner.Run(
                        routes, airSp != null ? airSp.transform.position : coreSp.transform.position,
                        coreSp.CoreTarget.position, solveGrowth: true, hpGrowth: 0f,
                        maxLive: def.maxLiveEnemies, out string err, wavesJsonPath: wavesJson,
                        startingSalvage: def.startingSalvage > 0 ? def.startingSalvage : -1,
                        towerDpsMult: def.towerDamageMultiplier,
                        towerRangeMult: def.towerRangeMultiplier);
                    if (model == null)
                        log.AppendLine($"  wave re-solve FAILED to run — {err}");
                }
                finally
                {
                    try { System.IO.File.Delete(wavesJson); } catch { /* temp file */ }
                }
                if (model == null)
                    return false;
                if (!model.in_band)
                {
                    log.AppendLine($"  synthesized waves OUT OF BAND even at solved growth " +
                                   $"{model.solved_hp_growth:0.###} — soften the recipe (budget, growth, " +
                                   "escalation) or reseed the stage. The model suggests:");
                    foreach (var row in model.rows)
                        if (row.advice != null)
                            foreach (string a in row.advice)
                                log.AppendLine($"      wave {row.wave}: {a}");
                    string tune = BalanceModelRunner.DescribeSuggestedTune(model);
                    if (tune.Length > 0)
                    {
                        foreach (string line in tune.Split('\n'))
                            log.AppendLine("      " + line);
                        log.AppendLine("      (synthesized stage — translate this into recipe knobs; a direct " +
                                       "tune would not survive regeneration)");
                    }
                    return false;
                }

                var defSo2 = new SerializedObject(def);
                defSo2.FindProperty("hpGrowthPerWave").floatValue = model.solved_hp_growth;
                defSo2.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                AssetDatabase.SaveAssets();
                log.AppendLine($"  waves certified: growth re-solved to {model.solved_hp_growth:0.###}, all in band.");
            }

            stage.acceptedSeed = usedSeed;
            EditorUtility.SetDirty(_authoring);
            AssetDatabase.SaveAssets();
            log.AppendLine($"  seed {usedSeed} accepted → {stage.scenePath}");

            // Self-containment: the committed stage must not reference
            // git-ignored vendor assets, or the campaign builds broken on any
            // other machine. Copy + remap, then reload the open scene so the
            // editor matches the rewritten file. Best-effort — a hiccup here
            // must not fail an accepted stage; preflight re-detects.
            try
            {
                LocalizeStage(stage, log);
            }
            catch (System.Exception ex)
            {
                log.AppendLine($"  localize WARNED — {ex.GetType().Name}: {ex.Message} " +
                               "(stage kept; preflight will re-detect external refs).");
            }
            return true;
        }

        /// <summary>Copy every vendor dependency of a stage's scene + rules into
        /// the committed Vendored folder and remap; reloads the scene when its
        /// file was rewritten.</summary>
        private void LocalizeStage(CampaignAuthoring.AuthoredStage stage, StringBuilder log)
        {
            if (string.IsNullOrEmpty(stage.scenePath))
                return;
            int copied = VendorLocalizer.Localize(
                new[] { stage.scenePath, stage.levelDefPath }, log);
            if (copied > 0)
            {
                var open = EditorSceneManager.GetActiveScene();
                if (open.IsValid() && open.path == stage.scenePath)
                    EditorSceneManager.OpenScene(stage.scenePath,
                        UnityEditor.SceneManagement.OpenSceneMode.Single);
            }
        }

        /// <summary>The recipe THIS stage synthesizes with: its own bespoke
        /// override when set, else the campaign's escalating programme.</summary>
        private WaveRecipe RecipeFor(CampaignAuthoring.AuthoredStage stage) =>
            stage.waveRecipe != null ? stage.waveRecipe : _authoring.waveRecipe;

        /// <summary>
        /// Adopt a MANUAL stage whose scene lives in the git-ignored generated
        /// folders: copy the scene BYTE-IDENTICALLY into the campaign's scene
        /// folder, copy its LevelDefinition and deep-clone its wave tables into
        /// campaign data, rewire the COPY (never the source) to the copied
        /// rules, and track everything on the stage. The stage stays manual —
        /// it plays exactly the scene the user accepted, nothing regenerates —
        /// but becomes campaign-owned: preflight's Generated-folder error, the
        /// untracked-rules warning and the wave-locality check all clear, and
        /// the original keeps working for single-map play.
        /// </summary>
        private bool AdoptManualStage(int index, StringBuilder log)
        {
            var stage = _authoring.stages[index];
            string src = stage.scenePath;
            log.AppendLine($"— Adopt Level {index + 1} '{stage.title}' —");
            if (string.IsNullOrEmpty(src) || !System.IO.File.Exists(src))
            {
                log.AppendLine("  FAILED: the stage's scene is missing on disk.");
                return false;
            }

            BuildCampaignScenes.EnsureFolder(_authoring.SceneFolder);
            BuildCampaignScenes.EnsureFolder(_authoring.DataFolder);

            string tag = $"L{index + 1:00}";
            string sceneDest = $"{_authoring.SceneFolder}/{tag}_{System.IO.Path.GetFileName(src)}";
            AssetDatabase.DeleteAsset(sceneDest);
            if (!AssetDatabase.CopyAsset(src, sceneDest))
            {
                log.AppendLine($"  FAILED: could not copy the scene to {sceneDest}.");
                return false;
            }
            log.AppendLine($"  scene copied → {sceneDest} (source untouched).");

            // Open the COPY additively — same no-switch pattern as the manifest
            // wiring — read its wired LevelDefinition, and rewire it to the
            // campaign-owned copies.
            var scene = EditorSceneManager.OpenScene(sceneDest, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            bool removeCopy = false;
            try
            {
                WaveManager wm = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    wm = root.GetComponentInChildren<WaveManager>(true);
                    if (wm != null)
                        break;
                }
                var wmSo = wm != null ? new SerializedObject(wm) : null;
                var def = wmSo != null ? wmSo.FindProperty("level").objectReferenceValue as LevelDefinition : null;
                if (def == null)
                {
                    log.AppendLine("  FAILED: the scene has no WaveManager with a wired LevelDefinition — " +
                                   "not a playable level. Copy removed; the stage still points at the original.");
                    removeCopy = true;
                    return false;
                }

                string defDest = $"{_authoring.DataFolder}/{tag}_{def.name}.asset";
                AssetDatabase.DeleteAsset(defDest);
                var defCopy = Object.Instantiate(def);
                defCopy.name = def.name;
                AssetDatabase.CreateAsset(defCopy, defDest);

                string wavesFolder = $"{_authoring.DataFolder}/{tag}_Waves";
                AssetDatabase.DeleteAsset(wavesFolder);
                BuildCampaignScenes.EnsureFolder(wavesFolder);
                var defSo = new SerializedObject(defCopy);
                var wavesProp = defSo.FindProperty("waves");
                int cloned = 0;
                for (int w = 0; w < wavesProp.arraySize; w++)
                {
                    var element = wavesProp.GetArrayElementAtIndex(w);
                    var shared = element.objectReferenceValue as WaveDefinition;
                    if (shared == null)
                        continue;
                    var copy = Object.Instantiate(shared);
                    copy.name = shared.name;
                    AssetDatabase.CreateAsset(copy, $"{wavesFolder}/{shared.name}.asset");
                    element.objectReferenceValue = copy;
                    cloned++;
                }
                defSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(defCopy);

                wmSo.FindProperty("level").objectReferenceValue = defCopy;
                wmSo.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                AssetDatabase.MakeEditable(sceneDest);
                if (!EditorSceneManager.SaveScene(scene))
                {
                    log.AppendLine("  FAILED: could not save the rewired copy (locked by version control?). " +
                                   "Copy removed; the stage still points at the original.");
                    removeCopy = true;
                    return false;
                }

                stage.scenePath = sceneDest;
                stage.levelDefPath = defDest;
                stage.wavesFolder = wavesFolder;
                // The generator stamps the seed into the filename — recover it
                // so the stage records which seed this map came from.
                string stem = System.IO.Path.GetFileNameWithoutExtension(src);
                int sIdx = stem.LastIndexOf("_s", System.StringComparison.Ordinal);
                if (sIdx >= 0 && int.TryParse(stem.Substring(sIdx + 2), out int seed))
                    stage.acceptedSeed = seed;
                EditorUtility.SetDirty(_authoring);
                AssetDatabase.SaveAssets();

                log.AppendLine($"  rules copied → {defDest}; {cloned} wave table(s) deep-cloned → {wavesFolder}.");
                log.AppendLine("  stage is now campaign-owned — still manual, plays exactly the scene you accepted.");
                // Self-containment for the adopted copy too. The additive copy
                // is closed WITHOUT saving in the finally below, so rewriting
                // its file here is safe — disk keeps the remap, memory is
                // discarded.
                VendorLocalizer.Localize(new[] { sceneDest, defDest }, log);
                return true;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (removeCopy)
                    AssetDatabase.DeleteAsset(sceneDest);
            }
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

            string wavesFolder = $"{_authoring.DataFolder}/{tag}_Waves";
            AssetDatabase.DeleteAsset(wavesFolder);
            EnsureFolder(wavesFolder);

            var defSo = new SerializedObject(def);
            var wavesProp = defSo.FindProperty("waves");

            WaveRecipe recipe = RecipeFor(stage);
            if (recipe != null)
            {
                // Part C: SYNTHESIZE this stage's waves from the recipe — the
                // variability lane. Deterministic from the stage's accepted
                // seed; the re-solve that follows certifies the CREATED assets
                // via WaveTableExporter (no side-channel twin).
                // The CAMPAIGN recipe is a programme: it escalates by stage
                // position. A STAGE OVERRIDE is bespoke: evaluated at position
                // 0, exactly as authored — what you tuned is what plays.
                int stagePos = stage.waveRecipe != null
                    ? 0
                    : Mathf.Max(0, _authoring.stages.IndexOf(stage));
                int groundRoutes = Object.FindObjectsByType<Corehold.Core.PathRoute>(FindObjectsSortMode.None).Length;
                var synth = WaveSynthesizer.Synthesize(recipe, stagePos, seed,
                                                       groundRoutes, wavesFolder);
                log.Append(synth.transcript);
                if (synth.waves == null || synth.waves.Length == 0)
                    return false; // the transcript already says why

                wavesProp.arraySize = synth.waves.Length;
                for (int w = 0; w < synth.waves.Length; w++)
                    wavesProp.GetArrayElementAtIndex(w).objectReferenceValue = synth.waves[w];
                defSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(def);
                AssetDatabase.SaveAssets();

                stage.scenePath = sceneDest;
                stage.levelDefPath = defDest;
                stage.wavesFolder = wavesFolder;
                log.AppendLine($"  {synth.waves.Length} waves SYNTHESIZED from '{recipe.name}' " +
                               (stage.waveRecipe != null
                                   ? "(stage override — as authored, no positional escalation)."
                                   : "(campaign programme)."));
                return true;
            }

            // No recipe: deep-clone the shipped wave tables (plan v2 §A.3) — the
            // emitted definition is a shallow clone still pointing at the SHARED
            // shipped WaveDefinition assets, and per-stage edits would otherwise
            // change every level at once, shipped map included.
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
            // new ones are written — but ONLY outputs THIS CAMPAIGN OWNS, i.e.
            // paths inside its scene/data folders. A stage can carry a MANUAL
            // scene (a Clone Level fork, a hand-authored level): assigning a
            // blueprint to such a stage used to route the user's scene through
            // this delete and DESTROY it. Ownership is the guard, not intent.
            int removed = 0;
            if (DeleteOwned(stage.scenePath, _authoring.SceneFolder, log)) removed++;
            if (DeleteOwned(stage.levelDefPath, _authoring.DataFolder, log)) removed++;
            if (DeleteOwned(stage.wavesFolder, _authoring.DataFolder, log)) removed++;
            if (removed > 0) log.AppendLine($"  deleted {removed} superseded campaign-owned output(s).");
            stage.scenePath = stage.levelDefPath = stage.wavesFolder = null;
            stage.acceptedSeed = 0;
        }

        /// <summary>Delete <paramref name="path"/> only when it lives under the
        /// campaign-owned <paramref name="ownedRoot"/>. Anything else (manual
        /// scenes, forks, shipped assets) is released from the stage RECORD but
        /// never touched on disk — and the log says so.</summary>
        private static bool DeleteOwned(string path, string ownedRoot, StringBuilder log)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (string.IsNullOrEmpty(ownedRoot) || !path.StartsWith(ownedRoot + "/"))
            {
                log.AppendLine($"  released '{path}' from the stage — not campaign-owned, never deleted by the builder.");
                return false;
            }
            return AssetDatabase.DeleteAsset(path);
        }

        // ----------------------------------------------------------- manifest

        /// <summary>Emit the runtime manifest and wire it into THIS campaign's
        /// Welcome scene. Returns whether the wiring actually happened — a
        /// manifest the Welcome scene does not reference boots to a dead menu,
        /// so callers surface a false loudly instead of assuming.</summary>
        private bool EmitManifest()
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

            // Wire into THIS campaign's welcome scene — the authored path, not
            // a fixed one (the old hardcoded target silently missed campaigns
            // whose welcome scene lived anywhere else).
            _wireCheckStamp = 0; // invalidate the cached wiring check
            return BuildCampaignScenes.WireManifestIntoWelcome(manifest, _authoring.welcomeScenePath);
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

        // One hardened implementation for every campaign-side folder create.
        private static void EnsureFolder(string path) => BuildCampaignScenes.EnsureFolder(path);

        // ---- Welcome-scene wiring check (cheap, cached by file timestamp) ----

        private string _wireCheckScene, _wireCheckGuid;
        private long _wireCheckStamp;
        private bool _wireCheckResult;

        /// <summary>
        /// True when the Welcome scene FILE references this campaign's manifest
        /// asset (by GUID). Text scan cached on the scene's write time, so the
        /// Issues panel can verify the wiring live without opening the scene —
        /// this is the check that catches "the manifest field went stale".
        /// </summary>
        private bool WelcomeWiredToManifest()
        {
            string scenePath = _authoring.welcomeScenePath;
            if (string.IsNullOrEmpty(scenePath) || !System.IO.File.Exists(scenePath))
                return false;
            string guid = AssetDatabase.AssetPathToGUID(_authoring.ManifestAssetPath);
            if (string.IsNullOrEmpty(guid))
                return false;

            long stamp = System.IO.File.GetLastWriteTimeUtc(scenePath).Ticks;
            if (scenePath == _wireCheckScene && guid == _wireCheckGuid && stamp == _wireCheckStamp)
                return _wireCheckResult;

            _wireCheckScene = scenePath;
            _wireCheckGuid = guid;
            _wireCheckStamp = stamp;
            _wireCheckResult = System.IO.File.ReadAllText(scenePath).Contains(guid);
            return _wireCheckResult;
        }
    }
}
