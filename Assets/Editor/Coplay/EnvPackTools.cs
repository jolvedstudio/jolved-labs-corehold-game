using System.Collections.Generic;
using System.Text;
using Corehold.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authoring aids for <see cref="EnvPack"/> (roadmap R25; consumed by R28's placer).
///
/// A pack is a content asset — Create → COREHOLD → Env Pack makes an empty one and
/// you drag prefabs in. What you should NOT do by hand is type the numbers, for a
/// reason specific to this system: <c>footprintRadius</c> and <c>height</c> are not
/// descriptive metadata, they are the ONLY inputs the clearance and sight-line tests
/// have. A radius typed 30% short does not look wrong in the inspector — it produces
/// a level that passes every gate with a prop sitting in the lane.
///
/// So both entry points here MEASURE rather than ask:
///
///   • <see cref="CreateRefineryPack"/> builds the pack the shipped map already
///     implies, from the nine prefab paths <c>RefineryDeltaBlockout.BuildStructures</c>
///     places, so R26 has a dressing pack to hit parity against.
///   • <see cref="MeasureSelected"/> fills in the numbers for a pack you assembled
///     yourself, and is the one you will use for every pack after the first.
///
/// Measurement is read-only: it walks the prefab's mesh bounds directly rather than
/// instantiating anything, so it cannot dirty a scene or leave debris behind.
///
/// ROLE is the one field measurement cannot settle, because it encodes intent — which
/// band of the level you want a prop to fill — and two props of identical size can
/// belong in different bands. So role is handled by admission rather than inference:
/// <see cref="EnvPack.PropRole.Unassigned"/> is the enum's zero value and the generate
/// gate rejects it, and <see cref="SuggestRole"/> offers a size-based starting point
/// for entries that have not been given one. A role you chose is never overwritten.
/// </summary>
public static class EnvPackTools
{
    // Data/<PluralNoun>/ is the established convention here — Enemies, Levels,
    // Towers, Waves, Blueprints. Packs are their own type, not LevelDefinitions,
    // so they get their own folder rather than sharing Data/Levels.
    private const string PackDir = "Assets/_COREHOLD/Data/EnvPacks";
    private const string RefineryPackPath = PackDir + "/EnvPack_RefineryDelta.asset";
    private const string CreepyRoot = "Assets/Vendor/Creepy_Cat/3D Scifi Kit Vol 4/Prefabs/";

    /// <summary>
    /// The shipped map's dressing, transcribed from RefineryDeltaBlockout. The scale
    /// column is the scale the blockout actually places at — metadata is measured at
    /// scale 1 and the placer multiplies, so scaleRange is what carries it.
    ///
    /// The ROLE column is authored, not derived. It comes from where the blockout
    /// actually puts each prop — a tank nested in a hairpin fold is mid-field, a
    /// pumping station alone at (-48, -6) is a landmark, the turbine on the east edge
    /// is skyline — which is far better evidence than the prefab's dimensions. The
    /// generator is being taught to reproduce a map a human already composed; the
    /// composition is the data.
    /// </summary>
    private static readonly (string path, EnvPack.PropRole role, float scale, bool inFold, string note)[] RefineryProps =
    {
        (CreepyRoot + "Props/Container & Crate/P_Tank_Cistern_01.prefab",
            EnvPack.PropRole.MidField, 0.80f, true,  "shipped in the x=-19/-9 fold at (-14, 4)"),
        (CreepyRoot + "Props/Machine/P_Storage_Liquid_Station_01.prefab",
            EnvPack.PropRole.MidField, 0.28f, true,  "shipped in the x=2/13 fold at (8, 3)"),
        (CreepyRoot + "Props/Machine/P_Pumping_Station_01.prefab",
            EnvPack.PropRole.Landmark, 0.30f, false, "back-left landmark at (-48, -6)"),
        (CreepyRoot + "Props/Container & Crate/P_Tank_Cistern_01_B.prefab",
            EnvPack.PropRole.Landmark, 0.90f, false, "east skyline at (30, 26)"),
        (CreepyRoot + "Props/Container & Crate/P_Container_01.prefab",
            EnvPack.PropRole.Clutter,  1.00f, false, "container pair at (-32, -18)"),
        (CreepyRoot + "Props/Container & Crate/P_Container_01_C.prefab",
            EnvPack.PropRole.Clutter,  1.00f, false, "container pair at (-28, -18)"),
        (CreepyRoot + "Props/Pipes & Cables/P_Pipe_Big_Line_01.prefab",
            EnvPack.PropRole.Clutter,  1.50f, false, "pipe run at (-4, 34)"),
        (CreepyRoot + "Props/Machine/P_Solar_Power_01.prefab",
            EnvPack.PropRole.MidField, 1.00f, false, "solar field at (-56, -26)"),
        (CreepyRoot + "Props/Machine/P_Wind_Turbine_01.prefab",
            EnvPack.PropRole.Silhouette, 1.00f, false, "tall east marker at (56, 22)"),
    };

    // The clearance envelope every placement is measured against (roadmap P2/P6):
    // laneHalfWidth 0.9 + maxBodyRadius 1.35 + padRadius 1.5.
    private const float ClearanceEnvelope = 3.75f;

    // ------------------------------------------------------------------- create

    [MenuItem("Tools/COREHOLD/Level/Create Refinery Env Pack", false, 3)]
    public static void CreateRefineryPack()
    {
        var log = new StringBuilder();
        log.AppendLine("=== Create Refinery Env Pack (R25) ===");

        if (!AssetDatabase.IsValidFolder(PackDir))
            AssetDatabase.CreateFolder("Assets/_COREHOLD/Data", "EnvPacks");

        var pack = AssetDatabase.LoadAssetAtPath<EnvPack>(RefineryPackPath);
        if (pack == null)
        {
            pack = ScriptableObject.CreateInstance<EnvPack>();
            AssetDatabase.CreateAsset(pack, RefineryPackPath);
        }

        var entries = new List<EnvPack.Entry>();
        var missing = new List<string>();

        foreach (var (path, role, scale, inFold, note) in RefineryProps)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                missing.Add(path);
                continue;
            }

            var entry = new EnvPack.Entry
            {
                prefab = prefab,
                role = role,
                scaleRange = new Vector2(scale, scale),   // shipped scale, exactly — vary it later
                allowInFold = inFold
            };

            if (TryMeasure(prefab, out Measurement m))
            {
                entry.footprintRadius = m.radius;
                entry.height = m.height;
                log.AppendLine($"  {prefab.name,-30} {role,-10} r={m.radius,6:0.00}  h={m.height,6:0.00}  " +
                               $"(at ×{scale:0.00}: r={m.radius * scale,5:0.00} h={m.height * scale,5:0.00})  — {note}");
                AppendMeasurementWarnings(prefab.name, m, scale, log);
            }
            else
            {
                log.AppendLine($"  {prefab.name,-30} {role,-10} NO RENDERABLE MESH — footprint/height left at 0, " +
                               "which the generate gate will reject. Fill them by hand or drop the entry.");
            }

            entries.Add(entry);
        }

        pack.entries = entries.ToArray();
        EditorUtility.SetDirty(pack);

        string wired = WireIntoRefineryBlueprint(pack);
        AssetDatabase.SaveAssets();
        Selection.activeObject = pack;

        log.AppendLine();
        log.AppendLine($"{RefineryPackPath} — {entries.Count} entr(ies), {pack.CountInvalid()} invalid.");
        log.AppendLine(wired);

        if (missing.Count > 0)
        {
            log.AppendLine();
            log.AppendLine($"{missing.Count} prefab(s) not found. Assets/Vendor/ is git-ignored, so this is " +
                           "expected on a checkout without the Creepy Cat kit installed:");
            foreach (string p in missing)
                log.AppendLine($"  • {p}");
        }

        AppendCommitWarning(pack, log);
        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------------ measure

    [MenuItem("Tools/COREHOLD/Level/Measure Env Pack Metadata", false, 4)]
    public static void MeasureSelected()
    {
        var pack = Selection.activeObject as EnvPack;
        if (pack == null)
        {
            Debug.LogWarning("[R25] Select an EnvPack asset in the Project window first. " +
                             "Create one with Create → COREHOLD → Env Pack, drag prefabs into its " +
                             "entries list, then run this to fill in footprint and height.");
            return;
        }

        var log = new StringBuilder();
        log.AppendLine($"=== Measure Env Pack — '{pack.name}' ===");

        if (pack.entries == null || pack.entries.Length == 0)
        {
            Debug.LogWarning($"[R25] '{pack.name}' has no entries yet. Drag prefabs into the entries " +
                             "list first — this tool fills in their numbers, it does not choose the props.");
            return;
        }

        int filled = 0, kept = 0, unmeasurable = 0, suggestedRoles = 0;

        for (int i = 0; i < pack.entries.Length; i++)
        {
            EnvPack.Entry e = pack.entries[i];
            if (e.prefab == null)
            {
                log.AppendLine($"  [{i}] <no prefab> — skipped.");
                continue;
            }

            if (!TryMeasure(e.prefab, out Measurement m))
            {
                log.AppendLine($"  [{i}] {e.prefab.name} — no renderable mesh, cannot measure.");
                unmeasurable++;
                continue;
            }

            float scale = e.scaleRange.y > 0f ? e.scaleRange.y : 1f;

            // Authored numbers win. A human who deliberately widened a keep-out is
            // making a judgement this tool cannot see, so it reports the difference
            // instead of quietly reverting it.
            bool hadRadius = e.footprintRadius > 0f;
            bool hadHeight = e.height > 0f;

            if (!hadRadius) e.footprintRadius = m.radius;
            if (!hadHeight) e.height = m.height;
            pack.entries[i] = e;

            if (!hadRadius || !hadHeight)
            {
                filled++;
                log.AppendLine($"  [{i}] {e.prefab.name,-28} filled  r={e.footprintRadius,6:0.00}  h={e.height,6:0.00}");
            }
            else
            {
                kept++;
                string rDelta = Mathf.Abs(e.footprintRadius - m.radius) > 0.05f
                    ? $"  (measures {m.radius:0.00})" : "";
                string hDelta = Mathf.Abs(e.height - m.height) > 0.05f
                    ? $"  (measures {m.height:0.00})" : "";
                log.AppendLine($"  [{i}] {e.prefab.name,-28} kept    r={e.footprintRadius,6:0.00}{rDelta}  " +
                               $"h={e.height,6:0.00}{hDelta}");
            }

            // Role is intent, not geometry — but size narrows it enough to be a
            // useful starting point, and a suggestion beats leaving the field on
            // its Unassigned default until the generate gate rejects it.
            EnvPack.PropRole suggested = SuggestRole(m, scale);
            if (e.role == EnvPack.PropRole.Unassigned)
            {
                e.role = suggested;
                pack.entries[i] = e;
                suggestedRoles++;
                log.AppendLine($"      → role was Unassigned, suggested {suggested} from " +
                               $"{m.footprint.x * scale:0.0} × {m.footprint.y * scale:0.0} × {m.height * scale:0.0} m. " +
                               "Change it if you meant it somewhere else.");
            }
            else if (e.role != suggested)
            {
                log.AppendLine($"      · role {e.role} kept (size alone suggests {suggested}) — " +
                               "left alone, since role is where you want it, not how big it is.");
            }

            AppendMeasurementWarnings(e.prefab.name, m, scale, log);
        }

        EditorUtility.SetDirty(pack);
        AssetDatabase.SaveAssets();

        log.AppendLine();
        log.AppendLine($"{filled} filled, {kept} already authored (left alone), {unmeasurable} unmeasurable, " +
                       $"{suggestedRoles} role(s) suggested. {pack.CountInvalid()} entr(ies) still invalid.");
        log.AppendLine($"Roles — Landmark {pack.CountInRole(EnvPack.PropRole.Landmark)}, " +
                       $"MidField {pack.CountInRole(EnvPack.PropRole.MidField)}, " +
                       $"Clutter {pack.CountInRole(EnvPack.PropRole.Clutter)}, " +
                       $"Silhouette {pack.CountInRole(EnvPack.PropRole.Silhouette)}, " +
                       $"Unassigned {pack.CountInRole(EnvPack.PropRole.Unassigned)}.");
        if (suggestedRoles > 0)
            log.AppendLine("Suggested roles are a starting point measured from size — read them before generating. " +
                           "A prop the placer puts in the wrong band is not a gate failure, it is just a worse map.");

        AppendCommitWarning(pack, log);
        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private struct Measurement
    {
        /// <summary>Circumscribing XZ radius measured about the PREFAB PIVOT, at scale 1.</summary>
        public float radius;
        /// <summary>Height above the pivot, at scale 1.</summary>
        public float height;
        /// <summary>Raw XZ footprint, for the aspect-ratio warning.</summary>
        public Vector2 footprint;
        /// <summary>How far the pivot sits below the lowest geometry (a prop that floats).</summary>
        public float baseOffset;
    }

    /// <summary>
    /// Measure a prefab asset from its mesh bounds without instantiating it.
    ///
    /// The radius is taken about the PIVOT, not the bounds centre, because the placer
    /// positions a prop by its pivot — so the keep-out circle is centred there. A prop
    /// whose pivot sits off to one side genuinely needs a larger radius, and measuring
    /// about the centre would under-report it by exactly the pivot offset.
    /// </summary>
    private static bool TryMeasure(GameObject prefab, out Measurement m)
    {
        m = default;

        Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
        bool any = false;
        Bounds b = default;

        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null || mr == null || !mr.enabled)
                continue;
            Encapsulate(mf.sharedMesh.bounds, toRoot * mf.transform.localToWorldMatrix, ref b, ref any);
        }

        foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null || !smr.enabled)
                continue;
            Encapsulate(smr.sharedMesh.bounds, toRoot * smr.transform.localToWorldMatrix, ref b, ref any);
        }

        if (!any)
            return false;

        float maxAbsX = Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
        float maxAbsZ = Mathf.Max(Mathf.Abs(b.min.z), Mathf.Abs(b.max.z));

        m.radius = Mathf.Sqrt(maxAbsX * maxAbsX + maxAbsZ * maxAbsZ);
        m.height = Mathf.Max(0f, b.max.y);
        m.footprint = new Vector2(b.size.x, b.size.z);
        m.baseOffset = b.min.y;
        return true;
    }

    /// <summary>Grow <paramref name="b"/> by a mesh's eight corners transformed into root space.</summary>
    private static void Encapsulate(Bounds local, Matrix4x4 xf, ref Bounds b, ref bool any)
    {
        Vector3 c = local.center, e = local.extents;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                c.x + ((i & 1) == 0 ? -e.x : e.x),
                c.y + ((i & 2) == 0 ? -e.y : e.y),
                c.z + ((i & 4) == 0 ? -e.z : e.z));
            Vector3 p = xf.MultiplyPoint3x4(corner);
            if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
            else b.Encapsulate(p);
        }
    }

    /// <summary>
    /// Flag the cases where a single radius is a poor model of the prop, so a human
    /// overrides it deliberately rather than discovering it as a prop in the lane.
    /// </summary>
    private static void AppendMeasurementWarnings(string name, Measurement m, float scale, StringBuilder log)
    {
        float longSide = Mathf.Max(m.footprint.x, m.footprint.y);
        float shortSide = Mathf.Max(0.01f, Mathf.Min(m.footprint.x, m.footprint.y));
        float aspect = longSide / shortSide;

        if (aspect >= 4f)
            log.AppendLine($"      ! {name} measures {m.footprint.x:0.0} × {m.footprint.y:0.0} m (aspect {aspect:0}:1). " +
                           $"A circle is a poor fit for it — r={m.radius:0.00} is conservative and may make it " +
                           "unplaceable. Hand-tune, or split the prop.");

        if (m.baseOffset > 0.05f)
            log.AppendLine($"      ! {name} geometry starts {m.baseOffset:0.00} m ABOVE its pivot — it will float " +
                           "when placed on the ground plane.");

        float effective = m.radius * scale;
        if (effective > ClearanceEnvelope)
            log.AppendLine($"      · {name} keep-out at ×{scale:0.00} is {effective:0.00} m, wider than the " +
                           $"{ClearanceEnvelope:0.00} m clearance envelope — it cannot sit beside a route or pad, " +
                           "only in open field.");
    }

    /// <summary>
    /// Propose a role from measured size. This is a STARTING POINT, not a derivation:
    /// role encodes where you want a prop placed, and two objects of identical size can
    /// belong in different bands. It exists only so a dragged-in prefab does not sit on
    /// Unassigned until the generate gate rejects it.
    ///
    /// The thresholds are read off the map's own dimensions rather than invented:
    ///   • ≥ 12 m tall — the far band sits beyond the 130×75 playfield, and with the
    ///     camera pitched 38° down nothing shorter clears the mid-field to read as skyline.
    ///   • ≥ 6 m in either axis — bigger than the 3.75 m clearance envelope, so it cannot
    ///     be tucked beside a route and has to be something you navigate by.
    ///   • ≤ 2 m and ≤ 2 m — below enemy eye height, so it cannot break a turret's line
    ///     to a covered span, which is what makes clutter safe to scatter freely.
    /// </summary>
    private static EnvPack.PropRole SuggestRole(Measurement m, float scale)
    {
        float h = m.height * scale;
        float r = m.radius * scale;

        if (h >= 12f)
            return EnvPack.PropRole.Silhouette;
        if (r >= 6f || h >= 6f)
            return EnvPack.PropRole.Landmark;
        if (h <= 2f && r <= 2f)
            return EnvPack.PropRole.Clutter;
        return EnvPack.PropRole.MidField;
    }

    /// <summary>Assign the pack to the shipped-map blueprint, which R26 rebuilds against.</summary>
    private static string WireIntoRefineryBlueprint(EnvPack pack)
    {
        const string bpPath = "Assets/_COREHOLD/Data/Blueprints/Blueprint_RefineryDelta.asset";
        var bp = AssetDatabase.LoadAssetAtPath<LevelBlueprint>(bpPath);
        if (bp == null)
            return "Blueprint_RefineryDelta not found — run Create Refinery Delta Blueprint, then re-run this " +
                   "to wire the pack into it.";

        if (bp.envPack == pack)
            return $"{bpPath} already points at this pack.";

        bp.envPack = pack;
        EditorUtility.SetDirty(bp);
        return $"Wired into {bpPath} (envPack).";
    }

    /// <summary>
    /// A pack referencing Assets/Vendor/ carries GUIDs that resolve to nothing for
    /// anyone without those packages. That failure is at least loud — CountInvalid
    /// reports it and the generate gate refuses — but it is worth saying out loud
    /// before the asset gets committed.
    /// </summary>
    private static void AppendCommitWarning(EnvPack pack, StringBuilder log)
    {
        int vendor = 0;
        if (pack.entries != null)
        {
            foreach (EnvPack.Entry e in pack.entries)
            {
                if (e.prefab == null)
                    continue;
                if (AssetDatabase.GetAssetPath(e.prefab).StartsWith("Assets/Vendor/"))
                    vendor++;
            }
        }

        if (vendor == 0)
            return;

        log.AppendLine();
        log.AppendLine($"NOTE: {vendor} entr(ies) reference Assets/Vendor/, which is git-ignored. Committing this " +
                       "pack ships GUIDs that resolve to null for anyone without those packages — the generate " +
                       "gate will reject it rather than dress a level wrongly, but they will need the kit.");
    }
}
