using System.Collections.Generic;
using System.Linq;
using Corehold.Data;
using Corehold.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// A0 stub builders for the campaign menu scenes (plan v2 §A.5) and a test
    /// manifest — enough to walk Welcome → Level → Level → Closing end-to-end.
    /// Real Welcome/Closing polish (logo lockup, settings, records) is A1; these
    /// scenes are deliberately minimal so the flow can be proven first.
    ///
    /// Scenes land in Assets/_COREHOLD/Scenes/Campaign/ — the VERSIONED campaign
    /// home (decision D1): unlike Scenes/Generated, this folder is committed.
    /// </summary>
    public static class BuildCampaignScenes
    {
        internal const string SceneDir = "Assets/_COREHOLD/Scenes/Campaign";
        internal const string WelcomePath = SceneDir + "/Campaign_Welcome.unity";
        internal const string ClosingPath = SceneDir + "/Campaign_Closing.unity";
        internal const string ManifestDir = "Assets/_COREHOLD/Data/Campaign";
        internal const string ManifestPath = ManifestDir + "/Manifest_Test.asset";

        // The game's palette — hardcoded defaults (UITheme lives in gameplay
        // scenes and menu scenes must not depend on one existing), read through
        // the campaign UI skin when the Campaign Builder has one active.
        private static readonly Color DefaultBg = new Color(0.043f, 0.062f, 0.086f); // deep navy
        private static readonly Color DefaultPanel = new Color(0.075f, 0.11f, 0.15f);
        private static readonly Color DefaultCyan = new Color(0.20f, 0.95f, 0.95f);
        private static readonly Color DefaultTextDim = new Color(0.62f, 0.72f, 0.78f);
        private static Color Bg => UISkin.Active != null ? UISkin.Active.background : DefaultBg;
        private static Color Panel => UISkin.Active != null ? UISkin.Active.panel : DefaultPanel;
        private static Color Cyan => UISkin.Active != null ? UISkin.Active.accent : DefaultCyan;
        private static Color TextDim => UISkin.Active != null ? UISkin.Active.textDim : DefaultTextDim;

        [MenuItem("Tools/COREHOLD/Campaign/Build Welcome + Closing Scenes (stub)", false, 10)]
        public static void BuildBoth()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            EnsureFolder(SceneDir);

            BuildWelcome();
            BuildClosing();

            RegisterInBuildSettings(WelcomePath);
            RegisterInBuildSettings(ClosingPath);

            Debug.Log($"[Campaign] Built {WelcomePath} and {ClosingPath} and registered both in Build Settings.\n" +
                      "Next: Tools → COREHOLD → Campaign → Create Test Manifest, then open the Welcome scene and press Play.");
        }

        [MenuItem("Tools/COREHOLD/Campaign/Create Test Manifest (first 2 registered levels)", false, 11)]
        public static void CreateTestManifest()
        {
            // Level candidates: enabled Build Settings scenes emitted by the
            // generator. The campaign runtime loads by path, so registration is
            // the one hard requirement.
            var levels = EditorBuildSettings.scenes
                .Where(s => s.enabled && s.path.Contains("/Scenes/Generated/"))
                .Select(s => s.path)
                .Take(2)
                .ToList();

            if (levels.Count < 2)
            {
                Debug.LogError($"[Campaign] Need 2 generated scenes in Build Settings, found {levels.Count}. " +
                               "Generate levels first (Tools → COREHOLD → Level → Level Generator), then re-run this.");
                return;
            }

            EnsureFolder(ManifestDir);

            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(ManifestPath);
            bool created = manifest == null;
            if (created)
                manifest = ScriptableObject.CreateInstance<CampaignManifest>();

            manifest.campaignId = "test";
            manifest.displayName = "COREHOLD";
            manifest.progression = new ProgressionRules
            {
                economyCarry = ProgressionRules.EconomyCarry.ResetPerLevel,
            };
            manifest.stages = new List<CampaignStageInfo>
            {
                new CampaignStageInfo { kind = CampaignStageKind.Welcome, title = "Welcome", scenePath = WelcomePath },
                new CampaignStageInfo { kind = CampaignStageKind.Level, title = "Operation 1", scenePath = levels[0],
                                        briefing = "Hold the core. Two routes in, no second chances." },
                new CampaignStageInfo { kind = CampaignStageKind.Level, title = "Operation 2", scenePath = levels[1],
                                        briefing = "They know the way now. Expect pressure." },
                new CampaignStageInfo { kind = CampaignStageKind.Closing, title = "Debrief", scenePath = ClosingPath },
            };

            if (created)
                AssetDatabase.CreateAsset(manifest, ManifestPath);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();

            WireManifestIntoWelcome(manifest);

            Debug.Log($"[Campaign] Test manifest at {ManifestPath}:\n" +
                      $"  L1 {levels[0]}\n  L2 {levels[1]}\n" +
                      "Wired into the Welcome scene. Open it and press Play to run the campaign.");
        }

        // ------------------------------------------------------------- welcome

        private static void BuildWelcome()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            MakeMenuCamera();
            var canvas = MakeCanvas("Canvas_Welcome");
            MakeEventSystem();

            var title = MakeText(canvas.transform, "Title", "COREHOLD", 92, Cyan, new Vector2(0.5f, 0.72f));
            title.fontStyle = FontStyles.Bold;
            var subtitle = MakeText(canvas.transform, "Subtitle", "CAMPAIGN", 30, TextDim, new Vector2(0.5f, 0.60f));

            var normal = MakeButton(canvas.transform, "Btn_Normal", "NORMAL", new Vector2(0.5f, 0.46f));
            var veteran = MakeButton(canvas.transform, "Btn_Veteran", "VETERAN", new Vector2(0.5f, 0.36f));
            var nightmare = MakeButton(canvas.transform, "Btn_Nightmare", "NIGHTMARE", new Vector2(0.5f, 0.26f));
            var cont = MakeButton(canvas.transform, "Btn_Continue", "CONTINUE RUN", new Vector2(0.5f, 0.14f));
            var contLabel = cont.GetComponentInChildren<TMP_Text>();
            if (contLabel != null) // warm-role — it resumes, not restarts
                contLabel.color = UISkin.Active != null ? UISkin.Active.warm : new Color(1f, 0.72f, 0.25f);

            var welcome = canvas.gameObject.AddComponent<CampaignWelcome>();
            var so = new SerializedObject(welcome);
            so.FindProperty("titleLabel").objectReferenceValue = title;
            so.FindProperty("subtitleLabel").objectReferenceValue = subtitle;
            so.FindProperty("normalButton").objectReferenceValue = normal;
            so.FindProperty("veteranButton").objectReferenceValue = veteran;
            so.FindProperty("nightmareButton").objectReferenceValue = nightmare;
            so.FindProperty("continueButton").objectReferenceValue = cont;
            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(ManifestPath);
            if (manifest != null)
                so.FindProperty("manifest").objectReferenceValue = manifest;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, WelcomePath);
        }

        // ------------------------------------------------------------- closing

        private static void BuildClosing()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            MakeMenuCamera();
            var canvas = MakeCanvas("Canvas_Closing");
            MakeEventSystem();

            var title = MakeText(canvas.transform, "Title", "CAMPAIGN COMPLETE", 64, Cyan, new Vector2(0.5f, 0.78f));
            title.fontStyle = FontStyles.Bold;
            var body = MakeText(canvas.transform, "Body", "", 30, Color.white, new Vector2(0.5f, 0.55f));
            body.alignment = TextAlignmentOptions.Center;
            var score = MakeText(canvas.transform, "Score", "", 40, Cyan, new Vector2(0.5f, 0.36f));

            var again = MakeButton(canvas.transform, "Btn_PlayAgain", "PLAY AGAIN", new Vector2(0.5f, 0.24f));
            var home = MakeButton(canvas.transform, "Btn_Welcome", "WELCOME", new Vector2(0.5f, 0.14f));

            var closing = canvas.gameObject.AddComponent<ClosingScreen>();
            var so = new SerializedObject(closing);
            so.FindProperty("titleLabel").objectReferenceValue = title;
            so.FindProperty("bodyLabel").objectReferenceValue = body;
            so.FindProperty("scoreLabel").objectReferenceValue = score;
            so.FindProperty("playAgainButton").objectReferenceValue = again;
            so.FindProperty("welcomeButton").objectReferenceValue = home;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ClosingPath);
        }

        // -------------------------------------------------------------- pieces

        private static void MakeMenuCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            cam.orthographic = true;
            go.AddComponent<AudioListener>();
        }

        private static Canvas MakeCanvas(string name)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private static void MakeEventSystem()
        {
            // New input system module, same reason as SceneSkeleton: the legacy
            // StandaloneInputModule throws every frame under the new input package.
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        private static TMP_Text MakeText(Transform parent, string name, string text, float size, Color color, Vector2 anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            if (UISkin.Active != null && UISkin.Active.font != null)
                tmp.font = UISkin.Active.font;
            tmp.alignment = TextAlignmentOptions.Center;
            var rt = tmp.rectTransform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(1400, size * 3.2f);
            rt.anchoredPosition = Vector2.zero;
            return tmp;
        }

        private static Button MakeButton(Transform parent, string name, string label, Vector2 anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            if (UISkin.Active != null && UISkin.Active.buttonNormal != null)
            {
                // The skin's shape language: sliced button sprite, tinted by the
                // Button state colors below (white base keeps the art true).
                img.sprite = UISkin.Active.buttonNormal;
                img.type = Image.Type.Sliced;
                img.color = Color.white;
            }
            else
            {
                img.color = Panel;
            }
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.12f, 0.19f, 0.25f);
            colors.pressedColor = new Color(0.16f, 0.30f, 0.34f);
            colors.disabledColor = new Color(Panel.r, Panel.g, Panel.b, 0.35f);
            btn.colors = colors;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(420, 84);
            rt.anchoredPosition = Vector2.zero;

            var text = MakeText(go.transform, "Label", label, 34, Cyan, new Vector2(0.5f, 0.5f));
            text.rectTransform.sizeDelta = rt.sizeDelta;
            return btn;
        }

        internal static void WireManifestIntoWelcome(CampaignManifest manifest)
        {
            if (!System.IO.File.Exists(WelcomePath))
            {
                Debug.LogWarning("[Campaign] Welcome scene not built yet — run Build Welcome + Closing Scenes, " +
                                 "which picks the manifest up automatically.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.OpenScene(WelcomePath, OpenSceneMode.Single);
            var welcome = Object.FindFirstObjectByType<CampaignWelcome>();
            if (welcome == null)
            {
                Debug.LogError("[Campaign] Welcome scene has no CampaignWelcome component — rebuild the stub scenes.");
                return;
            }

            var so = new SerializedObject(welcome);
            so.FindProperty("manifest").objectReferenceValue = manifest;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
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

        private static void RegisterInBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            var existing = scenes.FirstOrDefault(s => s.path == scenePath);
            if (existing != null)
            {
                existing.enabled = true;
            }
            else
            {
                scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
