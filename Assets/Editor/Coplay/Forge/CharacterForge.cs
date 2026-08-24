using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Corehold.Data;
using Corehold.Enemies;
using Corehold.Systems;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CoreholdEditor.Forge
{
    /// <summary>
    /// The one generalized character builder (plan v2 §B.3) — the codification
    /// of BuildColossusEnemy's proven assembly steps, driven by a
    /// <see cref="CharacterRecipe"/> instead of per-unit C#.
    ///
    /// Contract with the runtime: stats live on the DEFINITION and are applied
    /// at spawn (WaveManager calls Enemy/EnemyMover.Configure(def)), so the
    /// forge's prefab work is structural — component stack, muzzles, shadow,
    /// animator — with definition-mirroring defaults baked in for editor
    /// testing. The definition itself is a CLONE of the recipe's template, so
    /// every stat/audio/enrage field the template carries survives untouched.
    ///
    /// Gates, Discard-style: validation fails BEFORE anything is written, and a
    /// mid-build failure deletes whatever was created — no half-built units.
    /// </summary>
    public static class CharacterForge
    {
        private const string EnemyPrefabDir = "Assets/_COREHOLD/Prefabs/Enemies";
        private const string EnemyDefDir = "Assets/_COREHOLD/Data/Enemies";
        private const string TowerPrefabDir = "Assets/_COREHOLD/Prefabs/Towers";
        private const string TowerDefDir = "Assets/_COREHOLD/Data/Towers";
        private const string ControllerDir = "Assets/_COREHOLD/Art/AnimatorControllers";

        public static string Build(CharacterRecipe recipe)
        {
            var log = new StringBuilder();
            string gate = Validate(recipe);
            if (gate != null)
            {
                log.AppendLine("FORGE REFUSED — nothing was written:");
                log.AppendLine("  " + gate);
                return log.ToString();
            }

            return recipe.IsEnemy ? BuildEnemy(recipe, log) : BuildTurret(recipe, log);
        }

        // ------------------------------------------------------------- gates

        private static string Validate(CharacterRecipe r)
        {
            if (r.sourcePrefab == null) return "no source prefab assigned.";
            if (string.IsNullOrWhiteSpace(r.id)) return "no id (stable identifier) set.";
            if (string.IsNullOrWhiteSpace(r.displayName)) return "no display name set.";

            if (r.IsEnemy)
            {
                if (r.enemyTemplate == null)
                    return "enemy archetypes need an enemyTemplate definition to clone stats from " +
                           "(pick the closest existing unit — e.g. Enemy_Wasp for a light flier).";
                if (r.archetype == ForgeArchetype.Walker && (r.walkClip == null) != (r.dieClip == null))
                    return "Walker: assign BOTH walk and die clips (animated) or NEITHER (mover-driven).";
            }
            else
            {
                if (r.towerTemplate == null)
                    return "CombatTurret needs a towerTemplate definition to clone tiers from.";
                if (r.towerTemplate.tiers == null || r.towerTemplate.tiers.Length == 0)
                    return $"towerTemplate '{r.towerTemplate.name}' has no tiers — not a usable template.";
            }
            return null;
        }

        // ------------------------------------------------------------ enemies

        private static string BuildEnemy(CharacterRecipe r, StringBuilder log)
        {
            string prefabDir = string.IsNullOrEmpty(r.outputPrefabFolder) ? EnemyPrefabDir : r.outputPrefabFolder;
            string defDir = string.IsNullOrEmpty(r.outputDefinitionFolder) ? EnemyDefDir : r.outputDefinitionFolder;
            EnsureFolder(prefabDir);
            EnsureFolder(defDir);

            string unitName = Sanitise(r.displayName);
            string prefabPath = $"{prefabDir}/{unitName}.prefab";
            string defPath = $"{defDir}/Enemy_{unitName}.asset";
            string ctrlPath = $"{ControllerDir}/{unitName}_Anim.controller";

            var created = new List<string>();
            GameObject root = null;
            try
            {
                // ---- definition: clone the template, overwrite identity ----
                var def = CloneDefinition(r.enemyTemplate, defPath, created);
                def.id = r.id;
                def.displayName = r.displayName;
                def.isAir = r.archetype == ForgeArchetype.Flier;
                if (def.isAir && def.flightAltitude <= 0f) def.flightAltitude = 4f;
                log.AppendLine($"Definition {defPath} cloned from {r.enemyTemplate.name}.");

                // ---- prefab: instantiate + unpack the source ----
                root = (GameObject)PrefabUtility.InstantiatePrefab(r.sourcePrefab);
                root.name = unitName;
                if (PrefabUtility.IsPartOfPrefabInstance(root))
                    PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                root.transform.position = Vector3.zero;
                root.transform.rotation = Quaternion.identity;
                if (!Mathf.Approximately(r.scale, 1f) && r.scale > 0f)
                    root.transform.localScale = Vector3.one * r.scale;

                if (r.bodyMaterialOverride != null)
                {
                    int retinted = 0;
                    foreach (var mr in root.GetComponentsInChildren<Renderer>(true))
                    {
                        mr.sharedMaterials = Enumerable.Repeat(r.bodyMaterialOverride, mr.sharedMaterials.Length).ToArray();
                        retinted++;
                    }
                    log.AppendLine($"Applied material override to {retinted} renderer(s).");
                }

                Bounds bounds = RenderBounds(root);

                // ---- muzzles: probe the hint names, else generate the fallback ----
                var muzzles = new List<Transform>();
                foreach (string marker in r.muzzleMarkerNames ?? new string[0])
                {
                    var t = FindDeep(root.transform, marker);
                    if (t != null && !muzzles.Contains(t)) muzzles.Add(t);
                }
                if (muzzles.Count == 0 && r.weaponDamage > 0f)
                {
                    var m = new GameObject("Muzzle_Forge");
                    m.transform.SetParent(root.transform, false);
                    m.transform.localPosition = new Vector3(0f, Mathf.Max(0.5f, bounds.center.y), bounds.extents.z + 0.4f);
                    muzzles.Add(m.transform);
                    log.AppendLine("No muzzle marker matched — generated a forward marker (the Colossus fallback).");
                }
                else if (muzzles.Count > 0)
                {
                    log.AppendLine($"Resolved {muzzles.Count} muzzle(s): {string.Join(", ", muzzles.Select(m => m.name))}.");
                }

                // ---- hit point: centre-of-mass target for turret aim ----
                var hitPoint = new GameObject("HitPoint").transform;
                hitPoint.SetParent(root.transform, false);
                hitPoint.localPosition = new Vector3(0f, Mathf.Max(0.6f, bounds.center.y), 0f);

                // ---- component stack (values mirror the definition for editor
                //      testing; spawn re-applies from the definition anyway) ----
                var enemy = root.GetComponent<Enemy>() ?? root.AddComponent<Enemy>();
                var enemySo = new SerializedObject(enemy);
                enemySo.FindProperty("maxHealth").floatValue = def.baseHealth;
                enemySo.FindProperty("bounty").intValue = def.bounty;
                enemySo.FindProperty("leakDamage").floatValue = def.leakDamage;
                enemySo.FindProperty("isAir").boolValue = def.isAir;
                enemySo.FindProperty("armourType").enumValueIndex = (int)def.armourType;
                enemySo.FindProperty("hitPoint").objectReferenceValue = hitPoint;
                enemySo.FindProperty("definition").objectReferenceValue = def;
                enemySo.ApplyModifiedPropertiesWithoutUndo();

                var mover = root.GetComponent<EnemyMover>() ?? root.AddComponent<EnemyMover>();
                var moverSo = new SerializedObject(mover);
                moverSo.FindProperty("moveSpeed").floatValue = def.moveSpeed;
                moverSo.FindProperty("turnRate").floatValue = r.turnRate;
                moverSo.FindProperty("bodyRadius").floatValue =
                    r.bodyRadius > 0f ? r.bodyRadius : Mathf.Max(0.4f, Mathf.Max(bounds.extents.x, bounds.extents.z));
                moverSo.FindProperty("definition").objectReferenceValue = def;
                moverSo.ApplyModifiedPropertiesWithoutUndo();

                if (r.weaponDamage > 0f)
                {
                    var weapon = root.GetComponent<EnemyWeapon>() ?? root.AddComponent<EnemyWeapon>();
                    var wSo = new SerializedObject(weapon);
                    var arr = wSo.FindProperty("weapons");
                    arr.arraySize = 1;
                    var w = arr.GetArrayElementAtIndex(0);
                    w.FindPropertyRelative("range").floatValue = r.weaponRange;
                    w.FindPropertyRelative("fireRate").floatValue = r.weaponFireRate;
                    w.FindPropertyRelative("damage").floatValue = r.weaponDamage;
                    w.FindPropertyRelative("tracerColor").colorValue = r.tracerColor;
                    w.FindPropertyRelative("muzzle").objectReferenceValue = muzzles.Count > 0 ? muzzles[0] : null;
                    var list = w.FindPropertyRelative("muzzles");
                    list.arraySize = muzzles.Count;
                    for (int i = 0; i < muzzles.Count; i++)
                        list.GetArrayElementAtIndex(i).objectReferenceValue = muzzles[i];
                    wSo.ApplyModifiedPropertiesWithoutUndo();
                    log.AppendLine($"Return fire: dmg {r.weaponDamage} @ {r.weaponRange} m, {r.weaponFireRate}/s.");
                }

                // ---- animator (Walker with clips) — the Colossus controller shape ----
                if (r.archetype == ForgeArchetype.Walker && r.walkClip != null)
                {
                    EnsureFolder(ControllerDir);
                    AssetDatabase.DeleteAsset(ctrlPath);
                    var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
                    created.Add(ctrlPath);
                    ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
                    ctrl.AddParameter("Die", AnimatorControllerParameterType.Trigger);
                    var sm = ctrl.layers[0].stateMachine;
                    var locomotion = sm.AddState("Locomotion");
                    locomotion.motion = r.walkClip;
                    sm.defaultState = locomotion;
                    var death = sm.AddState("Death");
                    death.motion = r.dieClip;
                    var toDeath = sm.AddAnyStateTransition(death);
                    toDeath.AddCondition(AnimatorConditionMode.If, 0f, "Die");
                    toDeath.hasExitTime = false;
                    toDeath.hasFixedDuration = true;
                    toDeath.duration = 0.05f;
                    toDeath.canTransitionToSelf = false;

                    var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                    animator.runtimeAnimatorController = ctrl;

                    var bridge = root.GetComponent<EnemyAnimatorBridge>() ?? root.AddComponent<EnemyAnimatorBridge>();
                    var bSo = new SerializedObject(bridge);
                    bSo.FindProperty("animator").objectReferenceValue = animator;
                    bSo.FindProperty("mover").objectReferenceValue = mover;
                    bSo.FindProperty("animatorClipSpeedRef").floatValue =
                        def.animatorClipSpeedRef > 0 ? def.animatorClipSpeedRef : def.moveSpeed;
                    bSo.FindProperty("definition").objectReferenceValue = def;
                    bSo.FindProperty("moveSpeedFallback").floatValue = def.moveSpeed;
                    bSo.ApplyModifiedPropertiesWithoutUndo();
                    log.AppendLine($"Animator controller {ctrlPath} (Walk + Die) + bridge wired.");
                }
                else if (r.archetype == ForgeArchetype.Walker)
                {
                    log.AppendLine("No clips assigned — unit ships mover-driven (no Animator), like the Shrike.");
                }

                AddBlobShadow(root, r.shadowDiameter > 0f
                    ? r.shadowDiameter
                    : Mathf.Max(1.2f, 2.2f * Mathf.Max(bounds.extents.x, bounds.extents.z)), log);

                // ---- save + link ----
                AssetDatabase.DeleteAsset(prefabPath);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                {
                    Discard(created, log);
                    log.AppendLine($"FAILED to save {prefabPath} — created assets were discarded.");
                    return log.ToString();
                }
                created.Add(prefabPath);
                def.prefab = prefab;
                EditorUtility.SetDirty(def);
                AssetDatabase.SaveAssets();
                log.AppendLine($"Prefab {prefabPath} saved and linked into the definition.");

                AppendVendorAudit(prefabPath, log);
                AppendModelRow(r, def, log);
                log.AppendLine("\nIcons: run Tools/COREHOLD/Art/Render Icons to bake/assign this unit's icon " +
                               "(whole-roster bake; it names anything it skips).");
                return log.ToString();
            }
            catch (System.Exception e)
            {
                Discard(created, log);
                log.AppendLine($"FORGE FAILED: {e.Message} — created assets were discarded.");
                return log.ToString();
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------ turrets

        /// <summary>
        /// CombatTurret path — NEW ground, not a generalization (no tower-chassis
        /// builder ever existed; chassis are human-authored). The forge's job is
        /// the definition + locator hygiene: clone the template's tiers, ensure
        /// the chassis carries Mount_Top/RangeOrigin, save a project-owned copy,
        /// wire basePrefab. Components are added at runtime by Tower.Build.
        /// </summary>
        private static string BuildTurret(CharacterRecipe r, StringBuilder log)
        {
            string prefabDir = string.IsNullOrEmpty(r.outputPrefabFolder) ? TowerPrefabDir : r.outputPrefabFolder;
            string defDir = string.IsNullOrEmpty(r.outputDefinitionFolder) ? TowerDefDir : r.outputDefinitionFolder;
            EnsureFolder(prefabDir);
            EnsureFolder(defDir);

            string unitName = Sanitise(r.displayName);
            string prefabPath = $"{prefabDir}/Tower_{unitName}.prefab";
            string defPath = $"{defDir}/Tower_{unitName}.asset";

            var created = new List<string>();
            GameObject root = null;
            try
            {
                var def = CloneDefinition(r.towerTemplate, defPath, created);
                def.id = r.id;
                def.displayName = r.displayName;
                def.menuOrder = r.menuOrder != 0 ? r.menuOrder : 1000; // 0 = end of the menu until authored
                log.AppendLine($"Definition {defPath} cloned from {r.towerTemplate.name} " +
                               $"(tiers, damage profile, costs carried over; menuOrder {def.menuOrder}).");

                root = (GameObject)PrefabUtility.InstantiatePrefab(r.sourcePrefab);
                root.name = $"Tower_{unitName}";
                if (PrefabUtility.IsPartOfPrefabInstance(root))
                    PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                root.transform.position = Vector3.zero;
                if (!Mathf.Approximately(r.scale, 1f) && r.scale > 0f)
                    root.transform.localScale = Vector3.one * r.scale;

                // Locator hygiene: the runtime turret stack mounts and measures
                // from these two, so a chassis without them is not buildable.
                if (FindDeep(root.transform, "Mount_Top") == null)
                {
                    Bounds b = RenderBounds(root);
                    var mount = new GameObject("Mount_Top");
                    mount.transform.SetParent(root.transform, false);
                    mount.transform.localPosition = new Vector3(0f, b.size.y, 0f);
                    log.AppendLine($"Added Mount_Top at y={b.size.y:0.##} (top of chassis) — reposition in the prefab if the weapon sits wrong.");
                }
                if (FindDeep(root.transform, "RangeOrigin") == null)
                {
                    var ro = new GameObject("RangeOrigin");
                    ro.transform.SetParent(root.transform, false);
                    ro.transform.localPosition = Vector3.zero;
                    log.AppendLine("Added RangeOrigin at the base.");
                }

                AssetDatabase.DeleteAsset(prefabPath);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                {
                    Discard(created, log);
                    log.AppendLine($"FAILED to save {prefabPath} — created assets were discarded.");
                    return log.ToString();
                }
                created.Add(prefabPath);
                def.basePrefab = prefab;
                EditorUtility.SetDirty(def);
                AssetDatabase.SaveAssets();
                log.AppendLine($"Chassis {prefabPath} saved and wired as basePrefab.");

                AppendVendorAudit(prefabPath, log);
                log.AppendLine("\nNext: re-run Tools/COREHOLD/Scene Setup/Build Real UI so the build menu " +
                               "picks the turret up (registry-ordered), then Render Icons. The balance model's " +
                               "TOWERS dict is hand-maintained — add this turret's stats before trusting Gate 3 " +
                               "on maps where it matters.");
                return log.ToString();
            }
            catch (System.Exception e)
            {
                Discard(created, log);
                log.AppendLine($"FORGE FAILED: {e.Message} — created assets were discarded.");
                return log.ToString();
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------ helpers

        private static T CloneDefinition<T>(T template, string path, List<string> created) where T : ScriptableObject
        {
            AssetDatabase.DeleteAsset(path);
            var clone = Object.Instantiate(template);
            clone.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(clone, path);
            created.Add(path);
            return clone;
        }

        private static void Discard(List<string> created, StringBuilder log)
        {
            // Emit-nothing-on-failure (the generation pipeline's rule): a unit
            // either exists whole or not at all.
            foreach (var path in created)
                if (AssetDatabase.DeleteAsset(path))
                    log.AppendLine($"  discarded {path}");
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

        private static Bounds RenderBounds(GameObject go)
        {
            var bounds = new Bounds(go.transform.position, Vector3.one);
            bool has = false;
            foreach (var mr in go.GetComponentsInChildren<Renderer>(true))
            {
                if (mr is ParticleSystemRenderer) continue;
                if (!has) { bounds = mr.bounds; has = true; }
                else bounds.Encapsulate(mr.bounds);
            }
            return bounds;
        }

        private static void AddBlobShadow(GameObject root, float diameter, StringBuilder log)
        {
            if (FindDeep(root.transform, "BlobShadow") != null) return;

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

            string matGuid = AssetDatabase.FindAssets("BlobShadow t:Material").FirstOrDefault();
            if (matGuid != null)
                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(matGuid));
            else
                log.AppendLine("WARN: no BlobShadow material found in the project — assign one on the shadow quad.");

            var bs = shadow.AddComponent<BlobShadow>();
            var so = new SerializedObject(bs);
            var d = so.FindProperty("diameter");
            if (d != null) d.floatValue = diameter;
            so.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine($"BlobShadow added (diameter {diameter:0.##} m).");
        }

        /// <summary>
        /// Every GUID the saved prefab references that resolves to NOTHING in
        /// this repo — i.e. vendor (git-ignored) or missing content. This is the
        /// fresh-clone exposure made visible per unit: those references dangle
        /// anywhere the vendor pack is absent, and the unit spawns invisible.
        /// </summary>
        private static void AppendVendorAudit(string prefabPath, StringBuilder log)
        {
            var text = System.IO.File.ReadAllText(prefabPath);
            var unresolved = new HashSet<string>();
            foreach (Match m in Regex.Matches(text, @"guid: ([0-9a-f]{32})"))
            {
                string guid = m.Groups[1].Value;
                if (guid == "0000000000000000f000000000000000" || guid == "0000000000000000e000000000000000")
                    continue; // built-in resources
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    unresolved.Add(guid);
                else if (path.StartsWith("Assets/Vendor/"))
                    unresolved.Add($"{guid} ({path})");
            }
            log.AppendLine(unresolved.Count == 0
                ? "Dependency audit: fully self-contained — safe on a fresh clone."
                : $"Dependency audit: {unresolved.Count} out-of-repo reference(s) — this unit needs the vendor " +
                  $"pack present to render:\n    {string.Join("\n    ", unresolved.Take(12))}");
        }

        /// <summary>
        /// The balance model's ENEMIES table is hand-maintained BY DESIGN, and
        /// Gate 3 simulates the .py's own roster — a forged enemy is invisible
        /// to the gates until its row exists. Write the exact row so adding it
        /// is a paste, not a derivation.
        /// </summary>
        private static void AppendModelRow(CharacterRecipe r, EnemyDefinition def, StringBuilder log)
        {
            // The row must match the model's ACTUAL schema (dict(hp=…, armour=INT,
            // …)): armour is an index into DAMAGE_MULT, the altitude key is
            // "altitude", and leak is part of every row. The first version of this
            // printed a row that raised TypeError on paste — the audit caught it.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string air = def.isAir
                ? string.Format(inv, ", air=True, altitude={0:0.0#}", Mathf.Max(0.1f, def.flightAltitude))
                : ", air=False";
            log.AppendLine("\nBalance model — paste into docs/balance_model.py ENEMIES before using this unit in waves " +
                           "(without it, wave synthesis refuses the roster and --waves exits with the missing id):");
            log.AppendLine(string.Format(inv,
                "    \"{0}\": dict(hp={1:0.#}, armour={2}, speed={3:0.##}, bounty={4}, leak={5}{6}),",
                def.id, def.baseHealth, (int)def.armourType, def.moveSpeed, def.bounty, def.leakDamage, air));
        }

        private static string Sanitise(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "Unit";
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
