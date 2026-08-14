using System.Text;
using Corehold.Data;
using Corehold.Towers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authors the Floodlight sixth buildable (R24): a procedural chassis prefab
/// (no Vendor meshes — primitives + generated materials, all committed), a
/// single-tier TowerDefinition (70 salvage, 12 m light radius carried in
/// auraRadius), and the definition→prefab wiring. Idempotent; safe to re-run.
///
/// The light radius rides the tier's auraRadius deliberately: with zero aura
/// bonuses the SupportAura pass treats the floodlight as a relay granting
/// nothing (a no-op under max-per-axis), while IsSupportRelay classifies it as
/// a support tower everywhere it matters — it never acquires targets, never
/// receives buffs, and the TowerPanel shows the support layout. The runtime
/// behaviour lives in the Floodlight component (lit-area registry, R20/R24).
///
/// After running this, re-run Tools → COREHOLD → Scene Setup → Build Real UI so
/// the build menu picks up the sixth entry, and the icon tool to give it an icon.
/// </summary>
public static class SetupFloodlight
{
    private const string DefPath = "Assets/_COREHOLD/Data/Towers/Tower_Floodlight.asset";
    private const string PrefabPath = "Assets/_COREHOLD/Prefabs/Towers/Tower_Floodlight.prefab";
    private const string MatDir = "Assets/_COREHOLD/Art/Materials";

    // [TUNE] R24 shipped values.
    private const int Cost = 70;
    private const float LightRadius = 12f;

    [MenuItem("Tools/COREHOLD/Scene Setup/Floodlight Tower", false, 48)]
    public static void Setup()
    {
        var log = new StringBuilder();

        GameObject prefab = BuildPrefab(log);
        TowerDefinition def = BuildDefinition(prefab, log);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] SetupFloodlight:\n" + log +
                  "\nNow re-run Scene Setup → Build Real UI for the sixth build-menu entry.");
        if (def == null)
            Debug.LogError("[COREHOLD] SetupFloodlight: definition was not created — see log above.");
    }

    // ------------------------------------------------------------ prefab

    private static GameObject BuildPrefab(StringBuilder log)
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (existing != null)
        {
            log.AppendLine($"[ok] prefab already exists: {PrefabPath}");
            return existing;
        }

        var root = new GameObject("Tower_Floodlight");
        try
        {
            // Component set mirrors the other tower prefabs. Order satisfies the
            // RequireComponent chain (Tower → TowerWeapon → Targeting + Aim).
            root.AddComponent<TowerTargeting>();
            root.AddComponent<TurretAim>();
            root.AddComponent<TowerWeapon>();
            root.AddComponent<Tower>();
            root.AddComponent<Floodlight>();

            // --- Chassis: plinth, mast, head, lens (primitives, colliders off) ---
            Material mast = EnsureMaterial("M_FloodlightMast", new Color(0.28f, 0.30f, 0.34f, 1f), 0f);
            Material lens = EnsureMaterial("M_FloodlightLens", new Color(1f, 0.92f, 0.72f, 1f), 2.4f);
            Material glow = EnsureGlowMaterial("M_FloodlightGlow", new Color(1f, 0.88f, 0.62f, 0.09f));

            AddPart(root, PrimitiveType.Cylinder, "Base", new Vector3(0f, 0.15f, 0f),
                new Vector3(1.1f, 0.15f, 1.1f), Quaternion.identity, mast);
            AddPart(root, PrimitiveType.Cylinder, "Mast", new Vector3(0f, 2.05f, 0f),
                new Vector3(0.22f, 1.9f, 0.22f), Quaternion.identity, mast);
            AddPart(root, PrimitiveType.Cube, "Head", new Vector3(0f, 4.15f, 0f),
                new Vector3(1.3f, 0.45f, 0.95f), Quaternion.Euler(18f, 0f, 0f), mast);
            AddPart(root, PrimitiveType.Cube, "Lens", new Vector3(0f, 4.05f, 0.42f),
                new Vector3(1.1f, 0.3f, 0.12f), Quaternion.Euler(18f, 0f, 0f), lens);

            // --- The lit circle on the ground: gameplay-critical readability, so
            // it is geometry, not a Light cookie. 12 m radius = 24 m diameter. ---
            var disc = AddPart(root, PrimitiveType.Cylinder, "LightDisc",
                new Vector3(0f, 0.05f, 0f), new Vector3(LightRadius * 2f, 0.01f, LightRadius * 2f),
                Quaternion.identity, glow);
            var discRenderer = disc.GetComponent<MeshRenderer>();
            discRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            discRenderer.receiveShadows = false;

            // --- Real (non-shadowing) point light so the R23 night variant gets a
            // working lamp for free. Harmless by day; within the ≤10-lights budget
            // only if the player builds few — the visual truth is the disc. ---
            var lampGo = new GameObject("Lamp");
            lampGo.transform.SetParent(root.transform, false);
            lampGo.transform.localPosition = new Vector3(0f, 4.3f, 0f);
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.range = LightRadius + 3f;
            lamp.intensity = 2.2f;
            lamp.color = new Color(1f, 0.93f, 0.78f, 1f);
            lamp.shadows = LightShadows.None;

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.AppendLine($"[ok] built prefab: {PrefabPath}");
            return saved;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static GameObject AddPart(GameObject root, PrimitiveType type, string name,
        Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat)
    {
        var part = GameObject.CreatePrimitive(type);
        part.name = name;
        part.transform.SetParent(root.transform, false);
        part.transform.localPosition = localPos;
        part.transform.localScale = localScale;
        part.transform.localRotation = localRot;

        var col = part.GetComponent<Collider>();
        if (col != null)
            Object.DestroyImmediate(col);

        part.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return part;
    }

    private static Material EnsureMaterial(string name, Color color, float emission)
    {
        string path = $"{MatDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        mat.color = color;
        if (emission > 0f)
        {
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", color * emission);
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static Material EnsureGlowMaterial(string name, Color color)
    {
        string path = $"{MatDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        mat = new Material(shader) { name = name };

        // Full URP transparent state — _Surface alone leaves the opaque queue
        // (the WeatherApplier lesson).
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        mat.color = color;
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
        if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ------------------------------------------------------------ definition

    private static TowerDefinition BuildDefinition(GameObject prefab, StringBuilder log)
    {
        var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(DefPath);
        bool created = false;
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<TowerDefinition>();
            AssetDatabase.CreateAsset(def, DefPath);
            created = true;
        }

        def.id = "floodlight";
        def.displayName = "Floodlight";
        def.description = "Projects a 12 m circle of light. Lit enemies are acquired at full " +
                          "range under a Blackout. No damage.";
        def.damageType = DamageType.Kinetic; // unused: it never fires
        def.canTargetAir = false;
        if (prefab != null)
            def.basePrefab = prefab;

        // Single tier: 70 salvage, 12 m light in auraRadius, zero aura bonuses so
        // the SupportAura pass grants nothing, no weapons so it never fires.
        var tier = new TowerTier
        {
            cost = Cost,
            weapons = new TowerWeaponMount[0],
            range = 0f,
            minRange = 0f,
            auraRadius = LightRadius,
            auraFireRateBonus = 0f,
            auraRangeBonus = 0f,
            auraDamageBonus = 0f,
        };
        def.tiers = new[] { tier };

        EditorUtility.SetDirty(def);
        log.AppendLine($"[{(created ? "created" : "updated")}] {DefPath} (cost {Cost}, light {LightRadius} m)");
        return def;
    }
}
