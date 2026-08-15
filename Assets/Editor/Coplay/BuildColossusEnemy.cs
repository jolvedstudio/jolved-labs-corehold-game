using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;

namespace CoplayEditor
{
    /// <summary>
    /// Builds the Colossus boss enemy prefab (GDD §4.5, §6.1, §6.2).
    ///
    /// The Colossus is the strongest enemy and only appears in the final wave. Per
    /// the GDD go/no-go outcome it is a giant Mech Constructor Humanoid BIPED at
    /// 1.4x scale in a dark-red variant with heavy orange emissive, armed with an
    /// arm-cannon in each hand. It is driven by the same Enemy / EnemyMover /
    /// EnemyWeapon / EnemyAnimatorBridge stack every other ground enemy uses.
    /// Stats come from Enemy_Colossus.asset: HP 2800, Shielded armour, speed 3,
    /// bounty 250, leak 20, enrage below 50% HP at x1.4 speed, 25% stun resistance.
    /// </summary>
    public static class BuildColossusEnemy
    {
        // Full upright biped humanoid mech (body + head + arms + legs, rigged
        // humanoid so the Walker muscle clips retarget onto it).
        private const string HumanoidPrefabGuid = "d9a8d0a0d2fb8394aab372788d100d6c";

        // Arm cannon gadget mounted in each hand (has Barrel_End muzzle markers).
        private const string ArmCannonGuid = "9dc4e286ddc57c243a44a39218b67d36";

        // Walker animation clips (humanoid muscle clips, internal fileID 7400000).
        private const string WalkClipGuid = "6460ee2a20b434f49b305b1d0e5175ca";
        private const string DieClipGuid = "dd9d590e70532a0478698f4b4e67ab34";

        // URP Lit shader used by the pack materials.
        private const string UrpLitShaderGuid = "933532a4fcc9baf4fa0491de14d08ed7";
        private const string RedDiffuseGuid = "af7459b4387fa8f4fa3e05b70f290529";
        private const string EmissionMapGuid = "50a99efbe8bd8184eafe45d303df1ece";
        private const string NormalMapGuid = "3b033aeaa989ee748967865ada897850";

        // Blob shadow material shared by the other enemy prefabs.
        private const string BlobShadowMatGuid = "cd3d9ccb8c402bf48870aa57ff6803ff";

        // Reused SFX from the Breaker (heavy enemy) so the boss is not silent.
        private const string FireSoundGuid = "8fdcabd035384834882e425db462a5f3";
        private const string DeathSoundGuid = "a4934d1a3017c7148b17fa67d52005e8";

        private const string ColossusDefGuid = "3b63d6d6386832841b60e95507c0fb3c";

        private const string MatFolder = "Assets/_COREHOLD/Art/Materials";
        private const string CtrlFolder = "Assets/_COREHOLD/Art/AnimatorControllers";
        private const string PrefabFolder = "Assets/_COREHOLD/Prefabs/Enemies";

        private const string MatPath = MatFolder + "/Colossus_Body.mat";
        private const string CtrlPath = CtrlFolder + "/Colossus_Anim.controller";
        private const string PrefabPath = PrefabFolder + "/Colossus.prefab";

        public static string Execute()
        {
            var log = new System.Text.StringBuilder();

            EnsureFolder(MatFolder);
            EnsureFolder(CtrlFolder);
            EnsureFolder(PrefabFolder);

            Material bodyMat = BuildBodyMaterial(log);
            AnimatorController ctrl = BuildController(log);

            // ---------------------------------------------------------------
            // Instantiate the biped humanoid mech.
            // ---------------------------------------------------------------
            string humanoidPath = AssetDatabase.GUIDToAssetPath(HumanoidPrefabGuid);
            var humanoidModel = AssetDatabase.LoadAssetAtPath<GameObject>(humanoidPath);
            if (humanoidModel == null)
                return $"ERROR: humanoid mech not found at guid {HumanoidPrefabGuid} ({humanoidPath}).";

            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(humanoidModel);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = "Colossus";
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            // Boss scale: 1.4x the base humanoid (GDD §4.5).
            root.transform.localScale = Vector3.one * 1.4f;

            // ---------------------------------------------------------------
            // Mount an arm cannon in each hand bone + collect muzzles.
            // ---------------------------------------------------------------
            var armCannon = AssetDatabase.LoadAssetAtPath<GameObject>(
                AssetDatabase.GUIDToAssetPath(ArmCannonGuid));
            var muzzles = new List<Transform>();

            var rightHand = FindDeep(root.transform, "Mech_Humanoid_RightHand");
            var leftHand = FindDeep(root.transform, "Mech_Humanoid_LeftHand");
            MountWeapon(armCannon, rightHand, muzzles, "R", log);
            MountWeapon(armCannon, leftHand, muzzles, "L", log);

            // ---------------------------------------------------------------
            // Retint every mech renderer to the dark-red variant. Gather them as
            // the enrage renderers (emissive flips orange -> white below 50% HP).
            // ---------------------------------------------------------------
            var enrageRenderers = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer))
                    continue;
                var mats = r.sharedMaterials;
                bool touched = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null) { mats[i] = bodyMat; touched = true; }
                }
                if (touched) { r.sharedMaterials = mats; enrageRenderers.Add(r); }
            }

            // Animator: humanoid avatar (kept from the model) + Speed/Die controller.
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = ctrl;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            // ---------------------------------------------------------------
            // Core enemy components.
            // ---------------------------------------------------------------
            var enemy = root.AddComponent<Enemy>();
            var mover = root.AddComponent<EnemyMover>();
            var bridge = root.AddComponent<EnemyAnimatorBridge>();
            var weapon = root.AddComponent<EnemyWeapon>();

            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(
                AssetDatabase.GUIDToAssetPath(ColossusDefGuid));

            // Hit point at chest height (before the 1.4x scale it is ~2.8 m tall).
            var hitPoint = new GameObject("HitPoint");
            hitPoint.transform.SetParent(root.transform, false);
            hitPoint.transform.localPosition = new Vector3(0f, 2.8f, 0f);

            SetupEnemy(enemy, def, hitPoint.transform, enrageRenderers);
            SetupMover(mover, def);
            SetupBridge(bridge, animator, mover, def);
            SetupWeapon(weapon, muzzles);

            AddBlobShadow(root, log);

            // ---------------------------------------------------------------
            // Save the prefab and assign it to the definition.
            // ---------------------------------------------------------------
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            if (def != null && prefab != null)
            {
                def.prefab = prefab;
                if (def.fireSound == null)
                    def.fireSound = AssetDatabase.LoadAssetAtPath<AudioClip>(
                        AssetDatabase.GUIDToAssetPath(FireSoundGuid));
                if (def.deathSound == null)
                    def.deathSound = AssetDatabase.LoadAssetAtPath<AudioClip>(
                        AssetDatabase.GUIDToAssetPath(DeathSoundGuid));
                EditorUtility.SetDirty(def);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine($"Built Colossus BIPED prefab at {PrefabPath}.");
            log.AppendLine($"Retinted {enrageRenderers.Count} renderer(s) to the dark-red variant.");
            log.AppendLine($"Mounted {muzzles.Count} weapon muzzle(s).");
            log.AppendLine($"Assigned prefab to Enemy_Colossus definition: {(def != null ? "OK" : "DEFINITION MISSING")}.");
            return log.ToString();
        }

        private static void MountWeapon(GameObject weaponPrefab, Transform hand,
            List<Transform> muzzles, string side, System.Text.StringBuilder log)
        {
            if (weaponPrefab == null || hand == null)
            {
                log.AppendLine($"WARN: could not mount {side} weapon (hand or prefab missing).");
                return;
            }

            var w = (GameObject)PrefabUtility.InstantiatePrefab(weaponPrefab);
            PrefabUtility.UnpackPrefabInstance(w, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            w.name = $"ArmCannon_{side}";
            w.transform.SetParent(hand, false);
            w.transform.localPosition = Vector3.zero;
            w.transform.localRotation = Quaternion.identity;

            // Use a barrel-tip marker as the muzzle, else create one forward of the gun.
            var barrel = FindDeep(w.transform, "Barrel_End");
            if (barrel == null) barrel = FindDeep(w.transform, "Barrel_End_1");
            if (barrel != null)
            {
                muzzles.Add(barrel);
            }
            else
            {
                var m = new GameObject($"Muzzle_{side}");
                m.transform.SetParent(w.transform, false);
                m.transform.localPosition = new Vector3(0f, 0f, 1f);
                muzzles.Add(m.transform);
            }
            log.AppendLine($"Mounted arm cannon on {side} hand.");
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

        private static Material BuildBodyMaterial(System.Text.StringBuilder log)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                AssetDatabase.GUIDToAssetPath(UrpLitShaderGuid));
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Lit");

            var mat = new Material(shader) { name = "Colossus_Body" };

            var redTex = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(RedDiffuseGuid));
            var emitTex = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(EmissionMapGuid));
            var normTex = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(NormalMapGuid));

            if (redTex != null)
            {
                mat.SetTexture("_BaseMap", redTex);
                mat.SetTexture("_MainTex", redTex);
            }
            var darkRed = new Color(0.55f, 0.14f, 0.12f, 1f);
            mat.SetColor("_BaseColor", darkRed);
            mat.SetColor("_Color", darkRed);

            if (normTex != null)
            {
                mat.SetTexture("_BumpMap", normTex);
                mat.EnableKeyword("_NORMALMAP");
            }

            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (emitTex != null)
                mat.SetTexture("_EmissionMap", emitTex);
            mat.SetColor("_EmissionColor", new Color(1f, 0.35f, 0f, 1f) * 2f);

            mat.SetFloat("_Metallic", 0.6f);
            mat.SetFloat("_Smoothness", 0.45f);

            AssetDatabase.CreateAsset(mat, MatPath);
            log.AppendLine($"Created dark-red body material at {MatPath}.");
            return mat;
        }

        private static AnimatorController BuildController(System.Text.StringBuilder log)
        {
            AssetDatabase.DeleteAsset(CtrlPath);

            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(CtrlPath);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Die", AnimatorControllerParameterType.Trigger);

            var walkClip = LoadClip(WalkClipGuid);
            var dieClip = LoadClip(DieClipGuid);

            var sm = ctrl.layers[0].stateMachine;

            var locomotion = sm.AddState("Locomotion");
            locomotion.motion = walkClip;
            sm.defaultState = locomotion;

            var death = sm.AddState("Death");
            death.motion = dieClip;

            var toDeath = sm.AddAnyStateTransition(death);
            toDeath.AddCondition(AnimatorConditionMode.If, 0f, "Die");
            toDeath.hasExitTime = false;
            toDeath.hasFixedDuration = true;
            toDeath.duration = 0.05f;
            toDeath.canTransitionToSelf = false;

            EditorUtility.SetDirty(ctrl);
            log.AppendLine($"Created Colossus animator controller at {CtrlPath} (Walk + Die).");
            return ctrl;
        }

        private static AnimationClip LoadClip(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj is AnimationClip clip && !clip.name.StartsWith("__preview"))
                    return clip;
            }
            return null;
        }

        private static void SetupEnemy(Enemy enemy, EnemyDefinition def, Transform hitPoint,
            List<Renderer> enrageRenderers)
        {
            var so = new SerializedObject(enemy);
            so.FindProperty("maxHealth").floatValue = def != null && def.baseHealth > 0 ? def.baseHealth : 2800f;
            so.FindProperty("hitPoint").objectReferenceValue = hitPoint;
            so.FindProperty("bounty").intValue = def != null ? def.bounty : 250;
            so.FindProperty("leakDamage").floatValue = def != null ? def.leakDamage : 20f;
            so.FindProperty("deathAnimDuration").floatValue = 1.7f;
            so.FindProperty("isAir").boolValue = false;
            so.FindProperty("armourType").enumValueIndex = def != null ? (int)def.armourType : (int)ArmourType.Shielded;
            so.FindProperty("definition").objectReferenceValue = def;

            var arr = so.FindProperty("enrageRenderers");
            arr.arraySize = enrageRenderers.Count;
            for (int i = 0; i < enrageRenderers.Count; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = enrageRenderers[i];

            so.FindProperty("enrageEmissiveFrom").colorValue = new Color(1f, 0.35f, 0f, 1f) * 2f;
            so.FindProperty("enrageEmissiveTo").colorValue = new Color(1f, 1f, 1f, 1f) * 3f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupMover(EnemyMover mover, EnemyDefinition def)
        {
            var so = new SerializedObject(mover);
            so.FindProperty("moveSpeed").floatValue = def != null && def.moveSpeed > 0 ? def.moveSpeed : 3f;
            so.FindProperty("turnRate").floatValue = 180f;
            so.FindProperty("minDesiredSpeed").floatValue = 0.4f;
            so.FindProperty("bodyRadius").floatValue = 2.2f;
            so.FindProperty("definition").objectReferenceValue = def;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupBridge(EnemyAnimatorBridge bridge, Animator animator,
            EnemyMover mover, EnemyDefinition def)
        {
            var so = new SerializedObject(bridge);
            so.FindProperty("animator").objectReferenceValue = animator;
            so.FindProperty("mover").objectReferenceValue = mover;
            so.FindProperty("animatorClipSpeedRef").floatValue =
                def != null && def.animatorClipSpeedRef > 0 ? def.animatorClipSpeedRef : 3f;
            so.FindProperty("definition").objectReferenceValue = def;
            so.FindProperty("moveSpeedFallback").floatValue = 3f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetupWeapon(EnemyWeapon weapon, List<Transform> muzzles)
        {
            var so = new SerializedObject(weapon);
            var weapons = so.FindProperty("weapons");
            weapons.arraySize = 1;
            var w = weapons.GetArrayElementAtIndex(0);
            w.FindPropertyRelative("range").floatValue = 30f;      // long boss reach
            w.FindPropertyRelative("fireRate").floatValue = 1.2f;  // twin cannons, brisk
            w.FindPropertyRelative("damage").floatValue = 28f;     // hits turrets hard
            w.FindPropertyRelative("muzzle").objectReferenceValue = muzzles.Count > 0 ? muzzles[0] : null;

            // Twin-barrel: one weapon that alternates between both hand muzzles.
            var muzzleList = w.FindPropertyRelative("muzzles");
            muzzleList.arraySize = muzzles.Count;
            for (int i = 0; i < muzzles.Count; i++)
                muzzleList.GetArrayElementAtIndex(i).objectReferenceValue = muzzles[i];

            w.FindPropertyRelative("tracerColor").colorValue = new Color(4f, 1.2f, 0.3f, 1f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddBlobShadow(GameObject root, System.Text.StringBuilder log)
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
            var shadowMat = AssetDatabase.LoadAssetAtPath<Material>(
                AssetDatabase.GUIDToAssetPath(BlobShadowMatGuid));
            if (shadowMat != null)
                mr.sharedMaterial = shadowMat;

            var bs = shadow.AddComponent<BlobShadow>();
            var so = new SerializedObject(bs);
            var groundY = so.FindProperty("groundY");
            if (groundY != null) groundY.floatValue = 0.05f;
            var diameter = so.FindProperty("diameter");
            if (diameter != null) diameter.floatValue = 3.5f;
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("Added BlobShadow sized for the boss footprint.");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
