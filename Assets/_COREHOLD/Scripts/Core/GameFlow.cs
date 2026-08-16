using System.Collections;
using Corehold.Systems;
using Corehold.Towers;
using Corehold.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Corehold.Core
{
    /// <summary>
    /// Ties the real UI to the game loop (GDD §3.2, §9.1). Replaces the old
    /// IMGUI-driven <c>GameBootstrap</c> flow:
    ///
    ///   • On load the game sits in <see cref="GameState.Title"/> with the title
    ///     screen up. Nothing spawns and no audio plays (the audio gate — GDD §9.1).
    ///   • When the player picks a difficulty on the title screen, this applies it
    ///     to the <see cref="GameManager"/>, hides the title and moves to Build.
    ///   • The HUD's Start Wave button drives every wave from there; there is no
    ///     auto-start.
    ///
    /// It also does the minimal scene prep the old bootstrap did: put hardpoints on
    /// the Hardpoint layer with a tap collider and a turret mount, and ensure an
    /// InputRouter exists.
    /// </summary>
    [DisallowMultipleComponent]
    public class GameFlow : MonoBehaviour
    {
        [Header("Wiring (auto-found if null)")]
        [SerializeField] private TitleScreen titleScreen;
        [SerializeField] private WaveManager waveManager;

        [Tooltip("Layer name used for hardpoint tap raycasts.")]
        [SerializeField] private string hardpointLayerName = "Hardpoint";

        // Campaign start handed over by CampaignManager before Start's one-frame
        // delay resolves (sceneLoaded fires between Awake and Start). Consumed
        // exactly once; without it the flow runs the title path as always.
        private bool _campaignPending;
        private Difficulty _campaignDifficulty;
        private int _campaignSalvage = -1;
        private int _campaignIntegrity = -1;
        private bool _startRan;

        private void Awake()
        {
            // Create the InputRouter as early as possible so UI components (e.g.
            // BuildMenu) that subscribe to it in their own Awake/OnEnable can find
            // it. Previously this ran inside the Start coroutine one frame late, so
            // BuildMenu.OnEnable saw a null router and never subscribed — pads could
            // be tapped but nothing opened the build menu, and no turrets were ever
            // built (so nothing fired at enemies).
            EnsureInputRouter();
        }

        private IEnumerator Start()
        {
            yield return null; // let every Awake run (GameManager.Instance etc.)

            if (titleScreen == null) titleScreen = FindFirstObjectByType<TitleScreen>();
            if (waveManager == null) waveManager = FindFirstObjectByType<WaveManager>();

            PrepareHardpoints();
            EnsureInputRouter();

            _startRan = true;

            if (_campaignPending)
            {
                // Campaign takeover (plan v2 §A.6): the difficulty was chosen once
                // at the Welcome screen — that tap was also the WebGL audio-unlock
                // gesture — so this scene's title screen must not run. Skipping it
                // also skips the title tap's side effects, so replicate them here.
                _campaignPending = false;
                ExecuteCampaignStart();
                yield break;
            }

            if (GameManager.Instance != null)
                GameManager.Instance.SetState(GameState.Title);

            if (titleScreen != null)
            {
                titleScreen.OnPlay += HandlePlay;
                titleScreen.Show();
            }
            else
            {
                // No title in the scene — start straight into Build (editor testing).
                HandlePlay(GameManager.Instance != null ? GameManager.Instance.Difficulty : Difficulty.Normal);
            }
        }

        /// <summary>
        /// Start this level as part of a campaign, with the difficulty chosen at
        /// the Welcome screen — no title overlay, no per-level difficulty pick.
        /// Salvage/integrity overrides pass through to
        /// <see cref="GameManager.ConfigureCampaignRun"/> (-1 = the difficulty's
        /// own defaults — the reset-economy mode the generation gates certify).
        /// Safe to call from a sceneLoaded callback: before Start has run it is
        /// deferred and consumed there; after, it executes immediately.
        /// </summary>
        public void BeginCampaignRun(Difficulty difficulty, int salvageOverride = -1, int integrityOverride = -1)
        {
            _campaignDifficulty = difficulty;
            _campaignSalvage = salvageOverride;
            _campaignIntegrity = integrityOverride;

            if (!_startRan)
            {
                _campaignPending = true;
                return;
            }
            ExecuteCampaignStart();
        }

        private void ExecuteCampaignStart()
        {
            if (titleScreen != null)
                titleScreen.Hide();

            // The title tap normally does this (TitleScreen.Play). The unlock
            // gesture already happened on the Welcome screen, so starting music
            // here is legal for Web Audio.
            if (AudioDirector.Instance != null)
            {
                AudioDirector.Instance.Muted = SaveData.Muted;
                AudioDirector.Instance.StartMusic();
            }

            if (GameManager.Instance != null)
            {
                GameManager.Instance.ConfigureCampaignRun(_campaignDifficulty, _campaignSalvage, _campaignIntegrity);
                GameManager.Instance.SetState(GameState.Build);
            }

            if (waveManager != null)
                waveManager.ResetSequence();
        }

        private void OnDestroy()
        {
            if (titleScreen != null)
                titleScreen.OnPlay -= HandlePlay;
        }

        private void HandlePlay(Difficulty difficulty)
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.ConfigureRun(difficulty);
                GameManager.Instance.SetState(GameState.Build);
            }

            if (waveManager != null)
                waveManager.ResetSequence();
        }

        private void PrepareHardpoints()
        {
            int layer = LayerMask.NameToLayer(hardpointLayerName);
            foreach (var pad in FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            {
                if (layer >= 0 && pad.gameObject.layer != layer)
                    pad.gameObject.layer = layer;

                var col = pad.GetComponent<Collider>();
                if (col == null)
                {
                    var sc = pad.gameObject.AddComponent<SphereCollider>();
                    sc.isTrigger = true;
                    sc.radius = 1.5f;
                }

                var mount = pad.transform.Find("TurretMount");
                if (mount == null)
                {
                    var go = new GameObject("TurretMount");
                    go.transform.SetParent(pad.transform, false);
                    go.transform.localPosition = Vector3.zero;
                    mount = go.transform;
                }
                pad.EnsureMount(mount);
            }
        }

        private void EnsureInputRouter()
        {
            if (FindFirstObjectByType<InputRouter>() == null)
            {
                var go = new GameObject("InputRouter");
                go.AddComponent<InputRouter>();
            }
        }

        /// <summary>
        /// Restart the level being played — Retry, from the pause overlay or the
        /// result screen.
        ///
        /// "The level being played" is the ACTIVE SCENE and nothing else. Both
        /// screens used to carry a serialized scene name defaulting to "Game", so
        /// Retry on any generated map threw the player back into Refinery Delta:
        /// a stored name is a second answer to a question the runtime already
        /// knows, and it is wrong for every map the generator makes.
        ///
        /// Reloading needs the scene in Build Settings (the Level Generator
        /// registers what it saves). Without it Unity refuses the load, so say
        /// which scene and why rather than letting it fail silently.
        /// </summary>
        public static void RestartCurrentLevel()
        {
            Scene active = SceneManager.GetActiveScene();
            if (active.buildIndex >= 0)
            {
                LoadSceneClean(active.buildIndex);
                return;
            }

            Debug.LogError($"[GameFlow] Retry cannot reload '{active.name}' — it is not in Build Settings. " +
                           "Add it via File → Build Profiles (the Level Generator registers what it " +
                           "saves; a scene added by hand needs it too).");
            ClearCrossSceneState();
            SceneManager.LoadScene(active.name, LoadSceneMode.Single);
        }

        /// <summary>
        /// The one sanctioned way to change scenes (plan v2 §A.4). The 2× speed
        /// toggle, pause (timeScale 0) and the static live-enemy registry all
        /// survive a bare LoadScene — a campaign advancing from the pause overlay
        /// would load a frozen scene. Every transition funnels through here so the
        /// teardown contract lives in exactly one place.
        /// </summary>
        public static void LoadSceneClean(int buildIndex)
        {
            ClearCrossSceneState();
            SceneManager.LoadScene(buildIndex, LoadSceneMode.Single);
        }

        /// <summary>Path/name variant for campaign stages. The scene must be in
        /// Build Settings (the generator registers what it saves).</summary>
        public static void LoadSceneClean(string scenePath)
        {
            ClearCrossSceneState();
            if (SceneUtility.GetBuildIndexByScenePath(scenePath) < 0)
                Debug.LogError($"[GameFlow] '{scenePath}' is not in Build Settings — the load will fail. " +
                               "Run the campaign scene registration (or add it via File → Build Profiles).");
            SceneManager.LoadScene(scenePath, LoadSceneMode.Single);
        }

        private static void ClearCrossSceneState()
        {
            Enemies.Enemy.Live.Clear();
            Time.timeScale = 1f;
        }
    }
}
