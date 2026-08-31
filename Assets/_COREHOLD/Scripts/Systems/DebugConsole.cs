#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.IO;
using Corehold.Core;
using Corehold.Enemies;
using Corehold.Towers;
using Corehold.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Corehold.Systems
{
    /// <summary>
    /// Editor / development-build only debug console (GDD §12.4). Costs nothing
    /// in the ship build because the entire class body is compiled out.
    ///
    /// Uses the new Input System (the project's active input handler) so it works
    /// under the Input System package. Reads <see cref="Keyboard.current"/> each
    /// frame.
    ///
    /// Key bindings — <b>F2 prints this same list in-game</b>, which is the copy
    /// that stays true when someone adds a key and forgets this comment:
    ///
    ///   WAVES      ]  next wave      [  previous wave (index only)   0  jump to wave 9
    ///   ECONOMY    M  +1000 salvage  B  build on every free pad      U  upgrade all turrets
    ///   CORE       I  invulnerable   J  damage core by 1
    ///   TURRETS    G  immortal type  ⇧G cycle which type (or ALL)
    ///   ENEMIES    K  kill all       S  stun all 3 s                 L  slow all 50%/3 s
    ///   RUN        V  force VICTORY  X  force DEFEAT                 1/2/3 difficulty
    ///   CAMPAIGN   C  status dump    ⇧C wipe this campaign's saves
    ///   TIME       P  pause/resume   ,  slower                       .  faster
    ///   LOOK       T  cycle this level's mutators                   ⇧R re-roll wave draws
    ///              N  night toggle
    ///              W  re-apply weather                              ⇧W re-roll wave weather
    ///   OUTPUT     F1 stats overlay  F2 key map                      F3 screenshot
    ///
    /// The campaign keys are the reason this file grew: without V, walking to
    /// campaign level 8 means winning seven levels by hand, and the carry maths
    /// that A2 introduced only shows itself at a level boundary.
    /// </summary>
    [DisallowMultipleComponent]
    public class DebugConsole : MonoBehaviour
    {
        private enum Overlay { Off, Stats, Keys }

        private Overlay _overlay = Overlay.Off;
        private float _frameMs;

        /// <summary>Debug speed ladder. Pause is separate (P) so speed survives it.</summary>
        private static readonly float[] SpeedLadder = { 0.25f, 0.5f, 1f, 2f, 4f };
        private int _speedIndex = 2;
        private bool _paused;

        /// <summary>Which tower type G acts on. 0 = ALL TYPES, 1..N index the
        /// runtime roster — so one key covers per-type and blanket immortality,
        /// and several types can be immortal at once (the case that matters:
        /// "hold the front line, let the support die").</summary>
        private int _immortalCursor;

        private void Update()
        {
            // Smoothed frame time in milliseconds.
            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.1f);

            var kb = Keyboard.current;
            if (kb == null)
                return;

            bool shift = kb.shiftKey.isPressed;

            if (kb.rightBracketKey.wasPressedThisFrame)
                NextWave();
            if (kb.leftBracketKey.wasPressedThisFrame)
                PreviousWave();
            if (kb.digit0Key.wasPressedThisFrame)
                JumpToWave(9);
            if (kb.mKey.wasPressedThisFrame)
                GrantSalvage(1000);
            if (kb.bKey.wasPressedThisFrame)
                BuildOnEveryPad();
            if (kb.uKey.wasPressedThisFrame)
                UpgradeAllTurrets();
            if (kb.iKey.wasPressedThisFrame)
                ToggleCoreInvulnerability();
            if (kb.jKey.wasPressedThisFrame)
                DamageCore();
            if (kb.gKey.wasPressedThisFrame)
            {
                if (shift) CycleImmortalCursor();
                else ToggleImmortalAtCursor();
            }
            if (kb.kKey.wasPressedThisFrame)
                KillAllEnemies();
            if (kb.sKey.wasPressedThisFrame)
                StunAllEnemies();
            if (kb.lKey.wasPressedThisFrame)
                SlowAllEnemies();
            if (kb.vKey.wasPressedThisFrame)
                ForceVictory();
            if (kb.xKey.wasPressedThisFrame)
                ForceDefeat();
            if (kb.cKey.wasPressedThisFrame)
            {
                if (shift) WipeCampaignSaves();
                else DumpCampaignStatus();
            }
            if (kb.pKey.wasPressedThisFrame)
                TogglePause();
            if (kb.commaKey.wasPressedThisFrame)
                StepSpeed(-1);
            if (kb.periodKey.wasPressedThisFrame)
                StepSpeed(+1);
            if (kb.tKey.wasPressedThisFrame)
                CycleForcedMutatorAsset();
            if (kb.nKey.wasPressedThisFrame)
                ToggleNight();
            if (shift && kb.rKey.wasPressedThisFrame)
                RerollMutatorDraw();
            if (kb.wKey.wasPressedThisFrame)
            {
                if (shift) RerollWaveWeather();
                else ReapplyWeather();
            }
            if (kb.digit1Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Normal);
            if (kb.digit2Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Veteran);
            if (kb.digit3Key.wasPressedThisFrame)
                SetDifficulty(Difficulty.Nightmare);
            if (kb.f1Key.wasPressedThisFrame)
                _overlay = _overlay == Overlay.Stats ? Overlay.Off : Overlay.Stats;
            if (kb.f2Key.wasPressedThisFrame)
                _overlay = _overlay == Overlay.Keys ? Overlay.Off : Overlay.Keys;
            if (kb.f3Key.wasPressedThisFrame)
                Screenshot();
        }

        // ----- Waves -----

        private void NextWave()
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm != null && wm.StartNextWave())
            {
                Debug.Log($"[DebugConsole] Started wave (next index now {wm.NextWaveIndex}).");
                return;
            }

            var gm = GameManager.Instance;
            if (gm != null)
                gm.WaveIndex++;
            Debug.Log($"[DebugConsole] Next wave (WaveIndex now {gm?.WaveIndex}).");
        }

        private void PreviousWave()
        {
            // WaveManager has no rewind (waves are one-shot); this steps the
            // GameManager index back for tuning purposes only.
            var gm = GameManager.Instance;
            if (gm != null)
                gm.WaveIndex = Mathf.Max(0, gm.WaveIndex - 1);
            Debug.Log($"[DebugConsole] Previous wave (WaveIndex now {gm?.WaveIndex}).");
        }

        /// <summary>
        /// Fast-forward straight to the given 1-based wave for tuning and the
        /// draw-call check (GDD §12.4, Ticket 29). Advances the WaveManager's
        /// next-wave pointer without spawning the intervening waves, then starts it.
        /// </summary>
        private void JumpToWave(int waveNumber)
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogWarning("[DebugConsole] No WaveManager to jump with.");
                return;
            }

            int target = Mathf.Clamp(waveNumber, 1, wm.WaveCount);
            wm.JumpToWave(target);
            Debug.Log($"[DebugConsole] Jumped to wave {target} and started it.");
        }

        // ----- Economy & building -----

        private void GrantSalvage(int amount)
        {
            var gm = GameManager.Instance;
            if (gm != null)
                gm.AddSalvage(amount);
            Debug.Log($"[DebugConsole] Granted {amount} salvage.");
        }

        /// <summary>
        /// Fill every free pad with the first buildable turret, funding itself as
        /// it goes. Wave pressure is the thing worth testing; clicking twelve pads
        /// to reach it is not. Note this inflates RunSalvageEarned (it grants), so
        /// scores from a filled run are not comparable.
        /// </summary>
        private void BuildOnEveryPad()
        {
            var theme = UITheme.Instance;
            if (theme == null || theme.turrets == null || theme.turrets.Length == 0)
            {
                Debug.LogWarning("[DebugConsole] No UITheme turret catalogue in this scene.");
                return;
            }

            Corehold.Data.TowerDefinition pick = null;
            foreach (var t in theme.turrets)
                if (t != null && t.basePrefab != null && t.tiers != null && t.tiers.Length > 0) { pick = t; break; }

            if (pick == null)
            {
                Debug.LogWarning("[DebugConsole] No buildable turret definition (all missing basePrefab).");
                return;
            }

            var gm = GameManager.Instance;
            int built = 0;
            foreach (var pad in FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            {
                if (pad == null || pad.IsOccupied) continue;
                if (gm != null && gm.Salvage < pick.tiers[0].cost)
                    gm.AddSalvage(pick.tiers[0].cost);       // fund exactly this build
                if (pad.TryBuild(pick)) built++;
            }
            Debug.Log($"[DebugConsole] Built {pick.displayName} on {built} free pad(s) (salvage granted as needed).");
        }

        /// <summary>Upgrade every built turret one tier, funding each step.</summary>
        private void UpgradeAllTurrets()
        {
            var gm = GameManager.Instance;
            int upgraded = 0, maxed = 0;
            foreach (var pad in FindObjectsByType<TowerHardpoint>(FindObjectsSortMode.None))
            {
                if (pad == null || !pad.IsOccupied) continue;
                if (!pad.CanUpgrade) { maxed++; continue; }
                if (gm != null && gm.Salvage < pad.NextUpgradeCost)
                    gm.AddSalvage(pad.NextUpgradeCost);
                if (pad.TryUpgrade()) upgraded++;
            }
            Debug.Log($"[DebugConsole] Upgraded {upgraded} turret(s); {maxed} already at max tier.");
        }

        // ----- Core & enemies -----

        private void ToggleCoreInvulnerability()
        {
            var gm = GameManager.Instance;
            if (gm != null)
            {
                gm.CoreInvulnerable = !gm.CoreInvulnerable;
                Debug.Log($"[DebugConsole] Core invulnerable: {gm.CoreInvulnerable}.");
            }
        }

        // ----- Turret immortality by type (G / shift+G) -----

        /// <summary>The runtime roster, in build-menu order (the UITheme is the
        /// runtime mirror of the editor's RosterRegistry, which this cannot
        /// reach). Empty on a scene whose UI has not been built.</summary>
        private static Corehold.Data.TowerDefinition[] Roster()
        {
            var theme = UITheme.Instance;
            return theme != null && theme.turrets != null
                ? theme.turrets
                : new Corehold.Data.TowerDefinition[0];
        }

        private Corehold.Data.TowerDefinition CursorDefinition()
        {
            var roster = Roster();
            if (_immortalCursor <= 0 || roster.Length == 0)
                return null;   // 0 = ALL TYPES
            return roster[Mathf.Clamp(_immortalCursor - 1, 0, roster.Length - 1)];
        }

        private string CursorLabel()
        {
            var def = CursorDefinition();
            if (def == null)
                return "ALL TYPES";
            return string.IsNullOrEmpty(def.displayName) ? def.id : def.displayName;
        }

        private void CycleImmortalCursor()
        {
            int slots = Roster().Length + 1;   // +1 for the ALL TYPES slot
            _immortalCursor = (_immortalCursor + 1) % Mathf.Max(1, slots);
            Debug.Log($"[DebugConsole] Immortality cursor → {CursorLabel()} (G toggles it).");
        }

        private void ToggleImmortalAtCursor()
        {
            var roster = Roster();
            if (roster.Length == 0)
            {
                Debug.LogWarning("[DebugConsole] No turret roster in this scene (UITheme.turrets is empty) — " +
                                 "run Tools → COREHOLD → Scene Setup → Build Real UI.");
                return;
            }

            var def = CursorDefinition();
            if (def == null)
            {
                // ALL TYPES: on unless everything is already on.
                bool on = !TowerImmortality.Any;
                TowerImmortality.SetAll(roster, on);
                Debug.Log($"[DebugConsole] Turret immortality: ALL TYPES {(on ? "ON" : "OFF")}.");
                return;
            }

            bool state = TowerImmortality.Toggle(def);
            Debug.Log($"[DebugConsole] Turret immortality: {CursorLabel()} {(state ? "ON (live ones healed)" : "OFF")}. " +
                      $"Immortal now: {TowerImmortality.Describe()}.");
        }

        /// <summary>One point of core damage — the close-call feedback, the damage
        /// state material and (at zero) the real Defeat path, without a leak.</summary>
        private void DamageCore()
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            gm.DamageCore(1);
            Debug.Log($"[DebugConsole] Core damaged by 1 (integrity now {gm.Integrity}).");
        }

        private void KillAllEnemies()
        {
            var enemies = FindObjectsByType<Enemy>(FindObjectsSortMode.None);
            int killed = 0;
            foreach (var e in enemies)
            {
                if (e != null && e.IsAlive)
                {
                    e.TakeDamage(e.CurrentHealth);
                    killed++;
                }
            }
            Debug.Log($"[DebugConsole] Killed {killed} live enemies.");
        }

        private void StunAllEnemies()
        {
            int hit = 0;
            for (int i = 0; i < Enemy.Live.Count; i++)
            {
                var e = Enemy.Live[i];
                if (e != null && e.IsAlive) { e.ApplyStun(3f); hit++; }
            }
            Debug.Log($"[DebugConsole] Stunned {hit} live enemies for 3 s (R18; Colossus takes 2.25 s).");
        }

        private void SlowAllEnemies()
        {
            int hit = 0;
            for (int i = 0; i < Enemy.Live.Count; i++)
            {
                var e = Enemy.Live[i];
                if (e != null && e.IsAlive) { e.ApplySlow(3f, 0.5f); hit++; }
            }
            Debug.Log($"[DebugConsole] Slowed {hit} live enemies 50% for 3 s (R18).");
        }

        // ----- Run outcome -----

        /// <summary>
        /// End this level as a WIN now. The campaign accelerator: reaching stage 8
        /// otherwise means winning seven levels by hand, and A2's carry maths only
        /// shows itself at a level boundary.
        ///
        /// Live enemies are killed normally first (bounty IS paid, as with K), so
        /// the salvage that carries forward includes those payouts — set the bank
        /// deliberately with M if the carry number is what you are testing.
        /// </summary>
        private void ForceVictory()
        {
            KillAllEnemies();
            var gm = GameManager.Instance;
            if (gm == null) return;
            gm.SetState(GameState.Victory);
            Debug.Log("[DebugConsole] Forced VICTORY. In a campaign the result screen's second button " +
                      "is CONTINUE — it advances the stage and applies the carry rules.");
        }

        /// <summary>End this level as a LOSS now — straight to the state, so it works
        /// with an invulnerable core and without waiting for a leak.</summary>
        private void ForceDefeat()
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            gm.SetState(GameState.Defeat);
            Debug.Log("[DebugConsole] Forced DEFEAT (state set directly; integrity untouched).");
        }

        // ----- Campaign -----

        private void DumpCampaignStatus()
        {
            var c = CampaignManager.Instance;
            if (c == null || !c.HasActiveCampaign)
            {
                Debug.Log("[DebugConsole] No campaign active (single-map play). Start one from the " +
                          "Welcome scene; the manager is created there.");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[DebugConsole] CAMPAIGN '{c.Active.displayName}' (id {c.Active.campaignId})");
            sb.AppendLine($"  stage       : index {c.CurrentStageIndex} — level {c.CurrentLevelNumber}/{c.LevelCount}");
            sb.AppendLine($"  difficulty  : {c.ChosenDifficulty} (chosen once at Welcome)");
            sb.AppendLine($"  carry rules : {c.Active.progression.economyCarry}" +
                          $", keep {c.Active.progression.salvageKeepFraction:0.##}" +
                          $", floor {c.Active.progression.baseSalvagePerLevel}" +
                          $", integrity {(c.Active.progression.carryIntegrity ? "carried" : "reset")}" +
                          $" +{c.Active.progression.integrityHealPerLevel}/level");
            sb.AppendLine($"  entry snap  : salvage {Sentinel(c.CurrentEntrySalvage)}, integrity {Sentinel(c.CurrentEntryIntegrity)}" +
                          "   (-1 = difficulty default; Retry re-applies exactly this)");
            sb.AppendLine($"  elapsed     : {c.ElapsedSeconds:0.0} s over {c.Results.Count} recorded level(s)");
            for (int i = 0; i < c.Results.Count; i++)
            {
                var r = c.Results[i];
                sb.AppendLine(r == null
                    ? $"    L{i + 1}: (no result)"
                    : $"    L{i + 1}: {(r.victory ? "WON " : "LOST")} {r.stars}★ score {r.score}  {r.title}");
            }
            sb.AppendLine($"  total score : {c.CumulativeScore}");
            Debug.Log(sb.ToString());
        }

        private static string Sentinel(int v) => v < 0 ? "-1" : v.ToString();

        /// <summary>
        /// Wipe this campaign's PlayerPrefs (run blob, bests, per-stage stars) so
        /// the Welcome screen goes back to a virgin state — the only way to retest
        /// CONTINUE, first-run copy and record badges without editing prefs by hand.
        /// </summary>
        private void WipeCampaignSaves()
        {
            string id = null;
            var c = CampaignManager.Instance;
            if (c != null && c.HasActiveCampaign)
                id = c.Active.campaignId;
            else
            {
                var welcome = FindFirstObjectByType<CampaignWelcome>();
                if (welcome != null && welcome.Manifest != null)
                    id = welcome.Manifest.campaignId;
            }

            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning("[DebugConsole] No campaign id in reach — run this in a campaign level or " +
                                 "on the Welcome scene (whose manifest names the campaign).");
                return;
            }

            SaveData.ClearCampaignData(id);
            Debug.Log($"[DebugConsole] Wiped saves for campaign '{id}' (run, bests, stage stars). " +
                      "Re-enter the Welcome scene to see it as a first-time player.");
        }

        // ----- Time -----

        private void TogglePause()
        {
            _paused = !_paused;
            Time.timeScale = _paused ? 0f : SpeedLadder[_speedIndex];
            Debug.Log($"[DebugConsole] {(_paused ? "PAUSED" : $"Resumed at ×{SpeedLadder[_speedIndex]}")}. " +
                      "(The pause overlay owns timeScale too — if they disagree, this key wins last.)");
        }

        private void StepSpeed(int delta)
        {
            _speedIndex = Mathf.Clamp(_speedIndex + delta, 0, SpeedLadder.Length - 1);
            _paused = false;
            Time.timeScale = SpeedLadder[_speedIndex];
            Debug.Log($"[DebugConsole] Time ×{SpeedLadder[_speedIndex]}.");
        }

        // ----- Look -----

        /// <summary>
        /// Cycle through this level's mutators, adding one to every wave
        /// started after the press.
        ///
        /// The reason a new mutator is worth authoring at all: without this,
        /// seeing what one does means editing a wave asset, re-entering play
        /// and walking to that wave. The library comes off the WaveManager, so
        /// a mutator that is not in the level's list is not offered — which is
        /// also how a designer finds out they forgot to register one.
        /// </summary>
        private void CycleForcedMutatorAsset()
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogWarning("[DebugConsole] No WaveManager to force mutators on.");
                return;
            }

            var lib = wm.MutatorLibrary;
            if (lib == null || lib.Count == 0)
            {
                Debug.LogWarning("[DebugConsole] This scene's WaveManager has no mutator library. " +
                                 "Run Tools → COREHOLD → Scene Setup → Wave Mutators to fill it.");
                return;
            }

            // The cycle is None, then each asset in order, so a press always
            // has somewhere to go back to.
            int at = -1;
            for (int i = 0; i < lib.Count; i++)
                if (lib[i] == wm.DebugForceMutatorAsset) { at = i; break; }

            int next = at + 1;
            wm.DebugForceMutatorAsset = next >= lib.Count ? null : lib[next];

            var forced = wm.DebugForceMutatorAsset;
            Debug.Log(forced == null
                ? "[DebugConsole] Forced mutator: none (T again to cycle)."
                : $"[DebugConsole] Forced authored mutator: {forced.ResolvedId} — " +
                  $"{forced.title}: {forced.clause} (applies to waves started from now).");
        }

        /// <summary>
        /// Draw a new MUTATOR sequence for this run (R36).
        ///
        /// The counterpart of ⇧W for the gameplay half. Waves already started
        /// keep what they drew — re-rolling under a live wave would change the
        /// fight mid-fight — so the new sequence lands at the next wave start.
        /// </summary>
        private void RerollMutatorDraw()
        {
            var wm = FindFirstObjectByType<WaveManager>();
            if (wm == null)
            {
                Debug.LogWarning("[DebugConsole] No WaveManager to re-roll.");
                return;
            }
            uint seed = wm.RerollRunSeed();
            var next = wm.DrawnMutatorForWave(wm.NextWaveIndex + 1);
            Debug.Log($"[DebugConsole] Mutator draws re-rolled (seed {seed}). " +
                      $"Next wave draws: {(next != null ? next.ResolvedId : "nothing")}.");
        }

        /// <summary>
        /// Draw a NEW weather sequence for this run without leaving play mode.
        ///
        /// The roll is seeded per run, which is the point of it — and which
        /// makes judging the feature painful without this key, since every look
        /// at a different sequence would otherwise cost a level reload. Takes
        /// effect at the next wave start, the same moment a roll normally lands.
        /// </summary>
        private void RerollWaveWeather()
        {
            var weather = FindFirstObjectByType<WeatherApplier>();
            if (weather == null)
            {
                Debug.LogWarning("[DebugConsole] No WeatherApplier in the scene.");
                return;
            }
            uint seed = weather.RerollRunSeed();
            Debug.Log($"[DebugConsole] Wave-weather sequence re-rolled (seed {seed}). " +
                      "The next wave to start draws from the new sequence.");
        }

        /// <summary>Re-apply the active weather preset so live [TUNE] edits show now.</summary>
        private void ReapplyWeather()
        {
            var weather = FindFirstObjectByType<WeatherApplier>();
            if (weather == null)
            {
                Debug.LogWarning("[DebugConsole] No WeatherApplier in the scene.");
                return;
            }
            weather.Reapply();
            Debug.Log("[DebugConsole] Weather re-applied (live preset values picked up).");
        }

        /// <summary>R23: flip the night lighting variant, if the scene carries one.</summary>
        private void ToggleNight()
        {
            var night = NightVariant.Instance;
            if (night == null)
                night = FindFirstObjectByType<NightVariant>();
            if (night == null)
            {
                Debug.LogWarning("[DebugConsole] No NightVariant in the scene — run " +
                                 "Tools → COREHOLD → Scene Setup → Night Variant first.");
                return;
            }
            night.Toggle();
        }

        private void SetDifficulty(Difficulty d)
        {
            var gm = GameManager.Instance;
            if (gm != null)
                gm.Difficulty = d;
            Debug.Log($"[DebugConsole] Difficulty set to {d}.");
        }

        // ----- Output -----

        /// <summary>
        /// Full-resolution screenshot to &lt;project&gt;/Screenshots (editor) or the
        /// persistent data path (build). A bug report with a picture is worth ten
        /// without one, and the alternative is alt-tabbing out mid-wave.
        /// </summary>
        private void Screenshot()
        {
            string dir = Application.isEditor
                ? Path.Combine(Application.dataPath, "../Screenshots")
                : Path.Combine(Application.persistentDataPath, "Screenshots");
            Directory.CreateDirectory(dir);

            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"corehold_{stamp}.png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[DebugConsole] Screenshot → {path} (written at end of frame).");
        }

        private int LiveEnemyCount()
        {
            var enemies = FindObjectsByType<Enemy>(FindObjectsSortMode.None);
            int count = 0;
            foreach (var e in enemies)
            {
                if (e != null && e.IsAlive)
                    count++;
            }
            return count;
        }

        // ----- Overlay -----

        private void OnGUI()
        {
            // ALWAYS visible, overlay or not: turret immortality changes what
            // survives a wave, so it must never be possible to judge a fight
            // while quietly cheating. Deliberately loud, deliberately unmissable.
            if (TowerImmortality.Any)
            {
                var warn = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                    padding = new RectOffset(8, 8, 4, 4)
                };
                warn.normal.textColor = new Color(1f, 0.45f, 0.35f);
                string list = TowerImmortality.Describe();
                if (list.Length > 44) list = list.Substring(0, 41) + "…";
                GUI.Box(new Rect(Screen.width - 360f, 10f, 350f, 24f),
                        $"⚠ IMMORTAL TURRETS — {list}", warn);
            }

            if (_overlay == Overlay.Off)
                return;

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                padding = new RectOffset(10, 10, 10, 10)
            };
            style.normal.textColor = Color.white;

            if (_overlay == Overlay.Keys)
            {
                GUI.Box(new Rect(10, 10, 460, 268), KeyMapText(), style);
                return;
            }

            var gm = GameManager.Instance;
            var wm = FindFirstObjectByType<WaveManager>();

            int liveEnemies = LiveEnemyCount();
            int wave = wm != null ? wm.NextWaveIndex : (gm != null ? gm.WaveIndex : 0);
            int waveCount = wm != null ? wm.WaveCount : 0;
            int salvage = gm != null ? gm.Salvage : 0;
            int integrity = gm != null ? gm.Integrity : 0;

            // Draw call count if reachable.
            long drawCalls = -1;
#if UNITY_EDITOR
            drawCalls = UnityEditor.UnityStats.drawCalls;
#endif

            string speed = _paused ? "PAUSED" : $"×{SpeedLadder[_speedIndex]}";
            string mutators = wm != null && wm.DebugForceMutatorAsset != null
                ? $"   forced: {wm.DebugForceMutatorAsset.ResolvedId}" : "";
            var night = NightVariant.Instance;

            string text =
                "COREHOLD DEBUG                    F2 keys\n" +
                $"Live enemies : {liveEnemies}\n" +
                $"Wave         : {wave}/{waveCount}{mutators}\n" +
                $"Salvage      : {salvage}\n" +
                $"Integrity    : {integrity}{(gm != null && gm.CoreInvulnerable ? "  (INVULNERABLE)" : "")}\n" +
                $"Difficulty   : {(gm != null ? gm.Difficulty.ToString() : "—")}" +
                $"{(night != null && night.IsNight ? "   night" : "")}\n" +
                $"Time         : {speed}\n" +
                $"Frame time   : {_frameMs:0.0} ms ({(_frameMs > 0f ? (1000f / _frameMs) : 0f):0} fps)\n" +
                $"Draw calls   : {(drawCalls >= 0 ? drawCalls.ToString() : "n/a")}\n" +
                $"Immortal     : {TowerImmortality.Describe()}\n" +
                $"  G target   : {CursorLabel()}" +
                CampaignOverlayLines();

            GUI.Box(new Rect(10, 10, 320, 250), text, style);
        }

        /// <summary>Campaign block — only when one is running, so single-map play
        /// keeps the compact overlay it always had.</summary>
        private string CampaignOverlayLines()
        {
            var c = CampaignManager.Instance;
            if (c == null || !c.HasActiveCampaign)
                return "";

            return $"\n— campaign {c.Active.campaignId} —\n" +
                   $"Level        : {c.CurrentLevelNumber}/{c.LevelCount}\n" +
                   $"Entry        : salv {Sentinel(c.CurrentEntrySalvage)}  integ {Sentinel(c.CurrentEntryIntegrity)}\n" +
                   $"Run score    : {c.CumulativeScore}   (C dumps detail)";
        }

        private static string KeyMapText()
        {
            return
                "COREHOLD DEBUG — KEYS                       F2 to close\n\n" +
                "WAVES     ]  next wave     [  prev index    0  jump to w9\n" +
                "ECONOMY   M  +1000 salv    B  build all pads U  upgrade all\n" +
                "CORE      I  invuln        J  damage core 1\n" +
                "TURRETS   G  immortal type shift+G  pick type (or ALL TYPES)\n" +
                "ENEMIES   K  kill all      S  stun all       L  slow all\n" +
                "RUN       V  force WIN     X  force LOSS     1/2/3 difficulty\n" +
                "CAMPAIGN  C  status dump   shift+C  wipe this campaign's saves\n" +
                "TIME      P  pause         ,  slower         .  faster\n" +
                "LOOK      T  force mutator                \u21e7R reroll draws\n" +
                "          N  night\n" +
                "          W  reapply weather              \u21e7W reroll wave weather\n" +
                "OUTPUT    F1 stats         F2 this list      F3 screenshot\n\n" +
                "V is the campaign accelerator: force a win, press CONTINUE,\n" +
                "and the next stage loads with the carry rules applied.";
        }
    }
}
#endif
