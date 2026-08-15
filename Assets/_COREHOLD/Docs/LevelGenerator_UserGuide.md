# COREHOLD Level Generator — User Guide

*For everyone on the team. No generator code knowledge assumed.*

The Level Generator turns a **LevelBlueprint** asset into a complete, playable scene: routes, hardpoints, camera, floor, dressing, weather, and a balanced `LevelDefinition` wired into the WaveManager. Press Play and it runs ten waves.

Two properties define it:

- **Deterministic.** Every random decision derives from the blueprint's seed. Same seed = same map, same theme, same weather — on every machine. There is no "roll again until it looks nice" hidden anywhere.
- **Gated.** Four gates measure the map as it's built. A map that fails any gate is **discarded entirely** — no scene, no assets, just a report explaining why. The generator cannot hand you a broken level; the worst it can do is refuse.

---

## 1. What you need

| Requirement | Why | If missing |
|---|---|---|
| Unity project, compiled clean | Everything runs in-editor | — |
| **Python 3 on PATH** (`python3` or `python`) | Gate 3 runs `docs/balance_model.py` as a subprocess — the balance math lives there and only there | Generation fails at gate 3 with a message saying exactly this. The *shipped game* never needs Python — dev machines only |
| A **theme pack** with art (optional) | Dressing. See §8 | Levels generate undressed (a warning, not an error) |

---

## 2. Quick start A — rebuild the shipped map (2 minutes)

This is the sanity path. Do it once before anything else.

1. **Tools → COREHOLD → Level → Create Starter Blueprint** — authors `Blueprint_RefineryDelta` with the shipped map's measured values, the best-tested starting configuration for new maps.
2. **Tools → COREHOLD → Level → Level Generator** — the window opens with the blueprint picked up.
3. Press **Generate**. Watch the progress bar walk the 18 stages; total time is dominated by the balance model (a few seconds).
4. All rows green → press **▶ Enter Play Mode**. Play a wave or two.

You just rebuilt Refinery Delta through the full gated pipeline. The scene is at `Assets/_COREHOLD/Scenes/Generated/`, its rules asset at `Assets/_COREHOLD/Data/Levels/Generated/`.

## 3. Quick start B — your first generated map (2 minutes)

1. In the Generator window, open **Create a new map**.
2. Give it a **name**, pick a **shape** (see §4), pick a **theme** if you have one, pick a **pace**.
3. **Create map** → **Generate**.

That is the whole flow. The four choices are all a new map needs; everything else is defaulted to values that work, the Core is placed where your chosen shape needs it, and the seed is pre-picked to one that actually synthesizes — so a new map opens ready to generate rather than on a refusal you didn't cause.

**Do not duplicate `Blueprint_RefineryDelta` to start a new map.** It carries the shipped Core position, which is right for a corridor and wrong for anything that surrounds the Core, and you will meet a refusal about approach rings for a field you never chose.

Didn't like the result, or a gate refused the seed? **Seed +1 → Generate.** Reseeding is free and is the intended loop — see §10.

---

## 4. The Generator window

Top to bottom:

- **Blueprint** — the asset being generated. The window offers to create the starter blueprint when none exists.
- **Mode** — the two-button toolbar, and the most consequential control here:
  The generator synthesizes a NEW map from the seed on every run — the parity "rebuild shipped map" mode retired when the generator became a pure new-level tool (the hand-built Game.unity simply remains level 1).
- **Seed** — with **Seed +1** and **Random** buttons. Changing it clears stale results; edits are undoable.
- **Hardpoints** — **Total pads** plus the per-class breakdown (P/S/R/O). The blueprint stores *only* the breakdown; the total is its sum, so the two can never disagree. Type a new total and the mix **re-spreads itself**: growth goes to **Standard** (the only class with no structural precondition — Premium needs geometry scoring 4+ spans, Rear needs the final approach, Overwatch needs a ≥12 m fold), shrink comes off Standard, then Overwatch, then Rear, and Premium never drops below 3 because the coverage rule needs three pads at 4+ spans. Asking for more pads is a *request*: if the geometry can't host them, gate 2 refuses and names the class that came up short.
- **This seed draws** — a preview of the theme, weather, and ground *this exact seed* will pick from the pools, before you spend any time generating.
- **Validation** — live errors/warnings for the blueprint. Errors disable Generate: the pipeline refuses to start on an invalid blueprint rather than emit a half-right scene.
- **Suggested fixes** — the part you should actually use when something refuses. The window preflights the blueprint by running the real route synthesis on throwaway copies, and if it cannot generate *on any seed*, it searches for the smallest single change that makes it and offers that as a button. Each one explains the problem in map terms rather than geometry terms, and says what the change costs. Every suggestion has been generated once before it is offered, so "this works" means it worked. **A refusal here blocks Generate**, because a structural failure is not something reseeding can fix — see §10.
- **Generate until it passes** — generates, and on a gate failure retries the next seed, up to 6 times, stopping the moment a map passes. This is the reseed loop without the reading; the seeds it burns stay on the blueprint, so the map you get is still exactly what that seed makes. Six failures in a row is not bad luck — that is when the fix panel has something to say.
- **From the last run** — fixes for what only a built scene can reveal. The commonest: your mix asks for more pads of a class than this shape can host. Pad classes are earned by measurement, so the panel tells you the number the map actually offers and offers to ask for that instead.
- **Generate** — labelled with the stage and gate count. During the run a **cancelable progress bar** shows stage n/18 plus sub-stage detail (candidate scoring, model elapsed time). **Cancel is safe**: it routes through the same discard as a gate failure and leaves nothing behind.
- **Pipeline map** — always visible. Before a run: every stage pending (○), the four gates badged **⛨** in amber. After: ✓ / – (skipped) / ✗ per stage with timings; a failure shows in red and everything below it reads "not reached".
- **Copy report** — the full transcript to your clipboard. **This is how you report a failed seed**: paste it in chat and whoever looks at it sees the gate, the offending pads/knots/waves, and the timings, without a screenshot.
- **Authoring utilities** — the EnvPack tools and Organize Hierarchy, for convenience.

`Tools → COREHOLD → Level → Generate Level (headless)` runs the identical pipeline and prints the same transcript to the Console — for scripts and batch use.

---

## 5. The 18 stages

| # | Stage | What it does |
|---|---|---|
| 1 | Validate blueprint | The same checks the window shows live. Errors stop here. |
| 2 | Draw theme & weather | The seed picks from `envPackPool`; weather comes from the **theme's** pool unless the blueprint overrides. Empty pool = undressed (skip, not failure). |
| 3 | New scene + containers | Fresh scene; the five hierarchy containers (`_Systems`…`_Rendering`) created first so everything is born grouped. Offers to save your open scene — never builds over unsaved work. |
| 4 | Scene skeleton | GameManager, WaveManager, PoolRegistry, RouteTraffic, DebugConsole, ResultScreen, directors, all three UI canvases — and the EventSystem's input module swapped to the new Input System (otherwise the menu renders but no click registers). |
| 5 | Protected structure | The Core, at the blueprint's normalized position, using `protectedPrefab` if set (else the shipped platform stack). |
| 6 | Routes + spawners | Seeded synthesis — entrance legs, merge at ~20% of route length, 2–3 hairpin folds at exactly `foldWidth`, length fitted to ±5% of target. Merge tangent pinned (two legs would otherwise diverge on the shared tail). **Siege:** one serpentine per sector — an outer run with folds, a return run ~12.5 m inside it, and a near-radial tail to the Core; all congruent, angular span fitted to the length target; no merge to pin. It is the corridor snake bent into an annulus, deliberately: the gap between the runs is a fold-pocket-grade pad band, and the annulus inside is left empty for Rear/Overwatch pads. Spawners created and wired — ground approaches step over index 2, which every shipped wave table uses for air. |
| 7 | **⛨ GATE 1 — clearance** | See §6. |
| 8 | Hardpoints | Grid candidates filtered by clearance, scored by the real coverage validator, classified from measurement, picked deterministically with 5 m spacing. Each pad gets a visible `PadMarker` disc — a bare TowerHardpoint has no renderer. |
| 9 | **⛨ GATE 2 — coverage** | See §6. |
| 10 | Camera framing | The fixed-camera solve against the *generated* content bounds. |
| 11 | Floor fit + theme ground | Creates the ground if the scene has none, sizes it from the **camera frustum** (never the design box). **With a theme `groundPrefab`: fit only** — it was authored with its own material and tiling, so those pack fields are ignored. **Without one:** a plane is created and the pack's `groundMaterial` + `groundTilingPerMetre` are applied, tiling recomputed for the fitted size. |
| 12 | Dressing | Silhouette band, then themed props with measured footprints/heights, each stamped with a `PlacedProp` marker. Includes automatic self-repair against sight-lines (§6, gate 2b). |
| 13 | **⛨ GATE 2b — occlusion re-run** | See §6. |
| 14 | Weather | The WeatherApplier wired to the drawn preset (or the null preset — pixel-identical authored look). |
| 15 | Group & verify hierarchy | Sweeps stray roots into containers, then verifies: a second pass must move **zero** objects. Failure names the root to add to `SceneContainers.Groups`. |
| 16 | Emit LevelDefinition | Clones `rulesTemplate`; **every map gets a solved `hpGrowthPerWave` and a derived `maxLiveEnemies`**. Runs the balance model (needs Python). |
| 17 | **⛨ GATE 3 — model margins** | See §6. |
| 18 | Save scene | Only reachable with every gate green. Scene + asset written, and the scene is **registered in Build Settings** — `SceneManager.LoadScene` only accepts scenes on that list, so without it the map plays but Retry cannot reload it. Generated-scene entries whose file was deleted are pruned in the same pass. |

Any ✗ triggers the **Discard** row: half-built scene closed unsaved, created assets deleted. A failed run's only output is its report.

## 6. The four gates

**GATE 1 — clearance (routes).** Measured on the curves as enemies actually walk them: spline length within ±5% of `routeLengthTarget`; interior knots inside the 4 m field margin; no self- or cross-approach closer than 4.5 m (two lane bands would overlap). The merge is exempt — routes joining is a designed pinch. On synthesized routes the gate may **clamp margin-breaching knots back inside the field** — at most 3 passes, every move logged with before/after coordinates. Separation violations are never repaired, only reseeded: nudging fold legs apart moves the pockets the pads depend on.

**GATE 2 — coverage (pads).** Judged by the actual `HardpointCoverageGizmo` components in the scene — the same validator the shipped map was authored with. Every pad ≥ 2 covered spans; every Premium ≥ 4; at least 3 Premium pads; the class census must equal the blueprint's mix.

**GATE 2b — occlusion re-run (dressing).** Two different sight lines, both checked, because they answer different questions.

*Does the pad still work?* The distance-based count can't see a 12 m tank parked between a pad and its route. Every pad is recounted through **turret sight lines**: muzzle (1.5 m) to target (1.0 m) against every placed prop's cylinder at its *placed* size. The Mortar is exempt (arcing shell). The placer self-repairs first — deleting the prop that blocks the most spans, up to 10 removals — so this only fails when dressing and pads genuinely cannot coexist on this seed.

*Can the player see the pad?* The **camera sight line**, camera → pad, is a separate test. At the fixed 38° pitch a 12 m landmark hides roughly 15 m of ground behind it, well past the 6 m pad keep-out, so a prop can leave a pad fully functional and completely invisible. The placer refuses any position that would hide a pad, and this gate re-verifies it: a hidden pad fails the seed.

*Can the player watch the approach?* The route gets a **budget** rather than absolute protection, because it is 150 m of ground rather than a point — protecting every metre would exclude a band behind every prop position and leave the field bare. At most **6% of the route** may be hidden from the camera (~9 m on the shipped 154 m route: a couple of short stretches behind props, not a screen). The placer spends the budget as it places, charging each prop only for route nothing else was hiding; this gate re-measures the finished scene and fails the seed if the total is over. Shared route between the two entrances is counted once, so "a metre of route" means a metre of ground.

Both apply to generated dressing.

**GATE 3 — model margins (balance).** The scene's real geometry (route lengths, pad positions and turrets) goes to `docs/balance_model.py`. The model **solves** `hpGrowthPerWave` so the boss wave lands mid-band (~1.10). Every wave's margin must sit in the accepted band (≥1.00 all waves, boss ≤1.20). Out of band at the solved value means this geometry can't be balanced by growth alone — reseed.

---

## 7. Blueprint field reference

| Field | Meaning | Shipped value / notes |
|---|---|---|
| `randomSeed` | The only source of randomness. | Any int. Same seed = same everything. |
| `playfieldSize` | Design box in metres. Does **not** size the floor (the frustum does). | 130 × 75 |
| `protectedPrefab` | What you're defending. Empty = shipped platform stack. | warning if empty |
| `protectedNormalizedPos` | Core position, 0–1 from the field's SW corner. | (0.765, 0.413) → world (34.5, −6.5) |
| `routeLengthTarget` | Spline length the synthesis fits to ±5%. Balance-load-bearing. | 154 m |
| `foldWidth` | Hairpin pocket width — **hard constraint**. <7.5 m: no pad fits the pocket (validation error). >20 m: Arc Node can't reach both legs (error). <12 m with an Overwatch in the mix: warning — the Mortar pad will sit outside the folds. | 10–11 m shipped; field default **12** (the default mix asks for an Overwatch pad, which needs ≥12) |
| `topology` | The map's SHAPE, as one named parameter. **Corridor** (shipped: two legs merge into a folded run), **SingleLane** (one entrance, no merge), **Pincer** (2 approaches, opposite), **Siege** (3 approaches, all sides), **Encirclement** (4 approaches over 270° — all sides but one). Sector count and arc are not separate knobs: each name is a combination measured to generate — routes AND full pad mix — on a 130×75 field. **Measured route-length envelope: Pincer 120–185 m, Siege up to ~155 m (Standard pace fits), Encirclement up to ~120 m (Short pace — four approaches share one ring).** | Corridor |
| `airCorridor` | Straight air lane to the Core. | on |
| `classMix` | Premium/Standard/Rear/Overwatch counts — **and the pad count, which is their sum.** There is no separate total field. **Premium < 3 is a validation error** — the coverage rule needs three pads at 4+ spans. | 3/2/2/1 (8 pads) |
| `envPackPool` | Theme candidates; the seed picks one. **One entry = pinned theme.** Every pack in the pool is validated, not just the drawn one. | see §8 |
| `weatherPool` | **Override only.** Empty = the drawn theme's own pool decides (an ice map can't draw desert dust). Empty in both = the authored look, pixel-identical. | empty |
| `rulesTemplate` | LevelDefinition cloned as the emitted rules. Generated maps get solved growth + derived cap on the clone; the template itself is never touched. | `Level_RefineryDelta` |

## 8. Themes (dressing a level)

Art is filed, not configured:

```
Assets/Authoring/EnvPack/
  _Shared/    Landmarks/ MidField/ Clutter/ Silhouettes/   → folded into every theme
  Refinery/   Landmarks/ MidField/ Clutter/ Silhouettes/   → EnvPack_Refinery
  Ice/        …                                            → EnvPack_Ice
```

1. Drop prefabs into a theme's category folders (the folder *is* the role; `_Shared` props join every theme).
2. **Tools → COREHOLD → Level → Build Env Packs From Folders** — one measured pack per theme lands in `Assets/_COREHOLD/Data/EnvPacks/`. Footprint radius and height are **measured from the meshes**, never typed; re-running preserves any hand edits you made.
3. On the pack asset: assign `weatherPool` (the theme's weather) and optionally `groundMaterial` + `groundTilingPerMetre`.
4. Add the pack to your blueprint's `envPackPool`.

Vendor prefabs you can't move: add the role as an **asset label** (`Landmark`, `MidField`, `Clutter`, `Silhouette`) plus optionally a theme-name label — the builder sweeps the whole project for labelled prefabs. A prefab dragged in without a role fails validation loudly rather than defaulting to something.

## 9. Determinism — rules of the road

- Same blueprint + same seed = the identical scene, on any machine. Generate twice and diff if you're curious.
- Pools are drawn by **sorted name**, so reordering `envPackPool` in the Inspector changes nothing.
- **Adding, removing or renaming a theme changes what every past seed produces.** Once the daily-seed feature ships (R37), the theme pool is version-locked content — treat changes as content migrations.
- The selector uses no randomness at all: the seed shapes the routes, and the pads follow from geometry.

## 10. Hints & tips

- **Reseed, don't repair.** A failed seed costs nothing to throw away; hand-fixing a generated scene reintroduces exactly the drift the gates exist to prevent. If you love a seed's layout but hate one detail, that detail is usually a blueprint field, not a scene edit.
- **Refusals vs gate failures.** *"Route synthesis refused this blueprint"* means the **fields** are impossible (fold width vs field size, unreachable length target) — fix the blueprint; no seed will help. A **gate** failure is per-seed — Seed +1.
- **Read the Emit row.** For generated maps it prints the solved `hpGrowthPerWave` and derived `maxLiveEnemies` — your first signal of how hard the map runs.
- **Copy Report is the bug-report currency.** Paste it; don't screenshot it.
- **The generator is new-level-only.** Parity retired; Game.unity remains level 1 as a hand-built scene, no longer a generator target.
- **Watch fold width when designing.** It's the single field with the most gameplay leverage: pocket width decides which turrets can work the folds.
- Generated output is ordinary assets: scenes in `Scenes/Generated/`, rules in `Data/Levels/Generated/`. Ship them like any hand-built scene.

## 11. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Gate 3: "Could not start Python" | No `python3`/`python` on PATH | Install Python 3. Dev-machine-only; the shipped game never runs it |
| "field width … cannot fit 2 folds" | `foldWidth` × folds + entrance leg + Core standoff exceed the field | Widen `playfieldSize.x`, narrow `foldWidth`, or move the Core east |
| "no top-run band above the Core" | Field too shallow for the Core position | Deepen the field or move `protectedNormalizedPos` south |
| "could not reach … ±5%" | `routeLengthTarget` out of range for this field/foldWidth | Adjust the target or the field |
| **"cannot generate on any seed"** | A structural problem — the field, topology, Core position and route length cannot all be true at once | Click a **Suggested fix**. Reseeding will not help: nothing about this failure varies with the seed |
| Gate 2: "no candidate for Premium slot…" | This seed's pockets can't host the mix | Seed +1; persistent across many seeds → widen `foldWidth` or reduce the mix |
| Gate 2b: "pads still sight-blocked" | Dressing and pads can't coexist on this seed | Seed +1; recurring → your theme's MidField props are too tall/wide for the fold pockets — check `allowInFold` and measured heights |
| Gate 2b: "pads hidden from the camera" | A prop stands between the camera and a pad | Seed +1. Recurring means the theme's tall props are too numerous for the field |
| Gate 2b: "dressing hides N m of route… over budget" | Too much of the approach is behind props | Seed +1. Recurring → lower the theme's Landmark/MidField heights, or raise `RouteVisibility.HiddenBudgetFraction` if 6% is stricter than you want |
| Gate 3: "margins out of band" | Geometry can't be balanced by growth alone | Seed +1. The flagged waves in the report say which side (LOW = defense starved, HIGH = too easy) |
| Hierarchy verify: "unrecognised root(s)" | A tool emitted a root the container table doesn't know | Add the name to `SceneContainers.Groups` — one line |
| Gate 2 census is a **multiple** of the blueprint mix (e.g. 6/4/4/2 against 3/2/2/1) | Another scene was loaded and its pads were counted too | Fixed — every generation query is scoped to the active scene. If stage 3 still reports "N scenes are loaded", close the others |
| Retry / Main Menu drops me into Refinery Delta | Fixed — both used to carry a serialized scene name defaulting to `Game`. Retry now reloads the ACTIVE scene | If a scene still refuses to reload, it is not in Build Settings: generate it again (stage 18 registers it) or add it by hand |
| "envPackPool is empty" warning | No theme assigned | Fine for greybox testing; assign packs for visuals |
| Dust motes look huge, or precipitation is sparse | Your `Weather_*.asset` presets predate the R14 retune — they live in your project, not the repo, so code changes never touch them | Re-run **Tools → COREHOLD → Scene Setup → Weather** once; it re-authors the presets in place |
| Cancelled a run — leftovers? | None. Cancel routes through the same discard as a gate failure | — |

## 12. What the generator does *not* do yet

- **Wave tables are cloned, not generated** — every map runs the template's waves with solved HP growth (wave regeneration is R33).
- **No contact sheet yet** — R31 will run nine seeds and hand you a 3×3 picker; today you audition seeds one at a time.
- `EnvPack.groundPrefab` is not honoured (material + tiling are).
- The GDD §9.4 health-bar fade is unimplemented (noted in `OverlayManager`).
- Historical note: the retired parity path carried one deliberate divergence — `HP_Premium_2` at (7.5, 13), the documented fix for the shipped scene's coverage violation.

---

*Generator code: `Assets/Editor/Coplay/Generation/`. Stage list: `GenerationPipeline.Stages` — the window renders whatever is declared there. Roadmap tickets R25–R32 in `COREHOLD_roadmap_v2.md` carry the full design history.*
