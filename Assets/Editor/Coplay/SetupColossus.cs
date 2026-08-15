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

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
            prefab = BuildPrefab(log);
        else
            log.AppendLine($"[ok] prefab already exists: {PrefabPath}");

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
            // Torso, leaning forward like something that pushes through fire.
            Part(root, PrimitiveType.Cube, "Torso", new Vector3(0f, 2.9f, 0f),
                new Vector3(2.3f, 1.5f, 2.0f), Quaternion.Euler(6f, 0f, 0f), hull);
            Part(root, PrimitiveType.Cube, "Carapace", new Vector3(0f, 3.7f, -0.2f),
                new Vector3(2.6f, 0.45f, 2.3f), Quaternion.Euler(4f, 0f, 0f), hull);

            // Head block with an emissive visor strip (enrage glow part).
            Part(root, PrimitiveType.Cube, "Head", new Vector3(0f, 3.55f, 1.15f),
                new Vector3(1.0f, 0.62f, 0.8f), Quaternion.identity, hull);
            Part(root, PrimitiveType.Cube, "Visor", new Vector3(0f, 3.55f, 1.57f),
                new Vector3(0.84f, 0.18f, 0.06f), Quaternion.identity, glow);

            // Shoulder pylons + emissive core vents flanking the torso.
            Part(root, PrimitiveType.Cube, "Pylon_L", new Vector3(-1.35f, 3.45f, -0.1f),
                new Vector3(0.55f, 0.9f, 1.1f), Quaternion.Euler(0f, 0f, 8f), hull);
            Part(root, PrimitiveType.Cube, "Pylon_R", new Vector3(1.35f, 3.45f, -0.1f),
                new Vector3(0.55f, 0.9f, 1.1f), Quaternion.Euler(0f, 0f, -8f), hull);
            Part(root, PrimitiveType.Cube, "Vent_L", new Vector3(-1.18f, 2.75f, -0.55f),
                new Vector3(0.12f, 0.8f, 0.9f), Quaternion.identity, glow);
            Part(root, PrimitiveType.Cube, "Vent_R", new Vector3(1.18f, 2.75f, -0.55f),
                new Vector3(0.12f, 0.8f, 0.9f), Quaternion.identity, glow);

            // Four splayed legs (upper + shin each), spider-walker stance.
            Leg(root, hull, "FL", new Vector3(-0.95f, 0f, 0.75f), -28f, 14f);
            Leg(root, hull, "FR", new Vector3(0.95f, 0f, 0.75f), 28f, 14f);
            Leg(root, hull, "BL", new Vector3(-0.95f, 0f, -0.75f), -28f, -14f);
            Leg(root, hull, "BR", new Vector3(0.95f, 0f, -0.75f), 28f, -14f);

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
            moverSo.ApplyModifiedPropertiesWithoutUndo();

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.AppendLine($"[ok] built prefab: {PrefabPath} (bodyRadius {BodyRadius}, no Animator by design)");
            return saved;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static void Leg(GameObject root, Material hull, string tag, Vector3 hip,
        float splayZ, float splayX)
    {
        // Upper leg angles out from the hip; shin drops to the ground.
        Part(root, PrimitiveType.Cube, $"Leg_{tag}_Upper",
            hip + new Vector3(splayZ > 0 ? 0.55f : -0.55f, 2.1f, splayX > 0 ? 0.25f : -0.25f),
            new Vector3(0.42f, 1.7f, 0.42f), Quaternion.Euler(splayX, 0f, splayZ), hull);
        Part(root, PrimitiveType.Cube, $"Leg_{tag}_Shin",
            hip + new Vector3(splayZ > 0 ? 1.05f : -1.05f, 0.75f, splayX > 0 ? 0.5f : -0.5f),
            new Vector3(0.34f, 1.5f, 0.34f), Quaternion.Euler(-splayX * 0.4f, 0f, -splayZ * 0.35f), hull);
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
