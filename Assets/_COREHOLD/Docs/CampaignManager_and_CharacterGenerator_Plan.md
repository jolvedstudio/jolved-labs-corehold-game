# COREHOLD — Campaign Manager & Character Forge: Implementation Plan (v2)

> **Status:** Validated & merged. v1 (CoPlay's proposal) was audited claim-by-claim
> against the codebase; this v2 keeps its architecture where it held, corrects it
> where it didn't, and merges in the Track A/B elements from the roadmap stream
> (contact-sheet seed picking, roster registry refactor, balance-model discipline).
> The v1 text is in git history at this path.
>
> **Validation verdict in one line:** the shape is right — a `DontDestroyOnLoad`
> sequencer over self-contained generated scenes, and a data-driven forge — but
> v1's four "reused unchanged" pillars (GameManager, GameFlow, TitleScreen, the
> shared wave tables) all require modification, and four blocker-class gaps had
> to be designed away: title-screen takeover, the carry API, the balance-model
> economy blind spot, and shared `WaveDefinition` assets.

---

## 0. Corrections to v1's foundation (audited)

What v1 got right is not repeated here — `LevelBlueprint`, `RunAll`, the
self-contained-scene fact, PlayerPrefs-only `SaveData`, the audio gate, and
"Retry/Main Menu both reload the current scene" all checked out. What changed:

| v1 claim | Reality | Consequence |
|---|---|---|
| "18-stage / 3-gate pipeline" | 18 stages, **4** gate-flagged stages (1, 2, 2b occlusion re-run, 3) — `GeneratorWindow.cs:548` counts dynamically | Transcript code filtering on the gate flag sees four |
| "reuse `GeneratorWindow.CreateNewMap` / the seed-retry loop / `BuildReport`" | All three are **private instance members tangled with window state** (`MaxAutoSeeds` is a private const) | Campaign Builder reimplements a ~20-line retry around public `RunAll` and lifts the ~14-line transcript formatter |
| "invoke the existing `IconRenderer`" | Whole-roster batch baker, **no per-definition entry point** | Small refactor: a public `RenderOneDefinition` entry, or just trigger the full rebake |
| Binder runs "after `ConfigureRun` has run" at scene `Start` | **`ConfigureRun` only runs from the title-screen difficulty tap** | The campaign flow must call it itself (§A.6) |
| `CampaignManager.ApplyCarryInto` writes salvage/integrity | **`Salvage`/`Integrity` setters are private**; `AddSalvage` inflates `RunSalvageEarned` (score corruption), `DamageCore` fires defeat side-effects | New `GameManager.ConfigureCampaignRun` API (§A.6) |
| Stars exist ("via existing `SaveData.ComputeScore`") | `ComputeScore` exists, but **stars are display-only, computed privately in `ResultScreen.Show`, never persisted** | Extract a shared scorer; campaign star persistence is new (§A.7) |
| "Interstitials use the existing overlay canvas" | **Every canvas is scene-local** — nothing survives between scenes | Briefings render inside the *next* scene, pre-start (§A.5) |
| "reuse per-level wave tables, swap enemy definitions" (E-Q4) | The emitted `LevelDefinition` is a **shallow clone — all levels share the ten shipped `WaveDefinition` assets** | Swapping a def would mutate every level incl. the shipped map. Deep-clone per stage (§A.3) |
| "ties into roadmap R33" | R33 is **runtime endless-wave extension**, not editor-time table generation | Roster-driven waves are unticketed new work (§C) |
| "backup turret builders" generalize to the forge's tower path | **Fiction — no script builds a tower chassis from vendor art**; chassis are human-authored | The forge's tower path is new ground, scoped accordingly (§B) |
| 10 generated scenes ship as the campaign | **`.gitignore` ignores `Scenes/Generated/` AND `Data/Levels/Generated/`** — a fresh clone has neither the scenes nor their LevelDefinitions | Campaign output moves to a versioned folder (§A.3, decision D1) |
| "a tiny Boot scene" would be introduced | `Boot.unity` **already exists**, disabled in Build Settings; `PauseScreen.titleSceneName` is an existing (unset) hook for a menu scene | Reuse, don't introduce |
| `Difficulty` in `CampaignStage` | Two enums exist: the live `Corehold.Core.Difficulty` and an **unused decoy `Corehold.Data.Difficulty`** | Campaign types must qualify `Corehold.Core.Difficulty` explicitly |
| `GameState.Briefing` available for interstitials | **Dead state — nothing ever sets it** | Either claim it for campaign briefings or ignore it; don't rely on it occurring |
| `LevelDefinition.startingSalvage` seeds the economy | **Never read at runtime** — `GameManager` uses its own serialized `startingSalvage` | Carry writes go through the new API, not the definition |

Also surfaced by the audit, unrelated to this plan but urgent:
**the Colossus data is cross-wired** — `Enemy_Colossus_A.asset` carries id
`colossus_b` and points at `Colossus_B.prefab`; `Enemy_Colossus_B.asset` carries
id `colossus_c`; `BuildColossusEnemy`'s hardcoded definition GUID resolves to
nothing; `BuildColossusEnemy` and `SetupColossus` both write `Colossus.prefab`
(which no longer exists — prefabs are `Colossus_A/B/C`). Fix the def/prefab/id
triplets and retire the stale builders before the forge work starts.

---

# Part A — Campaign Manager (Level Sequencer)

## A.1 Goal (unchanged from v1)

Welcome → N generated levels in sequence with progression carried between them →
Closing screen, reusing the generation pipeline unchanged for the maps.

## A.2 Data: authoring/runtime split (replaces v1's single SO)

v1 put one runtime `CampaignDefinition` behind everything. That drags the entire
authoring graph — blueprints (theme/prop/rules pools), recipes, vendor prefabs —
into the WebGL build, duplicating what the scenes already bake, and undoing the
project's texture-memory work. Split it:

**`CampaignAuthoring` (editor-only asset, lives under `Assets/Editor/` or an
`Editor/` folder next to the builder):** what the designer edits.
Master `seed`, `campaignId`, `displayName`, stage list where each Level stage
holds a `LevelBlueprint` reference, the *accepted* seed (source of truth after
generation — see A.3), difficulty bias, title/briefing text, and editor-side
`SceneAsset` niceties. Plus `ProgressionRules` (v1's struct, kept — see A.6 for
the v-phasing) and, later, the roster (`CharacterRecipe[]`, Part C).

**`CampaignManifest` (runtime SO, `Scripts/Data/CampaignManifest.cs`):**
emitted by the Campaign Builder, and the ONLY campaign asset runtime code ever
references. Per stage: `StageKind`, title, briefing, `scenePath`, resolved seed,
display name. Plus `campaignId` and the resolved `ProgressionRules`. No
blueprints, no SceneAssets, no recipes, no `#if UNITY_EDITOR` fields (v1's
conditional-field struct changes serialized layout between editor and player —
the codebase has no precedent for it, and doesn't need one).

Stages are **classes, not structs** (v1's struct array + "store results back
into the stage" is a copy-modify-writeback lost-edit generator).

## A.3 Editor tool: `CampaignBuilderWindow`

`Tools → COREHOLD → Campaign → Campaign Builder`. Orchestrates, owns no
generation logic. Responsibilities, corrected from v1:

1. **Author** — create/edit a `CampaignAuthoring`; add stages; assign blueprints.
   The "Auto-vary" idea survives, but *not* by calling `GeneratorWindow.CreateNewMap`
   (private) — the builder gets its own small per-topology stage seeding, and
   **integrates the R31 contact sheet**: for any stage, render the 9-seed
   contact sheet for its blueprint and let the designer *pick* the seed
   visually instead of accepting the first gate-passer. (Merged from Track A —
   a campaign whose maps were chosen, not merely admitted.)
2. **Generate All Levels** — per Level stage:
   - work on a **temp clone of the blueprint** (`Instantiate`, never the
     authored asset — v1's reuse of the window's retry loop mutates
     `randomSeed` on the shared asset, so two campaigns using one blueprint
     would fight over it);
   - `clone.randomSeed = (int)GenerationPipeline.Fnv1a(campaign.seed, "level_" + index)`,
     retry bounded like `MaxAutoSeeds` on failure;
   - store the **final accepted seed in the stage** — that, not the master
     seed alone, is the reproducibility contract (determinism also requires
     freezing pool membership: adding an `EnvPack` to a pool re-shuffles every
     seed's theme draw);
   - `GenerationPipeline.RunAll(clone)` unchanged; keep the `List<StageRun>`
     transcript per stage (lift `GeneratorWindow.BuildReport`'s ~14 lines into
     a shared formatter);
   - **relocate the outputs**: move the emitted scene + `LevelDefinition` from
     the git-ignored `Scenes/Generated` / `Data/Levels/Generated` into the
     campaign's versioned home `Assets/_COREHOLD/Scenes/Campaign/<id>/`
     (+ `Data/Levels/Campaign/<id>/`), deleting any superseded stage output
     first — regenerations must not accumulate stale scenes into the build
     (see D1);
   - **deep-clone the wave tables**: clone each `WaveDefinition` the stage's
     `LevelDefinition` references into the stage folder and rewire `waves[]`
     to the clones. Shipped `WaveDefinition` assets are treated as read-only
     from this point on. This is what makes per-stage escalation and later
     roster swaps *possible* without cross-level mutation.
3. **Build menu scenes** — Welcome/Closing, reusing the existing
   `BuildRealUI`-built title/settings stack (that groundwork exists — the
   Welcome & Settings screen shipped; the campaign Welcome scene is that plus
   campaign start/resume, not a from-scratch UI).
4. **Register Campaign** — rewrite `EditorBuildSettings.scenes` wholesale:
   Welcome at index 0 (explicit decision — today the first enabled scene is
   `Game.unity`), levels in campaign order, Closing, then any explicitly-kept
   singles (`Game.unity` stays for single-map play). The pipeline's own
   registration is append-only and its prune pass only watches the
   `Scenes/Generated` prefix, so the builder owns ordering and campaign-folder
   hygiene itself, and re-running "Register Campaign" after any single-stage
   regeneration restores order.

## A.4 Runtime: `CampaignManager` + `CampaignLevelBinder`

`CampaignManager` (`Scripts/Core/CampaignManager.cs`): the project's **first**
`DontDestroyOnLoad` object — there is no existing pattern to copy, and every
scene load produces fresh `GameManager`/`AudioDirector`/`WaveManager` instances,
so the manager re-finds and re-subscribes per load (the binder does that work).
API essentially as v1, with the carry corrections from A.6:

```csharp
public static CampaignManager Instance;         // DDOL
public CampaignManifest Active;                  // manifest, not authoring asset
public int CurrentStageIndex;
public CampaignRunState RunState;                // includes stage-ENTRY snapshot
public Corehold.Core.Difficulty ChosenDifficulty; // picked once at Welcome

public void StartCampaign(CampaignManifest m, Difficulty d);
public void AdvanceToNextStage();                 // Victory → next Level/Closing
public void RetryCurrentStage();                  // re-applies the ENTRY snapshot
public void AbandonToWelcome();
```

All scene transitions go through **`GameFlow.LoadSceneClean(buildIndexOrPath)`**
— extract the body of `RestartCurrentLevel` (clear `Enemy.Live`, reset
`Time.timeScale`) so the teardown contract lives in exactly one place. A bare
`SceneManager.LoadScene` (v1's `LoadStage`) leaks the 2× speed toggle, pause
`timeScale = 0`, and stale static registries into the next scene.

`CampaignLevelBinder`: **attached to the existing GameManager object** in the
scene skeleton (`SceneSkeleton.EnsureSingletons` adds the component — the
containers verify pass polices root *names* only, so this needs no
`SceneContainers.Groups` change and the scene stays campaign-agnostic: the
binder no-ops when `HasActiveCampaign` is false, so generated scenes still work
standalone). Its real job is the flow takeover in A.6.

## A.5 Welcome, Closing, interstitials

- **Welcome** (`Scenes/Campaign/Campaign_Welcome.unity`): the existing
  title/settings UI stack + campaign start. The difficulty tap here is the one
  difficulty choice for the whole run *and* the WebGL audio-unlock gesture
  (v1 got that right). Offers **Continue** when a persisted run blob exists
  (A.7). `PauseScreen.titleSceneName` — the existing, currently-unset hook —
  gets set to this scene in campaign builds, giving Abandon-from-pause a home.
- **Closing** (`ClosingScreen.cs`, parallel to `ResultScreen`): totals, star
  strip, best-campaign records, Play Again / Welcome.
- **Interstitials**: there is no cross-scene canvas, so briefings show **inside
  the next level's scene** before the run starts — folded into the same binder
  hook that suppresses the title (it reuses that scene's canvas and `UITheme`).
  v1's "overlay between scenes" is dropped as unimplementable.

## A.6 The flow takeover (was v1's biggest blind spot)

Generated scenes boot to `GameState.Title` and wait for the title-screen
difficulty tap; that tap is also the only caller of `GameManager.ConfigureRun`
**and** of `AudioDirector.StartMusic`. Without changes, every campaign level
shows a full difficulty select (letting the player switch difficulty
mid-campaign, wrecking economy multipliers and records) and an auto-advanced
level would be silent. So — explicitly on the *modified* list now:

- **`GameFlow.BeginCampaignRun(Difficulty d)`** (new public entry): suppresses
  the TitleScreen overlay, runs the run-start sequence directly.
- **`GameManager.ConfigureCampaignRun(Difficulty d, int salvage, int integrity)`**
  (new, or an overload): seeds economy/integrity *directly*, raises
  `OnSalvageChanged`/`OnIntegrityChanged`, does **not** touch
  `RunSalvageEarned` (the `AddSalvage` backdoor corrupts the R4 record and
  every score downstream) and does not route through `DamageCore` (defeat
  side-effects). Called strictly *instead of* the title-tap `ConfigureRun`,
  never before it.
- The binder, in `Awake` (before `GameFlow.Start`'s one-frame delay resolves):
  if a campaign is active → show briefing if any → `BeginCampaignRun(chosen)`
  → `ConfigureCampaignRun(entry snapshot)` → `AudioDirector.StartMusic()`.
- `ResultScreen` gains **Continue** on Victory when a campaign is active
  (v1's design, kept) — and becomes genuinely campaign-aware: when
  `HasActiveCampaign`, it **suppresses `MarkCleared` and `SubmitScore`**
  (otherwise beating campaign level 1 unlocks difficulty tiers globally and
  campaign per-level scores pollute the single-map best on the title screen).
  Star/score computation moves to one shared static scorer used by both
  `ResultScreen` and the campaign (today it's private display logic inside
  `ResultScreen.Show` — two implementations would drift).

**Carry semantics** (fixes v1's snapshot hole): at Victory the binder snapshots
end-of-level `Salvage`/`Integrity` into `RunState` *before* advancing (the next
scene load destroys the source). Each stage stores an immutable **entry
snapshot**; Retry re-applies exactly that, so `integrityHealPerLevel` can't be
farmed by deliberate retries. Campaign star basis is integrity relative to the
*entry* snapshot (the absolute `StartingIntegrityFor` basis breaks under carry).

**Phasing the economy carry (defuses the balance-model blocker):**
- **v1 of the campaign ships `ResetPerLevel` + `baseSalvagePerLevel` only.**
  Gate 3 solves each level against the model's `STARTING_SALVAGE = 300`; with
  reset economy the gates certify the economy the player actually gets — no
  model changes needed, the "every map still passes the gates" promise stays
  true.
- `CarryFull`/`CarryFraction` are **phase 2**, gated on extending
  `balance_model.py` with `--starting-salvage` (threaded through
  `BalanceModelRunner`) so the builder can solve each stage against its carry
  envelope (verify worst-case floor AND best-case in-band) and re-verify
  whenever `ProgressionRules` or a bias changes an already-generated stage.
  The builder refuses "Generate All" when rules and model inputs disagree.

## A.7 Persistence (WebGL-real)

PlayerPrefs, additive keys — with two corrections:

- **The run itself persists**, not just records: `CampaignRunState` (incl. the
  entry snapshot) JSON-serialized to `corehold.campaign.<id>.run` at every
  level boundary. A 10-level campaign on WebGL/mobile *will* meet a tab
  refresh; a memory-only DDOL manager loses the run on the game's actual
  platform. Welcome's Continue reads this; completion/abandon clears it.
- **Per-level campaign records key by `campaignId` + stage index**
  (`corehold.campaign.<id>.stage.<n>.stars` …), *not* by `LevelDefinition`
  name — those names embed the seed (`Level_RockyDesert_s990168`), so every
  regeneration would orphan all records. `LevelId`-keyed records remain for
  single-map play only.
- Campaign records: `.furthestStage`, `.bestScore`, `.bestTime`. Carried
  salvage is scored **once** (closing total), not re-counted in every
  per-level score.

## A.8 File summary (Part A, corrected)

**New:** `CampaignManifest.cs`, `CampaignManager.cs` (+`CampaignRunState`),
`CampaignLevelBinder.cs`, `ClosingScreen.cs`, shared result scorer,
`CampaignBuilderWindow.cs` + `CampaignAuthoring` (editor), Welcome/Closing scenes.

**Modified (v1 claimed these untouched — they are not):**
`GameFlow.cs` (+`BeginCampaignRun`, +`LoadSceneClean` extraction),
`GameManager.cs` (+`ConfigureCampaignRun`), `ResultScreen.cs` (Continue +
campaign-aware suppression + scorer extraction), `TitleScreen.cs` (suppression
hook), `SaveData.cs` (+campaign keys), `SceneSkeleton.cs` (+binder component on
the GameManager object — one line), `.gitignore`/folders (campaign output home).

**Reused genuinely unchanged:** `GenerationPipeline` and everything below it,
`LevelBlueprint`, `WaveManager`, `UITheme`, the balance model (until carry
phase 2).

---

# Part B — Character Forge

## B.1 Scope correction (the audit's biggest Part-B finding)

v1 "generalizes the proven 6-step pattern" — but the pattern is verbatim in
**one** script (`BuildColossusEnemy`); `CreateDroneEnemy` does half of it;
`SetupColossus` (procedural, no vendor, no Animator, `ProceduralGait`) and
`BuildColossusVariants` (`LoadPrefabContents` on existing project prefabs) are
two more divergent variants; and **no tower-chassis builder exists at all**
(chassis are human-authored; scripts only author definitions and wire stacks).
So the forge is:

- **Enemy path: a real generalization** of `BuildColossusEnemy`, v1-scoped to
  *standard walkers and fliers* (Animator-based, vendor or project prefab).
- **Tower path: new ground**, v1-scoped to **combat turrets only**. Support
  towers have a different component stack (`CryoField`/`SalvageRig`/
  `SupportAura`/`Floodlight` instead of `TurretAim`/`TowerWeapon`) — an
  **archetype enum on the recipe maps to a required-component table**, and the
  forge's gates validate archetype↔stat coherence. Support archetypes land
  when a second data point exists to generalize from. `TowerHealth` is *not*
  added by the forge — `Tower.Build` auto-adds it at runtime.

## B.2 Recipe = template definition + assembly hints (replaces stat blocks)

v1's `EnemyStatBlock`/`TowerStatBlock` "map 1:1 onto the definitions" — audited,
they miss `stunResistance`, the six-field audio block, `animatorClipSpeedRef`,
`targetAirOnly`, `pierce`, `minRange`, chain/splash, the four aura fields, and
the whole `TowerWeaponMount[]` structure; and enemy return-fire stats live on
the *prefab's* `EnemyWeapon`, not the definition, so a definition-shaped block
has nowhere to put them. Parallel stat structs also mean every future
definition field must be added in three places or forged units silently lose it.

**Drop the stat blocks.** `CharacterRecipe` references a **template
`EnemyDefinition`/`TowerDefinition` asset**; the forge clones the template and
sets only identity + prefab + icon. Definitions stay the single stat schema
forever. The recipe adds only what a definition can't hold:

```csharp
public enum ForgeArchetype { Walker, Flier, CombatTurret /* v2: SupportTurret, … */ }

public class CharacterRecipe : ScriptableObject   // EDITOR assembly — recipes
{                                                  // reference vendor prefabs and
    public ForgeArchetype archetype;               // must never ship
    public GameObject sourcePrefab;                // developer-supplied
    public string id, displayName;
    public EnemyDefinition enemyTemplate;          // exactly one used, by archetype
    public TowerDefinition towerTemplate;
    public string[] muzzleMarkerNames;             // FindDeep hints (auto-detected)
    public AnimationClip walkClip, dieClip;
    public Material bodyMaterialOverride; public Color tint; public float scale;
    public EnemyWeaponMountSpec[] returnFire;      // → prefab EnemyWeapon, not def
    public string outputFolder;
}
```

The recipe class lives in the **editor assembly** (v1 put it in `Scripts/Data/`;
a runtime recipe referencing vendor prefabs and clips is a build-content leak
waiting for a reference).

## B.3 `CharacterForge` (editor engine)

As v1, with the audited corrections folded in:

- Lift `FindDeep` + the `BuildController` walk/die animator build out of
  `BuildColossusEnemy` into shared utils; keep its proven **generated-muzzle
  fallback** (real, confirmed) when no marker matches.
- Gates, `Discard`-style: source present; archetype↔template coherent; ≥1
  muzzle resolved for combat units; clips assigned (there is **no**
  `search_animation_library` in this project — v1 cited CoPlay-side
  infrastructure; the window offers manual assignment plus a scan of the source
  prefab's own clips, patterned on `EnemyAnimSetup`'s clip tables). Fail loud,
  emit nothing half-built.
- **Vendor reality, stated plainly:** `SaveAsPrefabAsset` stores *GUID
  references* to vendor meshes, it does not bake them. On a fresh clone those
  GUIDs dangle and units spawn as invisible bodies with working components
  (today's `Drone.prefab` carries 3 dangling vendor GUIDs, `Colossus_A` four —
  and this same class of breakage is what just bit the icon baker). That is the
  accepted single-dev tradeoff, but the forge *transcript* lists every
  out-of-repo GUID the saved prefab depends on, so the exposure is visible per
  unit instead of discovered later.
- **Balance-model discipline (merged from Track B):** the model's enemy table
  is hand-maintained by design, and Gate 3 never reads `WaveDefinition` assets
  — it simulates the .py's own hardcoded roster (live drift already exists:
  `Wave_10`'s boss is Colossus Vanguard 2400 HP while the model still simulates
  2800). A forged enemy is therefore **invisible to the gates until its model
  row exists**. The forge ends its transcript with the exact
  `balance_model.py` `ENEMIES` row to paste, and warns that waves referencing
  the unit before the row lands make the model either KeyError (id used in
  WAVES) or silently mis-certify (id only in game assets). Pasting stays a
  human act; the text is machine-written.
- Icon: single-definition bake via the new `IconRenderer` entry point.

## B.4 What "Add to roster" actually is (descoped from the window)

v1 scoped it as a button. Audited, a new *tower* in the build menu means:
appending to `UITheme.turrets` (instantly affects every scene including the
shipped map), the `BuildRealUI` order array, `HardpointCoverageGizmo.TurretKind`
+ `HardpointSelector` pad-class logic (C# enums, not data), and the model's
`TOWERS` dict. A new enemy in waves means per-stage wave-table work (Part C).
**The forge v1 produces prefab + definition + icon + transcript and stops.**
Roster integration is its own step (Part D order), starting with the registry
refactor that makes the UI side one-line: a single roster registry that
`UITheme.turrets`, the build-menu order, and the carousel all read.

---

# Part C — Roster → campaign waves (scoped honestly)

v1 called this "wire rosters into campaign generation" with a low-risk fallback
of swapping enemy defs inside existing tables. The audit killed the fallback
(shared assets — §0) and the R33 alias (different feature). What this actually
is: **a new emission capability** — generating per-stage `WaveDefinition`
tables from a roster with escalation, certified by the model.

Prerequisites, in order: per-stage deep-cloned tables (A.3 — done by then),
forged units with model rows (B.3), then a wave-synthesis pass that composes
groups from the roster against the model (shares R33's budget machinery;
ticketed separately). Until then, campaign stages ship the deep-cloned shipped
tables — already per-stage-editable by hand, already gate-certified.

---

# Part D — Build order (rebalanced)

v1 ordered B first ("de-risks content"). Backwards: the forge is a refactor of
a proven pattern; **Part A holds every unvalidated design decision** (flow
takeover, carry API, scoring semantics — the audit's blockers). Burn the
unknowns first, with the walking-skeleton discipline the generator itself used:

1. **A0 — Campaign walking skeleton.** Two existing generated scenes, reset
   economy: `BeginCampaignRun` + `ConfigureCampaignRun` + `LoadSceneClean` +
   binder + Continue + stub Welcome → prove Welcome → L1 → L2 → Closing
   end-to-end. Every blocker dies here or reshapes the design cheaply.
2. **A1 — Campaign Builder.** Authoring asset + manifest emission + Generate
   All (temp-clone blueprints, accepted seeds, versioned output folder,
   wave-table deep-clone) + contact-sheet seed picking + Register Campaign +
   run-blob persistence + real Welcome/Closing.
3. **B0 — Roster registry refactor** (the one-line-add precondition).
4. **B1 — Character Forge**: enemy path (walkers/fliers), then combat turrets;
   model-row transcripts; Colossus builder cleanup + def re-wiring fix.
5. **A2 — Carry phase 2**: `--starting-salvage` in the model, carry-envelope
   solving, `CarryFraction`/`CarryFull`, per-stage re-verify.
6. **C — Roster-driven wave synthesis** (own ticket, model-coupled).

---

# Part E — Decisions

Resolved by this v2 (previously open):
- **E-Q1 difficulty** → chosen once at Welcome, whole run; `difficultyBias`
  stays authorable but phase-2 (it shifts model inputs like carry does).
- **E-Q2 defeat** → unlimited Retry from the stage entry snapshot + Abandon;
  matches today's behavior, no farming exploit.
- **E-Q3 boot** → fold into Welcome; claim the existing disabled `Boot.unity`
  slot only if a loader scene proves necessary.
- **E-Q4 wave tables** → deep-clone per stage now; roster synthesis later
  (Part C). The v1 fallback is rejected as cross-level mutation.

Still genuinely open (owner: you):
- **D1 — Ship model for campaign scenes.** This plan's default: campaign output
  is **committed** under `Scenes/Campaign/<id>/` (campaign scenes are shipped
  content, like `Game.unity`; `Scenes/Generated` stays git-ignored scratch).
  Alternative: keep everything ignored and make "Generate All" a mandatory
  deterministic pre-build step. Committed is recommended — it matches the
  editor-only-generator doctrine and survives a fresh clone without tooling.
- **D2 — Campaign length/shape of the first shipped campaign** (how many
  levels, which topologies/themes) — an authoring decision for the Campaign
  Builder session, informed by contact sheets.
