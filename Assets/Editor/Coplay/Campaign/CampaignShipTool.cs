using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// Turning an approved campaign into a shippable build (plan v2 §A.3 / D1).
    ///
    /// Two steps, deliberately separate:
    ///
    ///   • <b>Preflight</b> — everything that is true in the editor but can be
    ///     false in a player: a scene missing from Build Settings, a stage still
    ///     pointing at the git-ignored Generated folder, wave tables that are
    ///     still the SHARED shipped assets rather than the stage's own clones, a
    ///     build-menu turret with no prefab (a dead button), art that only exists
    ///     on this machine. Every one of these ships silently and breaks later.
    ///   • <b>Build</b> — BuildPipeline with EXACTLY the campaign's scenes in
    ///     campaign order, Welcome first. Refuses to run while preflight has
    ///     errors, because a build that boots into the wrong scene is worse than
    ///     no build.
    ///
    /// The build deliberately excludes Game.unity and any other single-map scene:
    /// "ship this campaign" means the player boots into the campaign, and unused
    /// scenes are payload a WebGL download does not need.
    /// </summary>
    public static class CampaignShipTool
    {
        private const string DefaultBuildRoot = "Builds/WebGL";

        public class Check
        {
            public bool error;      // blocks the build
            public string message;
        }

        // -------------------------------------------------------- menu surface

        [MenuItem("Tools/COREHOLD/Campaign/Preflight (is this campaign shippable?)", false, 20)]
        public static void PreflightMenu()
        {
            CampaignPicker.Open("Preflight", c =>
            {
                string report = PreflightReport(c.manifest, c.authoring, out bool ok, out int errors);
                Debug.Log(report);
                EditorUtility.DisplayDialog("Campaign Preflight",
                    $"Campaign '{c.Id}':\n\n" +
                    (ok ? "READY — no blocking errors."
                        : $"NOT SHIPPABLE — {errors} blocking error(s).") +
                    "\n\nThe full line-by-line report is in the Console.", "OK");
            });
        }

        [MenuItem("Tools/COREHOLD/Campaign/Build Shippable Game (WebGL)", false, 21)]
        public static void BuildMenu()
        {
            CampaignPicker.Open("Build", c => BuildCampaign(c.manifest, c.authoring, null));
        }

        // ------------------------------------------------------------- picker

        /// <summary>One checkable campaign: an authoring asset (manifest may not
        /// be emitted yet — that is a preflight finding) or a bare manifest.</summary>
        public struct Candidate
        {
            public CampaignManifest manifest;
            public CampaignAuthoring authoring;

            public string Id => authoring != null ? authoring.campaignId
                              : manifest != null ? manifest.campaignId : "?";

            public string Label
            {
                get
                {
                    if (authoring != null)
                        return $"{Id}  —  authoring '{authoring.name}'" +
                               (manifest != null ? "" : "   (manifest not emitted yet)");
                    return $"{Id}  —  manifest-only '{manifest.name}'   (test flow)";
                }
            }
        }

        /// <summary>Every campaign in the project, authoring-backed first. An
        /// authoring's own emitted manifest is folded into its row, not listed twice.</summary>
        public static List<Candidate> AllCandidates()
        {
            var list = new List<Candidate>();
            var authorings = FindAll<CampaignAuthoring>();
            var claimedManifestPaths = new HashSet<string>();

            foreach (var a in authorings)
            {
                var m = AssetDatabase.LoadAssetAtPath<CampaignManifest>(a.ManifestAssetPath);
                if (m != null) claimedManifestPaths.Add(a.ManifestAssetPath);
                list.Add(new Candidate { authoring = a, manifest = m });
            }

            foreach (var m in FindAll<CampaignManifest>())
                if (!claimedManifestPaths.Contains(AssetDatabase.GetAssetPath(m)))
                    list.Add(new Candidate { manifest = m });

            return list;
        }

        /// <summary>
        /// The explicit ask: which campaign? Always shown — with several
        /// campaigns in a project, guessing from selection state is how the
        /// wrong one gets built. The Project-view selection only preselects.
        /// </summary>
        private class CampaignPicker : EditorWindow
        {
            private List<Candidate> _candidates;
            private string _action;
            private System.Action<Candidate> _onPick;
            private Vector2 _scroll;

            public static void Open(string action, System.Action<Candidate> onPick)
            {
                var candidates = AllCandidates();
                if (candidates.Count == 0)
                {
                    EditorUtility.DisplayDialog($"Campaign {action}",
                        "No campaign found: no CampaignAuthoring and no CampaignManifest in the project.\n\n" +
                        "Make one with the Campaign Builder (Tools → COREHOLD → Campaign), or the test " +
                        "flow's 'Create Test Manifest'.", "OK");
                    return;
                }

                // The Project-view selection floats to the top as the default.
                var selected = Selection.activeObject;
                candidates = candidates
                    .OrderByDescending(c => (Object)c.authoring == selected || (Object)c.manifest == selected)
                    .ToList();

                var w = CreateInstance<CampaignPicker>();
                w.titleContent = new GUIContent($"Campaign {action}");
                w._candidates = candidates;
                w._action = action;
                w._onPick = onPick;
                w.minSize = new Vector2(460, Mathf.Min(120 + candidates.Count * 30, 420));
                w.ShowUtility();
            }

            private void OnGUI()
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField($"Which campaign do you want to {_action.ToLowerInvariant()}?",
                                           EditorStyles.boldLabel);
                EditorGUILayout.Space(4);

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var c in _candidates)
                {
                    if (GUILayout.Button(c.Label, GUILayout.Height(26)))
                    {
                        var pick = c;
                        var act = _onPick;
                        // Defer past this GUI pass: the action may start a full
                        // player build, which must not run inside OnGUI.
                        EditorApplication.delayCall += () => act?.Invoke(pick);
                        Close();
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(6);
                if (GUILayout.Button("Cancel"))
                    Close();
            }
        }

        private static List<T> FindAll<T>() where T : Object
        {
            return AssetDatabase.FindAssets($"t:{typeof(T).Name}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(a => a != null).ToList();
        }

        // ----------------------------------------------------------- preflight

        /// <summary>Convenience overload for the Campaign Builder window.</summary>
        public static string PreflightReport(CampaignAuthoring authoring, out bool ok)
        {
            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(authoring.ManifestAssetPath);
            return PreflightReport(manifest, authoring, out ok, out _);
        }

        /// <summary>Human-readable preflight; <paramref name="ok"/> is false when
        /// any ERROR-level check failed. <paramref name="authoring"/> may be null
        /// (manifest-only campaign) — authoring-dependent checks degrade to warnings.</summary>
        public static string PreflightReport(CampaignManifest manifest, CampaignAuthoring authoring,
                                             out bool ok, out int errors)
        {
            var checks = Preflight(manifest, authoring);
            errors = checks.Count(c => c.error);
            ok = errors == 0;

            string id = manifest != null ? manifest.campaignId
                      : authoring != null ? authoring.campaignId : "?";
            var sb = new StringBuilder();
            sb.AppendLine($"[Ship] PREFLIGHT — campaign '{id}'" +
                          (authoring == null ? "  (manifest-only — no authoring asset)" : ""));
            if (checks.Count == 0)
                sb.AppendLine("  (no findings)");
            foreach (var c in checks)
                sb.AppendLine($"  {(c.error ? "ERROR" : "warn ")}  {c.message}");
            sb.AppendLine(ok
                ? "  READY — no blocking errors. Warnings above are judgement calls, not stoppers."
                : "  NOT SHIPPABLE — fix the ERROR lines and re-run preflight.");
            return sb.ToString();
        }

        public static List<Check> Preflight(CampaignManifest manifest, CampaignAuthoring authoring)
        {
            var checks = new List<Check>();
            void Error(string m) => checks.Add(new Check { error = true, message = m });
            void Warn(string m) => checks.Add(new Check { error = false, message = m });

            // ---- manifest ----
            if (manifest == null)
            {
                Error(authoring != null
                    ? $"no manifest at {authoring.ManifestAssetPath} — press 'Emit manifest' in the Campaign Builder."
                    : "no CampaignManifest — press 'Emit manifest' in the Campaign Builder (or 'Create Test Manifest').");
                return checks; // everything else reads the manifest
            }
            if (manifest.LevelCount == 0)
                Error("the manifest has no Level stages — generate at least one level.");
            if (manifest.StageOfKind(CampaignStageKind.Welcome) == null)
                Error("no Welcome stage in the manifest — the build would boot into a level.");
            if (manifest.StageOfKind(CampaignStageKind.Closing) == null)
                Warn("no Closing stage — finishing the last level will fall back to the Welcome screen.");

            // ---- scenes exist, are registered, and boot in the right order ----
            var enabled = EditorBuildSettings.scenes.Where(s => s.enabled).ToList();
            foreach (var stage in manifest.stages)
            {
                if (string.IsNullOrEmpty(stage.scenePath))
                {
                    Error($"stage '{stage.title}' has no scene path.");
                    continue;
                }
                if (!System.IO.File.Exists(stage.scenePath))
                {
                    Error($"stage '{stage.title}': scene missing on disk — {stage.scenePath}");
                    continue;
                }
                if (!enabled.Any(s => s.path == stage.scenePath))
                    Error($"stage '{stage.title}': not an enabled scene in Build Settings — " +
                          "press 'Register Campaign'.");

                // D1: campaign output must live in a COMMITTED folder. A scene
                // still under Scenes/Generated builds fine here and vanishes on a
                // fresh clone, because that folder is git-ignored.
                if (stage.scenePath.Contains("/Scenes/Generated/"))
                    Error($"stage '{stage.title}' still lives in the git-ignored Scenes/Generated — " +
                          "generate the stage through the Campaign Builder, which relocates output to " +
                          (authoring != null ? authoring.SceneFolder : "the committed Scenes/Campaign/<id>/ folder") +
                          ". (A test-manifest campaign always trips this: it exists to prove the FLOW, not to ship.)");
            }

            var welcome = manifest.StageOfKind(CampaignStageKind.Welcome);
            if (welcome != null && enabled.Count > 0 && enabled[0].path != welcome.scenePath)
                Error($"build index 0 is '{enabled[0].path}', not the Welcome scene — the player would " +
                      "boot into the wrong scene. Press 'Register Campaign'.");

            // ---- per-stage data: definition wired, wave tables stage-LOCAL ----
            if (authoring == null)
            {
                Warn("wave-table locality not checked — no CampaignAuthoring records where each stage's " +
                     "clones live. Manifest-only campaigns (the test flow) share the shipped wave tables " +
                     "by construction; build the real campaign through the Campaign Builder.");
            }
            else foreach (var stage in authoring.stages)
            {
                if (string.IsNullOrEmpty(stage.scenePath)) continue;

                // Manual stages (forks, hand-authored scenes) carry no tracked
                // rules path — Clone Level gives a fork its OWN definition and
                // wave clones, so this is informational, not a defect.
                if (string.IsNullOrEmpty(stage.levelDefPath))
                {
                    Warn($"stage '{stage.title}' is a manual scene — its rules identity is not tracked by " +
                         "the builder. If it came from Clone Level its definition/waves are its own; a raw " +
                         "scene copy shares them with its source.");
                    continue;
                }

                var def = AssetDatabase.LoadAssetAtPath<LevelDefinition>(stage.levelDefPath);
                if (def == null)
                {
                    Error($"stage '{stage.title}': LevelDefinition missing at '{stage.levelDefPath}'.");
                    continue;
                }
                if (def.waves == null || def.waves.Length == 0)
                {
                    Error($"stage '{stage.title}': its LevelDefinition has no waves.");
                    continue;
                }

                // The A1 invariant: a stage's waves are ITS OWN clones. If they
                // point back at the shared shipped tables, tuning one level
                // silently retunes every level (and the shipped map).
                int shared = 0;
                foreach (var w in def.waves)
                {
                    if (w == null) { Error($"stage '{stage.title}': a null entry in waves[]."); continue; }
                    string p = AssetDatabase.GetAssetPath(w);
                    if (string.IsNullOrEmpty(stage.wavesFolder) || !p.StartsWith(stage.wavesFolder))
                        shared++;
                }
                if (shared > 0)
                    Warn($"stage '{stage.title}': {shared} wave table(s) are NOT this stage's clones — " +
                         "editing them affects every level that shares them. Regenerate the stage to re-clone.");
            }

            // ---- build menu: dead buttons and blank icons ----
            var towers = RosterRegistry.AllTowersOrdered();
            var noPrefab = towers.Where(t => t.basePrefab == null).Select(t => t.name).ToList();
            if (noPrefab.Count > 0)
                Warn($"{noPrefab.Count} turret(s) have no basePrefab and ship as disabled 'WIP' buttons: " +
                     string.Join(", ", noPrefab));
            var noIcon = towers.Where(t => t.basePrefab != null && t.icon == null).Select(t => t.name).ToList();
            if (noIcon.Count > 0)
                Warn($"{noIcon.Count} buildable turret(s) have no icon (blank build-menu button): " +
                     string.Join(", ", noIcon) + " — run Tools/COREHOLD/Art/Render Icons.");

            // ---- art that only exists on this machine ----
            var vendorHits = new List<string>();
            foreach (var stage in manifest.stages)
            {
                if (string.IsNullOrEmpty(stage.scenePath) || !System.IO.File.Exists(stage.scenePath)) continue;
                var deps = AssetDatabase.GetDependencies(stage.scenePath, true)
                    .Where(d => d.StartsWith("Assets/Vendor/")).ToList();
                if (deps.Count > 0)
                    vendorHits.Add($"{System.IO.Path.GetFileName(stage.scenePath)} ({deps.Count})");
            }
            if (vendorHits.Count > 0)
                Warn($"scenes depending on git-ignored Assets/Vendor art: {string.Join(", ", vendorHits)}. " +
                     "The build works HERE because the pack is on this machine; a fresh clone or CI " +
                     "cannot reproduce it.");

            // ---- platform ----
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                Warn($"active build target is {EditorUserBuildSettings.activeBuildTarget}, not WebGL — " +
                     "building will switch targets first (slow, one-time reimport).");
            if (EditorUserBuildSettings.development)
                Warn("Development Build is ON — the debug console ships and the download is larger. " +
                     "Turn it off for a release build (it is what you want for a playtest build).");

            return checks;
        }

        // --------------------------------------------------------------- build

        /// <summary>Convenience overload for the Campaign Builder window.</summary>
        public static string BuildCampaign(CampaignAuthoring authoring, string outputDir)
        {
            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(authoring.ManifestAssetPath);
            return BuildCampaign(manifest, authoring, outputDir);
        }

        /// <summary>
        /// Build exactly this campaign. <paramref name="outputDir"/> null → the
        /// default Builds/WebGL/&lt;id&gt; under the project. Returns the output
        /// path, or null when preflight blocked or the build failed.
        /// </summary>
        public static string BuildCampaign(CampaignManifest manifest, CampaignAuthoring authoring, string outputDir)
        {
            string report = PreflightReport(manifest, authoring, out bool ok, out _);
            if (!ok)
            {
                Debug.LogError(report + "\n[Ship] Build ABORTED — preflight has errors.");
                return null;
            }
            Debug.Log(report);

            var scenes = manifest.stages
                .Select(s => s.scenePath)
                .Where(p => !string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
                .Distinct()
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[Ship] No scenes to build.");
                return null;
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                Debug.LogError("[Ship] The WebGL build module is not installed for this Unity version " +
                               "(Unity Hub → Installs → Add Modules → WebGL Build Support).");
                return null;
            }

            string dir = string.IsNullOrEmpty(outputDir)
                ? System.IO.Path.Combine(DefaultBuildRoot, manifest.campaignId)
                : outputDir;
            System.IO.Directory.CreateDirectory(dir);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,                       // campaign order — Welcome first
                locationPathName = dir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,           // release: no debug console, no profiler
            };

            Debug.Log($"[Ship] Building {scenes.Length} scene(s) → {dir}\n  " + string.Join("\n  ", scenes));
            BuildReport result = BuildPipeline.BuildPlayer(options);
            var summary = result.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[Ship] BUILD {summary.result} after {summary.totalTime:mm\\:ss} " +
                               $"({summary.totalErrors} error(s)). See the Console/Editor log above.");
                return null;
            }

            Debug.Log($"[Ship] BUILD SUCCEEDED — {dir}\n" +
                      $"  scenes {scenes.Length}, size {summary.totalSize / (1024f * 1024f):0.0} MB, " +
                      $"time {summary.totalTime:mm\\:ss}\n" +
                      "  WebGL needs to be SERVED, not opened from disk: run\n" +
                      $"    python3 -m http.server 8000 --directory \"{dir}\"\n" +
                      "  then open http://localhost:8000");
            return dir;
        }
    }
}
