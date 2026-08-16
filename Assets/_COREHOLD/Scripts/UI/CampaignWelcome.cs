using Corehold.Core;
using Corehold.Data;
using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
// Corehold.Data also declares a (legacy, unused) Difficulty enum; the game's
// live one is Corehold.Core's. Pin it so the bare name cannot go ambiguous.
using Difficulty = Corehold.Core.Difficulty;

namespace Corehold.UI
{
    /// <summary>
    /// The campaign Welcome screen (plan v2 §A.5): the ONE difficulty choice for
    /// the whole run, and the WebGL audio-unlock gesture — level scenes started
    /// by the campaign never show their own title, so this tap is the only user
    /// gesture before gameplay (mirrors <see cref="TitleScreen"/>'s gate).
    ///
    /// Lives in a dedicated menu scene built by
    /// Tools → COREHOLD → Campaign → Build Welcome + Closing Scenes (stub).
    /// References a <see cref="CampaignManifest"/> — the runtime-only campaign
    /// asset — never blueprints or authoring data.
    /// </summary>
    [DisallowMultipleComponent]
    public class CampaignWelcome : MonoBehaviour
    {
        [Header("Campaign")]
        [SerializeField] private CampaignManifest manifest;

        /// <summary>The campaign this screen starts. Read-only outside the
        /// inspector; the debug console uses it to name the save keys to wipe.</summary>
        public CampaignManifest Manifest => manifest;

        [Header("Layout")]
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text subtitleLabel;

        [Header("Difficulty buttons")]
        [SerializeField] private Button normalButton;
        [SerializeField] private Button veteranButton;
        [SerializeField] private Button nightmareButton;

        [Header("Resume")]
        [SerializeField] private Button continueButton;

        private void OnEnable()
        {
            if (normalButton != null) normalButton.onClick.AddListener(() => Begin(Difficulty.Normal));
            if (veteranButton != null) veteranButton.onClick.AddListener(() => Begin(Difficulty.Veteran));
            if (nightmareButton != null) nightmareButton.onClick.AddListener(() => Begin(Difficulty.Nightmare));
            if (continueButton != null) continueButton.onClick.AddListener(Resume);
            Refresh();
        }

        private void OnDisable()
        {
            if (normalButton != null) normalButton.onClick.RemoveAllListeners();
            if (veteranButton != null) veteranButton.onClick.RemoveAllListeners();
            if (nightmareButton != null) nightmareButton.onClick.RemoveAllListeners();
            if (continueButton != null) continueButton.onClick.RemoveListener(Resume);
        }

        private void Refresh()
        {
            if (titleLabel != null && manifest != null)
                titleLabel.text = manifest.displayName;

            if (subtitleLabel != null)
            {
                if (manifest == null)
                {
                    subtitleLabel.text = "NO CAMPAIGN MANIFEST ASSIGNED";
                }
                else
                {
                    int best = SaveData.GetCampaignBestScore(manifest.campaignId);
                    subtitleLabel.text = best > 0
                        ? $"CAMPAIGN — {manifest.LevelCount} LEVELS   BEST {best}"
                        : $"CAMPAIGN — {manifest.LevelCount} LEVELS";
                }
            }

            // Same unlock gating as the single-map title screen.
            if (veteranButton != null) veteranButton.interactable = SaveData.IsUnlocked(Difficulty.Veteran);
            if (nightmareButton != null) nightmareButton.interactable = SaveData.IsUnlocked(Difficulty.Nightmare);

            // Resume appears only while a persisted, still-valid run exists.
            if (continueButton != null)
                continueButton.gameObject.SetActive(CampaignManager.HasSavedRun(manifest));
        }

        private void Resume()
        {
            if (manifest == null) return;

            // Same audio-gate gesture as Begin — a resumed campaign still needs
            // the browser unlock from THIS page session.
            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = SaveData.Muted;
                AudioDirector.Instance.PlayUIClick();
            }

            if (!CampaignManager.EnsureExists().TryResumeCampaign(manifest))
                Refresh(); // blob was stale and got discarded — hide the button
        }

        private void Begin(Difficulty difficulty)
        {
            if (manifest == null)
            {
                Debug.LogError("[CampaignWelcome] No CampaignManifest assigned — nothing to start.");
                return;
            }
            if (!SaveData.IsUnlocked(difficulty))
                return;

            // The audio gate: this tap is the user gesture that unlocks Web Audio
            // for the whole campaign (levels start their own music without a tap).
            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = SaveData.Muted;
                AudioDirector.Instance.PlayUIClick();
            }

            CampaignManager.EnsureExists().StartCampaign(manifest, difficulty);
        }
    }
}
