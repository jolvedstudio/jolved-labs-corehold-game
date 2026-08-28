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
            return 0;
        }

        // ---- copy (or adopt an existing copy), building the guid map --------
        var guidMap = new Dictionary<string, string>();   // old -> new
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
            string newGuid = AssetDatabase.AssetPathToGUID(dest);
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
                if (Path.GetExtension(dest).ToLowerInvariant() == ".shader")
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
        var toRewrite = new HashSet<string>(
            AssetDatabase.GetDependencies(roots, true)
                .Where(p => !IsVendorPath(p) && p.StartsWith("Assets/", System.StringComparison.Ordinal)
                            && TextAssetExtensions.Contains(Path.GetExtension(p))));
        foreach (string src in external)
        {
            string dest = DestinationFor(src);
            if (TextAssetExtensions.Contains(Path.GetExtension(dest)) && File.Exists(dest))
                toRewrite.Add(dest);
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

        // Scene objects carrying pack components cannot be healed by copying —
        // code is never vendored — so say it, per offending script, instead of
        // leaving preflight red with no visible cause.
        var scriptDeps = AssetDatabase.GetDependencies(roots, true)
            .Where(p => IsVendorPath(p) &&
                        NeverCopyExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .ToList();
        if (scriptDeps.Count > 0)
            log.AppendLine($"  localize: WARNING — {scriptDeps.Count} pack script(s) still referenced by the " +
                           $"roots themselves (e.g. {Path.GetFileName(scriptDeps[0])}): some scene object " +
                           "carries a pack component. Remove that component by hand — script code is never vendored.");
        return copied;
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

        int stripped = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { VendoredRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // .prefab only: FindAssets(t:Prefab) also returns model assets
            // (.fbx), which carry no MonoBehaviours and cannot be re-saved.
            if (Path.GetExtension(path).ToLowerInvariant() == ".prefab")
                stripped += StripVendorScripts(path, log);
        }

        if (deleted > 0 || stripped > 0)
            log.AppendLine($"  localize: healed {VendoredRoot} — {deleted} stray script file(s) deleted, " +
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
            : "If slots still dangle, this machine is missing the source packs — run where they exist."));
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
