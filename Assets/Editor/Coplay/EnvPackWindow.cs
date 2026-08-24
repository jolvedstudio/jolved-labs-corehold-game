using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The asking front-end for env-pack building (R25). The old menu item ran the
/// authoring-tree build silently and reported only to the console; this window
/// owns that menu path now and makes both flows explicit:
///
///   • AUTHORING TREE — the curated <c>Assets/Authoring/EnvPack/</c> build,
///     one measured pack per theme, exactly as before, report shown inline.
///   • CHOSEN FOLDERS — point the tool at ANY project folder(s) (a vendor kit's
///     Prefabs directory, a modeller's drop folder…), scan them RECURSIVELY,
///     preview every prefab it considers relevant, then build one named pack.
///
/// "Relevant" is decided the same way the placer's gates think: a prefab must
/// carry renderable 3-D mesh (UI and logic prefabs are listed as skipped, not
/// silently dropped) and must not live under an Editor/ folder (stripped from
/// player builds — a prop placed from one vanishes in the build). Roles come
/// from the asset's label first, then a category-named ancestor folder
/// (Landmarks/MidField/Clutter/Silhouettes), then the size heuristic — and the
/// report says which source decided each one.
///
/// Building REFRESHES an existing pack of the same name with the same
/// edits-preserved merge the tree build uses: entries match by prefab
/// identity, authored numbers and roles are never overwritten, and entries
/// pointing outside the scan are carried over untouched.
/// </summary>
public class EnvPackWindow : EditorWindow
{
    private const float Wide = 340f;

    // ---- chosen-folders state ----
    private string _packName = "";
    private readonly List<DefaultAsset> _folders = new List<DefaultAsset> { null };

    private struct Row
    {
        public GameObject prefab;
        public string path;
        public EnvPack.PropRole role;
        public string roleSource;   // "label" / "folder" / "size"
        public float radius, height;
    }

    private List<Row> _rows;        // null until a scan ran
    private int _skippedEditor, _skippedNoMesh, _duplicates;
    private string _scannedSummary = "";

    // ---- report ----
    private string _report = "";
    private Vector2 _reportScroll, _mainScroll;

    [MenuItem("Tools/COREHOLD/Level/Build Env Packs From Folders", false, 4)]
    public static void Open()
    {
        var w = GetWindow<EnvPackWindow>("Env Packs");
        w.minSize = new Vector2(560f, 520f);
        w.Show();
    }

    private void OnGUI()
    {
        _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

        GUILayout.Label("Build measured Env Packs (R25)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Packs are MEASURED, never typed: footprint and height feed the clearance and " +
            "sight-line gates, so a number 30% short is a prop in the lane. Roles are intent — " +
            "review what the report suggests before generating with a new pack.",
            MessageType.Info);

        DrawAuthoringTreeSection();
        EditorGUILayout.Space(10f);
        DrawChosenFoldersSection();
        EditorGUILayout.Space(10f);
        DrawReport();

        EditorGUILayout.EndScrollView();
    }

    // ------------------------------------------------------------ authoring tree

    private void DrawAuthoringTreeSection()
    {
        GUILayout.Label("Authoring tree", EditorStyles.boldLabel);
        List<string> themes = EnvPackTools.DiscoverThemes();
        EditorGUILayout.LabelField("Root", "Assets/Authoring/EnvPack");
        EditorGUILayout.LabelField("Themes",
            themes.Count > 0 ? string.Join(", ", themes) : "none yet — the build creates the skeleton");

        if (GUILayout.Button("Build All Theme Packs", GUILayout.Width(Wide)))
        {
            _report = EnvPackTools.BuildFromFolders();
            _reportScroll = Vector2.zero;
        }
    }

    // ----------------------------------------------------------- chosen folders

    private void DrawChosenFoldersSection()
    {
        GUILayout.Label("Custom pack from chosen folders", EditorStyles.boldLabel);

        _packName = EditorGUILayout.TextField("Pack name", _packName);
        string packPath = PackPath();
        if (packPath != null)
        {
            bool exists = AssetDatabase.LoadAssetAtPath<EnvPack>(packPath) != null;
            EditorGUILayout.LabelField(" ", packPath + (exists ? "  (exists — will refresh, edits preserved)" : "  (new)"),
                EditorStyles.miniLabel);
        }

        GUILayout.Label("Folders to scan (recursive):");
        int removeAt = -1;
        for (int i = 0; i < _folders.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            _folders[i] = (DefaultAsset)EditorGUILayout.ObjectField(_folders[i], typeof(DefaultAsset), false);
            string p = _folders[i] != null ? AssetDatabase.GetAssetPath(_folders[i]) : null;
            if (p != null && !AssetDatabase.IsValidFolder(p))
                GUILayout.Label("not a folder", EditorStyles.miniLabel, GUILayout.Width(80f));
            if (GUILayout.Button("×", GUILayout.Width(22f)))
                removeAt = i;
            EditorGUILayout.EndHorizontal();
        }
        if (removeAt >= 0)
            _folders.RemoveAt(removeAt);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Slot", GUILayout.Width(80f)))
            _folders.Add(null);
        if (GUILayout.Button("Pick Folder…", GUILayout.Width(110f)))
            PickFolder();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4f);

        List<string> roots = ValidRoots();
        using (new EditorGUI.DisabledScope(roots.Count == 0))
        {
            if (GUILayout.Button($"SCAN {roots.Count} folder(s)", GUILayout.Width(Wide)))
                Scan(roots);
        }

        if (_rows != null)
        {
            EditorGUILayout.HelpBox(_scannedSummary, _rows.Count > 0 ? MessageType.None : MessageType.Warning);
            using (new EditorGUI.DisabledScope(_rows.Count == 0 || packPath == null))
            {
                if (GUILayout.Button($"BUILD PACK  ({_rows.Count} prefab(s))", GUILayout.Width(Wide)))
                    BuildPack(packPath);
            }
            if (packPath == null)
                EditorGUILayout.LabelField(" ", "name the pack to enable the build", EditorStyles.miniLabel);
        }
    }

    private string PackPath()
    {
        string s = Sanitise(_packName);
        return string.IsNullOrEmpty(s) ? null : $"{EnvPackTools.PackDir}/EnvPack_{s}.asset";
    }

    private static string Sanitise(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var sb = new StringBuilder();
        foreach (char c in s.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }

    private List<string> ValidRoots()
    {
        var roots = new List<string>();
        foreach (DefaultAsset f in _folders)
        {
            if (f == null)
                continue;
            string p = AssetDatabase.GetAssetPath(f);
            if (AssetDatabase.IsValidFolder(p) && !roots.Contains(p))
                roots.Add(p);
        }
        return roots;
    }

    private void PickFolder()
    {
        string abs = EditorUtility.OpenFolderPanel("Add prop folder (inside this project)", Application.dataPath, "");
        if (string.IsNullOrEmpty(abs))
            return;

        string norm = abs.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (!norm.StartsWith(dataPath))
        {
            EditorUtility.DisplayDialog("Outside the project",
                "Pack entries are asset references, so the folder must live inside this project's " +
                "Assets/. Import the content first, then point the scan at it.", "OK");
            return;
        }

        string rel = "Assets" + norm.Substring(dataPath.Length);
        var asset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(rel);
        if (asset == null || !AssetDatabase.IsValidFolder(rel))
        {
            EditorUtility.DisplayDialog("Not a folder", $"'{rel}' did not resolve to a project folder.", "OK");
            return;
        }

        // Fill the first empty slot rather than always appending.
        int slot = _folders.IndexOf(null);
        if (slot >= 0) _folders[slot] = asset;
        else _folders.Add(asset);
    }

    // ------------------------------------------------------------------- scan

    private void Scan(List<string> roots)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== Scan for props — {roots.Count} folder(s), recursive ===");
        foreach (string r in roots)
            log.AppendLine($"  {r}");
        log.AppendLine();

        _rows = new List<Row>();
        _skippedEditor = _skippedNoMesh = _duplicates = 0;
        var claimed = new HashSet<GameObject>();

        // FindAssets walks the folders recursively — subfolder layout is free.
        string[] guids = AssetDatabase.FindAssets("t:Prefab", roots.ToArray());
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;
            if (!claimed.Add(prefab))
            {
                _duplicates++;
                continue;
            }

            if (EnvPackTools.IsUnderEditorFolder(path))
            {
                _skippedEditor++;
                log.AppendLine($"  ! {prefab.name} — under an Editor/ folder ({path}), SKIPPED: Unity strips " +
                               "those from player builds, so a level dressed with it loses the prop in a build.");
                continue;
            }

            if (!EnvPackTools.TryMeasure(prefab, out EnvPackTools.Measurement m))
            {
                _skippedNoMesh++;
                log.AppendLine($"  · {prefab.name} — no renderable 3-D mesh (a UI or logic prefab?), skipped.");
                continue;
            }

            // Role: deliberate label > category-named ancestor folder > size.
            string source;
            EnvPack.PropRole role;
            if (EnvPackTools.TryRoleFromLabels(prefab, out EnvPack.PropRole labelled))
            {
                role = labelled;
                source = "label";
            }
            else if (TryRoleFromPath(path, out EnvPack.PropRole folderRole))
            {
                role = folderRole;
                source = "folder";
            }
            else
            {
                role = EnvPackTools.SuggestRole(m, 1f);
                source = "size";
            }

            _rows.Add(new Row
            {
                prefab = prefab,
                path = path,
                role = role,
                roleSource = source,
                radius = m.radius,
                height = m.height
            });

            log.AppendLine($"  + {prefab.name,-32} {role,-10} (by {source})  r={m.radius,6:0.00}  h={m.height,6:0.00}");
            EnvPackTools.AppendMeasurementWarnings(prefab.name, m, 1f, log);
        }

        int landmark = 0, mid = 0, clutter = 0, silhouette = 0;
        foreach (Row row in _rows)
        {
            switch (row.role)
            {
                case EnvPack.PropRole.Landmark: landmark++; break;
                case EnvPack.PropRole.MidField: mid++; break;
                case EnvPack.PropRole.Clutter: clutter++; break;
                case EnvPack.PropRole.Silhouette: silhouette++; break;
            }
        }

        _scannedSummary =
            $"{_rows.Count} usable prefab(s): Landmark {landmark}, MidField {mid}, Clutter {clutter}, " +
            $"Silhouette {silhouette}. Skipped: {_skippedNoMesh} without renderable mesh, " +
            $"{_skippedEditor} under Editor/, {_duplicates} duplicate(s).";

        log.AppendLine();
        log.AppendLine(_scannedSummary);
        if (_rows.Count > 0)
            log.AppendLine("Nothing is written yet — review the roles above, then BUILD PACK.");

        _report = log.ToString();
        _reportScroll = Vector2.zero;
        Debug.Log(_report);
    }

    /// <summary>Role from the nearest category-named ancestor folder (deepest wins),
    /// matching the authoring-tree folder names and the bare role names.</summary>
    private static bool TryRoleFromPath(string assetPath, out EnvPack.PropRole role)
    {
        role = EnvPack.PropRole.Unassigned;
        string[] segments = assetPath.Split('/');
        for (int i = segments.Length - 2; i >= 0; i--)   // -2: skip the file itself
        {
            foreach (var (folder, candidate) in EnvPackTools.CategoryFolders)
            {
                if (string.Equals(segments[i], folder, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segments[i], candidate.ToString(), System.StringComparison.OrdinalIgnoreCase))
                {
                    role = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ build

    private void BuildPack(string packPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== Build pack from scan — {packPath} ===");

        if (!AssetDatabase.IsValidFolder(EnvPackTools.PackDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "EnvPacks");

        var pack = AssetDatabase.LoadAssetAtPath<EnvPack>(packPath);
        bool created = pack == null;
        if (created)
        {
            pack = ScriptableObject.CreateInstance<EnvPack>();
            AssetDatabase.CreateAsset(pack, packPath);
        }
        log.AppendLine(created ? "(created)" : "(refreshed — authored edits preserved)");

        // Same merge contract as the authoring-tree build: identity by prefab,
        // authored values win, unknown entries carried over.
        var existing = new Dictionary<GameObject, EnvPack.Entry>();
        if (pack.entries != null)
            foreach (EnvPack.Entry e in pack.entries)
                if (e.prefab != null && !existing.ContainsKey(e.prefab))
                    existing[e.prefab] = e;

        var result = new List<EnvPack.Entry>();
        var claimed = new HashSet<GameObject>();
        int added = 0, refreshed = 0;

        foreach (Row row in _rows)
        {
            if (!claimed.Add(row.prefab))
                continue;

            if (existing.TryGetValue(row.prefab, out EnvPack.Entry entry))
            {
                refreshed++;
                EnvPackTools.FillMissing(ref entry, row.prefab, log);
            }
            else
            {
                added++;
                entry = new EnvPack.Entry
                {
                    prefab = row.prefab,
                    role = row.role,
                    scaleRange = new Vector2(1f, 1f),
                    allowInFold = false            // pockets are where pads live
                };
                EnvPackTools.FillMissing(ref entry, row.prefab, log);
                log.AppendLine($"  + {row.prefab.name,-32} {row.role,-10} (by {row.roleSource})  " +
                               $"r={entry.footprintRadius,6:0.00}  h={entry.height,6:0.00}");
            }
            result.Add(entry);
        }

        int outside = 0;
        if (pack.entries != null)
        {
            foreach (EnvPack.Entry e in pack.entries)
            {
                if (e.prefab == null || claimed.Contains(e.prefab))
                    continue;
                result.Add(e);
                outside++;
            }
        }

        if (string.IsNullOrEmpty(pack.themeName))
            pack.themeName = Sanitise(_packName);
        pack.entries = result.ToArray();
        EditorUtility.SetDirty(pack);
        AssetDatabase.SaveAssets();
        Selection.activeObject = pack;

        log.AppendLine();
        log.AppendLine($"{result.Count} entr(ies): {added} added, {refreshed} refreshed (edits preserved), " +
                       $"{outside} carried over from outside the scan. {pack.CountInvalid()} invalid.");
        log.AppendLine($"Roles — Landmark {pack.CountInRole(EnvPack.PropRole.Landmark)}, " +
                       $"MidField {pack.CountInRole(EnvPack.PropRole.MidField)}, " +
                       $"Clutter {pack.CountInRole(EnvPack.PropRole.Clutter)}, " +
                       $"Silhouette {pack.CountInRole(EnvPack.PropRole.Silhouette)}.");

        if (pack.groundMaterial == null && pack.groundPrefab == null)
            log.AppendLine("No ground assigned — this theme keeps whatever ground the scene has (and on " +
                           "terrain maps, groundMaterial + groundTilingPerMetre are what the relief mesh " +
                           "inherits). Set them on the pack asset.");
        if (pack.weatherPool == null || pack.weatherPool.Length == 0)
            log.AppendLine("No weatherPool — this theme generates on the null preset. Set it on the PACK " +
                           "rather than the blueprint, so an ice map cannot draw desert dust.");
        log.AppendLine("Roles marked 'by size' are heuristic starting points — review them on the asset, " +
                       "then add the pack to a blueprint's envPackPool to use it.");

        EnvPackTools.AppendCommitWarning(pack, log);

        _report = log.ToString();
        _reportScroll = Vector2.zero;
        Debug.Log(_report);
    }

    // ------------------------------------------------------------------ report

    private void DrawReport()
    {
        GUILayout.Label("Report", EditorStyles.boldLabel);
        if (string.IsNullOrEmpty(_report))
        {
            EditorGUILayout.LabelField("Nothing run yet — reports from scans and builds land here (and in the console).",
                EditorStyles.miniLabel);
            return;
        }

        _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll, GUILayout.MinHeight(180f));
        EditorGUILayout.TextArea(_report, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }
}
