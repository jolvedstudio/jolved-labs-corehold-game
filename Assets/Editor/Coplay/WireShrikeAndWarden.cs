using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;

namespace CoplayEditor
{
    /// <summary>
    /// Wires the two roster enemies whose prefabs shipped as bare bodies —
    /// Enemy_Shrike and Enemy_Warden — into fully functional units, matching the
    /// pattern of the existing enemies (Wasp for the air unit, Strider for the
    /// ground unit). Their EnemyDefinition assets already exist and are authored;
    /// this adds the runtime component stack, wires muzzles to the weapon mounts the
    /// bodies already have, links the prefab into the definition, and copies group
    /// SFX onto the definitions (they were missing sounds).
    ///
    ///   Shrike  — a tracked GROUND tank with twin top-mounted grenade launchers
    ///             (four barrel tips = four muzzles). Enemy + Mover + AnimatorBridge
    ///             (no locomotion animator on the body, tolerated) + Weapon + a
    ///             ground blob shadow.
    ///   Warden  — slow Reinforced ground SUPPORT. Same stack PLUS WardenAura, the
    ///             protective damage-reduction bubble that is its whole identity.
    /// </summary>
    public static class WireShrikeAndWarden
    {
        private const string ShrikePath = "Assets/_COREHOLD/Prefabs/Enemies/Enemy_Shrike.prefab";
        private const string WardenPath = "Assets/_COREHOLD/Prefabs/Enemies/Enemy_Warden.prefab";
        private const string ShrikeDefPath = "Assets/_COREHOLD/Data/Enemies/Enemy_Shrike.asset";
        private const string WardenDefPath = "Assets/_COREHOLD/Data/Enemies/Enemy_Warden.asset";

        private const string BlobShadowMatGuid = "cd3d9ccb8c402bf48870aa57ff6803ff";

        // Group SFX reused from the existing enemies (definitions ship silent).
        private const string WaspFireGuid = "b33e2cff5225f4437a43a11d35763d46";
        private const string SharedDeathGuid = "c27036221e6b287498c43036f7767c3e";
        private const string StriderFireGuid = "7d156b8a5b340cd46bcf0a76557dd2dd";

        public static string Execute()
        {
            var log = new System.Text.StringBuilder();
            WireShrike(log);
            WireWarden(log);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return log.ToString();
        }

        // ---------------------------------------------------------------------
        //  Shrike — air gunship
        // ---------------------------------------------------------------------
        private static void WireShrike(System.Text.StringBuilder log)
        {
            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(ShrikeDefPath);
            if (def == null) { log.AppendLine("ERROR: Enemy_Shrike.asset missing."); return; }

            // Shrike is a tracked GROUND tank, not an air unit — correct the
            // definition's movement classification and give it tank-like pacing.
            def.isAir = false;
            def.flightAltitude = 0f;
            if (def.moveSpeed > 6f) def.moveSpeed = 4.5f;      // tank crawl, not a 12 m/s flyer
            if (def.armourType == ArmourType.Unarmoured)
                def.armourType = ArmourType.Plated;            // it is armoured tracks
            def.animatorClipSpeedRef = def.moveSpeed;
            // Fill in the missing SFX on the definition.
            if (def.fireSound == null)
                def.fireSound = LoadClip(WaspFireGuid);
            if (def.deathSound == null)
                def.deathSound = LoadClip(SharedDeathGuid);
            EditorUtility.SetDirty(def);

            GameObject root = PrefabUtility.LoadPrefabContents(ShrikePath);
            try
            {
                // Clean up temporary cockpit muzzle stubs from the earlier air pass.
                RemoveStubMuzzle(root, "Mount_Weapon_L");
                RemoveStubMuzzle(root, "Mount_Weapon_R");

                var enemy = GetOrAdd<Enemy>(root);
                var mover = GetOrAdd<EnemyMover>(root);
                var bridge = GetOrAdd<EnemyAnimatorBridge>(root);
                var weapon = GetOrAdd<EnemyWeapon>(root);

                // Hit point at turret height (~2.2 m up).
                Transform hitPoint = EnsureHitPoint(root, 2.2f);

                // Four muzzles: the two barrel tips on each of the twin top-mounted
                // grenade launchers. These are the actual gun ends.
                var muzzles = FindAllDeep(root.transform, new[] { "Barrel_end_1", "Barrel_end_2" });

                ConfigureEnemy(enemy, def, hitPoint,
                    maxHealth: def.baseHealth, bounty: def.bounty, leak: def.leakDamage,
                    deathAnim: 0.7f, isAir: false, armour: def.armourType,
                    enrageRenderers: null); // Shrike does not enrage.

                ConfigureMover(mover, def, turnRate: 220f, bodyRadius: 1.1f);
                // No locomotion animator on this body — leave the bridge's animator null.
                ConfigureBridge(bridge, null, mover, def, moveSpeedFallback: def.moveSpeed);
                // Twin grenade launchers: one weapon cycling all four barrel muzzles.
                ConfigureWeapon(weapon, muzzles, range: 20f, fireRate: 1.2f, damage: 9f);

                if (root.GetComponentInChildren<BlobShadow>(true) == null)
                    AddBlobShadow(root, diameter: 3.4f);

                PrefabUtility.SaveAsPrefabAsset(root, ShrikePath);
                def.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShrikePath);
                EditorUtility.SetDirty(def);

                log.AppendLine($"Shrike: wired ground tank (HP {def.baseHealth}, {muzzles.Count} muzzle(s)), linked definition.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---------------------------------------------------------------------
        //  Warden — ground support with protection bubble
        // ---------------------------------------------------------------------
        private static void WireWarden(System.Text.StringBuilder log)
        {
            var def = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(WardenDefPath);
            if (def == null) { log.AppendLine("ERROR: Enemy_Warden.asset missing."); return; }

            if (def.fireSound == null)
                def.fireSound = LoadClip(StriderFireGuid);
            if (def.deathSound == null)
                def.deathSound = LoadClip(SharedDeathGuid);
            EditorUtility.SetDirty(def);

            GameObject root = PrefabUtility.LoadPrefabContents(WardenPath);
            try
            {
                var enemy = GetOrAdd<Enemy>(root);
                var mover = GetOrAdd<EnemyMover>(root);
                var bridge = GetOrAdd<EnemyAnimatorBridge>(root);
                var weapon = GetOrAdd<EnemyWeapon>(root);
                var aura = GetOrAdd<WardenAura>(root); // the Warden's defining component

                Transform hitPoint = EnsureHitPoint(root, 1.8f);

                // Single top turret gun.
                var muzzles = CollectMuzzles(root, new[]
                {
                    "Buggy_Top_Turret", "Mount_Top"
                }, forwardOffset: 1.4f, firstMatchOnly: true);

                var animator = root.GetComponentInChildren<Animator>(true);

                ConfigureEnemy(enemy, def, hitPoint,
                    maxHealth: def.baseHealth, bounty: def.bounty, leak: def.leakDamage,
                    deathAnim: 0.8f, isAir: false, armour: def.armourType,
                    enrageRenderers: null); // Warden does not enrage.

                ConfigureMover(mover, def, turnRate: 200f, bodyRadius: 1.2f);
                ConfigureBridge(bridge, animator, mover, def, moveSpeedFallback: def.moveSpeed);
                ConfigureWeapon(weapon, muzzles, range: 18f, fireRate: 0.8f, damage: 10f);

                // Warden bubble (defaults on the component are sensible: 8 m, 25%).
                // Left at authored defaults; the component's presence is what matters.

                if (root.GetComponentInChildren<BlobShadow>(true) == null)
                    AddBlobShadow(root, diameter: 3.2f);

                PrefabUtility.SaveAsPrefabAsset(root, WardenPath);
                def.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WardenPath);
                EditorUtility.SetDirty(def);

                log.AppendLine($"Warden: wired ground support + WardenAura (HP {def.baseHealth}, {muzzles.Count} muzzle(s)), linked definition.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---------------------------------------------------------------------
        //  Shared helpers
        // ---------------------------------------------------------------------
        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        private static AudioClip LoadClip(string guid) =>
            AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid));

        private static Transform EnsureHitPoint(GameObject root, float height)
        {
            var existing = FindDeep(root.transform, "HitPoint");
            if (existing != null) return existing;
            var hp = new GameObject("HitPoint");
            hp.transform.SetParent(root.transform, false);
            hp.transform.localPosition = new Vector3(0f, height, 0f);
            return hp.transform;
        }

        /// <summary>
        /// Build a list of muzzle transforms from named mount points already on the
        /// body. For each named mount found, a child "Muzzle" is created forward of
        /// it so tracers originate at the gun, not the pivot.
        /// </summary>
        private static List<Transform> CollectMuzzles(GameObject root, string[] names,
            float forwardOffset, bool firstMatchOnly = false)
        {
            var muzzles = new List<Transform>();
            foreach (var n in names)
            {
                var mount = FindDeep(root.transform, n);
                if (mount == null) continue;

                var existing = mount.Find("Muzzle");
                Transform muzzle;
                if (existing != null)
                {
                    muzzle = existing;
                }
                else
                {
                    var m = new GameObject("Muzzle");
                    m.transform.SetParent(mount, false);
                    m.transform.localPosition = new Vector3(0f, 0f, forwardOffset);
                    muzzle = m.transform;
                }
                muzzles.Add(muzzle);
                if (firstMatchOnly) break;
            }
            return muzzles;
        }

        private static void ConfigureEnemy(Enemy enemy, EnemyDefinition def, Transform hitPoint,
            float maxHealth, int bounty, float leak, float deathAnim, bool isAir,
            ArmourType armour, List<Renderer> enrageRenderers)
        {
            var so = new SerializedObject(enemy);
            so.FindProperty("maxHealth").floatValue = maxHealth;
            so.FindProperty("hitPoint").objectReferenceValue = hitPoint;
            so.FindProperty("bounty").intValue = bounty;
            so.FindProperty("leakDamage").floatValue = leak;
            so.FindProperty("deathAnimDuration").floatValue = deathAnim;
            so.FindProperty("isAir").boolValue = isAir;
            so.FindProperty("armourType").enumValueIndex = (int)armour;
            so.FindProperty("definition").objectReferenceValue = def;

            var arr = so.FindProperty("enrageRenderers");
            int n = enrageRenderers != null ? enrageRenderers.Count : 0;
            arr.arraySize = n;
            for (int i = 0; i < n; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = enrageRenderers[i];

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureMover(EnemyMover mover, EnemyDefinition def,
            float turnRate, float bodyRadius)
        {
            var so = new SerializedObject(mover);
            so.FindProperty("moveSpeed").floatValue = def.moveSpeed;
            so.FindProperty("turnRate").floatValue = turnRate;
            so.FindProperty("minDesiredSpeed").floatValue = 0.4f;
            so.FindProperty("bodyRadius").floatValue = bodyRadius;
            so.FindProperty("definition").objectReferenceValue = def;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureBridge(EnemyAnimatorBridge bridge, Animator animator,
            EnemyMover mover, EnemyDefinition def, float moveSpeedFallback)
        {
            var so = new SerializedObject(bridge);
            so.FindProperty("animator").objectReferenceValue = animator; // may be null (Shrike)
            so.FindProperty("mover").objectReferenceValue = mover;
            so.FindProperty("animatorClipSpeedRef").floatValue =
                def.animatorClipSpeedRef > 0f ? def.animatorClipSpeedRef : moveSpeedFallback;
            so.FindProperty("definition").objectReferenceValue = def;
            so.FindProperty("moveSpeedFallback").floatValue = moveSpeedFallback;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureWeapon(EnemyWeapon weapon, List<Transform> muzzles,
            float range, float fireRate, float damage)
        {
            var so = new SerializedObject(weapon);
            var weapons = so.FindProperty("weapons");
            weapons.arraySize = 1;
            var w = weapons.GetArrayElementAtIndex(0);
            w.FindPropertyRelative("range").floatValue = range;
            w.FindPropertyRelative("fireRate").floatValue = fireRate;
            w.FindPropertyRelative("damage").floatValue = damage;
            w.FindPropertyRelative("muzzle").objectReferenceValue = muzzles.Count > 0 ? muzzles[0] : null;

            var muzzleList = w.FindPropertyRelative("muzzles");
            muzzleList.arraySize = muzzles.Count;
            for (int i = 0; i < muzzles.Count; i++)
                muzzleList.GetArrayElementAtIndex(i).objectReferenceValue = muzzles[i];

            w.FindPropertyRelative("tracerColor").colorValue = new Color(4f, 1.2f, 0.3f, 1f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddBlobShadow(GameObject root, float diameter)
        {
            var shadow = new GameObject("BlobShadow");
            shadow.transform.SetParent(root.transform, false);
            shadow.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(diameter, diameter, 1f);

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
            var d = so.FindProperty("diameter");
            if (d != null) d.floatValue = diameter;
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

        /// <summary>Collect EVERY descendant transform whose name matches one of the given names.</summary>
        private static List<Transform> FindAllDeep(Transform root, string[] names)
        {
            var results = new List<Transform>();
            CollectAllDeep(root, names, results);
            return results;
        }

        private static void CollectAllDeep(Transform t, string[] names, List<Transform> results)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (t.name == names[i]) { results.Add(t); break; }
            }
            for (int i = 0; i < t.childCount; i++)
                CollectAllDeep(t.GetChild(i), names, results);
        }

        /// <summary>Delete a temporary "Muzzle" child previously created under a named mount.</summary>
        private static void RemoveStubMuzzle(GameObject root, string mountName)
        {
            var mount = FindDeep(root.transform, mountName);
            if (mount == null) return;
            var stub = mount.Find("Muzzle");
            if (stub != null)
                Object.DestroyImmediate(stub.gameObject);
        }
    }
}
