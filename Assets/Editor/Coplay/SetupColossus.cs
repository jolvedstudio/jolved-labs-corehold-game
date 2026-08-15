using System.Text;
using Corehold.Data;
using Corehold.Enemies;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Colossus boss (GDD §4.5, §6.2) as a PROCEDURAL prefab and wires
/// it into the existing `Enemy_Colossus.asset` — which has shipped with a null
/// prefab, so Wave 10's boss group warn-skipped and the finale never walked.
///
/// Procedural on purpose: every kit enemy leans on git-ignored Assets/Vendor
/// content (skin materials, avatars, clips), so this is the first enemy that
/// is fully self-contained in the repo. No Animator: EnemyAnimatorBridge is
/// null-safe, the walk reads through the heavy footfall shake (IsColossus)
/// and sheer scale, and death hides renderers + plays the pooled explosion.
///
/// Enrage contract (Enemy.SetEmissive): child renderers are auto-collected
/// and stamped with _EmissionColor orange→white via MaterialPropertyBlock.
/// MPBs cannot ENABLE the _EMISSION keyword, so the vents/visor material has
/// emission ON while the hull material has none — the glow parts shift on
/// enrage, the armour stays inert.
///
/// After running: Tools → COREHOLD → Art → Render Icons re-bakes the icon
/// (it currently shows the Strider fallback). Idempotent; safe to re-run.
/// </summary>
public static class SetupColossus
{
    private const string PrefabPath = "Assets/_COREHOLD/Prefabs/Enemies/Colossus.prefab";
    private const string DefPath = "Assets/_COREHOLD/Data/Enemies/Enemy_Colossus.asset";
    private const string MatDir = "Assets/_COREHOLD/Art/Materials";
    private const string BlobShadowMatPath = MatDir + "/Mat_BlobShadow.mat";

    // Stays within the shipped clearance envelope: WaveManager.largestBodyRadius
    // is 1.2 in Game.unity, and the derived-capacity math is calibrated to it.
    private const float BodyRadius = 1.2f;

    [MenuItem("Tools/COREHOLD/Scene Setup/Colossus Boss", false, 50)]
    public static void Setup()
    {
        var log = new StringBuilder();

        // Always rebuild: the prefab is procedural, so the tool IS its source of
        // truth — overwriting the same path keeps the GUID, so the definition's
        // reference survives. (An early-return here once left users stuck on a
        // gait-less first revision.)
        GameObject prefab = BuildPrefab(log);

        WireDefinition(prefab, log);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] SetupColossus:\n" + log +
                  "\nRe-run Tools → COREHOLD → Art → Render Icons to replace the fallback icon.");
    }

    // ------------------------------------------------------------ prefab

    private static GameObject BuildPrefab(StringBuilder log)
    {
        Material hull = EnsureHull();
        Material glow = EnsureGlow();

        var root = new GameObject("Colossus");
        try
        {
            // --- Chassis: a four-legged siege walker, ~4.6 m tall. ---
            // The upper works live in a "Body" group so the gait can heave and
            // sway them as one mass while the legs stay planted on their hips.
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);

            // Torso, leaning forward like something that pushes through fire.
            Part(body, PrimitiveType.Cube, "Torso", new Vector3(0f, 2.9f, 0f),
                new Vector3(2.3f, 1.5f, 2.0f), Quaternion.Euler(6f, 0f, 0f), hull);
            Part(body, PrimitiveType.Cube, "Carapace", new Vector3(0f, 3.7f, -0.2f),
                new Vector3(2.6f, 0.45f, 2.3f), Quaternion.Euler(4f, 0f, 0f), hull);

            // Head block with an emissive visor strip (enrage glow part).
            Part(body, PrimitiveType.Cube, "Head", new Vector3(0f, 3.55f, 1.15f),
                new Vector3(1.0f, 0.62f, 0.8f), Quaternion.identity, hull);
            Part(body, PrimitiveType.Cube, "Visor", new Vector3(0f, 3.55f, 1.57f),
                new Vector3(0.84f, 0.18f, 0.06f), Quaternion.identity, glow);

            // Shoulder pylons + emissive core vents flanking the torso.
            Part(body, PrimitiveType.Cube, "Pylon_L", new Vector3(-1.35f, 3.45f, -0.1f),
                new Vector3(0.55f, 0.9f, 1.1f), Quaternion.Euler(0f, 0f, 8f), hull);
            Part(body, PrimitiveType.Cube, "Pylon_R", new Vector3(1.35f, 3.45f, -0.1f),
                new Vector3(0.55f, 0.9f, 1.1f), Quaternion.Euler(0f, 0f, -8f), hull);
            Part(body, PrimitiveType.Cube, "Vent_L", new Vector3(-1.18f, 2.75f, -0.55f),
                new Vector3(0.12f, 0.8f, 0.9f), Quaternion.identity, glow);
            Part(body, PrimitiveType.Cube, "Vent_R", new Vector3(1.18f, 2.75f, -0.55f),
                new Vector3(0.12f, 0.8f, 0.9f), Quaternion.identity, glow);

            // Four splayed legs, each hinged on a pivot at its hip so the gait
            // can swing the whole limb. Diagonal pairs share a phase (a trot).
            Transform legFL = Leg(root, hull, "FL", new Vector3(-0.95f, 0f, 0.75f), -28f, 14f);
            Transform legFR = Leg(root, hull, "FR", new Vector3(0.95f, 0f, 0.75f), 28f, 14f);
            Transform legBL = Leg(root, hull, "BL", new Vector3(-0.95f, 0f, -0.75f), -28f, -14f);
            Transform legBR = Leg(root, hull, "BR", new Vector3(0.95f, 0f, -0.75f), 28f, -14f);

            // --- Blob shadow, the shared enemy convention. ---
            var blob = GameObject.CreatePrimitive(PrimitiveType.Quad);
            blob.name = "BlobShadow";
            Object.DestroyImmediate(blob.GetComponent<Collider>());
            blob.transform.SetParent(root.transform, false);
            blob.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            blob.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            blob.transform.localScale = Vector3.one * (BodyRadius * 5.4f);
            var blobMat = AssetDatabase.LoadAssetAtPath<Material>(BlobShadowMatPath);
            if (blobMat != null)
                blob.GetComponent<MeshRenderer>().sharedMaterial = blobMat;
            blob.AddComponent<Corehold.Systems.BlobShadow>();

            // --- Aim point at centre mass — a ground-level HitPoint on a 4.6 m
            // boss would make every turret shoot at its feet. ---
            var hit = new GameObject("HitPoint");
            hit.transform.SetParent(root.transform, false);
            hit.transform.localPosition = new Vector3(0f, 2.9f, 0f);

            // --- Runtime components (same contract as every enemy root). ---
            var enemy = root.AddComponent<Enemy>();
            var mover = root.AddComponent<EnemyMover>();
            root.AddComponent<EnemyAnimatorBridge>();   // null-safe without an Animator

            var so = new SerializedObject(enemy);
            so.FindProperty("hitPoint").objectReferenceValue = hit.transform;
            so.FindProperty("deathAnimDuration").floatValue = 0.6f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var moverSo = new SerializedObject(mover);
            moverSo.FindProperty("bodyRadius").floatValue = BodyRadius;
            // A 4.6 m walker SLEWS, it does not snap: the default 360°/s made the
            // slow giant ratchet onto each baked-tangent step ("stuttery" turns).
            // 90°/s turns each step into a continuous swing and still clears the
            // tightest hairpin with the enrage boost on.
            moverSo.FindProperty("turnRate").floatValue = 90f;
            moverSo.ApplyModifiedPropertiesWithoutUndo();

            // Distance-synced procedural gait (no Animator, no assets): heave and
            // sway on the Body group, diagonal leg pairs trotting. The 6 m stride
            // matches the footfall camera shake, so the thud lands on the dip.
            var gait = root.AddComponent<ProceduralGait>();
            var gaitSo = new SerializedObject(gait);
            gaitSo.FindProperty("body").objectReferenceValue = body.transform;
            var legsProp = gaitSo.FindProperty("legs");
            legsProp.arraySize = 4;
            legsProp.GetArrayElementAtIndex(0).objectReferenceValue = legFL;
            legsProp.GetArrayElementAtIndex(1).objectReferenceValue = legFR;
            legsProp.GetArrayElementAtIndex(2).objectReferenceValue = legBL;
            legsProp.GetArrayElementAtIndex(3).objectReferenceValue = legBR;
            var phasesProp = gaitSo.FindProperty("legPhases");
            phasesProp.arraySize = 4;
            phasesProp.GetArrayElementAtIndex(0).floatValue = 0f;
            phasesProp.GetArrayElementAtIndex(1).floatValue = Mathf.PI;
            phasesProp.GetArrayElementAtIndex(2).floatValue = Mathf.PI;
            phasesProp.GetArrayElementAtIndex(3).floatValue = 0f;
            gaitSo.ApplyModifiedPropertiesWithoutUndo();

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.AppendLine($"[ok] built prefab: {PrefabPath} (bodyRadius {BodyRadius}, " +
                           "turnRate 90, procedural gait wired, no Animator by design)");
            return saved;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static Transform Leg(GameObject root, Material hull, string tag, Vector3 hip,
        float splayZ, float splayX)
    {
        // Hinge pivot at the hip: the gait swings this root around X and the
        // whole limb follows. The original splayed stance is preserved as the
        // children's LOCAL offsets below the pivot.
        var pivot = new GameObject($"Leg_{tag}");
        pivot.transform.SetParent(root.transform, false);
        pivot.transform.localPosition = new Vector3(hip.x, 2.95f, hip.z);

        float sx = splayZ > 0 ? 1f : -1f;
        float sz = splayX > 0 ? 1f : -1f;
        Part(pivot, PrimitiveType.Cube, $"Leg_{tag}_Upper",
            new Vector3(sx * 0.55f, -0.85f, sz * 0.25f),
            new Vector3(0.42f, 1.7f, 0.42f), Quaternion.Euler(splayX, 0f, splayZ), hull);
        Part(pivot, PrimitiveType.Cube, $"Leg_{tag}_Shin",
            new Vector3(sx * 1.05f, -2.2f, sz * 0.5f),
            new Vector3(0.34f, 1.5f, 0.34f), Quaternion.Euler(-splayX * 0.4f, 0f, -splayZ * 0.35f), hull);
        return pivot.transform;
    }

    private static GameObject Part(GameObject root, PrimitiveType type, string name,
        Vector3 pos, Vector3 scale, Quaternion rot, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());   // enemies carry none
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.transform.localRotation = rot;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    private static Material EnsureHull()
    {
        const string path = MatDir + "/M_ColossusHull.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        mat = new Material(shader) { name = "M_ColossusHull" };
        var c = new Color(0.16f, 0.17f, 0.20f, 1f);   // scorched gunmetal
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        mat.color = c;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static Material EnsureGlow()
    {
        const string path = MatDir + "/M_ColossusGlow.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        mat = new Material(shader) { name = "M_ColossusGlow" };
        var c = new Color(0.25f, 0.10f, 0.03f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        mat.color = c;
        // Emission ENABLED here so Enemy.SetEmissive's MaterialPropertyBlock has
        // a live _EmissionColor to drive (orange at spawn, white on enrage).
        mat.EnableKeyword("_EMISSION");
        if (mat.HasProperty("_EmissionColor"))
            mat.SetColor("_EmissionColor", new Color(1f, 0.35f, 0f) * 2f);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ------------------------------------------------------------ definition

    private static void WireDefinition(GameObject prefab, StringBuilder log)
    {
        var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(DefPath);
        if (def == null)
        {
            log.AppendLine($"[warn] {DefPath} not found — run the wave-data generator first.");
            return;
        }

        bool changed = false;
        if (def.prefab != prefab && prefab != null)
        {
            def.prefab = prefab;
            changed = true;
            log.AppendLine("[ok] Enemy_Colossus.prefab linked — Wave 10's boss group now spawns.");
        }
        // The asset predates the R18 field, so it deserialized to 0.
        if (!Mathf.Approximately(def.stunResistance, 0.25f))
        {
            def.stunResistance = 0.25f;
            changed = true;
            log.AppendLine("[ok] stunResistance set to 0.25 (R18 — the boss shrugs off a quarter of stuns).");
        }

        if (changed)
            EditorUtility.SetDirty(def);
        else
            log.AppendLine("[ok] Enemy_Colossus.asset already wired.");
    }
}
