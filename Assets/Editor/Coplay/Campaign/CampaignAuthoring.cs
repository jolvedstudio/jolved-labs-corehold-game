using System;
using System.Collections.Generic;
using Corehold.Data;
using UnityEngine;

namespace CoreholdEditor.Campaign
{
    /// <summary>
    /// The EDITOR side of a campaign (plan v2 §A.2): what the designer edits.
    /// Holds blueprint references and generation bookkeeping — things that must
    /// never ship — and is compiled into the editor assembly so a build cannot
    /// even reference it. The Campaign Builder turns this into the runtime
    /// <see cref="CampaignManifest"/>, which carries only kinds, paths and text.
    /// </summary>
    [CreateAssetMenu(menuName = "COREHOLD/Campaign Authoring (editor)", fileName = "CampaignAuthoring_")]
    public class CampaignAuthoring : ScriptableObject
    {
        [Tooltip("Save-key namespace and output folder name, e.g. 'main'.")]
        public string campaignId = "main";
        public string displayName = "COREHOLD";

        [Tooltip("Master seed — every stage's generation seed derives from it via Fnv1a(seed, \"level_<i>\") unless the stage overrides.")]
        public int masterSeed = 1;

        public ProgressionRules progression = new ProgressionRules
        {
            economyCarry = ProgressionRules.EconomyCarry.ResetPerLevel,
        };

        [Tooltip("The campaign's UI identity — palette + font baked into every generated scene and both " +
                 "menu scenes. Null = the shipped look. Change it, then re-generate: the skin is applied " +
                 "at BUILD time, existing scenes keep whatever they were baked with.")]
        public UISkin uiSkin;

        [Tooltip("Part C: synthesize each stage's waves from this recipe (roster + intensity curve, " +
                 "seeded per stage, escalating per stage, re-certified by the balance model against the " +
                 "generated map). Null = clone the shipped wave tables per stage, as before.")]
        public WaveRecipe waveRecipe;

        [Tooltip("Menu scenes. Defaults are the stub-builder outputs; A1 reuses them.")]
        public string welcomeScenePath = BuildCampaignScenes.WelcomePath;
        public string closingScenePath = BuildCampaignScenes.ClosingPath;

        public List<AuthoredStage> stages = new List<AuthoredStage>();

        [Serializable]
        public class AuthoredStage
        {
            public string title = "Operation";
            [TextArea] public string briefing = "";

            [Tooltip("How many turrets (menu order, buildable only) this stage's roster offers. " +
                     "0 = the full roster. 3 on stage one and +1 per stage is the PvZ-style " +
                     "introduction curve (R-UI-2); the rail slides the newcomer in with a NEW tag.")]
            public int rosterCount;

            [Tooltip("The recipe this stage's map is generated from.")]
            public LevelBlueprint blueprint;

            [Tooltip("0 = derive from the master seed. Set from a contact-sheet pick to choose the map's shape by eye.")]
            public int seedOverride;

            [Tooltip("Optional wave recipe FOR THIS STAGE ONLY, overriding the campaign's. Semantics " +
                     "differ deliberately: the campaign recipe is a PROGRAMME (its escalationPerStage " +
                     "compounds by stage position), while a stage override is BESPOKE — evaluated " +
                     "exactly as authored, no positional escalation, so what you tuned is what plays. " +
                     "Still seeded per stage and re-certified by the balance model. Null = campaign's.")]
            public WaveRecipe waveRecipe;

            // ---- Generation bookkeeping (written by Generate) ----
            [Tooltip("The seed that actually passed the gates last generation (0 = never generated).")]
            public int acceptedSeed;
            public string scenePath;      // committed campaign copy, Scenes/Campaign/<id>/
            public string levelDefPath;   // committed LevelDefinition, Data/Levels/Campaign/<id>/
            public string wavesFolder;    // this stage's own WaveDefinition assets (cloned or synthesized)
        }

        /// <summary>Where this campaign's committed scenes live (decision D1).</summary>
        public string SceneFolder => $"{BuildCampaignScenes.SceneDir}/{campaignId}";
        public string DataFolder => $"Assets/_COREHOLD/Data/Levels/Campaign/{campaignId}";
        public string ManifestAssetPath => $"{BuildCampaignScenes.ManifestDir}/Manifest_{campaignId}.asset";
    }
}
