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
            Enemies.Enemy.Live.Clear();
            Time.timeScale = 1f;

            Scene active = SceneManager.GetActiveScene();
            if (active.buildIndex >= 0)
            {
                SceneManager.LoadScene(active.buildIndex, LoadSceneMode.Single);
                return;
            }

            Debug.LogError($"[GameFlow] Retry cannot reload '{active.name}' — it is not in Build Settings. " +
                           "Add it via File → Build Profiles (the Level Generator does this for scenes it " +
                           "saves; a scene added by hand needs it too).");
            SceneManager.LoadScene(active.name, LoadSceneMode.Single);
        }
    }
}
