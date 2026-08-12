using System.Collections.Generic;
using System.Linq;
using System.Text;
using Corehold.Enemies;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Ticket 25 — Animation wiring (GDD §6.3).
/// Builds a 4-state Animator Controller per enemy (Locomotion, Death; plus Roll
/// and Transform for the Roller), disables root motion, sets Cull Completely +
/// updateWhenOffscreen off, adds EnemyAnimatorBridge, and calibrates the
/// foot-slide correction reference.
/// </summary>
public static class EnemyAnimSetup
{
    const string CtrlDir = "Assets/_COREHOLD/Art/AnimatorControllers";

    class EnemyCfg
    {
        public string prefab;
        public string locomotion;   // clip path :: clip name
        public string death;
        public string roll;         // roller only
        public string transform;    // roller only
        public float moveSpeed;     // GDD §6.1 design speed == clip speed ref baseline
    }

    static string C(string path, string clip) => path + "::" + clip;

    static readonly EnemyCfg[] Configs =
    {
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Scuttler.prefab",
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Lt@Legs_Spider_Lt_Walk.fbx", "Legs_Spider_Lt_Walk"),
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Lt@Legs_Spider_Lt_Death.fbx", "Legs_Spider_Lt_Death"),
            moveSpeed = 7.5f,
        },
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Strider.prefab",
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Hvy@Legs_Spider_Hvy_Walk.fbx", "Legs_Spider_Hvy_Walk"),
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Hvy@Legs_Spider_Hvy_Death.fbx", "Legs_Spider_Hvy_Death"),
            moveSpeed = 5.0f,
        },
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Lancer.prefab",
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Tracks_Lvl1@Legs_Tracks_Lvl1_Roll.fbx", "Legs_Tracks_Lvl1_Roll"),
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Tracks_Lvl1@Legs_Tracks_Lvl1_Death.fbx", "Legs_Tracks_Lvl1_Death"),
            moveSpeed = 4.6f,
        },
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Wasp.prefab",
            // Wasp uses the Legs_Spider_Lt rig (transformer legs, ROOT/Pelvis/FL_Upper_Leg...),
            // so the spider-Lt walk/death clips match its bones, not the drone clips.
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Lt@Legs_Spider_Lt_Walk.fbx", "Legs_Spider_Lt_Walk"),
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Legs_Spider_Lt@Legs_Spider_Lt_Death.fbx", "Legs_Spider_Lt_Death"),
            moveSpeed = 9.0f,
        },
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Roller.prefab",
            // Buggy chassis rig: Forward is both roll-phase and walk-phase locomotion.
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Buggy_Chassis_Lvl1@Buggy_Chassis_Lvl1_Forward.fbx", "Buggy_Chassis_Lvl1_Forward"),
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Buggy_Chassis_Lvl1@Buggy_Chassis_Lvl1_Death.fbx", "Buggy_Chassis_Lvl1_Death"),
            roll       = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Buggy_Chassis_Lvl1@Buggy_Chassis_Lvl1_Forward.fbx", "Buggy_Chassis_Lvl1_Forward"),
            transform  = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Buggy_Chassis_Lvl1@Buggy_Chassis_Lvl1_Idle.fbx", "Buggy_Chassis_Lvl1_Idle"),
            moveSpeed = 11.0f,
        },
        new EnemyCfg {
            prefab = "Assets/_COREHOLD/Prefabs/Enemies/Breaker.prefab",
            locomotion = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Chassis_Tank_Anim_ROOT@Tank_Anim_Drive_Forward.fbx", "Tank_Anim_Drive_Forward"),
            // The tank chassis ships no death clip; fall back to Idle held on the last frame.
            death      = C("Assets/Vendor/Mech_Constructor_Spiders/Animations/Chassis_Tank_Anim_ROOT@Tank_Anim_Idle.fbx", "Tank_Anim_Idle"),
            moveSpeed = 3.75f,
        },
    };

    static AnimationClip Load(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        var parts = spec.Split(new[] { "::" }, System.StringSplitOptions.None);
        var path = parts[0];
        var name = parts.Length > 1 ? parts[1] : null;
        var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview")).ToArray();
        if (name != null)
        {
            var m = clips.FirstOrDefault(c => c.name == name);
            if (m != null) return m;
        }
        return clips.FirstOrDefault();
    }

    public static string Execute()
    {
        var sb = new StringBuilder();
        if (!AssetDatabase.IsValidFolder(CtrlDir))
        {
            System.IO.Directory.CreateDirectory(CtrlDir);
            AssetDatabase.Refresh();
        }

        foreach (var cfg in Configs)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(cfg.prefab);
            if (go == null) { sb.AppendLine($"MISSING prefab {cfg.prefab}"); continue; }
            string name = go.name;

            var loco = Load(cfg.locomotion);
            var death = Load(cfg.death);
            var roll = Load(cfg.roll);
            var transf = Load(cfg.transform);
            if (loco == null || death == null)
            {
                sb.AppendLine($"{name}: missing clips (loco={loco != null}, death={death != null}) — SKIPPED");
                continue;
            }

            bool isRoller = roll != null && transf != null;

            // ---- Build the controller ----
            string ctrlPath = $"{CtrlDir}/{name}_Anim.controller";
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Die", AnimatorControllerParameterType.Trigger);
            if (isRoller)
            {
                ctrl.AddParameter("Roll", AnimatorControllerParameterType.Bool);
                ctrl.AddParameter("Transform", AnimatorControllerParameterType.Trigger);
            }

            var sm = ctrl.layers[0].stateMachine;

            var locomotion = sm.AddState("Locomotion");
            locomotion.motion = loco;
            sm.defaultState = locomotion;

            var deathState = sm.AddState("Death");
            deathState.motion = death;

            // Any State -> Death on Die trigger.
            var toDeath = sm.AddAnyStateTransition(deathState);
            toDeath.AddCondition(AnimatorConditionMode.If, 0, "Die");
            toDeath.duration = 0.05f;
            toDeath.hasExitTime = false;
            toDeath.canTransitionToSelf = false;

            if (isRoller)
            {
                var rollState = sm.AddState("Roll");
                rollState.motion = roll;
                var transformState = sm.AddState("Transform");
                transformState.motion = transf;

                // Locomotion(walk) <-> Roll via Roll bool.
                var toRoll = locomotion.AddTransition(rollState);
                toRoll.AddCondition(AnimatorConditionMode.If, 0, "Roll");
                toRoll.hasExitTime = false; toRoll.duration = 0.1f;

                // Roll -> Transform on Transform trigger (the unpack), then to Locomotion.
                var rollToTransform = rollState.AddTransition(transformState);
                rollToTransform.AddCondition(AnimatorConditionMode.If, 0, "Transform");
                rollToTransform.hasExitTime = false; rollToTransform.duration = 0.05f;

                var transformToLoco = transformState.AddTransition(locomotion);
                transformToLoco.hasExitTime = true; transformToLoco.exitTime = 0.95f;
                transformToLoco.duration = 0.1f;

                // Roller starts in Roll form (fast phase), so make Roll the default.
                sm.defaultState = rollState;
            }

            EditorUtility.SetDirty(ctrl);

            // ---- Apply to the prefab ----
            using (var edit = new PrefabEditScope(cfg.prefab))
            {
                var root = edit.Root;
                var animator = root.GetComponentInChildren<Animator>(true);
                if (animator == null)
                {
                    sb.AppendLine($"{name}: NO Animator — SKIPPED");
                    continue;
                }

                animator.runtimeAnimatorController = ctrl;
                animator.applyRootMotion = false;                       // GDD §6.3
                animator.cullingMode = AnimatorCullingMode.CullCompletely; // GDD §6.3

                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    smr.updateWhenOffscreen = false;                    // GDD §6.3

                // EnemyAnimatorBridge
                var bridge = root.GetComponent<EnemyAnimatorBridge>();
                if (bridge == null) bridge = root.AddComponent<EnemyAnimatorBridge>();

                var so = new SerializedObject(bridge);
                so.FindProperty("animator").objectReferenceValue = animator;
                so.FindProperty("mover").objectReferenceValue = root.GetComponent<EnemyMover>();
                // Calibrate foot-slide correction: in-place clips have no root
                // translation, so use the design move speed as the reference. At
                // nominal speed Animator.speed = 1.0 (clip plays as authored) and
                // any speed change scales the leg cadence proportionally (GDD §6.3).
                so.FindProperty("animatorClipSpeedRef").floatValue = cfg.moveSpeed;
                so.FindProperty("moveSpeedFallback").floatValue = cfg.moveSpeed;
                so.ApplyModifiedPropertiesWithoutUndo();

                // Death display duration from the death clip length.
                var enemy = root.GetComponent<Enemy>();
                if (enemy != null)
                {
                    var eso = new SerializedObject(enemy);
                    eso.FindProperty("deathAnimDuration").floatValue = Mathf.Max(0.35f, death.length);
                    eso.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            sb.AppendLine($"{name}: controller={System.IO.Path.GetFileName(ctrlPath)} loco='{loco.name}' death='{death.name}'{(isRoller ? $" roll='{roll.name}' transform='{transf.name}'" : "")} clipSpeedRef={cfg.moveSpeed} deathDur={Mathf.Max(0.35f, death.length):0.00}s");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[COREHOLD] Enemy animation setup complete.\n" + sb);
        return sb.ToString();
    }

    /// <summary>Loads a prefab contents, edits, saves and unloads it.</summary>
    class PrefabEditScope : System.IDisposable
    {
        readonly string _path;
        public GameObject Root { get; }
        public PrefabEditScope(string path)
        {
            _path = path;
            Root = PrefabUtility.LoadPrefabContents(path);
        }
        public void Dispose()
        {
            PrefabUtility.SaveAsPrefabAsset(Root, _path);
            PrefabUtility.UnloadPrefabContents(Root);
        }
    }
}
