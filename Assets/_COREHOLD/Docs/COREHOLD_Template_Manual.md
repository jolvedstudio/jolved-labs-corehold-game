# COREHOLD Template — Team Manual

> For team members who know Unity and tower defense but not this project.
> Everything here describes the code as it stands on the campaign branch.
> Companion docs: `LevelGenerator_UserGuide.md` (deep dive on the generator),
> `CampaignManager_and_CharacterGenerator_Plan.md` (architecture rationale),
> `AssetStore_Readiness_Audit.md` (known genericization gaps), `COREHOLD_GDD.md`.

---

## 1. What you're looking at

COREHOLD is a complete WebGL/mobile tower defense **plus an editor toolchain**
that manufactures its content: a level generator (seeded, gate-validated), a
campaign builder (multi-level runs with progression), a character forge
(enemies/turrets from your prefabs), wave synthesis (rosters + curves instead
of hand tables), and per-campaign UI skinning. One shipped hand-built map
(`Game.unity`) plus as many generated ones as you ask for.

Four philosophies explain most of what feels unusual:

1. **Determinism.** All generation randomness derives from one seed via
   FNV-1a(seed, purpose) → xorshift. Same blueprint + same seed = the identical
   map, theme, weather, and (with a wave recipe) the identical waves — on any
   machine. Never use `System.Random` or `UnityEngine.Random` in generation code.
2. **Model certification.** A Python balance model (`docs/balance_model.py`)
   simulates every generated level; generation *fails* rather than emit a level
   whose difficulty margins fall outside the shipped envelope. "It generated" =
   "it is winnable and taut, per the model."
3. **Emit nothing on failure.** Generators and the forge delete everything they
   made when any gate fails. You never inherit a half-built scene or unit.
4. **Scenes are self-contained.** Every level scene carries its own GameManager,
   WaveManager, UI canvases, directors. There is no shared boot scene at play
   time; campaign flow is a thin `DontDestroyOnLoad` sequencer *over* scenes.

### Vendor art policy (read this before your first clone)
Purchased packs (`Assets/Vendor/`, `Assets/Layer Lab/`, `Assets/Yoge/`) are
**git-ignored — machine-local**. A fresh clone therefore shows pink/missing
meshes on most enemies/towers and falls back on fonts until you install the
packs locally (ask the lead for the pack list/licenses: TD Sci-Fi Turrets V2,
Mech Constructor Spiders, Creepy Cat Vol 4, Destructible Humanoid Robot,
RipVertices Drone, SCI-FI UI Pack Pro, JMO Cartoon FX Remaster, IndieGameModels
Turret SFX, GUI Pro Casual). Content that must survive a fresh clone gets
sprites/values **copied into `Assets/_COREHOLD/`** (the skin tool does this
automatically). The ship-preflight warns on vendor-dependent scenes.

---

## 2. First session, ten minutes

1. Clone, open in Unity 6 (URP 17.5). Install vendor packs if you have them.
2. Open `Assets/_COREHOLD/Scenes/Game.unity`, press Play.
3. Pick a difficulty (that tap is also the WebGL audio unlock). Tap a pad →
   build menu carousel → build. START WAVE drives everything; no auto-start.
4. Press **F2** — the debug console's key map. You will live in this thing:

| Keys | What |
|---|---|
| `]` `[` `0` | next wave / prev index / jump to wave 9 |
| `M` `B` `U` | +1000 salvage / build every free pad / upgrade all (self-funding) |
| `I` `J` | core invulnerable / damage core by 1 |
| `K` `S` `L` | kill all / stun all 3s / slow all 50% |
| `V` `X` | **force victory / force defeat** — the campaign accelerator |
| `C` shift+`C` | campaign status dump / wipe this campaign's saves |
| `P` `,` `.` | pause / slower / faster (×0.25–×4) |
| `T` `N` `W` | cycle forced mutators / night toggle / re-apply weather |
| `1` `2` `3` | difficulty Normal / Veteran / Nightmare |
| `F1` `F3` | stats overlay (fps, draw calls, campaign block) / screenshot |

The console compiles out of release builds entirely.

---

## 3. Game anatomy (runtime)

**States** (`GameManager`): Boot → Title → Build ⇄ Wave → Victory/Defeat.
(`Briefing` exists in the enum but nothing enters it.) One GameManager **per
scene**; `CampaignManager` is the only object that survives scene loads.

**Economy.** Start 300 salvage × difficulty economy multiplier (Normal 1.00 /
Veteran 1.12 / Nightmare 1.22). Income: bounty per kill (+streak bonus: +5% per
kill inside a 2 s chain, capped +50%), wave clear bonus `60 + 18×wave`, chain
bonus for starting the next wave while enemies are alive. Sell refunds 60% of
invested. Enemy HP scales by difficulty (1.00/1.25/1.55) and by the level's
solved `hpGrowthPerWave` compounding per wave.

**Enemies.** Stats live on `EnemyDefinition` assets and are applied **at spawn**
(`Enemy.Configure`/`EnemyMover.Configure`) — prefab component values are only
editor-testing defaults. Movement is 1-D car-following along spline routes;
speed is a product of slots: base × enrage × status (stun/slow) × wave mutator,
floored at 0.4 m/s (the "stun crawl" — nothing ever fully stops). Statuses
refresh rather than stack. Bosses: enrage below an HP fraction (emissive ramp +
speed), stun resistance, `ProceduralGait` for animator-less rigs. Some enemies
return fire at towers (`EnemyWeapon` on the prefab, not the definition).

**Towers.** `TowerDefinition` + 3 `TowerTier`s (cost/range/damage/fire rate/
mounts). Runtime stack (`Tower`, `TowerTargeting`, `TowerWeapon`, `TurretAim`)
is added by `Tower.Build` at build time — prefabs are chassis + `Mount_Top` +
`RangeOrigin` locators. Support towers instead carry behavior components
(`CryoField`, `SalvageRig` bounty aura, `SupportAura`/ScanRelay damage aura,
`Floodlight` for Blackout) using a shared registry + strongest-single-aura
pattern (auras never stack). Veterancy: towers gain +2%/wave damage from wave 3,
capped +12%. Kill attribution happens at the damage site; enemies never know
about towers.

**Waves.** `WaveDefinition` = `SpawnGroup[]` (enemy, count, gap, offset,
spawnerIndex) + clearBonus + optional mutators. Spawner indices: `0..n` ground
routes, **2 = air corridor** (project-wide convention). Mutators: Storm (+30%
air speed), Convoy (single-file one lane), Overcharge (+30% HP, +50% bounty),
Blackout (towers see unlit enemies at half range — Floodlights counter).
**Strike Wing**: tap-targeted airstrike, 120 salvage, cooldown.

**Persistence** (`SaveData`, PlayerPrefs only): best scores and tier unlocks per
difficulty, per-map records (waves/integrity/salvage/streak/time), settings
(`corehold.settings.*`), campaign keys (`corehold.campaign.<id>.*`: run blob,
bests, per-level stars). Difficulty tiers unlock by clearing the previous one.
Core integrity: 20/15/10 by difficulty; stars at ≥90% / ≥50% / >0 of the
**entry** integrity (carry-aware).

---

## 4. The data model — where content lives

| Asset | Folder | Role |
|---|---|---|
| `EnemyDefinition` | `_COREHOLD/Data/Enemies` | All enemy stats + prefab + icon. Ids are `snake_case` (`missile_battery`, not `MissileBattery`) |
| `TowerDefinition` | `_COREHOLD/Data/Towers` | Identity, damage type, `menuOrder` (build-menu position — the roster registry sorts by it), tiers, basePrefab |
| `WaveDefinition` | `_COREHOLD/Data/Waves` (shipped), per-stage folders (campaign) | Spawn tables. **Shipped ten are read-only in practice** — campaign stages get their own clones or synthesized sets |
| `LevelDefinition` | `_COREHOLD/Data/Levels[/Campaign]` | A level's rules: waves list, hpGrowthPerWave, maxLiveEnemies |
| `LevelBlueprint` | `_COREHOLD/Data/Blueprints` | A generator recipe: seed, field, topology, routes, pad mix, theme/weather pools |
| `EnvPack` / `WeatherPreset` | `_COREHOLD/Data/EnvPacks`, `/Weather` | Themes own their weather pools |
| `CampaignManifest` | `_COREHOLD/Data/Campaign` | RUNTIME campaign: stage kinds/paths/text. The only campaign asset a build sees |
| `CampaignAuthoring` (editor) | anywhere under `_COREHOLD/Data` | DESIGNER campaign: blueprints, seeds, skin, wave recipe, progression rules |
| `WaveRecipe` (editor) | with the authoring asset | Roster + intensity curve → synthesized waves |
| `UISkin` (editor) | `_COREHOLD/Art/UI/Skins` | Palette, fonts, sprite slots, proportions per campaign |
| `CharacterRecipe` (editor) | with your content | Forge input: source prefab + template definition + hints |

Editor-only assets (`CampaignAuthoring`, `WaveRecipe`, `UISkin`,
`CharacterRecipe`) live in the editor assembly **by design** — they reference
vendor prefabs and authoring data that must never ship.

---

## 5. Workflows

### W1 — Generate a level
`Tools → COREHOLD → Level → Level Generator`. Pick/create a blueprint
("Create a starter blueprint" gives known-good shipped-map values), set a seed,
**Generate**. 18 stages run with 4 gates (route clearance, pad coverage,
occlusion re-run, model margins); any failure discards everything and names the
gate. "Generate until it passes" auto-reseeds up to 6 times. Output: a
self-contained scene in `Scenes/Generated/` (git-ignored scratch), registered
in Build Settings, playable immediately. **Contact Sheet (9 seeds)** renders a
3×3 overview so you pick map shapes by eye — put the seed you like into the
blueprint. Full details: `LevelGenerator_UserGuide.md`.

### W2 — Fork and hand-edit a level
`Tools → COREHOLD → Level → Clone Level (fork the open scene)…` while the level
is open. You get a fully independent copy: own scene, own LevelDefinition (own
records identity), own deep-cloned wave tables, registered in Build Settings.
The fork is hand-authored from then on — the generator never touches it. After
editing (pads, waves, growth), re-earn certification with
`Tools → COREHOLD → Validate → Run Balance Model (open scene)`.

### W3 — Build a campaign
`Tools → COREHOLD → Campaign → Campaign Builder`:
1. Create a `CampaignAuthoring` asset. Set id, name, master seed.
2. Add levels. Each stage is **either** a blueprint (generated; optional seed
   override from a contact-sheet pick) **or** an existing scene (your Clone
   Level forks — this is how linear hand-tuned campaigns are made).
3. Optional: assign a `UISkin` (§W6) and a `WaveRecipe` (§W4).
4. Progression rules: economy carry (Reset / CarryFraction / CarryFull with a
   base floor), integrity carry + heal. Reset is the certified default; carry
   modes trigger a model verify at your worst-case entry bank.
5. **Generate ALL levels.** Per stage: seeded generation → outputs relocated to
   committed `Scenes/Campaign/<id>/` + `Data/Levels/Campaign/<id>/` → waves
   cloned or synthesized → (recipe) growth re-solved and certified → carry
   verify → manifest emitted → Welcome scene wired → Build Settings rewritten
   (Welcome at index 0, levels in order, Closing, then survivors like Game.unity).
6. `Build menu scenes (stub)` if Welcome/Closing don't exist yet.
7. Open `Scenes/Campaign/Campaign_Welcome.unity`, Play. Difficulty is chosen
   once for the whole run; levels auto-start without their own title screens;
   CONTINUE advances, ABANDON returns to Welcome; a run survives browser
   refresh (CONTINUE RUN appears on Welcome); finishing submits campaign bests.

### W4 — Wave variability (recipes)
Create a `WaveRecipe`: roster (enemy definitions), budget base/growth,
per-stage escalation, air-from-wave, boss finale, mutator chance. Assign it on
the campaign authoring asset. Each stage then synthesizes its own waves —
deterministic per stage seed, structured like the shipped tables (light
openers, air staggered in, boss finale + escort), priced by bounty — and the
balance model **re-solves the difficulty curve against those exact waves** on
the generated map. Synthesis refuses any roster enemy missing its model row
(§6). No recipe = stages clone the shipped ten tables (safe, less varied).

### W5 — Add an enemy or turret (the Forge)
`Tools → COREHOLD → Characters → Character Forge`:
1. Create a recipe. Pick archetype (Walker / Flier / CombatTurret).
2. Drop in the source prefab (vendor or yours). The window lists weapon-ish
   child names it detected; confirm muzzle markers.
3. Pick the **closest existing definition as the template** — its whole
   stat/audio/enrage block carries into the clone; you retune afterwards on the
   new asset. (This is why there is no giant stat form.)
4. FORGE. Enemy path: full component stack, muzzles (with generated fallback),
   hit point, blob shadow, Walk+Die animator if clips assigned (mover-driven
   otherwise). Turret path: locator hygiene + definition clone + `menuOrder`.
5. Read the transcript. It ends with (a) a **dependency audit** naming any
   vendor GUIDs the prefab needs (fresh-clone exposure), and (b) for enemies
   the exact **balance-model row to paste** into `docs/balance_model.py`
   ENEMIES. Paste it — synthesis and certification refuse unmodeled enemies.
6. Enemies: add to a `WaveRecipe` roster (or hand-edit a stage's wave assets).
   Turrets: run `Scene Setup → Build Real UI` so the carousel picks them up
   (registry-ordered by `menuOrder`; a definition without a basePrefab shows as
   a disabled WIP button). Then `Art → Render Icons` for both.
   *Current limit:* the map generator's pad **placement** logic only knows the
   original four turret kinds — new turrets are buildable everywhere but don't
   influence pad scoring (see `AssetStore_Readiness_Audit.md` P1.2).

### W6 — Skin a campaign
`Tools → COREHOLD → Campaign → Create Skin From UI Kit…`: point at a UI kit
folder (vendor location fine), SCAN, confirm the ten sprite-slot guesses,
CREATE — chosen sprites are **copied into committed** `Art/UI/Skins/<name>/`.
On the `UISkin` asset set palette (accent/warm/danger + textMuted/scrim/boss),
fonts, `textScale`, `uiScale` (whole-UI chunkiness), `buttonPadding`,
`cornerRoundness`. Preview instantly with `Campaign → Apply UI Skin to Open
Scene…`; the campaign's real look bakes at generation, so assign the skin on
the authoring asset and re-generate. Unfilled slots keep the shipped look.

### W7 — Test a campaign fast
`V` (force win) → CONTINUE → next stage loads with carry applied. `C` dumps the
campaign state (stage, entry snapshot, results); shift+`C` wipes its saves to
retest first-run/CONTINUE states. The F1 overlay shows the campaign block.

### W8 — Ship
`Campaign → Preflight` (picker asks which campaign): checks scenes on disk +
registered + Welcome at index 0, campaign output in committed folders, stage-
local wave tables, dead build buttons, blank icons, vendor-dependent scenes,
build target. Errors block; warnings are judgment calls. `Campaign → Build
Shippable Game (WebGL)` builds **exactly** the campaign's scenes (singles like
Game.unity deliberately excluded) to `Builds/WebGL/<id>/`. WebGL must be
**served** (`python3 -m http.server 8000`), never opened from `file://`.

---

## 6. The balance model (why generation can say no)

`docs/balance_model.py` simulates a level wave by wave — DPS delivery vs
required HP under focus/dwell/armour/mutator/veterancy/streak terms — and
verdicts margins against the shipped difficulty envelope. The generator's Gate 3
runs it with the map's real geometry; `--solve-hp-growth` bisects the difficulty
curve; `--waves` certifies synthesized tables; `--starting-salvage` certifies
carry economies. Editor-only; needs Python 3 on dev machines; the shipped game
never runs it.

**The discipline that matters to you:** the model's ENEMIES table is
hand-maintained. Every new enemy needs its row (the forge prints it
ready-to-paste) *before* it appears in waves — the tools enforce this loudly.
Any model edit must keep a bare run **byte-identical** to
`docs/baseline_today.txt` unless you intend a balance change; that regression
check is the model's whole safety story. Known caveat: parts of the shipped
tables have drifted from the live assets (documented in
`AssetStore_Readiness_Audit.md` P1.1 — the fix is planned).

---

## 7. House rules

- **One git door.** Commit from Unity to the working branch and let PRs carry
  to main. Committing the same state to two branches manufactures conflicts.
- **Never reference vendor assets from committed content** (new content — some
  legacy content predates the rule). The skin tool's copy-into-project flow is
  the pattern to imitate.
- **Ids are `snake_case`** and load-bearing (model rows, role tags, save keys).
- **Don't re-run retired one-shots.** `CoreholdWaveDataGenerator` and
  `RefineryDeltaBlockout.Build` are deliberately menu-less: both would revert
  hand-tuned live assets to their original transcriptions. If a class has no
  menu item, assume that's a decision, not an oversight.
- **The shipped ten `Wave_*.asset` are effectively frozen** — campaign stages
  own clones; edit those (or use recipes).
- **Append-only serialized enums** (`VFXDirector.Effect`, `AudioDirector.Sfx`):
  values are serialized by index; never reorder or insert.
- **`[TUNE]` marks designer-adjustable values** — tune freely; `W` re-applies
  weather live. `[MANUAL]` marks steps a human must do in the editor.
- Scratch generated scenes (`Scenes/Generated/`) are git-ignored and prunable;
  campaign output (`Scenes/Campaign/<id>/`) is committed and shippable.

## 8. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Pink or invisible enemies/towers on a fresh clone | Vendor packs absent (§1 policy). Install packs; forge/icon transcripts name the exact missing GUIDs |
| Generation FAILs at a gate | Read the stage line — it names the rule (clearance/coverage/occlusion/margins). Reseed first; a blueprint failing 6 seeds needs its fix panel, not more seeds |
| "Generation needs Python 3" | Install python3 on PATH (dev machines only) |
| Icons blank / stale | `Art → Render Icons`; its log names every skipped unit and why (missing prefab, filtered renderers, NO MESH) |
| Campaign won't load a stage | Scene not in Build Settings — press Register Campaign |
| CONTINUE RUN missing on Welcome | No valid run blob (finished, abandoned, or campaign regenerated since — stale blobs are discarded on purpose) |
| Synthesis refuses my roster | An enemy lacks its model row; the forge transcript prints the paste line |
| Wave 1 instantly lost after editing growth | Re-run `Validate → Run Balance Model (open scene)` and read the margins |
| UI looks wrong after skin edits | Skins bake at build time: Apply UI Skin (preview) or re-generate the campaign |

**Menu orientation** (`Tools → COREHOLD`): **Level** = make/inspect maps ·
**Campaign** = assemble/skin/ship runs · **Characters** = the forge ·
**Scene Setup** = per-scene builders the pipeline also calls (safe to re-run;
they're idempotent) · **Look** = camera/lighting/ground · **Art** = icons ·
**Validate** = certification and geometry checks · **Utilities** = misc.
