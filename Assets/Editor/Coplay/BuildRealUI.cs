using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Core;
using Corehold.Data;
using Corehold.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace CoreholdEditor
{
    /// <summary>
    /// Ticket 36 — builds the real uGUI from the Sci-Fi UI Pack Pro cyan V2 family,
    /// replacing all placeholder IMGUI. One font, two sizes; Canvas Scaler in Scale
    /// With Screen Size at 1920×1080, match 0.5; nine-sliced panel/button sprites for
    /// anything whose size varies; hover via colour tint (the pack has no hover
    /// sprite state). Wires every controller to the game's events.
    ///
    /// Idempotent: deletes any UI it previously built (by name) and rebuilds.
    /// Menu: Tools/COREHOLD/Scene Setup/Build Real UI.
    /// </summary>
    public static class BuildRealUI
    {
        // --- The cyan V2 sprite set (≤ a dozen, all nine-sliceable) ---
        const string Base = "Assets/Vendor/SCI-FI UI Pack Pro/SCI-FI UI Pack_V2 Pro(Cyan)/Textures/DemoScene_Cyan/";
        const string PanelSprite    = Base + "PopupsAndPanels/Panel_Weapon.png";
        const string PopupSprite    = Base + "PopupsAndPanels/DialogBox.png";
        const string BtnNormal      = Base + "Buttons/Btn_Rectangle01Cyan_n.png";
        const string BtnPressed     = Base + "Buttons/Btn_Rectangle01Cyan_p.png";
        const string BtnDisabled    = Base + "Buttons/Btn_Rectangle01Gray_n.png";
        const string BarBg          = Base + "Options/Hp_Bg.png";
        const string BarFill        = Base + "Options/Hp_Progress.png";
        const string PauseIcon      = Base + "Buttons/Btn_pause_n.png";
        const string StarFull       = Base + "Options/Icon_VictoryStar_light.png";
        const string StarEmpty      = Base + "Options/Icon_VictoryStar_gray.png";
        const string FontPath       = "Assets/Vendor/SCI-FI UI Pack Pro/Common/Fonts/Aldrich-Regular SDF.asset";

        // Cached theme colours.
        static readonly Color Cyan = new Color(0.20f, 0.85f, 1f, 1f);
        static readonly Color Amber = new Color(1f, 0.6f, 0.1f, 1f);
        static readonly Color Danger = new Color(1f, 0.3f, 0.3f, 1f);
        static readonly Color PanelTint = new Color(1f, 1f, 1f, 1f);

        static TMP_FontAsset _font;
        static float _large = 34f, _small = 22f;

        [MenuItem("Tools/COREHOLD/Scene Setup/Build Real UI", false, 47)]
        public static string Run()
        {
            var sb = new StringBuilder();
            _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (_font == null) _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

            // 1. Theme + catalogues.
            var theme = BuildTheme(sb);

            // 2. Remove any previously-built canvases so this is idempotent.
            DestroyIfExists("Canvas_HUD");
            DestroyIfExists("Canvas_Menus");
            DestroyIfExists("UI_Overlays");

            // 3. Build the HUD and menu canvases.
            var hud = BuildHudCanvas(theme, sb);
            var menus = BuildMenuCanvas(theme, sb);

            // 4. World-space overlay manager (health bars + armour pips).
            EnsureOverlayManager(sb);

            // 5. Game flow (title -> build), replacing the old bootstrap.
            EnsureGameFlow(hud, menus, sb);

            // 6. Clean out the old IMGUI ResultScreen object if it carries a missing script.
            CleanupLegacy(sb);

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("[COREHOLD] Build Real UI\n" + sb);
            return sb.ToString();
        }

        // ============================================================ THEME

        static UITheme BuildTheme(StringBuilder sb)
        {
            var go = GameObject.Find("UITheme");
            if (go == null) go = new GameObject("UITheme");
            var theme = go.GetComponent<UITheme>() ?? go.AddComponent<UITheme>();

            theme.font = _font;
            theme.fontSizeLarge = _large;
            theme.fontSizeSmall = _small;
            theme.panel = Load(PanelSprite);
            theme.popup = Load(PopupSprite);
            theme.buttonNormal = Load(BtnNormal);
            theme.buttonPressed = Load(BtnPressed);
            theme.buttonDisabled = Load(BtnDisabled);
            theme.barBackground = Load(BarBg);
            theme.barFill = Load(BarFill);
            theme.pauseIcon = Load(PauseIcon);
            theme.starFull = Load(StarFull);
            theme.starEmpty = Load(StarEmpty);
            theme.cyan = Cyan; theme.amber = Amber; theme.danger = Danger;

            // Catalogues, in menu order.
            string[] order = { "Autocannon", "MissileBattery", "ArcNode", "SiegeMortar", "ScanRelay" };
            var defs = AssetDatabase.FindAssets("t:TowerDefinition", new[] { "Assets/_COREHOLD/Data/Towers" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<TowerDefinition>)
                .Where(d => d != null).ToList();
            theme.turrets = order.Select(n => defs.FirstOrDefault(d => d.name.Contains(n)))
                                 .Where(d => d != null).ToArray();
            if (theme.turrets.Length == 0) theme.turrets = defs.ToArray();

            theme.damageTable = AssetDatabase.LoadAssetAtPath<DamageTable>("Assets/_COREHOLD/Data/DamageTable.asset");

            EditorUtility.SetDirty(theme);
            sb.AppendLine($"Theme: font={(_font != null ? _font.name : "MISSING")}, turrets={theme.turrets.Length}, table={(theme.damageTable != null ? "ok" : "missing")}");
            return theme;
        }

        // ============================================================ HUD

        static HUDController BuildHudCanvas(UITheme theme, StringBuilder sb)
        {
            var canvas = MakeCanvas("Canvas_HUD", 10);
            var hud = canvas.gameObject.AddComponent<HUDController>();

            // ---- Top-left: integrity ----
            var tl = MakePanel(canvas.transform, "IntegrityPanel",
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -24), new Vector2(460, 96), theme.panel);
            var integTitle = MakeText(tl, "Title", "CORE INTEGRITY", _small, TextAlignmentOptions.TopLeft,
                new Vector2(16, -8), new Vector2(300, 24));
            integTitle.color = Cyan;
            var segRoot = MakeRect(tl, "Segments", new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, -8), new Vector2(340, 26));
            var seghlg = segRoot.gameObject.AddComponent<Image>();
            seghlg.color = new Color(0, 0, 0, 0); // parent flashes; keep transparent
            var hl = segRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 2; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = true; hl.childForceExpandHeight = true;
            // segment template
            var segTemplate = new GameObject("SegTemplate", typeof(Image));
            segTemplate.transform.SetParent(segRoot, false);
            var segImg = segTemplate.GetComponent<Image>();
            segImg.color = Cyan;
            segTemplate.SetActive(false);
            var integVal = MakeText(tl, "Value", "20/20", _large, TextAlignmentOptions.Right,
                new Vector2(-90, -48), new Vector2(140, 40));
            SetAnchors(integVal.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-16, -50));

            // ---- Top-centre: wave + preview ----
            var tc = MakePanel(canvas.transform, "WavePanel",
                new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -18), new Vector2(560, 132), theme.panel);
            var waveLbl = MakeText(tc, "WaveLabel", "WAVE 1 / 10", _large, TextAlignmentOptions.Top,
                new Vector2(0, -8), new Vector2(540, 40));
            waveLbl.color = Cyan;
            var previewRow = MakeRect(tc, "PreviewRow", new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 12), new Vector2(540, 70));
            var prow = previewRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            prow.spacing = 10; prow.childAlignment = TextAnchor.MiddleCenter; prow.childControlWidth = false; prow.childControlHeight = false;
            var previewCell = BuildPreviewCellTemplate(previewRow, theme);

            // ---- Top-right: salvage ----
            var tr = MakePanel(canvas.transform, "SalvagePanel",
                new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -24), new Vector2(300, 96), theme.panel);
            var salTitle = MakeText(tr, "Title", "SALVAGE", _small, TextAlignmentOptions.Right,
                new Vector2(-16, -14), new Vector2(260, 24));
            salTitle.color = Cyan;
            var salVal = MakeText(tr, "Value", "300", _large, TextAlignmentOptions.Right,
                new Vector2(-16, -52), new Vector2(260, 44));

            // ---- Bottom-right: start wave + speed ----
            var startBtn = MakeButton(canvas.transform, "StartWaveButton", "START WAVE 1", theme,
                new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 96), new Vector2(300, 84));
            var speedBtn = MakeButton(canvas.transform, "SpeedButton", "1×", theme,
                new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 24), new Vector2(140, 64));

            // ---- Bottom-left: pause ----
            var pauseBtn = MakeIconButton(canvas.transform, "PauseButton", theme.pauseIcon, theme,
                new Vector2(0, 0), new Vector2(0, 0), new Vector2(24, 24), new Vector2(72, 72));

            // ---- Colossus bar (top, hidden by default) ----
            var bossRoot = MakeRect(canvas.transform, "ColossusBar", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -160), new Vector2(900, 40));
            var bossBg = bossRoot.gameObject.AddComponent<Image>();
            bossBg.sprite = theme.barBackground; bossBg.type = Image.Type.Sliced; bossBg.color = new Color(0.05f, 0.06f, 0.08f, 0.9f);
            var bossFillRect = MakeRect(bossRoot, "Fill", new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            bossFillRect.offsetMin = new Vector2(4, 4); bossFillRect.offsetMax = new Vector2(-4, -4);
            var bossFill = bossFillRect.gameObject.AddComponent<Image>();
            bossFill.sprite = theme.barFill; bossFill.type = Image.Type.Filled; bossFill.fillMethod = Image.FillMethod.Horizontal;
            bossFill.color = new Color(1f, 0.35f, 0.25f, 1f); bossFill.fillAmount = 1f;
            var bossLbl = MakeText(bossRoot, "Label", "COLOSSUS", _small, TextAlignmentOptions.Center, Vector2.zero, new Vector2(880, 34));
            bossRoot.gameObject.SetActive(false);

            // ---- Wire HUD ----
            var so = new SerializedObject(hud);
            SetRef(so, "waveManager", Object.FindFirstObjectByType<WaveManager>());
            SetRef(so, "theme", theme);
            SetRef(so, "integritySegments", segRoot);
            SetRef(so, "integrityValue", integVal);
            SetRef(so, "integritySegmentPrefabSource", segImg);
            SetRef(so, "waveLabel", waveLbl);
            SetRef(so, "previewRow", previewRow);
            SetRef(so, "salvageValue", salVal);
            SetRef(so, "startWaveButton", startBtn.GetComponent<Button>());
            SetRef(so, "startWaveLabel", startBtn.GetComponentInChildren<TMP_Text>());
            SetRef(so, "speedButton", speedBtn.GetComponent<Button>());
            SetRef(so, "speedLabel", speedBtn.GetComponentInChildren<TMP_Text>());
            SetRef(so, "pauseButton", pauseBtn.GetComponent<Button>());
            SetRef(so, "colossusBarRoot", bossRoot);
            SetRef(so, "colossusBarFill", bossFill);
            SetRef(so, "colossusBarLabel", bossLbl);
            SetRef(so, "previewCellTemplate", previewCell);
            so.ApplyModifiedPropertiesWithoutUndo();

            sb.AppendLine("Built Canvas_HUD (integrity/wave/salvage/start/speed/pause/boss).");
            return hud;
        }

        static GameObject BuildPreviewCellTemplate(RectTransform parent, UITheme theme)
        {
            var cell = new GameObject("PreviewCellTemplate", typeof(RectTransform));
            cell.transform.SetParent(parent, false);
            var rt = (RectTransform)cell.transform;
            rt.sizeDelta = new Vector2(58, 66);

            var icon = MakeRect(rt, "Icon", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(48, 48));
            var iconImg = icon.gameObject.AddComponent<Image>();
            iconImg.preserveAspect = true; iconImg.color = Color.white;

            var count = MakeText(rt, "Count", "×5", 18f, TextAlignmentOptions.Bottom, new Vector2(0, 2), new Vector2(56, 20));

            var pip = MakeRect(rt, "Pip", new Vector2(1, 1), new Vector2(1, 1), new Vector2(-2, -2), new Vector2(18, 18));
            var pipImg = pip.gameObject.AddComponent<Image>();
            pipImg.color = theme.plated;
            var letter = MakeText(pip, "Letter", "P", 12f, TextAlignmentOptions.Center, Vector2.zero, new Vector2(18, 18));
            letter.color = Color.black;

            cell.SetActive(false);
            return cell;
        }

        // ============================================================ MENUS

        static void BuildMenuCanvas(UITheme theme, out BuildMenu buildMenu, out TowerPanel towerPanel,
            out PauseScreen pauseScreen, out ResultScreen resultScreen, out TitleScreen titleScreen)
        {
            var canvas = MakeCanvas("Canvas_Menus", 20);

            // Shared range ring lives in the world, not the canvas.
            var ringGo = new GameObject("RangeRing", typeof(RangeRing));
            var ring = ringGo.GetComponent<RangeRing>();

            buildMenu = BuildBuildMenu(canvas, theme, ring);
            towerPanel = BuildTowerPanel(canvas, theme, ring);
            pauseScreen = BuildPauseScreen(canvas, theme);
            resultScreen = BuildResultScreen(canvas, theme);
            titleScreen = BuildTitleScreen(canvas, theme);

            // cross-link build menu -> tower panel
            var bmSo = new SerializedObject(buildMenu);
            SetRef(bmSo, "towerPanel", towerPanel);
            bmSo.ApplyModifiedPropertiesWithoutUndo();
        }

        static Canvas BuildMenuCanvas(UITheme theme, StringBuilder sb)
        {
            BuildMenuCanvas(theme, out var buildMenu, out var towerPanel, out var pauseScreen, out var resultScreen, out var titleScreen);
            sb.AppendLine("Built Canvas_Menus (build menu, tower panel, pause, result, title).");

            // Link HUD's pause reference now that the pause screen exists.
            var hud = Object.FindFirstObjectByType<HUDController>();
            if (hud != null)
            {
                var so = new SerializedObject(hud);
                SetRef(so, "pauseScreen", pauseScreen);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return GameObject.Find("Canvas_Menus").GetComponent<Canvas>();
        }

        static BuildMenu BuildBuildMenu(Canvas canvas, UITheme theme, RangeRing ring)
        {
            var root = MakePanel(canvas.transform, "BuildMenu",
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 24), new Vector2(760, 150), theme.panel);
            var comp = canvas.gameObject.AddComponent<BuildMenu>();

            var title = MakeText(root, "Title", "BUILD", _small, TextAlignmentOptions.TopLeft, new Vector2(16, -6), new Vector2(200, 22));
            title.color = Cyan;

            var entriesRow = MakeRect(root, "Entries", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -6), new Vector2(736, 110));
            var hlg = entriesRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8; hlg.childAlignment = TextAnchor.MiddleCenter; hlg.childControlWidth = false; hlg.childControlHeight = false;
            var entryTemplate = BuildTurretEntryTemplate(entriesRow, theme);

            var so = new SerializedObject(comp);
            SetRef(so, "router", Object.FindFirstObjectByType<Corehold.Systems.InputRouter>());
            SetRef(so, "theme", theme);
            SetRef(so, "root", root.gameObject);
            SetRef(so, "entriesRow", entriesRow);
            SetRef(so, "entryTemplate", entryTemplate);
            SetRef(so, "rangeRing", ring);
            so.ApplyModifiedPropertiesWithoutUndo();

            root.gameObject.SetActive(false);
            return comp;
        }

        static GameObject BuildTurretEntryTemplate(RectTransform parent, UITheme theme)
        {
            var cell = MakeButtonBase("EntryTemplate", parent, theme,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(140, 104));
            cell.gameObject.AddComponent<BuildEntryHover>();

            // Slightly bigger icon showing just the tower (Ticket e).
            var icon = MakeRect(cell, "Icon", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -6), new Vector2(58, 58));
            var iconImg = icon.gameObject.AddComponent<Image>();
            iconImg.preserveAspect = true;

            var nm = MakeText(cell, "Name", "Turret", 15f, TextAlignmentOptions.Center, new Vector2(0, -4), new Vector2(132, 18));
            SetAnchors(nm.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -4));
            var role = MakeText(cell, "Role", "ROLE", 11f, TextAlignmentOptions.Center, new Vector2(0, -22), new Vector2(132, 14));
            SetAnchors(role.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -22));
            role.color = new Color(0.7f, 0.85f, 0.9f, 1f);
            var cost = MakeText(cell, "Cost", "100", 18f, TextAlignmentOptions.Center, new Vector2(0, 10), new Vector2(132, 22));
            SetAnchors(cost.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 20));
            cost.color = Cyan;

            cell.gameObject.SetActive(false);
            return cell.gameObject;
        }

        static TowerPanel BuildTowerPanel(Canvas canvas, UITheme theme, RangeRing ring)
        {
            var root = MakePanel(canvas.transform, "TowerPanel",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-24, 0), new Vector2(420, 620), theme.popup);
            var comp = canvas.gameObject.AddComponent<TowerPanel>();

            var name = MakeText(root, "Name", "AUTOCANNON", _large, TextAlignmentOptions.TopLeft, new Vector2(20, -14), new Vector2(300, 40));
            name.color = Cyan;
            var tier = MakeText(root, "Tier", "TIER 1", 18f, TextAlignmentOptions.Right, new Vector2(-24, -58), new Vector2(180, 24));
            SetAnchors(tier.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -58));
            var dmgType = MakeText(root, "DamageType", "KINETIC", _small, TextAlignmentOptions.TopLeft, new Vector2(20, -52), new Vector2(300, 24));
            dmgType.color = Cyan;

            var dps = MakeText(root, "DPS", "DPS  20.0", _small, TextAlignmentOptions.TopLeft, new Vector2(20, -84), new Vector2(380, 24));
            var range = MakeText(root, "Range", "RANGE  12 m", _small, TextAlignmentOptions.TopLeft, new Vector2(20, -112), new Vector2(380, 24));
            var next = MakeText(root, "Next", "NEXT (T2): 130", 18f, TextAlignmentOptions.TopLeft, new Vector2(20, -142), new Vector2(380, 44));
            next.color = new Color(0.8f, 0.9f, 0.95f, 1f);

            // Priority selector.
            var prioLbl = MakeText(root, "PriorityLabel", "TARGETING", 16f, TextAlignmentOptions.Left, new Vector2(-172, -186), new Vector2(200, 20));
            SetAnchors(prioLbl.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-88, -186));
            prioLbl.color = Cyan;
            var prioRow = MakeRect(root, "PriorityRow", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -220), new Vector2(380, 44));
            var prioHlg = prioRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            prioHlg.spacing = 6; prioHlg.childControlWidth = true; prioHlg.childForceExpandWidth = true; prioHlg.childControlHeight = true; prioHlg.childForceExpandHeight = true;
            var pFirst = MakeButton(prioRow, "First", "FIRST", theme, new Vector2(0,0), new Vector2(0,0), Vector2.zero, new Vector2(120, 40));
            var pClose = MakeButton(prioRow, "Closest", "CLOSE", theme, new Vector2(0,0), new Vector2(0,0), Vector2.zero, new Vector2(120, 40));
            var pStrong = MakeButton(prioRow, "Strongest", "STRONG", theme, new Vector2(0,0), new Vector2(0,0), Vector2.zero, new Vector2(120, 40));

            // 3x3 counter grid.
            var gridLbl = MakeText(root, "GridLabel", "DAMAGE vs ARMOUR", 16f, TextAlignmentOptions.Left, new Vector2(-88, -256), new Vector2(300, 20));
            SetAnchors(gridLbl.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-88, -256));
            gridLbl.color = Cyan;
            var gridRoot = MakeRect(root, "CounterGrid", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -376), new Vector2(380, 180));
            var (cells, rows) = BuildCounterGrid(gridRoot, theme);

            // Actions.
            var upgrade = MakeButton(root, "UpgradeButton", "UPGRADE 130", theme, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 96), new Vector2(360, 64));
            var sell = MakeButton(root, "SellButton", "SELL +60", theme, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 24), new Vector2(360, 60));
            var close = MakeIconButton(root, "CloseButton", theme.pauseIcon, theme, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-8, -8), new Vector2(44, 44));
            close.GetComponentInChildren<TMP_Text>(true)?.SetText("X");

            var so = new SerializedObject(comp);
            SetRef(so, "theme", theme);
            SetRef(so, "root", root.gameObject);
            SetRef(so, "nameLabel", name);
            SetRef(so, "tierLabel", tier);
            SetRef(so, "damageTypeLabel", dmgType);
            SetRef(so, "dpsLabel", dps);
            SetRef(so, "rangeLabel", range);
            SetRef(so, "nextLabel", next);
            SetRef(so, "upgradeButton", upgrade.GetComponent<Button>());
            SetRef(so, "upgradeLabel", upgrade.GetComponentInChildren<TMP_Text>());
            SetRef(so, "sellButton", sell.GetComponent<Button>());
            SetRef(so, "sellLabel", sell.GetComponentInChildren<TMP_Text>());
            SetRef(so, "closeButton", close.GetComponent<Button>());
            SetRef(so, "priorityFirst", pFirst.GetComponent<Button>());
            SetRef(so, "priorityClosest", pClose.GetComponent<Button>());
            SetRef(so, "priorityStrongest", pStrong.GetComponent<Button>());
            SetRef(so, "rangeRing", ring);
            SetArray(so, "gridCells", cells.Cast<Object>().ToArray());
            SetArray(so, "rowHighlights", rows.Cast<Object>().ToArray());
            so.ApplyModifiedPropertiesWithoutUndo();

            root.gameObject.SetActive(false);
            return comp;
        }

        static (TMP_Text[] cells, Image[] rows) BuildCounterGrid(RectTransform root, UITheme theme)
        {
            string[] dmgNames = { "KIN", "ENR", "EXP" };
            string[] armNames = { "UNARM", "PLATE", "SHLD" };
            var cells = new TMP_Text[9];
            var rows = new Image[3];

            float w = 380f, h = 180f;
            float colW = w / 4f;   // first column is the row label
            float rowH = h / 4f;   // first row is the header

            // Header row (armour names). Grid root pivot is centre; use top-left anchors.
            for (int a = 0; a < 3; a++)
            {
                var hr = MakeRect(root, $"H_{a}", new Vector2(0,1), new Vector2(0,1),
                    new Vector2(colW*(a+1) + 2, -rowH/2 - 2), new Vector2(colW-4, rowH-4));
                var ht = hr.gameObject.AddComponent<TextMeshProUGUI>();
                Style(ht, armNames[a], 14f, TextAlignmentOptions.Center);
                ht.color = Cyan;
            }

            for (int d = 0; d < 3; d++)
            {
                float y = h/2 - rowH*(d+1) - rowH/2;
                // row highlight strip
                var hlRect = MakeRect(root, $"Row_{d}", new Vector2(0,1), new Vector2(0,1),
                    new Vector2(colW, -rowH*(d+1) - 2), new Vector2(w - colW - 2, rowH - 4));
                var hlImg = hlRect.gameObject.AddComponent<Image>();
                var hc = Cyan; hc.a = 0.30f; hlImg.color = hc; hlImg.enabled = false;
                rows[d] = hlImg;

                // row label
                var lbl = MakeRect(root, $"L_{d}", new Vector2(0,1), new Vector2(0,1), new Vector2(2, -rowH*(d+1) - 2), new Vector2(colW-4, rowH-4));
                var lblT = lbl.gameObject.AddComponent<TextMeshProUGUI>();
                Style(lblT, dmgNames[d], 14f, TextAlignmentOptions.Center); lblT.color = Amber;

                for (int a = 0; a < 3; a++)
                {
                    var cell = MakeRect(root, $"C_{d}_{a}", new Vector2(0,1), new Vector2(0,1),
                        new Vector2(colW*(a+1) + 2, -rowH*(d+1) - 2), new Vector2(colW-4, rowH-4));
                    var ct = cell.gameObject.AddComponent<TextMeshProUGUI>();
                    Style(ct, "×1.00", 16f, TextAlignmentOptions.Center);
                    cells[d*3 + a] = ct;
                }
            }
            return (cells, rows);
        }

        static PauseScreen BuildPauseScreen(Canvas canvas, UITheme theme)
        {
            var root = MakeFullscreenDim(canvas.transform, "PauseScreen");
            var comp = canvas.gameObject.AddComponent<PauseScreen>();

            var panel = MakePanel(root, "Panel", new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), Vector2.zero, new Vector2(520, 480), theme.popup);
            MakeText(panel, "Title", "PAUSED", _large, TextAlignmentOptions.Top, new Vector2(0, -20), new Vector2(480, 44)).color = Cyan;
            var resume = MakeButton(panel, "Resume", "RESUME", theme, new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0, -90), new Vector2(400, 70));
            var retry = MakeButton(panel, "Retry", "RETRY", theme, new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0, -172), new Vector2(400, 70));
            var menu = MakeButton(panel, "Menu", "MAIN MENU", theme, new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0, -254), new Vector2(400, 70));
            var mute = MakeButton(panel, "Mute", "SOUND: ON", theme, new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0, -336), new Vector2(400, 70));

            var so = new SerializedObject(comp);
            SetRef(so, "root", root.gameObject);
            SetRef(so, "resumeButton", resume.GetComponent<Button>());
            SetRef(so, "retryButton", retry.GetComponent<Button>());
            SetRef(so, "menuButton", menu.GetComponent<Button>());
            SetRef(so, "muteButton", mute.GetComponent<Button>());
            SetRef(so, "muteLabel", mute.GetComponentInChildren<TMP_Text>());
            so.ApplyModifiedPropertiesWithoutUndo();

            root.gameObject.SetActive(false);
            return comp;
        }

        static ResultScreen BuildResultScreen(Canvas canvas, UITheme theme)
        {
            var root = MakeFullscreenDim(canvas.transform, "ResultOverlay");
            var comp = canvas.gameObject.AddComponent<ResultScreen>();

            var panel = MakePanel(root, "Panel", new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), Vector2.zero, new Vector2(620, 560), theme.popup);
            var title = MakeText(panel, "Title", "VICTORY", 54f, TextAlignmentOptions.Top, new Vector2(0, -24), new Vector2(560, 70));
            title.color = Cyan;

            // stars
            var starRow = MakeRect(panel, "Stars", new Vector2(0.5f,1), new Vector2(0.5f,1), new Vector2(0, -110), new Vector2(360, 90));
            var srH = starRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            srH.spacing = 12; srH.childAlignment = TextAnchor.MiddleCenter; srH.childControlWidth = false; srH.childControlHeight = false;
            var stars = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                var s = MakeRect(starRow, $"Star_{i}", new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), Vector2.zero, new Vector2(84, 84));
                var img = s.gameObject.AddComponent<Image>();
                img.sprite = theme.starFull; img.preserveAspect = true;
                stars[i] = img;
            }

            var body = MakeText(panel, "Body", "Waves survived 10/10\nIntegrity 20/20", _small, TextAlignmentOptions.Top, new Vector2(0, -220), new Vector2(560, 80));
            var score = MakeText(panel, "Score", "SCORE 12345", _large, TextAlignmentOptions.Top, new Vector2(0, -310), new Vector2(560, 40));
            score.color = Amber;

            var retry = MakeButton(panel, "Retry", "RETRY", theme, new Vector2(0.5f,0), new Vector2(0.5f,0), new Vector2(-110, 30), new Vector2(240, 74));
            var menu = MakeButton(panel, "Menu", "MAIN MENU", theme, new Vector2(0.5f,0), new Vector2(0.5f,0), new Vector2(150, 30), new Vector2(240, 74));

            var so = new SerializedObject(comp);
            SetRef(so, "waveManager", Object.FindFirstObjectByType<WaveManager>());
            SetRef(so, "theme", theme);
            SetRef(so, "root", root.gameObject);
            SetRef(so, "titleLabel", title);
            SetRef(so, "bodyLabel", body);
            SetRef(so, "scoreLabel", score);
            SetArray(so, "stars", stars.Cast<Object>().ToArray());
            SetRef(so, "retryButton", retry.GetComponent<Button>());
            SetRef(so, "menuButton", menu.GetComponent<Button>());
            so.ApplyModifiedPropertiesWithoutUndo();

            root.gameObject.SetActive(false);
            return comp;
        }

        static TitleScreen BuildTitleScreen(Canvas canvas, UITheme theme)
        {
            var root = MakeFullscreenDim(canvas.transform, "TitleScreen", 0.85f);
            var comp = canvas.gameObject.AddComponent<TitleScreen>();

            var logo = MakeText(root, "Logo", "COREHOLD", 96f, TextAlignmentOptions.Center, new Vector2(0, 260), new Vector2(1200, 140));
            logo.color = Cyan;
            SetAnchors(logo.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 260));
            var tag = MakeText(root, "Tagline", "HOLD THE LINE", _small, TextAlignmentOptions.Center, new Vector2(0, 190), new Vector2(1200, 30));
            tag.color = new Color(0.8f,0.9f,0.95f,1f);
            SetAnchors(tag.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 185));
            var best = MakeText(root, "BestScore", "", _small, TextAlignmentOptions.Center, new Vector2(0, 150), new Vector2(1200, 30));
            best.color = Amber;
            SetAnchors(best.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 150));

            var diffRow = MakeRect(root, "Difficulties", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(960, 200));
            var dH = diffRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            dH.spacing = 24; dH.childAlignment = TextAnchor.MiddleCenter; dH.childControlWidth = false; dH.childControlHeight = false;

            var (nBtn, nBest, _) = BuildDifficultyCard(diffRow, "NORMAL", theme);
            var (vBtn, vBest, vLock) = BuildDifficultyCard(diffRow, "VETERAN", theme);
            var (mBtn, mBest, mLock) = BuildDifficultyCard(diffRow, "NIGHTMARE", theme);

            var mute = MakeButton(root, "Mute", "♪ ON", theme, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(200, 60));

            var so = new SerializedObject(comp);
            SetRef(so, "root", root.gameObject);
            SetRef(so, "bestScoreLabel", best);
            SetRef(so, "normalButton", nBtn.GetComponent<Button>());
            SetRef(so, "veteranButton", vBtn.GetComponent<Button>());
            SetRef(so, "nightmareButton", mBtn.GetComponent<Button>());
            SetRef(so, "normalBest", nBest);
            SetRef(so, "veteranBest", vBest);
            SetRef(so, "nightmareBest", mBest);
            SetRef(so, "muteButton", mute.GetComponent<Button>());
            SetRef(so, "muteLabel", mute.GetComponentInChildren<TMP_Text>());
            SetRef(so, "veteranLock", vLock);
            SetRef(so, "nightmareLock", mLock);
            so.ApplyModifiedPropertiesWithoutUndo();

            return comp;
        }

        static (RectTransform btn, TMP_Text best, GameObject lockGo) BuildDifficultyCard(RectTransform parent, string label, UITheme theme)
        {
            var btn = MakeButtonBase(label, parent, theme, new Vector2(0.5f,0.5f), new Vector2(0.5f,0.5f), Vector2.zero, new Vector2(280, 170));
            var name = MakeText(btn, "Name", label, _large, TextAlignmentOptions.Center, new Vector2(0, 44), new Vector2(260, 44));
            SetAnchors(name.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 44));
            var best = MakeText(btn, "Best", "BEST 0", 18f, TextAlignmentOptions.Center, new Vector2(0, -4), new Vector2(260, 26));
            best.color = Amber;
            SetAnchors(best.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -4));
            var lockText = MakeText(btn, "Lock", "LOCKED", _small, TextAlignmentOptions.Center, new Vector2(0, -44), new Vector2(260, 26));
            SetAnchors(lockText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -44));
            var lockGo = lockText.gameObject;
            lockGo.GetComponent<TMP_Text>().color = Danger;
            lockGo.SetActive(false);
            return (btn, best, lockGo);
        }

        // ============================================================ SYSTEMS

        static void EnsureOverlayManager(StringBuilder sb)
        {
            if (Object.FindFirstObjectByType<OverlayManager>() != null) { sb.AppendLine("OverlayManager present."); return; }
            var go = new GameObject("OverlayManager", typeof(OverlayManager));
            sb.AppendLine("Added OverlayManager (world-space bars + pips).");
        }

        static void EnsureGameFlow(HUDController hud, Canvas menus, StringBuilder sb)
        {
            var gmGo = GameObject.Find("GameManager");
            if (gmGo == null) gmGo = new GameObject("GameManager", typeof(GameManager));

            // Remove any leftover missing-script GameBootstrap component.
            var flow = Object.FindFirstObjectByType<GameFlow>();
            if (flow == null) flow = gmGo.AddComponent<GameFlow>();

            var so = new SerializedObject(flow);
            SetRef(so, "titleScreen", Object.FindFirstObjectByType<TitleScreen>());
            SetRef(so, "waveManager", Object.FindFirstObjectByType<WaveManager>());
            so.ApplyModifiedPropertiesWithoutUndo();
            sb.AppendLine("GameFlow wired (title -> build).");
        }

        static void CleanupLegacy(StringBuilder sb)
        {
            // The old IMGUI ResultScreen object may still be in the scene with the
            // now-recompiled ResultScreen (empty refs); it is harmless but redundant
            // because the real ResultScreen lives on Canvas_Menus. Remove the stray
            // component if the object has no other purpose.
            var stray = GameObject.Find("ResultScreen");
            if (stray != null)
            {
                var comps = stray.GetComponents<Component>();
                // Keep the object but strip a duplicate ResultScreen so only the
                // canvas-driven one runs.
                var rs = stray.GetComponent<ResultScreen>();
                if (rs != null)
                {
                    Object.DestroyImmediate(rs);
                    sb.AppendLine("Removed stray IMGUI-era ResultScreen component.");
                }
            }

            // GameManager may hold a missing GameBootstrap script component.
            var gm = GameObject.Find("GameManager");
            if (gm != null)
                RemoveMissingScripts(gm, sb);
        }

        static void RemoveMissingScripts(GameObject go, StringBuilder sb)
        {
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            if (removed > 0) sb.AppendLine($"Removed {removed} missing-script component(s) from {go.name}.");
        }

        // ============================================================ UI HELPERS

        static Sprite Load(string path)
        {
            var s = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (s == null) Debug.LogWarning($"[COREHOLD] UI sprite missing: {path}");
            return s;
        }

        static void DestroyIfExists(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }

        static Canvas MakeCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        static RectTransform MakeRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;
            return rt;
        }

        static RectTransform MakePanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, Sprite sprite)
        {
            var rt = MakeRect(parent, name, anchorMin, anchorMax, pos, size);
            // Pivot toward the anchored corner so top-left anchored panels sit inside the screen.
            rt.pivot = PivotForAnchor(anchorMin);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite; img.type = Image.Type.Sliced; img.color = PanelTint;
            if (sprite == null) img.color = new Color(0.06f, 0.09f, 0.12f, 0.85f);
            return rt;
        }

        static Vector2 PivotForAnchor(Vector2 anchorMin)
        {
            return new Vector2(anchorMin.x, anchorMin.y);
        }

        static TMP_Text MakeText(Transform parent, string name, string text, float size, TextAlignmentOptions align, Vector2 pos, Vector2 sizeDelta)
        {
            var rt = MakeRect(parent, name, new Vector2(0.5f, 1), new Vector2(0.5f, 1), pos, sizeDelta);
            // Re-anchor based on the alignment intent: keep simple, anchor to parent centre-top by default.
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            Style(t, text, size, align);
            return t;
        }

        static void Style(TMP_Text t, string text, float size, TextAlignmentOptions align)
        {
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = Color.white;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            if (_font != null) t.font = _font;
        }

        static RectTransform MakeButtonBase(string name, Transform parent, UITheme theme, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            var rt = MakeRect(parent, name, anchorMin, anchorMax, pos, size);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = theme.buttonNormal; img.type = Image.Type.Sliced; img.color = Color.white;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            btn.targetGraphic = img;
            var cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f); // hover tint (GDD §9.7)
            cb.pressedColor = new Color(0.7f, 0.85f, 0.95f, 1f);
            cb.selectedColor = Color.white;
            cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            return rt;
        }

        static RectTransform MakeButton(Transform parent, string name, string label, UITheme theme, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            var rt = MakeButtonBase(name, parent, theme, anchorMin, anchorMax, pos, size);
            rt.pivot = PivotForAnchor(anchorMin);
            var t = MakeText(rt, "Label", label, _small, TextAlignmentOptions.Center, Vector2.zero, size);
            SetAnchors(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero);
            t.rectTransform.offsetMin = Vector2.zero; t.rectTransform.offsetMax = Vector2.zero;
            return rt;
        }

        static RectTransform MakeIconButton(Transform parent, string name, Sprite icon, UITheme theme, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            var rt = MakeButtonBase(name, parent, theme, anchorMin, anchorMax, pos, size);
            rt.pivot = PivotForAnchor(anchorMin);
            var img = rt.GetComponent<Image>();
            if (icon != null) { img.sprite = icon; img.type = Image.Type.Simple; }
            return rt;
        }

        static RectTransform MakeFullscreenDim(Transform parent, string name, float alpha = 0.6f)
        {
            var rt = MakeRect(parent, name, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.02f, 0.03f, 0.05f, alpha);
            return rt;
        }

        static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max, Vector2 anchoredPos)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.anchoredPosition = anchoredPos;
        }

        static void SetRef(SerializedObject so, string prop, Object value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.objectReferenceValue = value;
            else Debug.LogWarning($"[COREHOLD] Missing serialized property '{prop}' on {so.targetObject.GetType().Name}");
        }

        static void SetArray(SerializedObject so, string prop, Object[] values)
        {
            var p = so.FindProperty(prop);
            if (p == null) { Debug.LogWarning($"[COREHOLD] Missing array property '{prop}'"); return; }
            p.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
