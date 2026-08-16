using System;
using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Data
{
    public enum CampaignStageKind { Welcome, Level, Closing }

    /// <summary>
    /// How progression carries between campaign levels. A0 implements
    /// ResetPerLevel ONLY — the balance model solves every level against its
    /// own starting economy, so reset is the one mode the generation gates
    /// certify truthfully. Carry modes ship with the model's
    /// --starting-salvage extension (plan v2 §A.6 phase 2); until then the
    /// manager falls back to reset and says so.
    /// </summary>
    [Serializable]
    public struct ProgressionRules
    {
        public enum EconomyCarry { ResetPerLevel, CarryFraction, CarryFull }

        public EconomyCarry economyCarry;
        [Range(0f, 1f)] public float salvageKeepFraction;
        public bool carryIntegrity;
        public int integrityHealPerLevel;
        public int baseSalvagePerLevel;
    }

    /// <summary>One stage of a campaign, as the runtime needs it: a kind, a
    /// scene path resolvable through Build Settings, and display text.</summary>
    [Serializable]
    public class CampaignStageInfo
    {
        public CampaignStageKind kind = CampaignStageKind.Level;
        public string title;
        [TextArea] public string briefing;

        [Tooltip("Scene path as registered in Build Settings, e.g. Assets/_COREHOLD/Scenes/Generated/RockyDesert_s990168.unity")]
        public string scenePath;

        [Tooltip("The accepted generation seed for this stage (informational at runtime; the Campaign Builder is its source of truth).")]
        public int seed;
    }

    /// <summary>
    /// The runtime face of a campaign — the ONLY campaign asset runtime code
    /// references. Holds stage kinds, scene paths, text and rules; never
    /// blueprints, recipes or SceneAssets, so referencing it from the Welcome
    /// scene pulls nothing of the authoring graph into a build (plan v2 §A.2).
    /// Authored by hand for now; emitted by the Campaign Builder from A1 on.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Campaign Manifest", fileName = "Campaign_")]
    public class CampaignManifest : ScriptableObject
    {
        [Tooltip("Save-key namespace, e.g. 'main'. Keys become corehold.campaign.<id>.*")]
        public string campaignId = "main";
        public string displayName = "COREHOLD";

        public ProgressionRules progression;

        public List<CampaignStageInfo> stages = new List<CampaignStageInfo>();

        // ---- Lookup helpers (linear scans; stage counts are tiny) ----

        public int FirstLevelIndex()
        {
            for (int i = 0; i < stages.Count; i++)
                if (stages[i].kind == CampaignStageKind.Level) return i;
            return -1;
        }

        /// <summary>Index of the next Level stage after <paramref name="index"/>, or -1.</summary>
        public int NextLevelIndex(int index)
        {
            for (int i = index + 1; i < stages.Count; i++)
                if (stages[i].kind == CampaignStageKind.Level) return i;
            return -1;
        }

        public CampaignStageInfo StageOfKind(CampaignStageKind kind)
        {
            for (int i = 0; i < stages.Count; i++)
                if (stages[i].kind == kind) return stages[i];
            return null;
        }

        public int LevelCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < stages.Count; i++)
                    if (stages[i].kind == CampaignStageKind.Level) n++;
                return n;
            }
        }

        /// <summary>1-based position of a Level stage among Level stages (for "LEVEL 2/10" UI).</summary>
        public int LevelNumberOf(int index)
        {
            int n = 0;
            for (int i = 0; i <= index && i < stages.Count; i++)
                if (stages[i].kind == CampaignStageKind.Level) n++;
            return n;
        }
    }
}
