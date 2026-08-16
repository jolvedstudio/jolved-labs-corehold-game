using Corehold.Core;
using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// The campaign Closing screen (plan v2 §A.5): total score, the per-level
    /// star strip, Play Again / Welcome. Reads everything from the live
    /// <see cref="CampaignManager"/> — the manifest stays Active while this
    /// scene shows, so Play Again can restart the same campaign at the same
    /// difficulty. Campaign best-score persistence lands with the A1 keys.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClosingScreen : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] private TMP_Text titleLabel;
        [SerializeField] private TMP_Text bodyLabel;
        [SerializeField] private TMP_Text scoreLabel;
        [SerializeField] private Button playAgainButton;
        [SerializeField] private Button welcomeButton;

        private void OnEnable()
        {
            if (playAgainButton != null) playAgainButton.onClick.AddListener(PlayAgain);
            if (welcomeButton != null) welcomeButton.onClick.AddListener(ToWelcome);
            Refresh();
        }

        private void OnDisable()
        {
            if (playAgainButton != null) playAgainButton.onClick.RemoveListener(PlayAgain);
            if (welcomeButton != null) welcomeButton.onClick.RemoveListener(ToWelcome);
        }

        private void Refresh()
        {
            var campaign = CampaignManager.Instance;
            bool hasRun = campaign != null && campaign.HasActiveCampaign;

            if (titleLabel != null)
                titleLabel.text = hasRun ? $"{campaign.Active.displayName} — COMPLETE" : "CAMPAIGN COMPLETE";

            if (bodyLabel != null)
            {
                if (hasRun)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < campaign.Results.Count; i++)
                    {
                        var r = campaign.Results[i];
                        string starStrip = r == null ? "---" :
                            new string('★', Mathf.Clamp(r.stars, 0, 3)) + new string('☆', 3 - Mathf.Clamp(r.stars, 0, 3));
                        string name = r != null && !string.IsNullOrEmpty(r.title) ? r.title : $"Level {i + 1}";
                        sb.AppendLine($"{name}   {starStrip}   {(r != null ? r.score : 0)}");
                    }
                    bodyLabel.text = sb.ToString();
                }
                else
                {
                    bodyLabel.text = "No campaign run in memory.\n(Started this scene directly? Begin from the Welcome scene.)";
                }
            }

            if (scoreLabel != null)
                scoreLabel.text = hasRun ? $"TOTAL SCORE {campaign.CumulativeScore}" : "";
        }

        private void PlayAgain()
        {
            var campaign = CampaignManager.Instance;
            if (campaign == null || !campaign.HasActiveCampaign) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            campaign.StartCampaign(campaign.Active, campaign.ChosenDifficulty);
        }

        private void ToWelcome()
        {
            var campaign = CampaignManager.Instance;
            if (AudioDirector.Instance != null) AudioDirector.Instance.PlayUIClick();
            if (campaign != null) campaign.AbandonToWelcome();
            else GameFlow.RestartCurrentLevel();
        }
    }
}
