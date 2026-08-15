using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;

namespace CoplayEditor
{
    /// <summary>
    /// Turns the two extra Colossus BODIES (Colossus_B, Colossus_C) — already
    /// assembled biped mechs with their own weapons — into fully functional bosses
    /// by ADDING the gameplay stack only. It does NOT touch the existing body or
    /// weapon hierarchy: muzzles are wired to the weapon-mount transforms that the
    /// prefabs already contain (hand gadget mounts on B, shoulder guns on C).
    ///
    /// For each variant it:
    ///   - adds Enemy / EnemyMover / EnemyAnimatorBridge / EnemyWeapon,
    ///   - assigns the shared Colossus animator controller (Walk + Die),
    ///   - retints the mech to a distinct boss palette (B = steel-blue, C = toxic
    ///     green) and registers those renderers for the enrage emissive flip,
    ///   - adds a HitPoint marker and a BlobShadow (the support objects every other
    ///     enemy prefab has),
    ///   - authors an EnemyDefinition SO and links the prefab to it.
    /// </summary>
    public static class BuildColossusVariants
    {
        private const string CtrlPath = "Assets/_COREHOLD/Art/AnimatorControllers/Colossus_Anim.controller";
        private const string MatFolder = "Assets/_COREHOLD/Art/Materials";
        private const string DefFolder = "Assets/_COREHOLD/Data/Enemies";
        private const string PrefabFolder = "Assets/_COREHOLD/Prefabs/Enemies";

        // Shared pack maps (same emission/normal the base pack materials use).
        private const string EmissionMapGuid = "50a99efbe8bd8184eafe45d303df1ece";
        private const string NormalMapGuid = "3b033aeaa989ee748967865ada897850";
        private const string UrpLitShaderGuid = "933532a4fcc9baf4fa0491de14d08ed7";

        // Distinct diffuse tints per variant.
        private const string BlueDiffuseGuid = "85ea61c2b2d91df4a8cbdc0b2e5ee626";
        private const string GreenDiffuseGuid = "965f6ddbd170d53409ff649499a7932f";

        private const string BlobShadowMatGuid = "cd3d9ccb8c402bf48870aa57ff6803ff";
        private const string FireSoundGuid = "8fdcabd035384834882e425db462a5f3";
        private const string DeathSoundGuid = "a4934d1a3017c7148b17fa67d52005e8";

        // EnemyDefinition MonoScript guid (from Enemy_Colossus.asset).
        private const string EnemyDefScriptGuid = "58a016623313bc749af838ac150c86f4";

        private struct Variant
        {
            public string prefabName;
            public string defName;
            public string id;
            public string displayName;
            public string diffuseGuid;
            public Color baseColor;
            public Color emissive;
            public float health;
            public float moveSpeed;
            public int bounty;
            public int leak;
            public float range;
            public float fireRate;
            public float damage;
            public string[] muzzleNames; // existing weapon-mount transforms to fire from
        }

        public static string Execute()
        {
            var log = new System.Text.StringBuilder();

            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(CtrlPath);
            if (ctrl == null)
                return $"ERROR: shared Colossus animator controller not found at {CtrlPath}. Build Colossus_A first.";

            var variants = new[]
            {
                new Variant
                {
                    prefabName = "Colossus_B",
                    defName = "Enemy_Colossus_B",
                    id = "colossus_b",
                    displayName = "Colossus Vanguard",
                    diffuseGuid = BlueDiffuseGuid,
                    baseColor = new Color(0.16f, 0.28f, 0.5f, 1f),
                    emissive = new Color(0.2f, 0.7f, 1f, 1f) * 2f,
                    health = 2400f,
                    moveSpeed = 3.4f,
                    bounty = 220,
                    leak = 18,
                    range = 26f,
                    fireRate = 1.6f,
                    damage = 22f,
                    // Hand-mounted gadget cannons already present on this body.
                    muzzleNames = new[] { "Mount_Gadget_R", "Mount_Gadget_L" }
                },
                new Variant
                {
                    prefabName = "Colossus_C",
                    defName = "Enemy_Colossus_C",
                    id = "colossus_c",
                    displayName = "Colossus Sentinel",
                    diffuseGuid = GreenDiffuseGuid,
                    baseColor = new Color(0.22f, 0.42f, 0.16f, 1f),
                    emissive = new Color(0.6f, 1f, 0.2f, 1f) * 2f,
                    health = 3200f,
                    moveSpeed = 2.6f,
                    bounty = 280,
                    leak = 24,
                    range = 34f,       // long-range shoulder guns
                    fireRate = 0.9f,
                    damage = 34f,
                    // Shoulder-mounted guns already present on this body.
                    muzzleNames = new[] { "HalfShoulder_Hog_R", "HalfShoulder_Hog_L" }
                }
            };

            foreach (var v in variants)
                BuildVariant(v, ctrl, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return log.ToString();
        }

        private static void BuildVariant(Variant v, RuntimeAnimatorController ctrl,
            System.Text.StringBuilder log)
        {
            string prefabPath = $"{PrefabFolder}/{v.prefabName}.prefab";
            if (!System.IO.File.Exists(prefabPath))
            {
                log.AppendLine($"ERROR: {prefabPath} not found — skipped.");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                // --- Retint every mech renderer to the variant palette. -----------
                Material bodyMat = BuildBodyMaterial(v, log);
                var enrageRenderers = new List<Renderer>();
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer))
                        continue;
                    if (r.GetComponent<BlobShadow>() != null)
                        continue;
                    var mats = r.sharedMaterials;
                    bool touched = false;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null) { mats[i] = bodyMat; touched = true; }
                    if (touched) { r.sharedMaterials = mats; enrageRenderers.Add(r); }
                }

                // --- Animator: keep the humanoid avatar, add the shared controller. -
                var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                animator.runtimeAnimatorController = ctrl;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

                // --- Gameplay components. -----------------------------------------
                var enemy = GetOrAdd<Enemy>(root);
                var mover = GetOrAdd<EnemyMover>(root);
                var bridge = GetOrAdd<EnemyAnimatorBridge>(root);
                var weapon = GetOrAdd<EnemyWeapon>(root);

                // --- SO definition. ------------------------------------------------
                EnemyDefinition def = BuildDefinition(v, prefabPath, log);

                // --- HitPoint marker (chest height). Support object only. ----------
                Transform hitPoint = FindDeep(root.transform, "HitPoint");
                if (hitPoint == null)
                {
                    var hp = new GameObject("HitPoint");
                    hp.transform.SetParent(root.transform, false);
                    hp.transform.localPosition = new Vector3(0f, 2.8f, 0f);
                    hitPoint = hp.transform;
                }

                // --- Muzzles: reuse the weapon transforms already on the body. -----
                var muzzles = new List<Transform>();
                foreach (var n in v.muzzleNames)
                {
                    var t = FindDeep(root.transform, n);
                    if (t != null) muzzles.Add(t);
                }
                if (muzzles.Count == 0)
                {
                    // Fall back to the hand bones so the boss is never disarmed.
                    var rh = FindDeep(root.transform, "Mech_Humanoid_RightHand");
                    var lh = FindDeep(root.transform, "Mech_Humanoid_LeftHand");
                    if (rh != null) muzzles.Add(rh);
                    if (lh != null) muzzles.Add(lh);
                    log.AppendLine($"{v.prefabName}: named mounts not found, using hand bones as muzzles.");
                }

                SetupEnemy(enemy, def, hitPoint, enrageRenderers, v);
                SetupMover(mover, def, v);
                SetupBridge(bridge, animator, mover, def, v);
                SetupWeapon(weapon, muzzles, v);

                // --- BlobShadow (support object every ground enemy has). -----------
                if (FindDeep(root.transform, "BlobShadow") == null)
                    AddBlobShadow(root);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                // Link the freshly saved prefab back into the definition.
                if (def != null)
                {
                    def.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    EditorUtility.SetDirty(def);
                }

                log.AppendLine($"{v.prefabName}: added enemy stack, retinted {enrageRenderers.Count} renderer(s), " +
                               $"wired {muzzles.Count} muzzle(s), linked {v.defName}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        private static Material BuildBodyMaterial(Variant v, System.Text.StringBuilder log)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(UrpLitShaderGuid))
                         ?? Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = v.prefabName + "_Body" };

            var diff = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(v.diffuseGuid));
            var emit = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(EmissionMapGuid));
            var norm = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(NormalMapGuid));

            if (diff != null)
            {
                mat.SetTexture("_BaseMap", diff);
                mat.SetTexture("_MainTex", diff);
            }
            mat.SetColor("_BaseColor", v.baseColor);
            mat.SetColor("_Color", v.baseColor);

            if (norm != null)
            {
                mat.SetTexture("_BumpMap", norm);
                mat.EnableKeyword("_NORMALMAP");
            }

            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (emit != null) mat.SetTexture("_EmissionMap", emit);
            mat.SetColor("_EmissionColor", v.emissive);

            mat.SetFloat("_Metallic", 0.6f);
            mat.SetFloat("_Smoothness", 0.45f);

            string matPath = $"{MatFolder}/{v.prefabName}_Body.mat";
            AssetDatabase.DeleteAsset(matPath);
            AssetDatabase.CreateAsset(mat, matPath);
            log.AppendLine($"Created {matPath}.");
            return mat;
        }

        private static EnemyDefinition BuildDefinition(Variant v, string prefabPath,
            System.Text.StringBuilder log)
        {
            string defPath = $"{DefFolder}/{v.defName}.asset";
            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(defPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<EnemyDefinition>();
                AssetDatabase.CreateAsset(def, defPath);
                log.AppendLine($"Created {defPath}.");
            }

            def.id = v.id;
            def.displayName = v.displayName;
            def.baseHealth = v.health;
            def.armourType = ArmourType.Shielded;
            def.moveSpeed = v.moveSpeed;
            def.bounty = v.bounty;
            def.leakDamage = v.leak;
            def.isAir = false;
            def.animatorClipSpeedRef = 3f;
            def.enrageAtHealthFraction = 0.5f;
            def.enrageSpeedMultiplier = 1.4f;
            def.stunResistance = 0.25f;
            def.fireSound = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(FireSoundGuid));
            def.fireVolume = 0.7f;
            def.firePitchSpread = 0.08f;
            def.deathSound = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(DeathSoundGuid));
            def.deathVolume = 0.8f;
            def.deathPitchSpread = 0.06f;

            EditorUtility.SetDirty(def);
            return def;
        }

        private static void SetupEnemy(Enemy enemy, EnemyDefinition def, Transform hitPoint,
            List<Renderer> enrageRenderers, Variant v)
        {
            var so = new SerializedObject(enemy);
            so.FindProperty("maxHealth").floatValue = v.health;
            so.FindProperty("hitPoint").objectReferenceValue = hitPoint;
            so.FindProperty("bounty").intValue = v.bounty;
            so.FindProperty("leakDamage").floatValue = v.leak;
            so.FindProperty("deathAnimDuration").floatValue = 1.7f;
            so.FindProperty("isAir").boolValue = false;
            so.FindProperty("armourType").enumValueIndex = (int)ArmourType.Shielded;
            so.FindProperty("definition").objectReferenceValue = def;

            var arr = so.FindProperty("enrageRenderers");
            arr.arraySize = enrageRenderers.Count;
            for (int i = 0; i < enrageRenderers.Count; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = enrageRenderers[i];

            so.FindProperty("enrageEmissiveFrom").colorValue = v.emissive;
            so.FindProperty("enrageEmissiveTo").colorValue = new Color(1f, 1f, 1f, 1f) * 3f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupMover(EnemyMover mover, EnemyDefinition def, Variant v)
        {
            var so = new SerializedObject(mover);
            so.FindProperty("moveSpeed").floatValue = v.moveSpeed;
            so.FindProperty("turnRate").floatValue = 180f;
            so.FindProperty("minDesiredSpeed").floatValue = 0.4f;
            so.FindProperty("bodyRadius").floatValue = 2.2f;
            so.FindProperty("definition").objectReferenceValue = def;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupBridge(EnemyAnimatorBridge bridge, Animator animator,
            EnemyMover mover, EnemyDefinition def, Variant v)
        {
            var so = new SerializedObject(bridge);
            so.FindProperty("animator").objectReferenceValue = animator;
            so.FindProperty("mover").objectReferenceValue = mover;
            so.FindProperty("animatorClipSpeedRef").floatValue = 3f;
            so.FindProperty("definition").objectReferenceValue = def;
            so.FindProperty("moveSpeedFallback").floatValue = v.moveSpeed;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupWeapon(EnemyWeapon weapon, List<Transform> muzzles, Variant v)
        {
            var so = new SerializedObject(weapon);
            var weapons = so.FindProperty("weapons");
            weapons.arraySize = 1;
            var w = weapons.GetArrayElementAtIndex(0);
            w.FindPropertyRelative("range").floatValue = v.range;
            w.FindPropertyRelative("fireRate").floatValue = v.fireRate;
            w.FindPropertyRelative("damage").floatValue = v.damage;
            w.FindPropertyRelative("muzzle").objectReferenceValue = muzzles.Count > 0 ? muzzles[0] : null;

            var muzzleList = w.FindPropertyRelative("muzzles");
            muzzleList.arraySize = muzzles.Count;
            for (int i = 0; i < muzzles.Count; i++)
                muzzleList.GetArrayElementAtIndex(i).objectReferenceValue = muzzles[i];

            w.FindPropertyRelative("tracerColor").colorValue = v.emissive;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddBlobShadow(GameObject root)
        {
            var shadow = new GameObject("BlobShadow");
            shadow.transform.SetParent(root.transform, false);
            shadow.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(3.5f, 3.5f, 1f);

            var mf = shadow.AddComponent<MeshFilter>();
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");

            var mr = shadow.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var shadowMat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(BlobShadowMatGuid));
            if (shadowMat != null) mr.sharedMaterial = shadowMat;

            var bs = shadow.AddComponent<BlobShadow>();
            var so = new SerializedObject(bs);
            var groundY = so.FindProperty("groundY");
            if (groundY != null) groundY.floatValue = 0.05f;
            var diameter = so.FindProperty("diameter");
            if (diameter != null) diameter.floatValue = 3.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
