# COREHOLD — Roadmap v2 & CoPlay Prompt Book (R-series)

## TL;DR
- This roadmap sequences COREHOLD from its shipped 10-wave MVP to a retention-capable F2P web/mobile TD in ~14 weekly drops, gated end-to-end by a **balance model that must be built first (P0)** and re-run before every tune; splines land before terrain dressing, the level generator produces map 2 and unlocks a daily-seed mode, and a filtered set of researched features (endless survival, score attack + local/portal leaderboard, weekly mutators, per-map medals/stars, daily-seed challenge) are added as expansions of the existing directors — never parallel systems.
- Every ticket names real classes/files (GameManager, AudioDirector, VFXDirector, PathRoute, RouteTraffic, WaveManager, LevelDefinition, RefineryDeltaBlockout, HardpointCoverageGizmo) and obeys the standing rules: new SFX → `AudioDirector.Sfx` enum + `AudioDirector.Instance.Play`; new VFX → `VFXDirector.Effect` enum (pooled); run flow → `GameManager.SetState` + events; world overlays → OverlayManager; UI → the three existing canvases; all numbers in ScriptableObjects or `[TUNE]` fields; one ticket per CoPlay thread; git commit before each ticket.
- The strategic bet: retention on web/mobile TD in 2024–2026 comes from **local-first, server-free loops** — a daily-seed challenge (Slay the Spire / Spelunky), endless survival with a score leaderboard (Garden TD, Rush Royale ladders), weekly mutators (Slay the Spire custom mode; BTD6 Odyssey), and non-P2W meta (Kingdom Rush stars, BTD6 Monkey Knowledge). All are achievable solo with CoPlay and the owned asset kits.

## Key Findings

1. **A balance model does not exist in the repo and must be item #0.** `docs/balance_model.py` is absent; every balance-touching change downstream (splines lengthen routes, the generator emits new LevelDefinitions, endless extends waves, mutators change HP/speed) is un-gateable until a geometry-parameterized model reproduces today's per-wave margins. This is the single highest-leverage first action.

2. **Splines must precede terrain dressing and the flyover — geometry freezes before composition.** The spline backbone (behind a `useSpline` bool in `PathRoute.Recompute()`), merge-knot tangent pinning, and the curve-based rewrite of `HardpointCoverageGizmo` all change route Length and coverage classes. Dressing rocks, weather, and a camera flyover onto geometry that then shifts wastes the most expensive (manual) work.

3. **The level generator is the content engine, not a side-stream.** A deterministic `LevelBlueprint` SO (all randomness from `randomSeed`) plus a parity rebuild of `RefineryDeltaBlockout` generalizes the shipped map into a repeatable pipeline that produces map 2 and — critically — enables the daily-seed challenge mode (seed-of-the-day through the generation gate), the highest-retention feature in the researched set.

4. **Retention benchmarks justify the feature order.** Per GameAnalytics' *Mobile Gaming Benchmarks 2025* report (11,600 apps, 9 regions, 16 genres, 1.48B MAU, data through end of 2024), "the median D7 Retention across all projects ranges from 3.42% to 3.94%," and "for the top 25% of projects, this indicator is at 7-8%, while for the weakest 25% of projects, it barely reaches 1.5%." Even the best day-1 numbers are modest: GameAnalytics found "the average D1 Retention for the top 25% of games ranges from 26.48% to 27.69%." For strategy specifically, AppsFlyer's benchmark data (via Mistplay) puts D1 at 25.39% and D7 at 8.06% — among the lowest D1 of any genre because "their depth takes longer to hook new players," but with a healthier D7-to-D1 ratio. The implication for COREHOLD: depth-and-return mechanics (daily seed, endless, medals) matter more than first-session flash, so they are weighted early after the geometry/generator foundation.

5. **Server-free is the correct constraint, not a limitation.** WebGL leaderboards that trust the client are trivially spoofable (multiple itch.io devs report players "disabled java and was able to input their own score"), so COREHOLD should ship local-first leaderboards + shareable seed/score codes (the Spelunky/Slay-the-Spire "same seed, one attempt, share text" pattern), with an optional portal leaderboard only where the host (Poki/CrazyGames) provides a sanctioned SDK.

6. **Non-P2W meta is a solved problem in the genre.** Kingdom Rush's stars (earned from campaign + Heroic/Iron challenge completion, spent on an upgrade tree with a full reset option) and BTD6's Monkey Knowledge (all unlockable by playing; CHIMPS mode disables it entirely so the hardest content is pure skill) both prove ad-monetised, cosmetic-or-loadout meta that never gates power behind money. COREHOLD's per-map stars should unlock cosmetic/starting-loadout variety only.

## Details

### The reference games, and what COREHOLD takes from each

- **Bloons TD 6 (Odyssey, Boss Events, Contested Territory, Boss Rush, Monkey Knowledge, Paragons):** BTD6 runs a fortnightly rotation of limited-time modes (Contested Territory alternates with Boss Rush every two weeks) and a Challenge Editor/browser. The transferable, server-free ideas are **weekly mutator rotations** (COREHOLD's WaveDefinition mutators) and **boss events** (Colossus already exists at wave 10). Contested Territory (six teams of 15, up to 90 players) and co-op are out of scope — they need a backend.
- **Kingdom Rush 5: Alliance (heroes, stars, Heroic/Iron challenges, mini-bosses):** The **stars meta** (three-star campaign clears + challenge-mode stars feed an upgrade tree that can be freely reset) is the model for COREHOLD's **per-map medals → stars → cosmetic/loadout unlocks**. Heroes are a large content investment; COREHOLD's already-designed **Strike Wing active ability** (from the reference E-series) is the feasible, mechanic-first substitute.
- **Arknights (live-ops cadence):** HyperGryph sustains retention with a "steady cadence of Events to keep the gameplay fresh," bucketed into Major and Minor Events. The lesson for a solo dev is the **cadence pyramid** (one long-running loop + mid-term events + short daily activities), realised at COREHOLD scale as: endless/score-attack (long), weekly mutator (mid), daily-seed (short).
- **Rush Royale (session shape, seasonal ladders, events):** Matches run **3–5 minutes** with seasonal ladders and daily quests. Confirms COREHOLD's ~10-minute run is already in the sticky band and that **short, repeatable, ladder-scored sessions** drive return. PvP/merge/gacha are out of scope (backend + P2W risk).
- **Isle of Arrows (Daily Defense mode):** A puzzle-TD with a **Campaign / Gauntlet / Daily Defense** three-mode split — direct proof that a **single deterministic daily** is a shippable, beloved feature in a small TD.
- **Slay the Spire (Daily Climb, Custom Mode, Ascension):** The gold standard for **daily seed + 3 random modifiers, same seed for all players, one attempt**, plus a **Custom Mode** modifier sandbox. This is the precise template for COREHOLD's daily-seed challenge and weekly mutator rotation.
- **Thronefall / Emberward / Rogue Tower (roguelite TDs):** Show that **run-based variety** and light meta keep "the strategic puzzle from ever feeling solved." Caution from Thronefall's community: meta-progression that forces you to "lose a few games to gain levels" before content is beatable is resented — so COREHOLD's meta must be additive/cosmetic, never a power gate.
- **Web portals (Poki / CrazyGames / Kongregate):** What makes TDs sticky in-browser is **instant restart, short sessions, and leaderboards**; players "love the steady sense of progress." Reinforces instant-restart and score-attack as first-class.

### Retention data that shapes the sequence
Strategy games have the lowest D1 hook of any mobile genre (25.39% D1 per AppsFlyer/Mistplay) but retain relatively well to D7 (8.06%). GameAnalytics' "40/20/10" rule (≈40% D1, 20% D7, 10% D30) is the shorthand baseline, but its 2025 data shows real-world top-quartile D7 is only 7–8% — so the marginal returns are in **giving returning players a fresh reason to open the game**, exactly what daily-seed, weekly mutators, and endless leaderboards provide. Session-length data says COREHOLD should support **multiple short sittings per day**: GameAnalytics reports "the median daily playtime across all games in 2024 is 22 minutes," a median session of 5–6 minutes, and 6–7 sessions/day for mid-core projects — which the daily-seed + score-attack loop delivers. On mechanics, per AppAgent citing Unity, "games using rewarded formats saw a 4-percentage-point lift in D7 retention and a 2-point lift in D30" (with the watch-out that incentivized installs can inflate early retention while underdelivering on D30). Airship's study of 47 million new app users found "app users who receive any amount of notifications in their first 90-days have an average retention rate that's nearly 3X higher (190 percent)" and that "95% of opt-in users who don't receive a push notification in the first 90 days will churn" — note COREHOLD is web/local-first, so push applies only to installed-PWA/mobile builds.

### Technical grounding for the spline and generator work
Unity's `com.unity.splines` (2.9.0 in both the manifest and the lock file, referenced nowhere in game code) exposes exactly the primitives the plan needs: `Spline` knots authored with `TangentMode.AutoSmooth`, `NativeSpline` for allocation-free evaluation, and world-space evaluation by baking a transform matrix into the native spline when it is built. AutoSmooth tangents are computed from neighbouring knot positions — which is precisely why the two routes' identical-position merge knots produce **divergent tangents** (their pre-merge neighbours differ), confirming the merge-knot pinning ticket is necessary, not speculative. For endless mode, BTD6's freeplay is the reference pattern: after the final defined round, apply **continuous per-round HP and speed ramping** (in BTD6, "all bloons also become faster for each round past 80: mostly 2% per round, but with some sudden jumps," and MOAB-class HP scales infinitely into freeplay) — COREHOLD's balance model already produces per-wave margin, so procedural extension is just "solve the next wave's budget to hold a target margin."

---

# THE ROADMAP

Operating discipline, standing constraints, and the gate ritual are defined once in the header below and assumed by every ticket.

## Header — read once, applies to all tickets

**Vision.** COREHOLD is a sci-fi tower defense that respects the player's time: a ~10-minute, 10-wave hardpoint defense that looks and feels premium on a phone or in a browser tab, ships something new every week, and gives returning players a fresh, fair reason to come back — never a paywall.

**Operating discipline (state once, obey always).**
- One ticket per CoPlay thread. Git commit before starting each ticket.
- Never debug conversationally — if a ticket goes wrong, revert and re-scope.
- Unpin files when the ticket is done; reply within five minutes to keep the thread alive.
- All numbers live in ScriptableObjects or `[TUNE]`-marked serialized fields — never hard-coded literals.
- `[MANUAL]` = a human composition/authoring day (CoPlay assists, human decides). `[GATE]` = a validation ticket that ships no new feature.
- Every gameplay ticket ends with: **re-run the balance model before tuning.**

**Expand, don't parallel (the standing constraint, with exact APIs).**
- New sound → add an entry to the **`AudioDirector.Sfx`** enum, register it, and play via **`AudioDirector.Instance.Play(Sfx id[, volumeScale])`**.
- New visual effect → add an entry to the **`VFXDirector.Effect`** enum, register its prefab + prewarm count in the director's effects table (pooled), and play via the overloads **`VFXDirector.Instance.Play(Effect, pos)`** / **`Play(Effect, pos, forward)`** / **`Play(Effect, pos, rotation, scale)`**, or the typed helpers `PlayMuzzle(type, pos, forward)` / `PlayImpact(pos)` / `PlayExplosion(pos, splashRadius)` (splash ≥ 4 m ⇒ `ExplosionLarge`) / `PlayEnemyDeath(pos)` / `PlayCoreHit(pos)` / `PlayBuildPuff(pos)`.
- Run/flow state → **`GameManager.SetState(GameState)`** and subscribe to `OnStateChanged` / `OnSalvageChanged` / `OnIntegrityChanged`. Never add a second state machine.
- World-space overlays anchored to units or the field → **`OverlayManager`** (today it owns the pooled enemy health bars + armour-pip chevrons, one shared material, one LateUpdate; combo counts, telegraph rings, rank chevrons and banners extend it).
- UI → inside the three existing canvases only: **`Canvas_HUD`**, **`Canvas_Menus`**, **`Canvas_RotatePrompt`**.
- A ticket that needs a hook the director doesn't have **adds the hook to that director** — it does not create a sibling manager.

**The universal gate ritual (every phase boundary).** A phase is not done until: (1) **clearance clean** — `ValidateRouteClearance` reports no conflicts (report-only; a human moves named waypoints/knots per the report, CoPlay proposes edits and stops); (2) **coverage classes confirmed/accepted** — `HardpointCoverageGizmo` shows every pad ≥2 covered segments and ≥3 Premium pads ≥4, with any class changes explicitly accepted; (3) **model margins in band** — the balance model re-run reports no wave margin moved >0.15 unaccountably, and any late-Normal margin >1.2 has triggered a wave-HP scalar bump of +0.01–0.02 before shipping; (4) **zero console errors**; (5) **CoPlay screenshot set filed** (the drop's before/after at 907×510 legibility and 1920×1080 sign-off).

---

## P0 — Balance model (locate-or-create). One drop.
**Objective:** Stand up the universal gate before touching anything else.
**Tickets:** R1.
**Exit gate:** Model reproduces today's shipped per-wave margins from the live map geometry; committed and runnable; a "today baseline" report is filed.

## P1 — Juice foundation (J Juice). One drop.
**Objective:** Raise felt quality (highest-expectation, lowest-risk) using existing directors.
**Why now:** Juice is cheap, ships immediately, and strategy games' low D1 (25.39%) means first-session feel matters — but it must not mask design, so it comes after the model exists to keep tuning honest.
**Tickets:** R2 (kill-streak combo), R3 (near-miss / CLOSE CALL), R4 (run-stats + records), R5 (hit-stop & screen-kick standard).
**Exit gate:** Universal ritual; combo/close-call read clearly at 907×510; zero new manager classes.

## P2 — Splines (geometry freeze). Two drops.
**Objective:** Replace piecewise-linear routes with a spline backbone; freeze geometry before any composition.
**Tickets:** R6 (spline backbone behind `useSpline`), R7 (merge-knot tangent pinning), R8 (curve-based coverage rewrite), R9 `[GATE]` (spline revalidation + flip default), R10 `[GATE]` (balance model geometry params + re-run with new Lengths).
**Exit gate:** Universal ritual; `useSpline` default on; Length delta % filed; ≤0.05 m merge divergence over the shared tail.

## P3 — Spectacle (S Spectacle). Two drops.
**Objective:** Kill the void, add weather and an establishing flyover — all on now-frozen geometry.
**Tickets:** R11 (surround skirt), R12 `[MANUAL]` (rock/terrain dressing day), R13 (WeatherPreset + applier), R14 (Rain + Dust presets), R15 (establishing flyover), R16 (boss letterbox entrance), R17 (optional double-tap zoom).
**Exit gate:** Universal ritual; ≤5 draw-call skirt; weather ≤3 alpha layers; null preset pixel-identical; legibility held at 907×510.

## P4 — Systems depth (Y Systems). Two drops.
**Objective:** Add the tactical layer that makes runs re-playable.
**Tickets:** R18 (status effects: stun/slow), R19 (Strike Wing active ability — mechanic-first), R20 (wave mutators as WaveDefinition field), R21 (turret veterancy), R22 `[GATE]` (balance model extension terms).
**Exit gate:** Universal ritual; mutators toggle per-wave via SO; Colossus stun-resist honoured; model extension terms in band.

## P5 — Night variant (N Night). One drop.
**Objective:** A lighting variant of the shipped layout (not a new map), plus a support-tower build.
**Tickets:** R23 `[MANUAL]` (night lighting variant), R24 (Floodlight sixth buildable).
**Exit gate:** Universal ritual; ≤10 non-shadowing point lights; Floodlight uses SupportAura pattern + registry check.

## P6 — Generator (content engine). Three drops.
**Objective:** Turn the shipped map into a deterministic pipeline; produce map 2; enable daily-seed.
**Tickets:** R25 (LevelBlueprint SO + menu), R26 (parity rebuild), R27 (route synthesis → splines), R28 (hardpoint candidate scoring + selection), R29 `[GATE]` (three-stage generation gate), R30 (model-driven LevelDefinition emission), R31 (contact-sheet tool), R32 `[MANUAL]` (map-2 authoring day).
**Exit gate:** Universal ritual on generated geometry; a failing blueprint emits no scene; map 2 ships.

## P7 — Retention loop (researched features). Three drops.
**Objective:** Add the local-first, server-free return mechanics, now that the generator and mutators exist.
**Tickets:** R33 (endless survival — model-driven wave extension), R34 (score attack + local leaderboard + share code), R35 (per-map medals → stars → cosmetic/loadout meta), R36 (weekly mutator rotation), R37 (daily-seed challenge), R38 (optional portal leaderboard adapter).
**Exit gate:** Universal ritual; all persistence via existing SaveData; determinism verified (same seed → identical run); no power locked behind money.

---

# THE PROMPT BOOK (R1–R38)

Each ticket is self-contained. Paste one into one CoPlay thread. `Pin:` lines name the real files to attach.

---

### R1 — Balance model: locate or create (P0)
`Pin: @LevelDefinition.cs @WaveDefinition.cs @EnemyDefinition.cs @TowerDefinition.cs @TowerTier.cs @DamageTable.cs @PathRoute.cs @RouteTraffic.cs @WaveManager.cs`
First, search the repo and the developer's machine outside version control for any existing `balance_model.py` or spreadsheet; if none exists, create `docs/balance_model.py` from scratch. The model is **geometry-parameterized**: inputs are route Length(s), hardpoint count and class mix, spawner legs (west ground / north ground / air), plus the SO data (waves, enemy defs, tower DPS from `TowerTier.TotalDps` × the `DamageTable` type-vs-armour multiplier, hpGrowthPerWave, chainBonusPerLiveEnemy/Cap, startingSalvage, coreIntegrity, maxLiveEnemies). For each wave it computes total incoming effective HP vs. deliverable tower damage over the time enemies are in range (derived from Length ÷ enemy speed and per-pad coverage), producing a **per-wave margin** (deliverable damage ÷ required damage). Defaults MUST equal today's live map (130×75 field, ~150 m routes, 8 hardpoints in the shipped mix) and MUST reproduce the current tuning as the baseline. Emit a per-wave table (margin, live-enemy count, salvage curve) and flag any wave margin outside a configurable band. Model assumptions live at the top of the file as named constants.
**Done when:** running the model with default (live) geometry prints a per-wave margin table that matches the shipped game's observed difficulty (no wave flagged unexpectedly), the file is committed, and a `baseline_today.txt` report is saved alongside it. This model is now the universal gate; re-run it before tuning anything.

---

### R2 — Salvage kill-streak combo (J Juice)
`Pin: @GameManager.cs @AudioDirector.cs @VFXDirector.cs @OverlayManager.cs`
Add a streak/combo system that grants bonus salvage on rapid kills. Create a `StreakConfig` ScriptableObject with `[TUNE]` fields: `perStepBonus` (default +5%), `bonusCap` (default +50%), `windowSeconds` (default 2.0). Track streak state in `GameManager` (reset when the window lapses), and route the bonus through the existing salvage path so `OnSalvageChanged` fires normally. Feedback only via existing directors: a new `Sfx.StreakStep` enum entry registered in the sfx table, played with a rising pitch step — the shipped overloads are `Play(Sfx)` / `Play(Sfx, volumeScale)` with only a random per-entry `pitchSpread`, so add a pitch-scaled overload to `AudioDirector` (the hook goes on the director, per the standing rule) — and a world-space combo count via `OverlayManager`. No new HUD manager.
**Done when:** consecutive kills within the window escalate the salvage bonus up to the cap, the combo count is legible at 907×510, the sound steps with the streak, and letting the window lapse resets it. Re-run the balance model before tuning (streak income is a model term — see R22).

---

### R3 — Near-miss "CLOSE CALL" banner + last-kill time dip (J Juice)
`Pin: @GameManager.cs @WaveManager.cs @Canvas_HUD @AudioDirector.cs`
When a wave ends with core integrity below a `[TUNE]` threshold, show a "CLOSE CALL" wave banner inside `Canvas_HUD` and, on the wave's last kill, dip `Time.timeScale` to 0.30 for 0.35 s then restore. The dip must be **interrupt-safe**: if the next wave or a state change occurs, timeScale is restored immediately and cleanly. Add `Sfx.CloseCall` and play via `AudioDirector.Instance.Play`. Drive the banner off a `GameManager` state/event, not a polling loop.
**Done when:** a genuinely close wave shows the banner and the brief slow-mo on the final kill, the effect never leaves timeScale stuck, and it respects the existing 2× speed toggle. Re-run the balance model before tuning.

---

### R4 — Run-stats screen with personal records (J Juice)
`Pin: @ResultScreen.cs @GameManager.cs @SaveData.cs @Canvas_Menus`
Extend the existing `ResultScreen` to show per-run stats (waves cleared, salvage earned, integrity remaining, longest streak, time). Persist per-map + per-difficulty personal records in the existing `SaveData` — a static PlayerPrefs-backed store that already keeps a best score per difficulty (`SubmitScore`), a cleared flag per tier and the mute toggle; add keys/accessors there, don't create a new save system. Show a "NEW RECORD" badge when a stat beats the stored best. All UI lives in `Canvas_Menus`/`ResultScreen`.
**Done when:** finishing a run displays stats, new bests are persisted to SaveData and reloaded across sessions, and NEW RECORD badges appear only on genuine bests. This screen is the sink for medals (R35) and score attack (R34).

---

### R5 — Hit-stop & screen-kick standard (J Juice)
`Pin: @CameraShake.cs @VFXDirector.cs @GameManager.cs @Main Camera`
A camera-feedback system already exists — `CameraShake` on the Main Camera: an additive Perlin trauma shake over a captured rest pose, a 1.5 s **unscaled** cooldown, and the `Shake(trauma)` / `ShakeCoreHit()` / `ShakeFootfall()` helpers (today it fires only on Core hits and Colossus footfalls). **Extend it, don't build a sibling:** add a directional kick method with rapid exponential decay and an optional micro hit-stop, exposed as helpers so future tickets reuse them (never re-implement). Keep the additive rest-pose model so the kick never fights the framed camera position, and gate intensity behind `[TUNE]` fields (default: small kick on `PlayImpact`/`PlayCoreHit`, larger on `PlayExplosion`). Keep it readability-first: exponential decay back to the framed position, no motion-sickness-inducing random jitter, and an accessibility `[TUNE]` scalar (0 = off) that also zeroes the existing trauma shake.
**Done when:** impacts and explosions produce a directional, quickly-settling kick that never breaks the 38°-pitch content framing, the accessibility scalar zeroes it out, and no gameplay-readability regression appears at 907×510.

---

### R6 — Spline backbone in PathRoute behind `useSpline` (P2)
`Pin: @PathRoute.cs @RouteTraffic.cs @EnemyMover.cs`
Add a serialized `bool useSpline` (default OFF) to `PathRoute`. In `Recompute()`, when `useSpline`, build a `UnityEngine.Splines.Spline` from the `waypoints` positions as `TangentMode.AutoSmooth` knots and bake it into an arc-length table (sampled positions + cumulative distance); delegate `SamplePosition(distance, out tangent)` and `Length` to that table. Because `Recompute()` is called from `Awake`, `OnValidate`, **and every `OnDrawGizmos`**, guard it with a dirty-check keyed on a hash of waypoint positions so the rebuild only runs when waypoints actually move — **zero GC allocation per frame** in steady state. Air units are untouched (EnemyMover air path stays route-free straight flight to Core at flightAltitude). Update the gizmo to draw polyline vs. spline overlay for comparison.
**Done when:** with `useSpline` on, ground enemies follow a smooth curve via the same `SamplePosition`/`Length` API, the profiler shows no per-frame allocations from `OnDrawGizmos`, air movement is unchanged, and toggling `useSpline` off restores exact piecewise-linear behaviour. Re-run the balance model before tuning (Length changes — see R10).
**As shipped** (three deliberate notes): (1) `NativeSpline` fills the table inside a `using` scope rather than being cached across frames — same allocation-free evaluation, but no persistent native collection to leak on a domain reload, which matters because the rebuild path runs from `OnDrawGizmos` in edit mode. Tangents come from finite differences along the table. (2) The dirty-check also fixes a **pre-existing** bug: the cumulative-distance array was reallocated on every gizmo frame, splines or not. (3) The `Handles.Label` distance labels format a string per waypoint per frame, so "no per-frame allocations from `OnDrawGizmos`" holds only with those off — they now sit behind a `drawDistanceLabels` toggle. The bake cross-checks its chord-summed length against `NativeSpline.GetLength()` and warns if the table is too coarse, because `Length` feeds the model.

---

### R7 — Merge-knot tangent pinning (P2)
`Pin: @PathRoute.cs @RefineryDeltaBlockout.cs`
The west and north routes carry duplicated, identical-position knots for the shared snake tail, but AutoSmooth derives each knot's tangent from its neighbours — and the pre-merge neighbours differ — so the two splines diverge at the merge. Fix by pinning **identical explicit tangents** at the merge knot on both routes. Add an editor check that samples both route splines every 0.5 m over the shared tail and reports maximum divergence.
**Done when:** the editor check reports ≤0.05 m divergence between the two route splines across the entire shared tail, and enemies from both legs visually track the same curve after the merge. Re-run the balance model before tuning.
**As shipped.** The live geometry makes this tighter than the ticket assumed: the merge is knot **index 2 on both routes** at (−30, 18), each approach leg is exactly 30.000 m, and the shared tail is **119.985 m** (240 samples at 0.5 m). Crucially, **no knot after the merge differs between the routes**, so their AutoSmooth tangents already agree there — pinning is only needed on the merge knot itself, and only its **outgoing** tangent (the shared tail depends on that plus the next knot's incoming tangent, which already matches). The incoming tangent keeps its AutoSmooth value so each route's un-shared approach leg keeps its natural shape; `TangentMode.Broken` is what lets the two differ. Pins are authored data (`PathRoute.TangentPin[]`, folded into the dirty-check hash) written by **Tools → COREHOLD → Pin Merge Knots**, which derives the value from the primary route's own neighbours as (next − prev)/3 = (8.667, 0, 0) — straight along the tail, so the shipped west route barely moves. **Tools → COREHOLD → Check Route Divergence** is the report-only gate and states which mode it measured (with `useSpline` off it compares the identical polylines and must read 0.0000 m).

---

### R8 — Curve-based HardpointCoverageGizmo rewrite (P2)
`Pin: @HardpointCoverageGizmo.cs @PathRoute.cs`
Rework `HardpointCoverageGizmo` to count **knot-interval spans measured on the curve** instead of straight `GetPoint(i)` segments: a span counts as covered when any arc-sampled point within it falls inside the turret's range. Preserve the `SegKey` de-dup of shared snake segments (0.1 m rounding) so shared tail spans aren't double-counted across routes, and preserve the Mortar 6 m dead zone. The load-bearing rule must keep meaning: every pad ≥2 covered spans, at least three Premium pads ≥4. `RouteClearance` needs no changes — it already samples `SamplePosition`.
**Done when:** the gizmo reports covered spans on the spline curve, the ≥2 / Premium ≥4 thresholds are evaluated against curve spans, SegKey de-dup and the Mortar dead zone still hold, and a before/after coverage table is filed for the shipped map.
**As shipped.** A span is tested per **sub-segment** between arc samples, not per sample point, so a span that only clips the ring between two samples is still caught — the curve test is a strict generalisation of the chord test rather than an approximation of it. The Mortar dead zone is the original endpoint rule applied at that finer granularity (a piece lying entirely inside 6 m does not count). `CountCoveredSegmentsLinear()` is retained alongside the curve count purely so the before/after table is one deterministic run instead of a manual toggle-and-compare. Coverage is now **cached** behind a hash of the pad and its routes: the old version rebuilt a `HashSet` of formatted strings for every pad on every `OnDrawGizmos` frame, and curve sampling would have multiplied that — this is the same class of per-frame allocation the R6 dirty-check removed from `PathRoute`.

---

### R9 — `[GATE]` Spline revalidation + flip default (P2)
`Pin: @PathRoute.cs @HardpointCoverageGizmo.cs @RefineryDeltaBlockout.cs`
Validation-only ticket. Run `ValidateRouteClearance` (report-only; the envelope is laneHalfWidth 0.9 + maxBodyRadius 1.35 (the "Breaker" constant) + padRadius 1.5 ⇒ 3.75 m off the route centreline): where it flags a conflict, **a human moves the named knots per the report** — CoPlay proposes the edits and stops, never silently nudges. Run the curve-based coverage (R8); any coverage class change must be **explicitly accepted** in the ticket notes. Capture the 1920×1080 screenshot sign-off set across the three framing aspects. File the route **Length delta %** vs. the pre-spline baseline. Finally, flip `useSpline` default ON.
**Done when:** clearance is clean after human knot edits, coverage classes are confirmed/accepted, the 1920×1080 set is filed, Length delta % is recorded, `useSpline` defaults on, and the console is error-free.
**As shipped.** The four mechanical stages run as one command — **Tools → COREHOLD → Run Spline Gate (R9)** — which writes `docs/spline_gate_report.txt` next to the balance-model baseline, so "filed" means a committed file rather than console scrollback. It reports clearance (report-only; it names knots and never nudges them), the per-pad before/after coverage table with any pad whose pass/fail **flips** called out for explicit acceptance, the R7 divergence check, and per-route polyline-vs-curve length with the delta % that R10 re-baselines against. It deliberately does **not** flip `useSpline` or capture screenshots: the flip is the reward for a clean gate, not part of measuring it, so it stays a human decision made after the knot edits, the written class acceptance, the 1920×1080 set and the R10 model re-run.
**Closed out.** `useSpline` now defaults **ON**. The committed scene carries no serialized value for it (its `PathRoute` components predate R6), so the code default is what the shipped map uses. Two gate items were **waived rather than satisfied**, and both remain open against this map: (1) the coverage rule is **NOT MET** — HP_Premium_2 covers 3 spans where its Premium class needs 4, a *pre-existing* condition that chord and curve counts agree on, caused by the clearance pass moving it from (4, 13) to (7.5, 1.5); the fix is a move to ≈(7.5, 13) (5 spans, 5.0 m clearance) or a relabel with HP_Standard_1 promoted. (2) The scene carries no `tangentPins`, so the two legs **diverge at the merge** until **Tools → COREHOLD → Pin Merge Knots** is run once and the scene saved. Clearance passed clean, and the 1920×1080 sign-off set was not captured.

---

### R10 — `[GATE]` Balance model geometry params + spline re-run (P2)
`Pin: @docs/balance_model.py @PathRoute.cs`
Add first-class geometry parameters to the balance model (route Length(s), hardpoint count/mix, spawner legs) if R1 stubbed them, then re-run with the **new spline Lengths** from R9. Longer curved routes give enemies more time-in-range, which eases margins; quantify the shift and report every wave whose margin moved >0.15. Where late-Normal margins now exceed 1.2, apply a wave-HP scalar of +0.01–0.02 before shipping.
**Done when:** the model runs on the spline geometry, the per-wave margin delta table is filed, any >0.15 movers are explained, late-Normal >1.2 margins are corrected via the HP scalar, and the new geometry is the model's baseline going forward.
**As shipped.** `Route` gained a `scale` (measured spline length ÷ polyline length) applied to **time, not coordinates** — sampling and coverage stay in polyline space so pad-to-route distances remain honest, while a longer curve earns its extra time-in-range. That is sound precisely because R9's gate reported an **identical covered-span count on chords and on the curve for all eight pads**: the curve threads the same knots and the same pockets, so it covers the same spans, just more slowly. Run it with `--measured-lengths WEST,NORTH` and it appends the delta table; the air corridor is a straight flight and is deliberately not scalable.
**Result on the live map** (`docs/spline_margin_delta.txt`): West +2.50% (149.985 → 153.742 m), North +3.02% (→ 154.518 m). **No wave moved >0.15.** Waves 1–9 sit at the 1.50 overkill cap and cannot move — they are over-killed either way — so the only wave with real headroom is the boss, which eases **1.05 → 1.07**. That is inside the ≤1.20 close band, so **no wave-HP scalar bump is owed**. Checked for robustness across every plausible post-re-pin North length (153.89–154.52): wave 10 lands at 1.07 in all cases, so the pending merge re-pin cannot change this conclusion. **Closed out.** Spline geometry is now the model's **standing baseline**: `SPLINE_ROUTE_LENGTHS = {west: 153.742, north: 154.518}` is applied by default, `docs/baseline_today.txt` is regenerated on it across all three tiers, and `--polyline` recovers the pre-spline map for comparison. Those two numbers are a model **input**, not a derived value — re-measure them whenever route geometry changes.

---

### R11 — Surround skirt (S Spectacle)
`Pin: @RefineryDeltaBlockout.cs @CameraFramingSetup.cs @VFXDirector.cs`
**Rescoped — the void is already gone; do not build the skirt as originally written.** Measured against the live camera (pos −12.75, 70.65, −84.16; pitch 38°; vFOV 35°), three facts changed this ticket:

1. **The horizon is never in frame.** The top edge of the frustum sits 20.5° *below* horizontal, so the top of the screen is ground at z ≈ +105 m. A skybox cannot fix anything here — one is already assigned and simply never seen. It becomes relevant only if R15's flyover tilts the camera up, which is a reason to settle the flyover path before finalising this ticket.
2. **The floor is already 300×300** (scale 30, not the blockout's 130×75), which is why no void is visible. It covers the frame completely at 16:9 and 16:10; at 20:9 the far-left corner overruns by **4.1 m**, revealing skybox rather than black. Scale 32 clears all three.
3. **`RefineryDeltaBlockout.BuildFloor` hard-codes `FieldW/10, FieldD/10`** and silently reverts that fix on any rebuild — see R26's stage order.

So the work is: **drive the floor extent from the camera frustum** rather than the design box (intersect each aspect's edge rays with y=0, take max |x| / |z|, add margin); **retint the distance fog**, which is already enabled but set to mid-grey 0.5 against a dark blue-slate field so it reads as haze rather than depth — a one-property change and the highest-value pixel in the ticket; and only then add **3–5 silhouette meshes** in the far band (z ≈ 90–105) for depth. Constraints on those: **unlit**, excluded from lightmaps, darker than the playfield, placed via the container/`Place()` pattern.
**Done when:** the floor extent is computed from the frustum (not the design box) and survives a blockout rebuild, fog reads as distance rather than haze, no void appears at any of the three aspects, the silhouette band adds ≤5 draw calls, and the 907×510 legibility bar is unaffected.

---

### R12 — `[MANUAL]` Rock/terrain dressing day (S Spectacle)
`Pin: @RefineryDeltaBlockout.cs @HardpointCoverageGizmo.cs`
Human-led dressing day on the now-frozen spline geometry. Dress the hairpins and field with owned rock/environment assets following the **sight-line rule**: nothing may occlude a hardpoint's line to its covered route spans or obscure enemy readability. CoPlay places candidates via `Place()`; the human approves each. Re-run coverage after dressing to confirm no span was visually blocked.
**Done when:** the field is dressed, the sight-line rule holds at all pads, coverage is unchanged from R8's accepted table, and screenshots are filed. Geometry is frozen — do not move waypoints/knots here.

---

### R13 — WeatherPreset SO + WeatherApplier (S Spectacle)
`Pin: @GameManager.cs @VFXDirector.cs @Main Camera`
Create a `WeatherPreset` ScriptableObject (ambient tint, fog, precipitation type/density, wind — all `[TUNE]`) and a `WeatherApplier` that applies the preset at map load via a `MaterialPropertyBlock` (no per-object material instances). Precipitation is **camera-attached, screen-space**. A **null preset must be pixel-identical** to today's look. Apply on the appropriate `GameManager` state.
**Done when:** applying a preset changes ambient/precipitation via MaterialPropertyBlock, a null preset produces a pixel-identical frame to the current build, and there is no per-frame allocation.

---

### R14 — Rain + Dust presets (S Spectacle)
`Pin: @VFXDirector.cs @GameManager.cs`
Author two `WeatherPreset` assets — Rain and Dust — using CFXR where appropriate, each with ≤3 alpha layers of overdraw. Verify both hold the **907×510 legibility bar** (enemies and turret states remain clearly readable through the effect).
**Done when:** Rain and Dust presets apply via R13's applier, each uses ≤3 alpha layers, overdraw stays within the mobile budget, and legibility is confirmed at 907×510 with screenshots filed.

---

### R15 — Establishing flyover in Briefing state (S Spectacle)
`Pin: @GameManager.cs @CameraFramingSetup.cs @Main Camera @Canvas_HUD`
Add a ~4 s establishing camera flyover during a Briefing state driven by `GameManager.SetState` — **no Cinemachine, no Timeline**, just a scripted camera move that ends at the framed play position from `CameraFramingSetup`. Support **tap-to-skip** (jumps immediately to play framing). Show a skip hint in `Canvas_HUD`.
**Done when:** entering Briefing plays the 4 s flyover, tapping skips instantly to the correct play framing, no Cinemachine/Timeline dependency is added, and the flyover never desyncs the content-bounds framing.

---

### R16 — Boss letterbox entrance (S Spectacle)
`Pin: @WaveManager.cs @GameManager.cs @Canvas_HUD @AudioDirector.cs`
When the Colossus boss spawns at wave 10, play a 2.5 s letterbox entrance on **unscaled time** with `timeScale = 0` underneath (gameplay frozen during the cinematic). Letterbox bars and any text live in `Canvas_HUD`. Add `Sfx.BossEntrance`. Ensure clean restore of timeScale and interrupt-safety (skippable, and safe if the app loses focus).
**Done when:** the Colossus entrance plays for 2.5 s on unscaled time with gameplay frozen, timeScale restores cleanly, the sequence is skippable, and it never leaves the game paused.

---

### R17 — Optional two-state double-tap zoom (S Spectacle)
`Pin: @CameraFramingSetup.cs @Main Camera @GameManager.cs`
Add an optional two-state zoom toggled by double-tap: the default framed view and one closer view, both respecting the 12%/18% top/bottom margins and content-bounds fitting across the three aspects. `[TUNE]` the close-view FOV/pitch. Off by default if it risks legibility; must not break portrait-locked landscape framing.
**Done when:** double-tap toggles between two valid framings, both respect margins and content bounds across all three aspects, and neither state breaks the 907×510 legibility bar.

---

### R18 — Status effects: stun/slow (Y Systems)
`Pin: @Enemy.cs @EnemyMover.cs @EnemyDefinition.cs @VFXDirector.cs`
Add a status-effect list (a struct list) on `Enemy` for stun and slow. Rules: **refresh-not-stack** (re-applying refreshes duration, doesn't add), effects act through the multiplier side of `EnemyMover`'s speed model (`BaseSpeed` × `SpeedMultiplier` — never mutate the stored base), and **no effect may fully stop a unit**: `EnemyMover.DesiredSpeed` clamps to the `minDesiredSpeed` floor (0.4 m/s) exactly so a future slow/stun can never stall the car-following chain — so "stun" means dropping the unit to that crawl floor for the duration, not a hard halt. Add `stunResistance` to `EnemyDefinition` (Colossus = 25% duration reduction). Show status via `OverlayManager`/`VFXDirector` (new pooled `Effect.Stun` / `Effect.Slow` entries registered in the effects table), not a new component per enemy.
**Done when:** stun drops movement to the minDesiredSpeed crawl and slow scales speed, both refresh rather than stack, base speed is never corrupted, Colossus takes 25% reduced stun duration, and status VFX are pooled through VFXDirector. Re-run the balance model before tuning (stun uptime is a model term — R22).

---

### R19 — Strike Wing active ability, mechanic-first (Y Systems)
`Pin: @GameManager.cs @PathRoute.cs @RouteTraffic.cs @VFXDirector.cs @AudioDirector.cs @Canvas_HUD`
Implement the Strike Wing as a player active ability: cost 120 salvage, 45 s cooldown, one-tap **route-snapped** targeting (the target snaps to the nearest point on a route via `SamplePosition`). On fire, after a 1.2 s telegraph ring (`OverlayManager`), deliver an EM burst: 6 m radius stun 3 s + 50% slow 3 s (uses R18's status system). This ticket is **mechanic-first** — use a placeholder ground ring + burst VFX (`Effect.StrikeWingBurst`, pooled) and `Sfx.StrikeWing`; the Vehicle Constructor flyer presentation is deferred to a later cosmetic ticket. Cooldown/cost are `[TUNE]`/SO. Drive cost through the existing salvage path.
**Done when:** the ability costs salvage, respects cooldown, snaps targeting to routes, telegraphs for 1.2 s, and applies the EM stun+slow in radius via the R18 system, all fed through existing directors and HUD. Re-run the balance model before tuning.

---

### R20 — Wave mutators as optional WaveDefinition field (Y Systems)
`Pin: @WaveDefinition.cs @WaveManager.cs @EnemyMover.cs @EnemyDefinition.cs`
Add an optional mutator field to `WaveDefinition`. Implement four, each reading from SO/`[TUNE]` values: **Storm** (air +30% speed), **Convoy** (single-file — force one lane via RouteTraffic ordering), **Overcharge** (+30% HP, +50% bounty), **Blackout** (acquisition range ×2 outside floodlights — ties to R24). Mutators are data on the wave, applied by `WaveManager`/`EnemyMover` at spawn — no parallel spawner. This field is the foundation for the weekly mutator rotation (R36).
**Done when:** each mutator visibly changes its wave when set on the WaveDefinition, mutators compose without conflict, and clearing the field restores vanilla behaviour. Re-run the balance model before tuning (Overcharge flags feed the model — R22).

---

### R21 — Turret veterancy (Y Systems)
`Pin: @TowerDefinition.cs @TowerTier.cs @GameManager.cs @OverlayManager.cs`
Add kill-tracked veterancy to towers: ranks at 25 / 75 / 150 kills granting +4% damage per rank. Show rank chevrons via `OverlayManager` (world-space, not a new UI layer). Selling a tower **forfeits its rank**. Store rank per-instance; damage bonus reads through the existing damage path.
**Done when:** towers rank up at the kill thresholds, gain +4% damage/rank, display chevrons via OverlayManager, and lose rank on sell. Re-run the balance model before tuning (veterancy ramp is a model term — R22).

---

### R22 — `[GATE]` Balance model extension terms (Y Systems)
`Pin: @docs/balance_model.py @StreakConfig @WaveDefinition.cs @EnemyDefinition.cs`
Extend the balance model with the new systems as first-class terms: **streak income** (+15% on dense waves, +5% otherwise), **stun uptime** (+4.5 enemy-seconds of neutralization per Strike Wing use, minus its salvage cost), **Overcharge flags** (per-wave HP/bounty multipliers), and **veterancy ramp** (+2% per wave from wave 3, capped +12%). Re-run against the live geometry.
**Done when:** the model accounts for streak, stun, Overcharge, and veterancy; the per-wave margin table stays in band; and any wave pushed >1.2 late-Normal is corrected with the HP scalar before shipping.

---

### R23 — `[MANUAL]` Night lighting variant (N Night)
`Pin: @RefineryDeltaBlockout.cs @WeatherPreset @Main Camera`
Author a **night lighting variant of the shipped layout** — not a new map. Ambient 0.25, emissive materials ×1.5–2, ≤10 non-shadowing point lights placed for readability. Human-led; CoPlay assists placement. Reuse the WeatherPreset pipeline where it helps (ambient tint). Geometry unchanged.
**Done when:** the shipped map has a selectable night variant with ambient 0.25, boosted emissives, ≤10 non-shadowing lights, the 907×510 legibility bar holds in the dark, and no geometry moved.

---

### R24 — Floodlight sixth buildable (N Night)
`Pin: @TowerDefinition.cs @GameManager.cs @VFXDirector.cs`
Add a sixth buildable tower, Floodlight: 70 salvage, 12 m light radius, built on hardpoints, following the existing **SupportAura pattern**. Register its lit area so it can be **checked at acquisition** (this is what Blackout mutator R20 reads — enemies outside floodlight get ×2 acquisition range). No new placement system; reuse the pad/registry flow.
**Done when:** Floodlight builds for 70 salvage on a hardpoint, projects a 12 m radius registered for acquisition checks, follows SupportAura, and correctly counteracts the Blackout mutator. Re-run the balance model before tuning.

---

### R25 — LevelBlueprint SO + generate menu (P6)
`Pin: @LevelDefinition.cs @PathRoute.cs @RefineryDeltaBlockout.cs`
Create `LevelBlueprint` in `Corehold.Data`: `playfieldSize`, `randomSeed` (**all randomness derives from it — determinism is a hard rule**), `protectedPrefab` + `protectedNormalizedPos` (default 0.765, 0.413 — the shipped Core at world (34.5, −6.5) normalized on the 130×75 field from its south-west corner), `routeLengthTarget`, `groundSpawnLegs` (1–2), `airCorridor` (bool), `hardpointCount` + a class-mix struct, `envPack`, and `rulesTemplate` (a `LevelDefinition` clone source). Add a `Tools → COREHOLD → Generate Level` menu entry (stub the generation call for now).
**`envPack` is its own ScriptableObject, not a folder path.** Define `EnvPack` alongside the blueprint: a list of **direct prefab references** (not the asset-path strings `Place()` uses today — paths break when assets move) each carrying the metadata a placer actually needs — **footprint radius** (to test route/pad clearance), **height** (to test sight-line occlusion, R28), and a **role** tag (`Landmark` / `MidField` / `Clutter` / `Silhouette`) so the placer can fill each band deliberately. Note that `Assets/Vendor/` is git-ignored, so a pack referencing vendor prefabs carries dangling GUIDs for anyone without those packages — decide up front whether packs are committed or local-only.
**Done when:** `LevelBlueprint` exists in Corehold.Data with all fields, `EnvPack` exists with per-prefab footprint/height/role metadata, the menu item appears, and the SO is documented as the single source of generation determinism (same seed ⇒ same everything).

---

### R26 — Parity rebuild from a blueprint (P6)
`Pin: @RefineryDeltaBlockout.cs @CameraFramingSetup.cs @AudioDirector.cs @VFXDirector.cs @WaveManager.cs`
Generalize `RefineryDeltaBlockout`'s helpers (promote the private statics to `internal`; keep the container/`Place()`/`MakeHP()`/`WireOne()` patterns) so a `LevelBlueprint` can rebuild the **exact shipped map**. Reuse `SetupAudioDirector` / `SetupVFXDirector` / `CameraFramingSetup`. Parity target = the complete live Game-scene root set: GameManager, PoolRegistry, RouteTraffic, AudioDirector, VFXDirector, WaveManager, ResultScreen, DebugConsole, UITheme, EventSystem, Main Camera, Directional Light, Global Volume, LightProbeGroup, ReflectionProbe, Canvas_HUD, Canvas_Menus, Canvas_RotatePrompt, Spawner_West/North/Air, Floor, RefineryLevel — plus the RangeRing objects and the OverlayManager (which today sits parented under a RangeRing, not at the root). **Never target or run `BuildGameScene.cs`** (stale Ticket-28 scaffolding that destroys scene roots).
**Stage order is load-bearing — the ground plane must be fitted AFTER the camera is framed.** Today `BuildFloor` sizes the floor from the design box (`FieldW/10, FieldD/10`) before any camera exists, which is why the shipped map's hand-widened 300×300 floor reverts to 130×75 on every rebuild and why R11 had a void to kill in the first place. Generated maps make this worse: each has its own `playfieldSize` and gets re-solved by `CameraFramingSetup`, so a design-box floor is wrong by a different amount every time. The pipeline must run **place protected structure → synthesize routes → select hardpoints → frame camera to the generated content → fit floor to the camera frustum → dress**, with the floor extent derived from the frustum (R11) rather than from the blueprint.
**Done when:** a blueprint configured to the shipped values rebuilds a scene with the full live root set, the floor is sized from the framed camera rather than the design box, and a full 10-wave run on it is behaviourally identical to the shipped map.

---

### R27 — Route synthesis → splines (P6)
`Pin: @PathRoute.cs @RefineryDeltaBlockout.cs @docs/balance_model.py`
From a `LevelBlueprint`, synthesize routes as Splines with AutoSmooth knots at 3–4 seeded hairpin anchors (folded-diagonal layout, merge at ~20% of route length, keep a 4 m field margin). Apply the merge-knot pinning from R7 — generated routes merge two legs exactly like the shipped map, so they inherit the AutoSmooth divergence wholesale and cannot be correct without it. Pin the **world-space** tangent: `BezierKnot` stores tangents in the knot's local frame, so writing the same value to two routes with different approach directions points them different ways (this cost a debugging round on the shipped map). Iterate the anchor positions deterministically (from `randomSeed`) until spline `Length` hits `routeLengthTarget` ±5%.

**Fold width is a hard synthesis constraint, not an aesthetic choice.** Hardpoints live in the pockets between a hairpin's parallel legs, and the pocket width W decides whether a pad can exist there at all — so the route synthesiser, not the pad placer, is what makes good pads possible. Measured off the shipped map, whose folds run **10 m** (x = −19/−9) and **11 m** (x = 2/13) apart:
- **W ≥ 7.5 m**, or no pad fits: a centred pad sits W/2 from each leg and the clearance envelope is 3.75 m.
- **W ≤ ~20 m**, or the shortest-ranged turret (Arc Node, 10 m) cannot reach both legs from the centre.
- **W ≥ 12 m** for any fold intended to host a Mortar, because a centred pad in a narrower fold has both legs inside its 6 m dead zone — which is exactly why the shipped Mortar is an Overwatch pad set back at (24, −8) rather than nested in a fold.

Synthesize hairpins at **W ≈ 10–14 m** and the Premium pads fall out of the geometry; synthesize outside the band and R28 will be unable to form a legal set no matter how it scores.
**Refactor this ticket owns.** R6 baked the spline inside `PathRoute`'s private rebuild, driven by `Transform[] waypoints` — so measuring a candidate route today means instantiating GameObjects and triggering a rebuild, which is wasteful in a length-targeting search loop that iterates per seed and runs again for each of R31's nine seeds. Extract the bake into a MonoBehaviour-free helper over a plain point list (`BakeArcTable(IList<Vector3> knots, int samplesPerCurve, …) → length`) that both `PathRoute` and the generator call. Mechanical, but do it here rather than bolting a second bake path onto the generator. (`SplineUtility.ConvertIndexUnit(…, PathIndexUnit.Distance, PathIndexUnit.Normalized)` is the package-native alternative if exact distance→t is ever wanted over the flat table.)
**Done when:** generated routes are pinned splines whose Length lands within ±5% of the target, hairpins respect the 4 m margin, the merge divergence check (≤0.05 m) passes, and the same seed reproduces identical routes.

---

### R28 — Hardpoint candidate scoring + selection (P6)
`Pin: @HardpointCoverageGizmo.cs @PathRoute.cs @RefineryDeltaBlockout.cs`
Generate a candidate superset of ~20 hardpoint positions at ≥3.75 m from the centreline — **seed the fold centres explicitly** (per R27's width band) rather than only offsetting generically from route bends, because the pocket between two parallel legs is where 4+ span coverage actually lives. Score every candidate once with the curve sampler per `TurretKind`. Classify with the existing `PadClass` semantics: **Premium** (4+ covered spans), **Standard** (2–3), **Rear** (final approach + air-terminal leg), **Overwatch** (Siege Mortar home — set back, sparse close coverage inside the 20 m ring / 6 m dead zone). Discard any <2 spans. Run **one deterministic greedy selection pass** by class need, tie-broken by score, enforcing a minimum inter-pad spacing (~6 m, `[TUNE]`).

**Classify from the measurement, never from an intent label — this is the ticket's whole point.** The shipped map shows the failure it prevents: a human declared HP_Premium_2 a Premium pad and separately chose its position, the clearance pass later moved it from (4, 13) — 5 spans but only 2.0 m off the centreline — out to (7.5, 1.5), which cleared the envelope and silently dropped it to 3 spans, and nothing re-measured. Clearance and coverage were each satisfied in turn and the pad ended up failing its own class. Here that cannot happen: **clearance is a precondition of candidacy and coverage is the score over the survivors**, so both hold jointly by construction, and a pad is Premium *because* it measured 4+, not because it was named one.
**Environment props reuse this exact machinery — and need a check that does not exist yet.** Dressing from the blueprint's `EnvPack` (R25) is the same candidate → reject → score → deterministic-greedy pass, with rejection on footprint radius + `laneHalfWidth` + `maxBodyRadius` off any centreline, plus a keep-out around pads and the protected structure. **But the sight-line rule R12 states has no implementation behind it, and the obvious check does not work:** `HardpointCoverageGizmo` is a pure *distance* test with no notion of occluders, so a 12 m storage tank parked between a pad and the route still reports every span as covered. R12's "re-run coverage after dressing" would therefore pass a fully blocked pad. Build the missing test here: for each pad, sample its covered spans and test the pad→sample segment against every prop's footprint cylinder at turret muzzle height, dropping spans that are blocked. Automated dressing is unsafe without it. Props also spend draw calls against the GDD budget, so the pack should carry batching intent (`TurretMeshCombiner` is the precedent).
**Done when:** the generator produces a classified, spaced hardpoint set matching the blueprint's count/mix, every kept pad has ≥2 spans with ≥3 Premium at ≥4, selection is deterministic from the seed, no pad sits closer than the spacing minimum, and coverage is re-evaluated through the occlusion test after props are placed.

---

### R29 — `[GATE]` Three-stage generation gate (P6)
`Pin: @RefineryDeltaBlockout.cs @HardpointCoverageGizmo.cs @docs/balance_model.py @PathRoute.cs`
Wire a three-stage gate into generation: (1) **clearance** on live numbers — inside generation a knot adjustment IS allowed but must be **logged and followed by a full re-run of coverage + model**, ≤3 passes, then **fail loudly with an actionable report** (never emit-and-warn); (2) **coverage** on the final geometry; (3) **model margins** in band. A blueprint that fails any stage **emits no scene**.
**Reject and reseed — never hand-repair.** The stages must be re-run **as a set** after any adjustment, because clearance and coverage can pull against each other: fixing one by moving geometry can silently break the other, which is precisely how the shipped map ended up with a Premium pad covering 3 spans (see R28). A seed that cannot satisfy every stage inside the ≤3-pass loop is **discarded**, not patched — throwing away a seed costs nothing and R31's contact sheet hands the human nine that passed, whereas hand-repairing one bad map costs an afternoon and reintroduces exactly the drift this gate exists to prevent. That trade is the entire scalability argument for the generator.
**Done when:** a valid blueprint passes all three stages and emits a scene; an invalid one emits nothing and prints an actionable failure report naming the offending knots/pads/waves; and the ≤3-pass clearance loop is enforced.

---

### R30 — Model-driven LevelDefinition emission (P6)
`Pin: @LevelDefinition.cs @WaveManager.cs @docs/balance_model.py @RouteTraffic.cs`
On successful generation, emit a `LevelDefinition`: clone `rulesTemplate`, then **solve `hpGrowthPerWave` so margins match the shipped map's band** (use the balance model as the solver), and set `maxLiveEnemies` from the generated routes' derived capacity — at generation time compute Σ `PathRoute.TotalCapacity(largestRadius)` over the ground routes plus the air-corridor allowance, the same math `RouteTraffic.DerivedCapacity(largestRadius)` runs on live tracks in play (it reads registered movers, so it cannot be called on an unplayed scene). Wire spawners (index 0 west ground / 1 north ground / 2 air) via the existing `WireOne`.
**How C# reaches the solver — the one unresolved integration in the generator chain.** The balance model is Python and the generator is C#. Do **not** port the margin math into C#: two implementations of the gate will drift, and the whole point of R1 is that there is exactly one. Generation is editor-time, so shell out — run `python3 docs/balance_model.py` from the editor with the generated geometry and read back `--json`, which R1 already emits. `--measured-lengths` already accepts route lengths; extend the CLI with hardpoint count/mix as the generator needs it. That keeps one model, one band, and one place to re-tune. It does make Python a dev-machine dependency for generation, which is acceptable for an editor tool and should be stated in the failure message when the process cannot start.
**Done when:** generated levels ship a LevelDefinition whose modeled per-wave margins sit in the shipped band, maxLiveEnemies is derived from the generated capacity, the model is invoked as a subprocess rather than reimplemented, and a full 10-wave run is winnable at parity difficulty.

---

### R31 — Contact-sheet tool (P6)
`Pin: @RefineryDeltaBlockout.cs @CameraFramingSetup.cs @docs/balance_model.py`
Build an editor tool that runs **9 seeds through the full generation gate**, captures a top-down orthographic screenshot of each, and assembles a **3×3 grid PNG** plus a per-seed table (route Lengths, pad mix, modeled margin summary, pass/fail). This is the human's map-selection surface.
**Done when:** the tool outputs a 3×3 contact-sheet PNG and a per-seed data table for 9 gate-passing seeds in one run, letting the human pick a seed at a glance.

---

### R32 — `[MANUAL]` Map-2 authoring day (P6)
`Pin: @RefineryDeltaBlockout.cs @WeatherPreset @HardpointCoverageGizmo.cs @WaveManager.cs`
Human-led: pick a seed from R31's contact sheet, then author map 2 — dress the generated hairpins, extend the skirt, choose a weather preset, **re-run the gate after dressing**, bake lighting, and write map-2 wave tables (via LevelDefinition). CoPlay assists dressing/placement; the human owns aesthetic and difficulty decisions.
**Done when:** map 2 is a fully dressed, gate-clean, baked scene with its own wave tables, selectable alongside map 1, and a 10-wave run on it passes the universal ritual. Map 2 ships this drop.

---

### R33 — Endless survival: model-driven wave extension (P7)
`Pin: @WaveManager.cs @LevelDefinition.cs @docs/balance_model.py @GameManager.cs`
After the final defined wave, enter an endless state via `GameManager.SetState` and **procedurally extend waves driven by the balance model**: each new wave's HP/speed/count budget is solved so the modeled margin holds a slowly-tightening target (mirror BTD6 freeplay ramping — continuous per-wave HP and speed increase). Reuse existing enemy defs and spawners; respect the `min(maxLiveEnemies, DerivedCapacity)` cap. Track the reached wave for scoring (R34).
**Done when:** clearing wave 10 transitions to endless, each extended wave is model-budgeted (no difficulty cliff, no trivial plateau), the live-enemy cap is respected, and the reached wave is recorded. Re-run the balance model before tuning the ramp curve. *(Reference: BTD6 freeplay — proven endless pattern for a bounded-wave core.)*

---

### R34 — Score attack + local leaderboard + share code (P7)
`Pin: @ResultScreen.cs @SaveData.cs @GameManager.cs @Canvas_Menus`
A run score already exists — `SaveData.ComputeScore` = wavesCleared·1000 + integrityRemaining·250 + salvageUnspent + difficultyBonus (0 / 2500 / 6000), with the per-difficulty best kept by `SubmitScore` and already submitted from `ResultScreen`. Extend that formula with streak and time terms, and persist a **local leaderboard** in the existing `SaveData` (per map + mode). On the `ResultScreen`, generate a **shareable code/string** stamped with the seed and score (the Spelunky/Slay-the-Spire "same seed, share text" pattern) so players can challenge friends on identical terms. **Do not trust any client-submitted remote score** — local + share code only here.
**Done when:** every run produces a score, the local leaderboard persists and reloads, and a share code encoding seed+score is generated and copyable from ResultScreen. *(Reference: web-portal TDs — leaderboards + instant restart are what make browser TDs sticky.)*

---

### R35 — Per-map medals → stars → cosmetic/loadout meta (P7)
`Pin: @ResultScreen.cs @SaveData.cs @GameManager.cs @Canvas_Menus @TowerDefinition.cs`
Define per-map medal criteria — **no-leak** (integrity untouched), **no-sell** (never sold a tower), **under-time** — evaluated at run end and feeding the R4 run-stats screen. Medals grant **meta-stars** — a spendable currency, distinct from the per-run 1–3 star integrity rating `ResultScreen` already displays (3 at ≥90% of starting integrity, 2 at ≥50%, 1 above zero); stars unlock **cosmetic or starting-loadout variety only** (e.g., turret skins from the hand-painted set, alternate starting-salvage/loadout presets) — **never power, never P2W**. Persist stars/unlocks in `SaveData`. Include a free "respec"/reset for any spent stars.
**Done when:** medals evaluate correctly at run end, stars accrue in SaveData, unlocks are strictly cosmetic/loadout with no combat-power advantage, and stars can be reset freely. *(Reference: Kingdom Rush stars + BTD6 Monkey Knowledge — non-P2W meta that skill content like CHIMPS can ignore.)*

---

### R36 — Weekly mutator rotation (P7)
`Pin: @WaveDefinition.cs @WaveManager.cs @GameManager.cs @SaveData.cs @Canvas_Menus`
Build a weekly mode that applies a deterministic set of the R20 mutators to a run, rotating on a 7-day boundary derived from the date (no server — the date is the seed). Surface the active mutators in `Canvas_Menus`. Track completion/best in `SaveData`. This is the "mid-term" layer of the cadence pyramid.
**Done when:** the current week's mutator set is derived deterministically from the date, applies to the run via the R20 field, is shown in the menu, and completion persists — identical for every player in that week without any backend. *(Reference: Slay the Spire custom/daily modifiers; BTD6 fortnightly rotation.)*

---

### R37 — Daily-seed challenge (P7)
`Pin: @LevelBlueprint.cs @RefineryDeltaBlockout.cs @WaveManager.cs @ResultScreen.cs @SaveData.cs @Canvas_Menus`
The flagship retention feature. Generate a **seed-of-the-day** from the UTC date, run it through the generator gate (R29) to produce that day's map + wave tables, and offer **one attempt per day** tracked in `SaveData`. All players worldwide get the identical layout and enemies. On finish, produce the R34 share code stamped with the date. Reset at UTC midnight. If a generated daily seed ever fails the gate, deterministically fall back to the next passing seed for that date (logged).
**Done when:** the daily seed is date-derived and identical across devices, one attempt per day is enforced locally, the run flows through the existing gate/WaveManager/ResultScreen, a dated share code is produced, and it resets at UTC midnight. *(Reference: Isle of Arrows Daily Defense, Slay the Spire Daily Climb, Spelunky daily — the single most proven server-free retention loop in the genre.)*

---

### R38 — Optional portal leaderboard adapter (P7)
`Pin: @ResultScreen.cs @GameManager.cs @SaveData.cs`
Add a thin, optional adapter that submits scores to a **host-provided sanctioned leaderboard SDK** (e.g., a Poki/CrazyGames leaderboard API) **only when running on that portal and only through the host's SDK** — never a custom trust-the-client HTTP submit. Local leaderboard (R34) remains the source of truth; the portal board is a best-effort mirror. Feature-flag it off for the standalone/WebGL-on-own-domain build.
**Done when:** on a supported portal the score submits via the host SDK, on any other build the adapter is inert and the local leaderboard is unaffected, and no unsanctioned client-trusting network call exists. *(Reference: WebGL leaderboards that trust the client are trivially spoofed — use host SDKs or stay local.)*

---

# SUGGESTED ORDER & DEPENDENCY SUMMARY

## Suggested order (weekly drops)
1. **Drop 1 — P0:** R1 (balance model). *Nothing else may ship until this exists.*
2. **Drop 2 — P1:** R2–R5 (juice).
3. **Drops 3–4 — P2:** R6–R7 (spline backbone + pin), then R8–R10 (coverage rewrite + gates). *Geometry freezes here.*
4. **Drops 5–6 — P3:** R11–R14 (skirt, dressing, weather), then R15–R17 (flyover, boss letterbox, zoom).
5. **Drops 7–8 — P4:** R18–R19 (status + Strike Wing), then R20–R22 (mutators, veterancy, model gate).
6. **Drop 9 — P5:** R23–R24 (night + Floodlight).
7. **Drops 10–12 — P6:** R25–R26 (blueprint + parity), R27–R28 (route + hardpoint synthesis), R29–R32 (gate, emission, contact sheet, map-2 day).
8. **Drops 13–14 — P7:** R33–R35 (endless, score attack, medals/stars), R36–R38 (weekly mutators, daily seed, portal adapter).

## One-page dependency summary
- **R1 (balance model) blocks everything balance-touching** — it is the universal gate re-run in R10, R22, R30, R33, and every "re-run before tuning" line.
- **R6 (spline backbone) → R7 (pin) → R8 (coverage) → R9 (revalidation/flip) → R10 (model re-run).** R7 depends on R6; R8 depends on R6; R9 depends on R7+R8; R10 depends on R9. **All of P3 depends on P2 being frozen** (dressing/flyover on stable geometry).
- **R18 (status system) is a hard dependency of R19 (Strike Wing uses stun/slow) and of the R20 Blackout ↔ R24 Floodlight interaction.** R22 depends on R2 (streak), R18 (stun), R20 (Overcharge), R21 (veterancy).
- **Generator chain:** R25 (blueprint) → R26 (parity) → R27 (routes, which reuse R7's pin) → R28 (hardpoints, which reuse R8's curve sampler) → R29 (gate, which reuses clearance + coverage + model) → R30 (emission, which uses the R1 model as solver) → R31 (contact sheet) → R32 (map-2 day). R27 depends on P2; R28 depends on R8; R30 depends on R1.
- **Retention chain:** R33 (endless) depends on R1 + WaveManager; R34 (score) depends on R4's SaveData extension; R35 (medals/stars) depends on R4 + R34; R36 (weekly mutators) depends on R20; **R37 (daily seed) depends on the entire generator gate (R29) + R34's share code**; R38 depends on R34.
- **Nothing in P7 ships before P6's gate (R29) exists**, because the daily seed and any future generated content route through it.

## Recommendations
- **Ship R1 first, alone, this week.** It is the cheapest way to buy safety for every future tune. Do not let P1 juice tempt you to skip it — juice that masks a broken curve is a net negative. **Threshold to proceed:** the model reproduces today's per-wave margins with no wave flagged unexpectedly.
- **Treat the P2→P3 boundary as sacred.** Do not begin any dressing, weather, or flyover ticket until `useSpline` is default-on (R9) and the model has re-baselined on spline Lengths (R10). **Threshold to proceed to P3:** ≤0.05 m merge divergence, coverage classes accepted, Length delta filed, model in band.
- **Prioritise the retention loop's ordering by proven impact.** If drops slip, ship in this priority within P7: (1) **R37 daily-seed** (the single most proven server-free retention driver — Spelunky/Slay-the-Spire/Isle of Arrows), (2) **R33 endless + R34 score attack** (gives the daily and the base game a scored spine), (3) **R35 medals/stars** (D30-oriented meta), (4) **R36 weekly mutators**, (5) **R38 portal adapter** (only where a host SDK exists). Given strategy-genre D7 sits at ~8% and top-quartile all-genre D7 is only 7–8%, the daily loop is where marginal retention is won.
- **Instrument before you optimise.** Add lightweight local counters (runs/day, daily-seed attempts, endless wave reached) to `SaveData` alongside R4 so you can see whether a drop moved behaviour. **Benchmark that would change the plan:** if daily-seed attempt-rate is low after two weeks live, front-load a share-to-friend nudge and a "yesterday's seed" replay-for-fun (no leaderboard) before building more content.
- **Keep meta strictly cosmetic/loadout.** The moment a star unlock touches combat power, you have created a P2W-adjacent gate and the Thronefall-style backlash risk. Re-affirm this in every R35-adjacent ticket.
- **For installed mobile/PWA builds only, add a single daily-seed reminder notification** — Airship's data (3× retention for users notified in the first 90 days) makes one well-timed, opt-in daily nudge one of the highest-ROI additions; do not build it for the plain WebGL build where push is unavailable.

## Caveats
- Retention benchmark figures (GameAnalytics 2025: median D7 3.42–3.94%, top-quartile D7 7–8%, top-quartile D1 26.48–27.69%, median daily playtime 22 minutes; AppsFlyer/Mistplay strategy D1 25.39% / D7 8.06%; the Unity +4pp D7 rewarded-format lift; Airship's 3× / 190% first-90-day notification figure from a 47-million-user study) largely reflect **marketed mobile titles**; COREHOLD as an organic web/mobile indie should expect the lower pooled medians (~22–23% D1, ~3–4% D7) and treat higher mid-core numbers as aspiration for a marketed build, not a launch expectation. The strategy-specific 25.39%/8.06% figures originate from AppsFlyer Q3 2022 data re-published by Mistplay — directionally sound but not fresh.
- Portal leaderboards (R38) depend on each host's SDK terms; treat as best-effort and keep the local leaderboard authoritative.