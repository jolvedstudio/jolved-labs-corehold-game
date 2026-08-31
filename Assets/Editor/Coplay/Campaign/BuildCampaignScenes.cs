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
    /// Builders for the campaign menu scenes (plan v2 §A.5) and a test manifest
    /// — the Welcome → Level → Level → Closing walk, and the two screens that
    /// bracket it.
    ///
    /// These began as stubs (centred text, a stack of buttons, a flat fill) and
    /// read as a different game from the HUD they bracket. Both now wear ONE
    /// chrome built by <see cref="BuildMenuChrome"/> — backdrop, rule, eyebrow,
    /// title, nine-sliced content frame — on one row ruler, so the campaign
    /// opens and closes on the same screen with different words in it.
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
        private static Color Panel => UISkin.Active != null ? UISkin.Active.panelColor : DefaultPanel;
        private static Color Cyan => UISkin.Active != null ? UISkin.Active.accent : DefaultCyan;
        private static Color TextDim => UISkin.Active != null ? UISkin.Active.textDim : DefaultTextDim;

        [MenuItem("Tools/COREHOLD/Campaign/Build Welcome + Closing Scenes", false, 10)]
        public static void BuildBoth()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            EnsureFolder(SceneDir);

            // The stub flow wires the TEST manifest if one exists — the real
            // campaign flow goes through BuildBoth(authoring) below.
            BuildWelcome(WelcomePath, AssetDatabase.LoadAssetAtPath<CampaignManifest>(ManifestPath));
            BuildClosing(ClosingPath);

            RegisterInBuildSettings(WelcomePath);
            RegisterInBuildSettings(ClosingPath);

            Debug.Log($"[Campaign] Built {WelcomePath} and {ClosingPath} and registered both in Build Settings.\n" +
                      "Next: Tools → COREHOLD → Campaign → Create Test Manifest, then open the Welcome scene and press Play.");
        }

        /// <summary>
        /// The Campaign Builder's menu-scene build: scenes live in THE
        /// CAMPAIGN'S OWN folder (Scenes/Campaign/&lt;id&gt;/), the authoring's
        /// scene paths are updated to match, and the campaign's OWN manifest is
        /// wired into the Welcome scene when it already exists. This is what
        /// removes the old fragility where every campaign shared one Welcome
        /// scene at a fixed path and the last-emitted manifest won.
        /// </summary>
        internal static string BuildBoth(CampaignAuthoring authoring)
        {
            if (authoring == null || string.IsNullOrWhiteSpace(authoring.campaignId))
                return "Menu scenes NOT built — the campaign needs an id first (step 2).";
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return "Menu scenes NOT built — cancelled at the save prompt.";

            string dir = authoring.SceneFolder;
            EnsureFolder(dir);
            string welcomePath = $"{dir}/Campaign_Welcome.unity";
            string closingPath = $"{dir}/Campaign_Closing.unity";

            var manifest = AssetDatabase.LoadAssetAtPath<CampaignManifest>(authoring.ManifestAssetPath);
            BuildWelcome(welcomePath, manifest);
            BuildClosing(closingPath);

            authoring.welcomeScenePath = welcomePath;
            authoring.closingScenePath = closingPath;
            EditorUtility.SetDirty(authoring);
            AssetDatabase.SaveAssets();

            RegisterInBuildSettings(welcomePath);
            RegisterInBuildSettings(closingPath);

            return $"Welcome + Closing built in {dir} and registered.\n" +
                   (manifest != null
                       ? $"Manifest '{manifest.name}' wired into the Welcome scene."
                       : "No manifest emitted yet — 'Emit manifest + wire Welcome' (or Generate ALL) " +
                         "wires it into this scene once it exists.");
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

        private static void BuildWelcome(string scenePath, CampaignManifest manifestToWire)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            MakeMenuCamera();
            var canvas = MakeCanvas("Canvas_Welcome");
            MakeEventSystem();

            RectTransform frame = BuildMenuChrome(canvas, "CAMPAIGN", "COREHOLD", out TMP_Text title);

            // The subtitle names the CAMPAIGN and is filled at runtime from the
            // manifest; it sits inside the frame as the panel's own caption.
            var subtitle = MakeText(frame, "Subtitle", "", 28, TextDim, new Vector2(0.5f, 1f));
            PlaceInFrame(frame, subtitle.rectTransform, 34f, new Vector2(620, 40));

            MakeRule(frame, "Rule_Panel", new Vector2(0.5f, 1f), 560f);
            var panelRule = frame.Find("Rule_Panel") as RectTransform;
            if (panelRule != null)
            {
                panelRule.pivot = new Vector2(0.5f, 1f);
                panelRule.anchoredPosition = new Vector2(0f, -74f);
            }

            // Each tier says what it CHANGES (pacing e1 made difficulty more than
            // health bars); rich-text subline keeps one button per tier.
            var normal = MakeButton(frame, "Btn_Normal",
                "NORMAL\n<size=45%>build at your own pace</size>", new Vector2(0.5f, 1f));
            var veteran = MakeButton(frame, "Btn_Veteran",
                "VETERAN\n<size=45%>+25% enemies · timed builds</size>", new Vector2(0.5f, 1f));
            var nightmare = MakeButton(frame, "Btn_Nightmare",
                "NIGHTMARE\n<size=45%>+55% enemies · relentless waves</size>", new Vector2(0.5f, 1f));
            var cont = MakeButton(frame, "Btn_Continue", "CONTINUE RUN", new Vector2(0.5f, 1f));

            // One ruler for the rows, so the stack is even and both screens
            // share it. 84 tall on a 96 pitch: the 12 px of air is what stops
            // four buttons reading as one slab.
            var rowSize = new Vector2(560, 84);
            PlaceInFrame(frame, (RectTransform)normal.transform, 108f, rowSize);
            PlaceInFrame(frame, (RectTransform)veteran.transform, 204f, rowSize);
            PlaceInFrame(frame, (RectTransform)nightmare.transform, 300f, rowSize);
            PlaceInFrame(frame, (RectTransform)cont.transform, 408f, rowSize);

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
            if (manifestToWire != null)
                so.FindProperty("manifest").objectReferenceValue = manifestToWire;
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveBuiltScene(scene, scenePath);
        }

        // ------------------------------------------------------------- closing

        private static void BuildClosing(string scenePath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            MakeMenuCamera();
            var canvas = MakeCanvas("Canvas_Closing");
            MakeEventSystem();

            // IDENTICAL chrome to Welcome — same helper, same geometry, same
            // type scale. The campaign now opens and closes on one screen with
            // different words in it.
            RectTransform frame = BuildMenuChrome(canvas, "DEBRIEF", "COREHOLD", out TMP_Text title);

            var body = MakeText(frame, "Body", "", 28, Color.white, new Vector2(0.5f, 1f));
            body.alignment = TextAlignmentOptions.Top;
            PlaceInFrame(frame, body.rectTransform, 34f, new Vector2(620, 150));

            MakeRule(frame, "Rule_Panel", new Vector2(0.5f, 1f), 560f);
            var panelRule = frame.Find("Rule_Panel") as RectTransform;
            if (panelRule != null)
            {
                panelRule.pivot = new Vector2(0.5f, 1f);
                panelRule.anchoredPosition = new Vector2(0f, -196f);
            }

            var score = MakeText(frame, "Score", "", 40, Cyan, new Vector2(0.5f, 1f));
            PlaceInFrame(frame, score.rectTransform, 216f, new Vector2(620, 56));

            var again = MakeButton(frame, "Btn_PlayAgain", "PLAY AGAIN", new Vector2(0.5f, 1f));
            var home = MakeButton(frame, "Btn_Welcome", "WELCOME", new Vector2(0.5f, 1f));

            // The SAME row ruler Welcome uses, landing on its last two slots so
            // the buttons sit where the eye already learned to look.
            var rowSize = new Vector2(560, 84);
            PlaceInFrame(frame, (RectTransform)again.transform, 300f, rowSize);
            PlaceInFrame(frame, (RectTransform)home.transform, 408f, rowSize);

            var closing = canvas.gameObject.AddComponent<ClosingScreen>();
            var so = new SerializedObject(closing);
            so.FindProperty("titleLabel").objectReferenceValue = title;
            so.FindProperty("bodyLabel").objectReferenceValue = body;
            so.FindProperty("scoreLabel").objectReferenceValue = score;
            so.FindProperty("playAgainButton").objectReferenceValue = again;
            so.FindProperty("welcomeButton").objectReferenceValue = home;
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveBuiltScene(scene, scenePath);
        }

        /// <summary>Save a freshly built menu scene over its path, asking VCS to
        /// make an existing file writable first and refusing to fail silently —
        /// SaveScene returns false without a word when the file is locked.</summary>
        private static void SaveBuiltScene(Scene scene, string scenePath)
        {
            if (System.IO.File.Exists(scenePath))
                AssetDatabase.MakeEditable(scenePath);
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                Debug.LogError($"[Campaign] SaveScene FAILED for '{scenePath}' — is the file locked by " +
                               "version control? Check it out / make it writable and rebuild the menu scenes.");
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

        /// <summary>
        /// The chrome BOTH menu screens wear, built once so they cannot drift
        /// apart: backdrop, a hairline rule, an eyebrow, the title, and a
        /// nine-sliced content frame.
        ///
        /// This is the fix for menus that read as a different game from the one
        /// they bracket. They were stubs — centred text and a stack of buttons
        /// floating on a flat fill — while the HUD wears framed panels, rules
        /// and small-caps labels. Same chrome, same geometry, same type scale
        /// for Welcome and Closing means the campaign opens and closes on the
        /// same screen with different words in it, which is what "identical"
        /// has to mean for two screens that do different jobs.
        ///
        /// Everything reads the ambient <see cref="UISkin"/>, so re-skinning
        /// the game re-skins these with it and no literal here has to be
        /// chased down twice.
        /// </summary>
        private static RectTransform BuildMenuChrome(Canvas canvas, string eyebrow, string title,
                                                     out TMP_Text titleLabel)
        {
            float scale = UISkin.Active != null ? UISkin.Active.uiScale : 1f;
            float type = UISkin.Active != null ? UISkin.Active.textScale : 1f;

            // Backdrop: the canvas carries its own ground rather than trusting
            // the camera's clear colour, so a screenshot or a canvas-only
            // render is never transparent behind the frame.
            var back = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
            back.transform.SetParent(canvas.transform, false);
            var backRt = (RectTransform)back.transform;
            backRt.anchorMin = Vector2.zero;
            backRt.anchorMax = Vector2.one;
            backRt.offsetMin = backRt.offsetMax = Vector2.zero;
            back.GetComponent<Image>().color = Bg;
            back.GetComponent<Image>().raycastTarget = false;

            // Eyebrow over title over rule — the HUD's own label hierarchy,
            // where a small letterspaced caption names what the big number is.
            var eye = MakeText(canvas.transform, "Eyebrow", eyebrow, 24f * type, TextDim,
                               new Vector2(0.5f, 0.845f));
            eye.characterSpacing = 14f;
            eye.fontStyle = FontStyles.UpperCase;

            titleLabel = MakeText(canvas.transform, "Title", title, 88f * type, Cyan,
                                  new Vector2(0.5f, 0.755f));
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.characterSpacing = 6f;

            MakeRule(canvas.transform, "Rule_Top", new Vector2(0.5f, 0.695f), 560f * scale);

            // The content frame: the single biggest reason the stubs read as
            // unfinished. Buttons standing on a nine-sliced panel belong to the
            // same interface as the HUD; buttons floating on a fill do not.
            var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            frameGo.transform.SetParent(canvas.transform, false);
            var frame = (RectTransform)frameGo.transform;
            frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 0.36f);
            frame.sizeDelta = new Vector2(720f * scale, 520f * scale);
            frame.anchoredPosition = Vector2.zero;

            var frameImg = frameGo.GetComponent<Image>();
            Sprite panelSprite = UISkin.Active != null ? UISkin.Active.panel : null;
            if (panelSprite != null)
            {
                frameImg.sprite = panelSprite;
                frameImg.type = Image.Type.Sliced;
                frameImg.color = Color.white;
            }
            else
            {
                // No skin sprite: a tinted plate still reads as a surface, which
                // is more than the stubs had.
                frameImg.color = new Color(Panel.r, Panel.g, Panel.b, 0.92f);
            }
            frameImg.raycastTarget = false;

            return frame;
        }

        /// <summary>A hairline rule in the accent colour — the cheapest thing
        /// that makes a screen look composed rather than centred.</summary>
        private static void MakeRule(Transform parent, string name, Vector2 anchor, float width)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(width, 2f);
            rt.anchoredPosition = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = new Color(Cyan.r, Cyan.g, Cyan.b, 0.55f);
            img.raycastTarget = false;
        }

        /// <summary>Place a control inside the content frame by pixel offset
        /// from its top edge, so both screens stack their rows on one ruler.</summary>
        private static void PlaceInFrame(RectTransform frame, RectTransform child, float fromTop, Vector2 size)
        {
            // The skin's uiScale sizes the FRAME, so it has to size the ruler
            // too — otherwise a chunkier skin grows the panel and leaves its
            // contents bunched at the top of it.
            float scale = UISkin.Active != null ? UISkin.Active.uiScale : 1f;

            child.SetParent(frame, false);
            child.anchorMin = child.anchorMax = new Vector2(0.5f, 1f);
            child.pivot = new Vector2(0.5f, 1f);
            child.sizeDelta = size * scale;
            child.anchoredPosition = new Vector2(0f, -fromTop * scale);

            // MakeButton sizes its label to the button it was born with, so a
            // resized button would otherwise wear a label of the old width and
            // wrap its subline early.
            Transform label = child.Find("Label");
            if (label is RectTransform labelRt)
                labelRt.sizeDelta = size * scale;
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

        /// <summary>Test-tool overload: wires into the stub Welcome scene.</summary>
        internal static void WireManifestIntoWelcome(CampaignManifest manifest)
            => WireManifestIntoWelcome(manifest, WelcomePath);

        /// <summary>
        /// Wire the manifest into the campaign's Welcome scene — the ACTUAL
        /// path the campaign uses, not a fixed one.
        /// </summary>
        internal static bool WireManifestIntoWelcome(CampaignManifest manifest, string welcomePath)
            => WireManifestInto<CampaignWelcome>(
                manifest, welcomePath, "manifest", "Welcome scene",
                "build the menu scenes; the campaign flow wires the manifest into them automatically.",
                "rebuild the menu scenes.");

        /// <summary>
        /// Wire the manifest into the campaign's FIRST LEVEL, whose TitleScreen
        /// overlay is the front door when the campaign has no Welcome scene.
        ///
        /// Every generated level already carries a TitleScreen — it is how a
        /// single map asks which difficulty to play. Handing it the manifest
        /// promotes it: the difficulty buttons start the campaign in the scene
        /// that is already open, and CONTINUE RUN appears when there is a run
        /// to continue. That is the entire entry screen, with no second scene
        /// to build, boot into, and stream away from before the player sees the
        /// game they came for.
        /// </summary>
        internal static bool WireManifestIntoLevelOne(CampaignManifest manifest, string levelOnePath)
            => WireManifestInto<TitleScreen>(
                manifest, levelOnePath, "campaign", "first level",
                "generate the campaign's levels first.",
                "regenerate it — every generated level builds a TitleScreen overlay.");

        /// <summary>
        /// Write <paramref name="manifest"/> into the <paramref name="fieldName"/>
        /// field of the <typeparamref name="T"/> in the scene at
        /// <paramref name="scenePath"/>. A LOADED copy (active OR additive) is
        /// wired in place; otherwise the scene is opened, wired, saved, and the
        /// previous scene restored.
        ///
        /// The save is REQUESTED FROM VERSION CONTROL and then VERIFIED against
        /// the file bytes: under a checkout workflow (Unity VCS, Perforce) a
        /// scene file can be read-only, and SaveScene then returns false
        /// SILENTLY — which is exactly how "wired" scenes kept coming back
        /// empty unless the user happened to have the scene open (their manual
        /// edit context had checked the file out). Returns true only when the
        /// manifest reference demonstrably reached the file on disk.
        ///
        /// One implementation for both front doors. Proving the save is the
        /// part of this that took an afternoon to learn, and it should not
        /// exist in two copies with only one of them kept honest.
        /// </summary>
        private static bool WireManifestInto<T>(CampaignManifest manifest, string scenePath,
                                                string fieldName, string noun,
                                                string missingSceneHint, string missingComponentHint)
            where T : Component
        {
            if (string.IsNullOrEmpty(scenePath) || !System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning($"[Campaign] {noun} not built yet ({scenePath}) — {missingSceneHint}");
                return false;
            }

            // Already loaded? (Active OR additive — wire the loaded copy.)
            Scene target = default;
            bool wasLoaded = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.path == scenePath && s.isLoaded)
                {
                    target = s;
                    wasLoaded = true;
                    break;
                }
            }

            // Not loaded → open ADDITIVELY beside whatever the user has open:
            // no scene switch, no save prompt, no restore step — every quiet
            // abort the old Single-mode roundtrip could hit is gone, and the
            // user's editing context is untouched.
            bool openedHere = false;
            if (!wasLoaded)
            {
                target = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                openedHere = true;
            }

            try
            {
                // Find the component in THE TARGET SCENE's roots — a global find
                // could grab the same component from some other loaded scene.
                T host = null;
                foreach (GameObject root in target.GetRootGameObjects())
                {
                    host = root.GetComponentInChildren<T>(true);
                    if (host != null)
                        break;
                }
                if (host == null)
                {
                    Debug.LogError($"[Campaign] '{scenePath}' has no {typeof(T).Name} component — {missingComponentHint}");
                    return false;
                }

                var so = new SerializedObject(host);
                so.FindProperty(fieldName).objectReferenceValue = manifest;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(target);

                // Ask VCS to make the file writable, save, then PROVE the save:
                // the manifest asset's GUID must appear in the scene file bytes.
                AssetDatabase.MakeEditable(scenePath);
                bool saved = EditorSceneManager.SaveScene(target);
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(manifest));
                bool verified = saved && !string.IsNullOrEmpty(guid) &&
                                System.IO.File.ReadAllText(scenePath).Contains(guid);

                if (verified)
                    Debug.Log($"[Campaign] Manifest '{manifest.name}' wired into '{scenePath}' " +
                              $"({(wasLoaded ? "scene was open" : "opened additively")}; save verified on disk).");
                else
                    Debug.LogError($"[Campaign] Manifest wiring DID NOT PERSIST to '{scenePath}' " +
                                   $"(SaveScene {(saved ? "reported success" : "FAILED")}, " +
                                   $"manifest guid {(string.IsNullOrEmpty(guid) ? "MISSING — is the .meta imported?" : guid)}). " +
                                   "If the scene is under version control, check it out / make it writable, " +
                                   $"then Emit again — an unwired {noun} boots to a dead menu.");
                return verified;
            }
            finally
            {
                // Close only what this call opened; a scene the user had open
                // stays exactly as they had it (now saved if we wired it).
                if (openedHere)
                    EditorSceneManager.CloseScene(target, true);
            }
        }

        // -------------------------------------------------------------- utils

        /// <summary>
        /// Create every missing folder along an Assets/… path. Hardened: a path
        /// with no separator (or outside Assets) is refused loudly instead of
        /// throwing on Substring — folder creation is the campaign flow's
        /// foundation, and it must never die half-way with no explanation.
        /// </summary>
        internal static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            if (slash <= 0 || !path.StartsWith("Assets"))
            {
                Debug.LogError($"[Campaign] Cannot create folder '{path}' — not a valid Assets/ path.");
                return;
            }
            string parent = path.Substring(0, slash);
            string leaf = path.Substring(slash + 1);
            if (string.IsNullOrEmpty(leaf))
            {
                Debug.LogError($"[Campaign] Cannot create folder '{path}' — empty leaf (trailing slash?).");
                return;
            }
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
