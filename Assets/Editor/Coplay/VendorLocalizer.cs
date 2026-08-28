using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes committed content SELF-CONTAINED: every asset a level, campaign stage
/// or config actually references from a git-ignored vendor pack is COPIED into
/// the committed <see cref="VendoredRoot"/> (original folder structure kept)
/// and every reference in the closure is remapped to the copy — so a fresh
/// clone, another machine, or a CI build gets the same scene the author saw,
/// instead of dangling GUIDs and silently missing dressing/effects.
///
/// Why copy-and-remap rather than committing the packs: the packs are large,
/// partly redistribution-restricted, and 95% unused — the standing policy
/// git-ignores them (Vendor/, Yoge/, Layer Lab/, the VFX packs). What a level
/// SHIPS, though, must live in the repo. This tool moves exactly the used
/// subset across that line, deduplicated by source path so ten levels dressing
/// from one kit share one copy.
///
/// Mechanics: dependencies come from <see cref="AssetDatabase.GetDependencies(string, bool)"/>
/// (recursive, so a vendor prefab's own materials/textures/meshes ride along);
/// copies get NEW GUIDs (CopyAsset), and every TEXT-serialized asset in the
/// closure — the roots, any committed mid-assets, and the copies themselves,
/// which may reference each other — has old GUIDs rewritten to new. The
/// project serializes assets as text (Force Text), which is what makes the
/// rewrite safe; binary content files (textures, meshes, audio) are carriers
/// of no references and are copied verbatim.
///
/// SCRIPTS ARE NEVER COPIED. A copied pack script is the same class compiled
/// TWICE on any machine that still has the pack installed (CS0101 —
/// git-ignored does not mean not compiled), so .cs/.asmdef/.dll stay behind
/// and the copied prefabs' vendor MonoBehaviours are STRIPPED instead.
/// That is a policy fit, not just a workaround: the director/pool owns
/// effect lifecycle, light stripping and camera shake, so pack helper
/// scripts are redundant here — and a script-free committed copy behaves
/// identically on the author's machine, a fresh clone, and CI.
///
/// Idempotent: a source already copied resolves to its existing copy; a
/// closure with nothing external is a no-op that says so. Every run also
/// self-heals the vendored root — stray copied scripts are deleted and
/// vendor/missing script components are stripped from every vendored
/// prefab, which is what repairs a project bitten by the pre-policy copier.
/// </summary>
public static class VendorLocalizer
{
    /// <summary>Committed destination for vendored copies.</summary>
    public const string VendoredRoot = "Assets/_COREHOLD/Vendored";

    /// <summary>
    /// Git-ignored roots (see .gitignore) — anything referenced from under
    /// these must be localized for the reference to survive a fresh clone.
    /// THE single in-code definition; keep in sync with .gitignore.
    /// </summary>
    public static readonly string[] IgnoredVendorRoots =
    {
        "Assets/Vendor/", "Assets/Yoge/", "Assets/Layer Lab/",
        "Assets/Eric VFX Studio/", "Assets/Free Slash VFX/",
    };

    /// <summary>Extensions that carry serialized references (Force Text YAML).</summary>
    private static readonly HashSet<string> TextAssetExtensions = new HashSet<string>
    {
        ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim",
        ".overrideController", ".playable", ".shadergraph", ".shadersubgraph",
        ".renderTexture", ".physicMaterial", ".terrainlayer",
    };

    /// <summary>Never copied: code would define the same types twice (CS0101/
    /// CS0111) on every machine where the source pack is still installed.
    /// Components referencing these are stripped from the copies instead.</summary>
    private static readonly HashSet<string> NeverCopyExtensions = new HashSet<string>
    {
        ".cs", ".asmdef", ".dll",
    };

    /// <summary>True when <paramref name="path"/> lives under an ignored vendor root.</summary>
    public static bool IsVendorPath(string path)
    {
        foreach (string root in IgnoredVendorRoots)
            if (path.StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Vendor-pack dependencies of the given roots (recursive closure),
    /// deterministic order. Used by preflight to DETECT before ship what
    /// <see cref="Localize"/> would fix.
    /// </summary>
    public static List<string> FindExternalDependencies(IEnumerable<string> rootAssetPaths)
    {
        var roots = rootAssetPaths.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
        if (roots.Length == 0)
            return new List<string>();
        return AssetDatabase.GetDependencies(roots, true)
            .Where(IsVendorPath)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Localize the closure of <paramref name="rootAssetPaths"/>: copy every
    /// vendor-pack dependency under <see cref="VendoredRoot"/> and remap every
    /// reference in the closure (and among the copies) to the copies. Returns
    /// how many assets were copied; the log carries the full account.
    /// Callers whose roots include an OPEN scene must reload it afterwards —
    /// the remap rewrites the file on disk.
    /// </summary>
    public static int Localize(IEnumerable<string> rootAssetPaths, StringBuilder log)
    {
        var roots = rootAssetPaths.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();

        // Always, even when there is nothing new to copy: heal the vendored
        // root itself. This is what repairs a project the pre-policy copier
        // bit — its script copies are deleted and the components that pointed
        // at them (now missing, or still resolving to a pack) are stripped.
        SelfHealVendoredRoot(log);

        List<string> external = FindExternalDependencies(roots);
        if (external.Count == 0)
        {
            log.AppendLine("  localize: closure is fully committed — nothing to copy.");
            ReportDanglingReferences(roots, log);
            return 0;
        }

        // ---- copy (or adopt an existing copy), building the guid map --------
        var guidMap = new Dictionary<string, string>();   // old -> new
        var bakedShaderGuids = new HashSet<string>();     // refs need fileID surgery
        int copied = 0, reused = 0, scriptsSkipped = 0;
        long bytes = 0;
        foreach (string src in external)
        {
            if (NeverCopyExtensions.Contains(Path.GetExtension(src).ToLowerInvariant()))
            {
                scriptsSkipped++;   // components referencing it are stripped below
                continue;
            }
            string dest = DestinationFor(src);
            string oldGuid = AssetDatabase.AssetPathToGUID(src);

            // CFXR ships its shaders as .cfxrshader — a custom extension that
            // imports ONLY through the pack's own ScriptedImporter (an editor
            // script inside the ignored pack). A verbatim copy therefore works
            // on this machine and is a dead file on every clean clone. Bake it
            // to a plain .shader instead, which the standard importer handles
            // everywhere; the reference fileIDs are fixed after the remap.
            if (Path.GetExtension(src).ToLowerInvariant() == ".cfxrshader")
            {
                string bakedDest = Path.ChangeExtension(dest, ".shader");
                string bakedGuid = File.Exists(bakedDest) ? AssetDatabase.AssetPathToGUID(bakedDest) : null;
                if (string.IsNullOrEmpty(bakedGuid))
                {
                    EnsureFolder(Path.GetDirectoryName(bakedDest).Replace('\\', '/'));
                    CopyShaderSiblingIncludes(src, bakedDest);
                    bakedGuid = BakeCustomShader(src, bakedDest, log);
                    if (!string.IsNullOrEmpty(bakedGuid))
                    {
                        copied++;
                        var bfi = new FileInfo(bakedDest);
                        if (bfi.Exists) bytes += bfi.Length;
                    }
                }
                if (!string.IsNullOrEmpty(bakedGuid) && !string.IsNullOrEmpty(oldGuid))
                {
                    guidMap[oldGuid] = bakedGuid;
                    bakedShaderGuids.Add(bakedGuid);
                    continue;
                }
                // Bake failed: fall through to the verbatim copy so this
                // machine still works; the bake log line names the clone gap.
            }
            // File.Exists, not AssetPathToGUID alone: AssetPathToGUID ALSO
            // answers for recently DELETED assets (its documented default),
            // which once made a wiped Vendored folder read as "79 already
            // vendored" and remapped the config onto guids of deleted files.
            // The question here is strictly "is there a copy on disk".
            string newGuid = File.Exists(dest) ? AssetDatabase.AssetPathToGUID(dest) : null;
            if (string.IsNullOrEmpty(newGuid))
            {
                EnsureFolder(Path.GetDirectoryName(dest).Replace('\\', '/'));
                if (!AssetDatabase.CopyAsset(src, dest))
                {
                    log.AppendLine($"  localize: COPY FAILED {src} → {dest} — reference left as-is.");
                    continue;
                }
                newGuid = AssetDatabase.AssetPathToGUID(dest);
                copied++;
                var fi = new FileInfo(dest);
                if (fi.Exists) bytes += fi.Length;

                // Shader include files travel by RELATIVE path, not GUID, so
                // GetDependencies never lists them — copy the shader's sibling
                // includes along so the copy compiles on a pack-less machine.
                string destExt = Path.GetExtension(dest).ToLowerInvariant();
                if (destExt == ".shader" || destExt == ".cfxrshader")
                    CopyShaderSiblingIncludes(src, dest);
            }
            else
            {
                reused++;
            }
            if (!string.IsNullOrEmpty(oldGuid) && !string.IsNullOrEmpty(newGuid))
                guidMap[oldGuid] = newGuid;
        }

        // ---- remap: closure text assets + the copies themselves -------------
        // .meta files ride along for BOTH: an importer's references live in
        // the meta (an FBX's material-remap table points at pack materials),
        // and a meta left unrewritten keeps the closure impure forever. A
        // meta's own "guid:" line is safe — map KEYS are pack guids, never a
        // committed or copied asset's own guid.
        var closure = AssetDatabase.GetDependencies(roots, true)
            .Where(p => !IsVendorPath(p) && p.StartsWith("Assets/", System.StringComparison.Ordinal))
            .ToArray();
        var toRewrite = new HashSet<string>(
            closure.Where(p => TextAssetExtensions.Contains(Path.GetExtension(p))));
        foreach (string p in closure)
            if (File.Exists(p + ".meta"))
                toRewrite.Add(p + ".meta");
        foreach (string src in external)
        {
            string dest = DestinationFor(src);
            if (TextAssetExtensions.Contains(Path.GetExtension(dest)) && File.Exists(dest))
                toRewrite.Add(dest);
            if (File.Exists(dest + ".meta"))
                toRewrite.Add(dest + ".meta");
        }

        int rewritten = 0;
        foreach (string path in toRewrite.OrderBy(p => p, System.StringComparer.Ordinal))
        {
            if (!File.Exists(path))
                continue;
            string text = File.ReadAllText(path);
            string updated = text;
            foreach (var kv in guidMap)
                updated = updated.Replace("guid: " + kv.Key, "guid: " + kv.Value);
            // Baked shaders: a scripted import's main object carries a hashed
            // fileID, a plain .shader's Shader object is always 4800000 — the
            // guid swap alone would leave references pointing at a nonexistent
            // sub-object of the right asset.
            foreach (string bg in bakedShaderGuids)
                updated = System.Text.RegularExpressions.Regex.Replace(
                    updated, "\\{fileID: -?\\d+, guid: " + bg + ", type: \\d+\\}",
                    "{fileID: 4800000, guid: " + bg + ", type: 3}");
            if (!ReferenceEquals(updated, text) && updated != text)
            {
                AssetDatabase.MakeEditable(path);
                File.WriteAllText(path, updated);
                rewritten++;
            }
        }
        if (rewritten > 0)
            AssetDatabase.Refresh();

        // ---- strip: the copies must carry no pack scripts -------------------
        int strippedComponents = 0;
        foreach (string src in external)
        {
            string dest = DestinationFor(src);
            if (Path.GetExtension(dest).ToLowerInvariant() == ".prefab" && File.Exists(dest))
                strippedComponents += StripVendorScripts(dest, log);
        }

        log.AppendLine($"  localize: {copied} vendor asset(s) copied into {VendoredRoot} " +
                       $"({bytes / 1024f / 1024f:0.0} MB){(reused > 0 ? $", {reused} already vendored" : "")}; " +
                       $"{rewritten} file(s) remapped to the committed copies.");
        if (scriptsSkipped > 0 || strippedComponents > 0)
            log.AppendLine($"  localize: {scriptsSkipped} pack script(s) NOT copied (code must compile exactly " +
                           $"once) and {strippedComponents} vendor script component(s) stripped from the copies — " +
                           "the director/pool owns effect lifecycle, so the copies behave the same on every machine.");

        // Convergence check: the whole point is an EMPTY vendor closure. When
        // dependencies remain, name each one AND the closure files still
        // holding a reference to it — "which file is stuck" is the actual
        // question when repeated localize runs do not converge.
        var remaining = FindExternalDependencies(roots);
        if (remaining.Count > 0)
        {
            var textCache = new Dictionary<string, string>();
            string TextOf(string p)
            {
                if (!textCache.TryGetValue(p, out string t))
                    textCache[p] = t = File.Exists(p) ? File.ReadAllText(p) : "";
                return t;
            }
            var holdersScan = AssetDatabase.GetDependencies(roots, true)
                .Where(p => !IsVendorPath(p) && p.StartsWith("Assets/", System.StringComparison.Ordinal))
                .ToList();
            log.AppendLine($"  localize: WARNING — {remaining.Count} vendor dependenc" +
                           $"{(remaining.Count == 1 ? "y" : "ies")} REMAIN after localize:");
            foreach (string dep in remaining.Take(10))
            {
                string depGuid = AssetDatabase.AssetPathToGUID(dep);
                var holders = new List<string>();
                foreach (string p in holdersScan)
                {
                    if (TextAssetExtensions.Contains(Path.GetExtension(p)) && TextOf(p).Contains(depGuid))
                        holders.Add(Path.GetFileName(p));
                    else if (TextOf(p + ".meta").Contains(depGuid))
                        holders.Add(Path.GetFileName(p) + " (meta)");
                    if (holders.Count >= 3)
                        break;
                }
                string tag = NeverCopyExtensions.Contains(Path.GetExtension(dep).ToLowerInvariant())
                    ? "   [script — never vendored: remove/strip the component]"
                    : "";
                log.AppendLine($"    {dep}{tag}");
                log.AppendLine($"      held by: {(holders.Count > 0 ? string.Join(", ", holders) : "(no text ref found — importer-internal)")}");
            }
            if (remaining.Count > 10)
                log.AppendLine($"    … and {remaining.Count - 10} more.");
        }

        // Post-condition, every run: the roots must not reference anything that
        // does not exist. A remap that lands on ghosts (the recently-deleted
        // guid trap above) must SCREAM here, never report success.
        ReportDanglingReferences(roots, log);
        return copied;
    }

    /// <summary>
    /// "Nothing to copy" is also what a BROKEN root looks like: a reference to
    /// a deleted vendored copy (or an uninstalled pack) resolves to NO asset at
    /// all, so it never appears as a vendor dependency. Scan the roots' text
    /// for guids that resolve to nothing and say so — the difference between
    /// "already localized" and "quietly dangling" is invisible otherwise.
    /// </summary>
    private static void ReportDanglingReferences(string[] roots, StringBuilder log)
    {
        var dangling = new HashSet<string>();
        foreach (string root in roots)
        {
            if (!File.Exists(root) ||
                !TextAssetExtensions.Contains(Path.GetExtension(root).ToLowerInvariant()))
                continue;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(root), "guid: ([0-9a-f]{32})"))
            {
                string guid = m.Groups[1].Value;
                if (guid == "0000000000000000e000000000000000" ||   // unity builtin extra
                    guid == "0000000000000000f000000000000000" ||   // unity default resources
                    guid == "00000000000000000000000000000000")
                    continue;
                if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    dangling.Add(guid);
            }
        }
        if (dangling.Count == 0)
            return;
        log.AppendLine($"  localize: WARNING — {dangling.Count} reference(s) in the roots resolve to NO asset on " +
                       "this machine, and a reference that points at nothing cannot be localized. If vendored " +
                       "copies were deleted after a remap, either restore Assets/_COREHOLD/Vendored from git or " +
                       "DISCARD the root files' local changes (so they reference the packs again), then re-run.");
    }

    /// <summary>
    /// Enforce the "no code in the vendored root" policy on what is already on
    /// disk: delete stray script copies (the pre-policy copier made them — the
    /// CS0101/CS0111 duplicate-class compile break on machines that still have
    /// the pack) and strip vendor-owned or missing script components from every
    /// vendored prefab. Idempotent; silent when there is nothing to heal.
    /// </summary>
    private static void SelfHealVendoredRoot(StringBuilder log)
    {
        if (!AssetDatabase.IsValidFolder(VendoredRoot))
            return;

        int deleted = 0;
        foreach (string file in Directory.GetFiles(VendoredRoot, "*", SearchOption.AllDirectories))
        {
            string path = file.Replace('\\', '/');
            if (!NeverCopyExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                continue;
            if (AssetDatabase.DeleteAsset(path))
                deleted++;
        }
        if (deleted > 0)
            AssetDatabase.Refresh();

        // Normalize custom-extension shaders that are ALREADY vendored: a
        // verbatim .cfxrshader copy imports only through the pack's own
        // ScriptedImporter, so it works on this machine and is a dead file on
        // every clean clone. Bake it to a plain .shader beside it, repoint
        // every vendored reference (fileID 4800000 — see the remap note), and
        // drop the custom copy. Prefers the pristine pack source when this
        // machine still has it.
        int normalized = 0;
        foreach (string file in Directory.GetFiles(VendoredRoot, "*.cfxrshader", SearchOption.AllDirectories))
        {
            string path = file.Replace('\\', '/');
            string packSrc = "Assets/" + path.Substring(VendoredRoot.Length + 1);
            string bakeSrc = File.Exists(packSrc) ? packSrc : path;
            string bakedDest = Path.ChangeExtension(path, ".shader");
            CopyShaderSiblingIncludes(bakeSrc, bakedDest);
            string oldGuid = AssetDatabase.AssetPathToGUID(path);
            string bakedGuid = File.Exists(bakedDest) ? AssetDatabase.AssetPathToGUID(bakedDest)
                                                      : BakeCustomShader(bakeSrc, bakedDest, log);
            if (string.IsNullOrEmpty(bakedGuid) || string.IsNullOrEmpty(oldGuid))
                continue;   // bake failed — the verbatim copy stays, log already says so

            foreach (string tf in Directory.GetFiles(VendoredRoot, "*", SearchOption.AllDirectories))
            {
                string tp = tf.Replace('\\', '/');
                if (!TextAssetExtensions.Contains(Path.GetExtension(tp).ToLowerInvariant()))
                    continue;
                string text = File.ReadAllText(tp);
                string updated = System.Text.RegularExpressions.Regex.Replace(
                    text, "\\{fileID: -?\\d+, guid: " + oldGuid + ", type: \\d+\\}",
                    "{fileID: 4800000, guid: " + bakedGuid + ", type: 3}");
                if (updated != text)
                {
                    AssetDatabase.MakeEditable(tp);
                    File.WriteAllText(tp, updated);
                }
            }
            AssetDatabase.DeleteAsset(path);
            normalized++;
            log.AppendLine($"  localize: normalized {Path.GetFileName(path)} → baked .shader; vendored references repointed.");
        }
        if (normalized > 0)
            AssetDatabase.Refresh();

        int stripped = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { VendoredRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // .prefab only: FindAssets(t:Prefab) also returns model assets
            // (.fbx), which carry no MonoBehaviours and cannot be re-saved.
            if (Path.GetExtension(path).ToLowerInvariant() == ".prefab")
                stripped += StripVendorScripts(path, log);
        }

        if (deleted > 0 || stripped > 0 || normalized > 0)
            log.AppendLine($"  localize: healed {VendoredRoot} — {deleted} stray script file(s) deleted, " +
                           $"{normalized} custom shader(s) baked, " +
                           $"{stripped} vendor/missing script component(s) stripped from vendored prefabs.");
    }

    /// <summary>
    /// Remove pack-owned and missing-script MonoBehaviours from one vendored
    /// prefab. Missing ones are the aftermath of deleting stray script copies;
    /// resolving ones point back into a pack that only exists on this machine.
    /// Returns how many components were removed.
    /// </summary>
    private static int StripVendorScripts(string prefabPath, StringBuilder log)
    {
        if (!File.Exists(prefabPath))
            return 0;

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        int removed = 0;
        try
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                try { removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject); }
                catch (System.Exception) { /* nested-instance objects can refuse; the log below still names the prefab */ }
            }

            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;
                MonoScript script = MonoScript.FromMonoBehaviour(mb);
                string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                bool vendorOwned = !string.IsNullOrEmpty(scriptPath) &&
                    (IsVendorPath(scriptPath) ||
                     scriptPath.StartsWith(VendoredRoot + "/", System.StringComparison.OrdinalIgnoreCase));
                if (!vendorOwned)
                    continue;
                try
                {
                    Object.DestroyImmediate(mb);
                    removed++;
                }
                catch (System.Exception)
                {
                    log.AppendLine($"  localize: could not strip '{script.name}' inside {prefabPath} " +
                                   "(nested prefab instance) — open the copy and remove the component by hand.");
                }
            }

            if (removed > 0)
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return removed;
    }

    /// <summary>
    /// Bake a custom-extension shader (CFXR's .cfxrshader) into a plain
    /// .shader asset. The source file is complete ShaderLab — the custom
    /// extension exists for the pack's editor tooling — so the standard
    /// importer compiles it on any machine, pack installed or not. The
    /// declared shader name is prefixed "Vendored/" so it can never collide
    /// with (or be found instead of) the pack's own. Returns the baked
    /// asset's guid, or null when the result does not compile — in which
    /// case the bake is deleted and the caller keeps the verbatim copy.
    /// </summary>
    private static string BakeCustomShader(string srcPath, string destShaderPath, StringBuilder log)
    {
        try
        {
            string source = File.ReadAllText(srcPath);
            source = new System.Text.RegularExpressions.Regex("Shader\\s+\"([^\"]+)\"")
                .Replace(source, m => $"Shader \"Vendored/{m.Groups[1].Value}\"", 1);
            File.WriteAllText(destShaderPath, source);
            AssetDatabase.ImportAsset(destShaderPath);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(destShaderPath);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                AssetDatabase.DeleteAsset(destShaderPath);
                log.AppendLine($"  localize: shader bake FAILED for {Path.GetFileName(srcPath)} — keeping the " +
                               "pack-importer copy. It works on THIS machine; a clean clone has no importer for " +
                               "it, so those effects need a committed replacement shader (aesthetic lane).");
                return null;
            }
            log.AppendLine($"  localize: baked {Path.GetFileName(srcPath)} → plain .shader " +
                           "(standard importer, compiles on any machine).");
            return AssetDatabase.AssetPathToGUID(destShaderPath);
        }
        catch (System.Exception e)
        {
            log.AppendLine($"  localize: shader bake ERROR for {srcPath}: {e.Message}");
            return null;
        }
    }

    /// <summary>Copy a shader's sibling *.cginc / *.hlsl files next to its copy —
    /// #include is path-relative and invisible to GetDependencies, so a copied
    /// shader would otherwise fail to compile on a machine without the pack.</summary>
    private static void CopyShaderSiblingIncludes(string srcShaderPath, string destShaderPath)
    {
        string srcDir = Path.GetDirectoryName(srcShaderPath).Replace('\\', '/');
        string destDir = Path.GetDirectoryName(destShaderPath).Replace('\\', '/');
        foreach (string file in Directory.GetFiles(srcDir))
        {
            string path = file.Replace('\\', '/');
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".cginc" && ext != ".hlsl")
                continue;
            string dest = $"{destDir}/{Path.GetFileName(path)}";
            if (!File.Exists(dest))
                AssetDatabase.CopyAsset(path, dest);
        }
    }

    /// <summary>Menu repair for the effect wiring: the committed VFXDirectorConfig
    /// references machine-local Cartoon FX prefabs — on a fresh clone EVERY effect
    /// slot dangles and a clean build ships with no combat VFX. Run this once on a
    /// machine that has the packs; commit the result.</summary>
    [MenuItem("Tools/COREHOLD/VFX/Localize VFX Config (vendored copies)", false, 62)]
    public static void LocalizeVfxConfig()
    {
        var log = new StringBuilder();
        log.AppendLine("VFX config localization:");
        int copied = Localize(new[] { "Assets/_COREHOLD/Data/VFXDirectorConfig.asset" }, log);
        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString() + (copied > 0
            ? "Commit Assets/_COREHOLD/Vendored and the updated config — clean clones then build with effects."
            : "If slots still dangle, read the lines above: a dangling-reference WARNING means the config points " +
              "at deleted copies — restore Assets/_COREHOLD/Vendored from git, or discard the config's local " +
              "changes so it references the packs again, then re-run. No warning and no packs installed means " +
              "run this on a machine that has them."));
    }

    private static string DestinationFor(string srcPath)
    {
        // Keep the pack-relative structure under the vendored root:
        // Assets/Vendor/JMO/CFXR/Foo.prefab -> Assets/_COREHOLD/Vendored/Vendor/JMO/CFXR/Foo.prefab
        string tail = srcPath.StartsWith("Assets/", System.StringComparison.Ordinal)
            ? srcPath.Substring("Assets/".Length)
            : srcPath;
        return $"{VendoredRoot}/{tail}";
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
