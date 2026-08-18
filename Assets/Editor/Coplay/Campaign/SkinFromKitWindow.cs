using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// Turn a purchased UI kit (GUI Pro Casual, Kenney, anything sprite-first)
    /// into a <see cref="UISkin"/> without hand-copying files:
    ///
    ///   1. Point it at the kit's folder — wherever it was imported, including
    ///      the git-ignored Assets/Vendor.
    ///   2. SCAN keyword-matches the kit's sprites onto the skin's slots and
    ///      prefills a best guess per slot; every guess is an ObjectField, so
    ///      confirming or swapping is a drag, not typing names blind.
    ///   3. CREATE SKIN copies exactly the chosen sprites into the COMMITTED
    ///      Assets/_COREHOLD/Art/UI/Skins/&lt;name&gt;/ folder (CopyAsset keeps
    ///      importer settings, so 9-slice borders survive) and writes the skin
    ///      asset referencing the copies — never the originals. The kit can be
    ///      git-ignored, uninstalled or updated; shipped skins do not care.
    ///
    /// Palette and font stay authored on the skin asset afterwards — sprites
    /// carry the shape language, colors stay a deliberate choice.
    /// </summary>
    public class SkinFromKitWindow : EditorWindow
    {
        private const string SkinRoot = "Assets/_COREHOLD/Art/UI/Skins";

        private DefaultAsset _kitFolder;
        private string _skinName = "Casual";
        private Vector2 _scroll;
        private string _report = "";

        // Slot table: name → (positive keywords, negative keywords). Scoring is
        // heuristic on purpose — the human confirms every pick.
        private static readonly (string slot, string[] yes, string[] no)[] Slots =
        {
            ("panel",          new[] { "panel", "frame", "window", "box" },            new[] { "icon", "button", "bar" }),
            ("popup",          new[] { "popup", "dialog", "window", "modal" },         new[] { "icon", "button" }),
            ("buttonNormal",   new[] { "button", "btn" },                              new[] { "pressed", "push", "down", "disable", "gray", "grey", "icon", "small" }),
            ("buttonPressed",  new[] { "button", "btn", "pressed", "push", "down" },   new[] { "disable", "icon" }),
            ("buttonDisabled", new[] { "button", "btn", "disable", "gray", "grey", "inactive" }, new[] { "icon" }),
            ("barBackground",  new[] { "bar", "gauge", "slider", "back", "bg", "empty", "frame" }, new[] { "fill", "icon" }),
            ("barFill",        new[] { "bar", "gauge", "slider", "fill", "gage" },     new[] { "back", "bg", "empty", "frame", "icon" }),
            ("pauseIcon",      new[] { "pause" },                                      new string[0]),
            ("starFull",       new[] { "star" },                                       new[] { "empty", "off", "slot", "gray", "grey", "outline" }),
            ("starEmpty",      new[] { "star", "empty", "off", "slot", "gray", "grey", "outline" }, new string[0]),
        };

        private readonly Dictionary<string, Sprite> _picks = new Dictionary<string, Sprite>();
        private List<Sprite> _kitSprites;

        [MenuItem("Tools/COREHOLD/Campaign/Create Skin From UI Kit…", false, 30)]
        public static void Open()
        {
            var w = GetWindow<SkinFromKitWindow>("Skin From Kit");
            w.minSize = new Vector2(440, 520);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "1. Drop the UI kit's folder (from the Project view — Vendor is fine).\n" +
                "2. SCAN, then confirm or swap each slot's sprite.\n" +
                "3. CREATE SKIN — chosen sprites are COPIED into the committed skin folder; " +
                "the kit itself never ships and may stay git-ignored.", MessageType.Info);

            _kitFolder = (DefaultAsset)EditorGUILayout.ObjectField("Kit folder", _kitFolder, typeof(DefaultAsset), false);
            _skinName = EditorGUILayout.TextField("Skin name", _skinName);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _kitFolder != null;
                if (GUILayout.Button("SCAN kit for slot candidates", GUILayout.Height(24)))
                    Scan();
                GUI.enabled = true;
            }

            if (_kitSprites != null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField($"Slots  ({_kitSprites.Count} sprites in the kit)", EditorStyles.boldLabel);
                foreach (var (slot, _, _) in Slots)
                {
                    _picks.TryGetValue(slot, out var current);
                    var picked = (Sprite)EditorGUILayout.ObjectField(slot, current, typeof(Sprite), false);
                    if (picked != current) _picks[slot] = picked;
                }

                EditorGUILayout.Space(8);
                GUI.enabled = _picks.Values.Any(s => s != null) && !string.IsNullOrWhiteSpace(_skinName);
                if (GUILayout.Button($"CREATE SKIN '{Sanitise(_skinName)}' (copies sprites into the project)",
                                     GUILayout.Height(28)))
                    CreateSkin();
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(_report))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(_report, GUILayout.MinHeight(120));
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- scan

        private void Scan()
        {
            string root = AssetDatabase.GetAssetPath(_kitFolder);
            _kitSprites = AssetDatabase.FindAssets("t:Sprite", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .SelectMany(p => AssetDatabase.LoadAllAssetsAtPath(p).OfType<Sprite>())
                .ToList();

            _picks.Clear();
            foreach (var (slot, yes, no) in Slots)
            {
                Sprite best = null;
                int bestScore = 0;
                foreach (var sprite in _kitSprites)
                {
                    string n = sprite.name.ToLowerInvariant();
                    int score = yes.Count(k => n.Contains(k)) * 2 - no.Count(k => n.Contains(k)) * 3;
                    if (score <= 0) continue;
                    // Shorter names edge out decorated variants at equal score.
                    if (best == null || score > bestScore ||
                        (score == bestScore && sprite.name.Length < best.name.Length))
                    {
                        best = sprite;
                        bestScore = score;
                    }
                }
                _picks[slot] = best;
            }

            int filled = _picks.Values.Count(s => s != null);
            _report = $"Scanned {_kitSprites.Count} sprites; prefilled {filled}/{Slots.Length} slots.\n" +
                      "Guesses are heuristic — eyeball each one (kits name things creatively) and drag in " +
                      "replacements from the kit where the guess is wrong. Empty slots keep the default look.";
        }

        // -------------------------------------------------------------- create

        private void CreateSkin()
        {
            string skinName = Sanitise(_skinName);
            string dir = $"{SkinRoot}/{skinName}";
            EnsureFolder(dir);

            var log = new StringBuilder();
            var copied = new Dictionary<string, Sprite>();

            foreach (var pair in _picks.Where(p => p.Value != null))
            {
                string sourcePath = AssetDatabase.GetAssetPath(pair.Value);
                string destPath = $"{dir}/{System.IO.Path.GetFileName(sourcePath)}";

                // One source texture can serve several slots (sprite sheets) —
                // copy it once, resolve each slot's sprite out of the copy.
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(destPath) == null)
                {
                    if (!AssetDatabase.CopyAsset(sourcePath, destPath))
                    {
                        log.AppendLine($"  {pair.Key}: COPY FAILED for {sourcePath} — slot left empty.");
                        continue;
                    }
                    log.AppendLine($"  copied {System.IO.Path.GetFileName(sourcePath)}");
                }

                var sprites = AssetDatabase.LoadAllAssetsAtPath(destPath).OfType<Sprite>().ToList();
                var match = sprites.FirstOrDefault(s => s.name == pair.Value.name) ?? sprites.FirstOrDefault();
                if (match == null)
                {
                    log.AppendLine($"  {pair.Key}: no sprite in the copied asset — slot left empty.");
                    continue;
                }
                copied[pair.Key] = match;
            }

            string skinPath = $"{SkinRoot}/Skin_{skinName}.asset";
            var skin = AssetDatabase.LoadAssetAtPath<UISkin>(skinPath);
            bool created = skin == null;
            if (created) skin = CreateInstance<UISkin>();

            // Overwrite only what this run filled — re-running the tool must not
            // blank slots that were assigned by hand or by an earlier pass.
            void Set(string key, ref Sprite field)
            {
                if (copied.TryGetValue(key, out var s)) field = s;
            }
            Set("panel", ref skin.panel);
            Set("popup", ref skin.popup);
            Set("buttonNormal", ref skin.buttonNormal);
            Set("buttonPressed", ref skin.buttonPressed);
            Set("buttonDisabled", ref skin.buttonDisabled);
            Set("barBackground", ref skin.barBackground);
            Set("barFill", ref skin.barFill);
            Set("pauseIcon", ref skin.pauseIcon);
            Set("starFull", ref skin.starFull);
            Set("starEmpty", ref skin.starEmpty);

            if (created) AssetDatabase.CreateAsset(skin, skinPath);
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();

            _report = $"Skin at {skinPath} — {copied.Count} slot(s) filled from committed copies in {dir}.\n" +
                      log +
                      "Next: set the skin's palette/font in the Inspector (sprites carry shape; color stays " +
                      "a choice), assign it to the campaign's authoring asset, and RE-GENERATE the campaign — " +
                      "skins bake at build time.";
            EditorGUIUtility.PingObject(skin);
            Debug.Log($"[SkinFromKit] {_report}");
        }

        private static string Sanitise(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "Skin";
        }

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
