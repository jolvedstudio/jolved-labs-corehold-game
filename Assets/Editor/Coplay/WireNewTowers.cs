using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Corehold.Data;
using Corehold.Towers;
using Corehold.Systems;

namespace CoplayEditor
{
    /// <summary>
    /// Wires the four roster towers whose prefabs shipped as bare bodies —
    /// Tower_CryoNode, Tower_FlakArray, Tower_Railgun, Tower_SalvageRig — into
    /// functional turrets, matching the component pattern of the existing towers
    /// (Autocannon/SiegeMortar for combat, Floodlight/ScanRelay for support). Their
    /// TowerDefinition assets already exist and are authored; this adds the runtime
    /// component stack, links the definition, wires yaw/pitch pivots and muzzle
    /// points to the transforms already in each body, and adds a BlobShadow.
    ///
    ///   FlakArray — air-only combat gun (twin barrels). TowerTargeting + TurretAim
    ///               + TowerWeapon (two muzzles) + AudioSource + Tower + BlobShadow.
    ///   Railgun   — long-range piercing combat gun. Same combat stack, single muzzle.
    ///   CryoNode  — support slow field. Tower + TowerTargeting + CryoField (no aim/
    ///               muzzle: it is a pure aura like the Floodlight) + BlobShadow.
    ///   SalvageRig— support bounty aura. Tower + TowerTargeting + SalvageRig +
    ///               BlobShadow.
    ///
    /// The Tower component is normally added by the build flow, but the shipped
    /// support prefab (Floodlight) bakes it in so the special aura component has its
    /// RequireComponent satisfied in the prefab; we follow that and bake Tower on all
    /// four for consistency.
    /// </summary>
    public static class WireNewTowers
    {
        private const string BlobShadowMatGuid = "cd3d9ccb8c402bf48870aa57ff6803ff";

        private const string CryoDefGuid = "?"; // resolved by path below
        private const string TowerFolder = "Assets/_COREHOLD/Prefabs/Towers";
        private const string DefFolder = "Assets/_COREHOLD/Data/Towers";

        public static string Execute()
        {
            var log = new System.Text.StringBuilder();

            WireCombat("Tower_FlakArray",
                yawName: "Shoulders_Plates",
                pitchName: "DoubleGun_Barrel_Pivot",
                muzzleNames: new[] { "Barrel_End_1", "Barrel_End_2" },
                tracer: new Color(3.5f, 3f, 1f, 1f), log);

            WireCombat("Tower_Railgun",
                yawName: "Rotation_Pivot",
                pitchName: "Raygun_Barrel",
                muzzleNames: new[] { "Barrel_End" },
                tracer: new Color(3.2f, 1.4f, 4.5f, 1f), log);

            WireSupport("Tower_CryoNode", addCryo: true, addSalvage: false, log);
            WireSupport("Tower_SalvageRig", addCryo: false, addSalvage: true, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return log.ToString();
        }

        // ------------------------------------------------------------------
        //  Combat towers (FlakArray, Railgun)
        // ------------------------------------------------------------------
        private static void WireCombat(string name, string yawName, string pitchName,
            string[] muzzleNames, Color tracer, System.Text.StringBuilder log)
        {
            string prefabPath = $"{TowerFolder}/{name}.prefab";
            string defPath = $"{DefFolder}/{name}.asset";
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(defPath);
            if (def == null) { log.AppendLine($"ERROR: {defPath} missing."); return; }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform yaw = FindDeep(root.transform, yawName);
                Transform pitch = FindDeep(root.transform, pitchName);
                var muzzles = new List<Transform>();
                foreach (var m in muzzleNames)
                {
                    var t = FindDeep(root.transform, m);
                    if (t != null) muzzles.Add(t);
                }

                Transform rangeOrigin = EnsureChild(root.transform, "RangeOrigin", new Vector3(0f, 2.8f, 0f));

                var tower = GetOrAdd<Tower>(root);
                var targeting = GetOrAdd<TowerTargeting>(root);
                var aim = GetOrAdd<TurretAim>(root);
                var weapon = GetOrAdd<TowerWeapon>(root);
                var barrelSpin = GetOrAdd<TurretBarrelSpin>(root);
                if (root.GetComponent<AudioSource>() == null)
                    root.AddComponent<AudioSource>().playOnAwake = false;

                ConfigureTower(tower, def);
                ConfigureTargeting(targeting, rangeOrigin);
                ConfigureAim(aim, yaw, pitch);
                ConfigureWeapon(weapon, def, muzzles, tracer);
                ConfigureBarrelSpin(barrelSpin, pitch);

                if (root.GetComponentInChildren<BlobShadow>(true) == null)
                    AddBlobShadow(root, 4f);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                def.basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                EditorUtility.SetDirty(def);

                log.AppendLine($"{name}: combat wired (yaw={(yaw!=null?yaw.name:"MISSING")}, " +
                               $"pitch={(pitch!=null?pitch.name:"MISSING")}, {muzzles.Count} muzzle(s)), linked definition.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ------------------------------------------------------------------
        //  Support towers (CryoNode, SalvageRig)
        // ------------------------------------------------------------------
        private static void WireSupport(string name, bool addCryo, bool addSalvage,
            System.Text.StringBuilder log)
        {
            string prefabPath = $"{TowerFolder}/{name}.prefab";
            string defPath = $"{DefFolder}/{name}.asset";
            var def = AssetDatabase.LoadAssetAtPath<TowerDefinition>(defPath);
            if (def == null) { log.AppendLine($"ERROR: {defPath} missing."); return; }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform rangeOrigin = EnsureChild(root.transform, "RangeOrigin", new Vector3(0f, 2.6f, 0f));

                var tower = GetOrAdd<Tower>(root);
                var targeting = GetOrAdd<TowerTargeting>(root);

                ConfigureTower(tower, def);
                ConfigureTargeting(targeting, rangeOrigin);

                if (addCryo) GetOrAdd<CryoField>(root);
                if (addSalvage) GetOrAdd<SalvageRig>(root);

                if (root.GetComponentInChildren<BlobShadow>(true) == null)
                    AddBlobShadow(root, 4f);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                def.basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                EditorUtility.SetDirty(def);

                log.AppendLine($"{name}: support wired ({(addCryo ? "CryoField" : "")}{(addSalvage ? "SalvageRig" : "")}), linked definition.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ------------------------------------------------------------------
        //  Component configuration
        // ------------------------------------------------------------------
        private static void ConfigureTower(Tower tower, TowerDefinition def)
        {
            var so = new SerializedObject(tower);
            so.FindProperty("definition").objectReferenceValue = def;
            so.FindProperty("tierIndex").intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureTargeting(TowerTargeting targeting, Transform rangeOrigin)
        {
            var so = new SerializedObject(targeting);
            so.FindProperty("rangeOrigin").objectReferenceValue = rangeOrigin;
            so.FindProperty("priority").enumValueIndex = 0; // First
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureAim(TurretAim aim, Transform yaw, Transform pitch)
        {
            var so = new SerializedObject(aim);
            so.FindProperty("yawPivot").objectReferenceValue = yaw;
            so.FindProperty("pitchPivot").objectReferenceValue = pitch;
            so.FindProperty("yawSpeed").floatValue = 160f;
            so.FindProperty("pitchSpeed").floatValue = 110f;
            so.FindProperty("aimTolerance").floatValue = 6f;
            so.FindProperty("idleScan").boolValue = true;
            so.FindProperty("idleScanSpeed").floatValue = 22f;
            so.FindProperty("idleScanArc").floatValue = 70f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureWeapon(TowerWeapon weapon, TowerDefinition def,
            List<Transform> muzzles, Color tracer)
        {
            var so = new SerializedObject(weapon);
            so.FindProperty("definition").objectReferenceValue = def;
            so.FindProperty("tierIndex").intValue = 0;

            var mp = so.FindProperty("muzzlePoints");
            mp.arraySize = muzzles.Count;
            for (int i = 0; i < muzzles.Count; i++)
                mp.GetArrayElementAtIndex(i).objectReferenceValue = muzzles[i];
            if (muzzles.Count > 0)
                so.FindProperty("muzzlePoint").objectReferenceValue = muzzles[0];

            so.FindProperty("chainJumpRange").floatValue = 6f;
            so.FindProperty("chainColor").colorValue = new Color(0.5f, 0.85f, 1f, 1f);
            so.FindProperty("tracerColor").colorValue = tracer;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureBarrelSpin(TurretBarrelSpin spin, Transform barrel)
        {
            var so = new SerializedObject(spin);
            var b = so.FindProperty("barrel");
            if (b != null) b.objectReferenceValue = barrel;
            var recoil = so.FindProperty("recoilDistance");
            if (recoil != null) recoil.floatValue = 0.18f;
            var recover = so.FindProperty("recoverSpeed");
            if (recover != null) recover.floatValue = 12f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------
        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        private static Transform EnsureChild(Transform root, string name, Vector3 localPos)
        {
            var existing = FindDeep(root, name);
            if (existing != null) return existing;
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = localPos;
            return go.transform;
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
    }
}
