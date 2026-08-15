using Corehold.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Corehold.UI
{
    /// <summary>
    /// The Settings panel (Welcome &amp; Settings screen): three volume sliders
    /// (master / SFX / music), the camera-shake accessibility switch and the
    /// night-lighting preference. Everything applies LIVE through the owning
    /// system (AudioDirector properties, CameraShake's SaveData gate,
    /// NightVariant when the scene has one) and persists through
    /// <see cref="SaveData"/>, so preferences survive sessions and apply at
    /// boot even on scenes where this panel never opens.
    ///
    /// Built and wired by BuildRealUI; opened from the title screen's SETTINGS
    /// button. Hidden by default.
    /// </summary>
    [DisallowMultipleComponent]
    public class SettingsPanel : MonoBehaviour
    {
        [SerializeField] private GameObject root;
        [SerializeField] private Slider masterSlider;
        [SerializeField] private Slider sfxSlider;
        [SerializeField] private Slider musicSlider;
        [SerializeField] private Button shakeButton;
        [SerializeField] private TMP_Text shakeLabel;
        [SerializeField] private Button nightButton;
        [SerializeField] private TMP_Text nightLabel;
        [SerializeField] private Button closeButton;

        private bool _syncing;

        private void OnEnable()
        {
            if (masterSlider != null) masterSlider.onValueChanged.AddListener(OnMaster);
            if (sfxSlider != null) sfxSlider.onValueChanged.AddListener(OnSfx);
            if (musicSlider != null) musicSlider.onValueChanged.AddListener(OnMusic);
            if (shakeButton != null) shakeButton.onClick.AddListener(ToggleShake);
            if (nightButton != null) nightButton.onClick.AddListener(ToggleNight);
            if (closeButton != null) closeButton.onClick.AddListener(Hide);
        }

        private void OnDisable()
        {
            if (masterSlider != null) masterSlider.onValueChanged.RemoveListener(OnMaster);
            if (sfxSlider != null) sfxSlider.onValueChanged.RemoveListener(OnSfx);
            if (musicSlider != null) musicSlider.onValueChanged.RemoveListener(OnMusic);
            if (shakeButton != null) shakeButton.onClick.RemoveListener(ToggleShake);
            if (nightButton != null) nightButton.onClick.RemoveListener(ToggleNight);
            if (closeButton != null) closeButton.onClick.RemoveListener(Hide);
        }

        /// <summary>Open the panel, syncing every control from live/persisted state.</summary>
        public void Show()
        {
            if (root != null)
            {
                // Above whatever opened it (title screen, later pause) regardless
                // of the order the builder created the canvases' children in.
                root.transform.SetAsLastSibling();
                root.SetActive(true);
            }

            _syncing = true;
            var audio = AudioDirector.Instance;
            if (masterSlider != null)
                masterSlider.value = audio != null ? audio.MasterVolume : Persisted("master", 1f);
            if (sfxSlider != null)
                sfxSlider.value = audio != null ? audio.SfxVolume : Persisted("sfx", 0.9f);
            if (musicSlider != null)
                musicSlider.value = audio != null ? audio.MusicVolume : Persisted("music", 0.6f);
            _syncing = false;

            RefreshToggles();
        }

        public void Hide()
        {
            if (AudioDirector.Instance != null)
                AudioDirector.Instance.PlayUIClick();
            if (root != null)
                root.SetActive(false);
        }

        private static float Persisted(string channel, float fallback)
        {
            float v = SaveData.GetVolume(channel);
            return v >= 0f ? v : fallback;
        }

        private void OnMaster(float v)
        {
            if (_syncing) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.MasterVolume = v;
            SaveData.SetVolume("master", v);
        }

        private void OnSfx(float v)
        {
            if (_syncing) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.SfxVolume = v;
            SaveData.SetVolume("sfx", v);
        }

        private void OnMusic(float v)
        {
            if (_syncing) return;
            if (AudioDirector.Instance != null) AudioDirector.Instance.MusicVolume = v;
            SaveData.SetVolume("music", v);
        }

        private void ToggleShake()
        {
            SaveData.ShakeEnabled = !SaveData.ShakeEnabled;
            RefreshToggles();
            // Immediate, felt feedback when turning it ON.
            if (SaveData.ShakeEnabled && CameraShake.Instance != null)
                CameraShake.Instance.Shake();
        }

        private void ToggleNight()
        {
            SaveData.NightPreferred = !SaveData.NightPreferred;
            RefreshToggles();
            // Apply live when the current scene carries the rig (in-game settings
            // access later; on the title there is nothing to relight).
            if (NightVariant.Instance != null)
                NightVariant.Instance.SetNight(SaveData.NightPreferred);
        }

        private void RefreshToggles()
        {
            if (shakeLabel != null)
                shakeLabel.text = SaveData.ShakeEnabled ? "SCREEN SHAKE: ON" : "SCREEN SHAKE: OFF";
            if (nightLabel != null)
                nightLabel.text = SaveData.NightPreferred ? "NIGHT MODE: ON" : "NIGHT MODE: OFF";
        }
    }
}
