# COREHOLD — Campaign Manager & Character Generator: Implementation Plan

> **Status:** Proposal / design doc. Nothing here is built yet.
> **Scope:** Two features that sit *on top of* the existing systems without
> rewriting them:
>   - **A. Level Sequencer / Campaign Manager** — generate and drive a whole game
>     (Welcome Screen → 10 generated Levels → Closing Screen), reusing the Level
>     Generator unchanged for the maps.
>   - **B. Character Generator** — a data-driven forge that turns a
>     developer-supplied prefab into a game-ready Enemy or Tower (prefab +
>     definition), generalizing the bespoke `CreateXEnemy.cs` builders.
>
> **Guiding principles (inherited from the codebase):** deterministic-seed
> ScriptableObjects, gate-validated pipelines, *emit nothing on failure*,
> paste-ready transcripts, PlayerPrefs-only persistence, and backward
> compatibility (single-map play must keep working untouched).

---

## 0. Foundation — what already exists

Both features build strictly on top of systems that already ship. Nothing below
is modified except where explicitly noted in §A.6 and §A.7.

| System | Role today | Location |
|---|---|---|
| `LevelBlueprint` (SO) | Seed-deterministic recipe for one map (topology, playfield, routes, pad mix, theme/weather pools, rules template) | `Scripts/Data/LevelBlueprint.cs` |
| `GenerationPipeline.RunAll(blueprint)` | 18-stage / 3-gate pipeline → emits a **self-contained `.unity` scene** + a `LevelDefinition` asset, wires the scene's `WaveManager`, **registers the scene in Build Settings** | `Editor/Coplay/Generation/GenerationPipeline.cs` |
| `GenerationPipeline.Fnv1a(seed, purpose)` | Stable cross-platform hash — the determinism primitive every draw derives from | same file |
| `GeneratorWindow` | Team UI over the pipeline; includes per-topology "Create a new map" authoring and "Generate until it passes" seed retry | `Editor/Coplay/Generation/GeneratorWindow.cs` |
| `GameManager` (singleton) | State machine (`Boot/Title/Briefing/Build/Wave/Victory/Defeat`), salvage, integrity, difficulty, run stats, streaks, time-dip | `Scripts/Core/GameManager.cs` |
| `GameFlow` | Title → run start wiring; `RestartCurrentLevel()` reloads the active scene by build index | `Scripts/Core/GameFlow.cs` |
| `SaveData` | PlayerPrefs: best score / cleared-tier unlock / per-map+difficulty records | `Scripts/Systems/SaveData.cs` |
| `TitleScreen` | Title overlay + **audio gate** (browsers need a user gesture before audio) | `Scripts/UI/TitleScreen.cs` |
| `ResultScreen` | Victory/Defeat overlay; today only offers Retry / Main Menu (both reload the current scene) | `Scripts/UI/ResultScreen.cs` |
| `EnemyDefinition` / `TowerDefinition` (SO) | Stats + prefab (+ towers: 3× `TowerTier`) | `Scripts/Data/` |
| `WaveDefinition` / `SpawnGroup` | Wave = groups of `EnemyDefinition` + counts + spawner index + mutators | `Scripts/Data/WaveDefinition.cs` |
| `CreateDroneEnemy`, `BuildColossusEnemy`, backup turret builders | Bespoke editor scripts: unpack vendor prefab → add component stack → set fields → save prefab + definition | `Editor/Coplay/` |
| `IconRenderer` | Renders a definition icon sprite from a prefab | `Editor/IconRenderer.cs` |

### Two facts that shape the whole design

1. **Each level is a single, self-contained scene.** A generated scene already
   contains its own Title overlay, HUD, `GameManager`, and a `WaveManager`
   pre-wired to a `LevelDefinition`. Today "Main Menu" and "Retry" both just
   reload the current scene (`GameFlow.RestartCurrentLevel`) — **there is no
   cross-level flow**. A campaign is therefore *a scene-transition + persistent
   progression layer* over scenes the generator already knows how to make.

2. **Character creation is already a repeatable pattern.** Every enemy builder
   does the same thing: *instantiate + unpack vendor prefab → add the runtime
   component stack → resolve bones/muzzles by name → set serialized fields →
   `SaveAsPrefabAsset` → create/link the definition SO*. The Character Generator
   is the generalization of that pattern into one data-driven tool.

---

# Part A — Level Sequencer / Campaign Manager

## A.1 Goal

Generate and drive a **whole game**: Welcome Screen → 10 generated Levels (played
in sequence, with progression carried between them) → Closing/Victory Screen —
while **reusing the Level Generator unchanged** for the 10 maps.

## A.2 New data asset: `CampaignDefinition` (ScriptableObject)

**File:** `Scripts/Data/CampaignDefinition.cs`

```csharp
[CreateAssetMenu(menuName = "COREHOLD/Campaign Definition", fileName = "Campaign_")]
public class CampaignDefinition : ScriptableObject
{
    public string campaignId;          // save-key namespace, e.g. "corehold.main"
    public string displayName;         // "Refinery Front"
    public int seed;                   // master seed → derives every per-level seed

    public CampaignStage[] stages;     // ordered game: welcome, 10 levels, closing
    public ProgressionRules progression;
}

public enum StageKind { Welcome, Level, Interstitial, Closing }

[System.Serializable]
public struct CampaignStage
{
    public StageKind kind;
    public string title;
    [TextArea] public string briefing;

    // Level stages only:
    public LevelBlueprint blueprint;   // the recipe used to synthesize this map
    public int levelSeedOffset;        // combined with campaign seed for determinism
    public Difficulty difficultyBias;  // optional per-stage escalation

    // Filled in by the Campaign Builder after generation:
    public UnityEditor.SceneAsset generatedScene;   // wrapped in #if UNITY_EDITOR
    public LevelDefinition generatedLevel;
    public string generatedScenePath;                // runtime-usable (build-settings lookup)
}
```

`ProgressionRules` (serializable struct) chooses the carry model between levels:

```csharp
[System.Serializable]
public struct ProgressionRules
{
    public enum EconomyCarry { ResetPerLevel, CarryFull, CarryFraction }
    public EconomyCarry economyCarry;
    [Range(0f,1f)] public float salvageKeepFraction;  // for CarryFraction
    public bool carryIntegrity;                        // keep core integrity between levels?
    public int integrityHealPerLevel;                  // + integrity granted on level start
    public int baseSalvagePerLevel;                    // floor granted regardless of carry
}
```

**Why an asset, not code:** mirrors `LevelBlueprint`'s "single deterministic
source" philosophy. A designer edits one asset; the same campaign seed reproduces
the same 10 maps on every machine via `GenerationPipeline.Fnv1a`.

## A.3 New editor tool: `CampaignBuilderWindow`

**File:** `Editor/Coplay/Generation/CampaignBuilderWindow.cs`
**Menu:** `Tools/COREHOLD/Campaign/Campaign Builder`

A sibling of `GeneratorWindow`. It owns **no** generation logic — it orchestrates
existing pipelines, exactly as `GeneratorWindow` renders what the pipeline declares.

Responsibilities:

1. **Author the campaign** — pick/create a `CampaignDefinition`; add stages; assign
   one `LevelBlueprint` per Level stage. Offer an **"Auto-vary" generator** that
   populates 10 Level stages by cycling topology / theme / pace / difficulty for
   variety (reuse `GeneratorWindow.CreateNewMap`'s per-topology authoring so each
   blueprint opens in a generatable state).
2. **"Generate All Levels"** — for each Level stage:
   - `blueprint.randomSeed = (int)GenerationPipeline.Fnv1a(campaign.seed, "level_" + index)`
   - call **`GenerationPipeline.RunAll(blueprint)` unchanged**
   - reuse the existing "Generate until it passes" seed-retry loop so a stubborn
     level auto-reseeds within its own seed family (bounded, like `MaxAutoSeeds`)
   - on success, store `generatedScene` / `generatedScenePath` / `generatedLevel`
     back into the stage
   - accumulate a per-campaign transcript (reuse `StageRun` + `BuildReport`) so a
     failed level is a paste-ready report
3. **"Build Menu Scenes"** — generate/refresh the Welcome and Closing scenes (§A.5).
4. **"Register Campaign"** — ensure Welcome, all 10 Levels, and Closing are in Build
   Settings **in campaign order** (the pipeline already registers level scenes;
   this inserts the two menu scenes and orders the whole list).

Result: a one-button "generate the whole game" where every map still flows through
the audited 18-stage / 3-gate pipeline.

## A.4 Runtime: `CampaignManager` (persistent singleton)

**File:** `Scripts/Core/CampaignManager.cs`

Because levels are separate scenes, campaign state must survive scene loads. This
is the one genuinely new runtime piece.

```csharp
public class CampaignManager : MonoBehaviour
{
    public static CampaignManager Instance { get; private set; }  // DontDestroyOnLoad
    public CampaignDefinition Active { get; private set; }
    public int CurrentStageIndex { get; private set; }
    public CampaignRunState RunState { get; private set; }

    public void StartCampaign(CampaignDefinition c);   // resets RunState, loads first stage
    public void AdvanceToNextStage();                  // called on level Victory
    public void LoadStage(int index);                  // SceneManager.LoadScene by path/build index
    public void ApplyCarryInto(GameManager gm);        // seed next level economy/integrity
    public void RecordLevelResult(bool victory, int stars, int score);
    public bool HasActiveCampaign => Active != null;
}

[System.Serializable]
public class CampaignRunState
{
    public int carriedSalvage;
    public int carriedIntegrity;
    public int cumulativeScore;
    public float elapsedSeconds;
    public int[] starsPerLevel;
}
```

- Created once (from the Welcome scene, or a tiny `Boot` scene) and marked
  `DontDestroyOnLoad`.
- The reference to the `CampaignDefinition` is resolved via `Resources` or a
  serialized bootstrap component (a ScriptableObject cannot be dragged into a
  scene that survives loads without one of these); simplest is a `Boot` scene /
  Welcome scene that holds the reference and passes it into `StartCampaign`.

### `CampaignLevelBinder` (small per-level component)

**File:** `Scripts/Core/CampaignLevelBinder.cs`

Bridges a generated level scene to the persistent manager. Added to level scenes
by the pipeline's skeleton stage **or** discovered/attached at runtime.

- On level `Start` (after `GameManager.ConfigureRun` has run), if a campaign is
  active it calls `CampaignManager.Instance.ApplyCarryInto(GameManager.Instance)`
  so carried salvage/integrity override the per-level defaults.
- Subscribes to `GameManager.OnStateChanged`; on `Victory` computes stars/score
  (via existing `SaveData.ComputeScore`) and calls `RecordLevelResult`, then lets
  the UI's Continue button (see §A.6) drive `AdvanceToNextStage`.

## A.5 Welcome & Closing screens

Two lightweight **dedicated menu scenes** (not gameplay scenes):

- **`Scenes/Campaign/Campaign_Welcome.unity`** — reuses the existing
  `TitleScreen` / `UITheme` UI stack. Difficulty select + "Begin Campaign".
  On click: `CampaignManager.StartCampaign(def)` → `LoadStage(firstLevel)`.
  This is also the **audio gate** the codebase requires (the difficulty tap is
  the user gesture that starts audio — see `TitleScreen`).
- **`Scenes/Campaign/Campaign_Closing.unity`** — a new `ClosingScreen` component
  (parallel to `ResultScreen`): total campaign score, per-level star strip, total
  time, best-run persistence, "Play Again" / "Main Menu".

`Interstitial` stages are optional briefing overlays shown by `CampaignManager`
between levels using the existing overlay canvas — no new scene, just a toggle
before `LoadStage`.

**New file:** `Scripts/UI/ClosingScreen.cs`.

## A.6 Cross-level flow (the sequencer) — the only changes to existing code

- **`ResultScreen` (modified, additive):** add a **"Continue"** button. When
  `CampaignManager.Instance?.HasActiveCampaign == true` and the state is Victory,
  Continue calls `CampaignManager.AdvanceToNextStage()`. When no campaign is
  active, `ResultScreen` behaves **exactly as today** (Retry / Main Menu). This
  keeps single-map play fully backward compatible.
- **On Defeat:** Retry reloads the current level (existing
  `GameFlow.RestartCurrentLevel`); an "Abandon" action returns to the Welcome
  scene when a campaign is active.
- **Progression carry:** `CampaignManager.ApplyCarryInto` writes into the next
  level's `GameManager` according to `ProgressionRules` (e.g. keep 50% unspent
  salvage, +5 integrity between levels). Score accumulates via
  `SaveData.ComputeScore` per level, summed in `CampaignRunState`.

### Sequencer state machine (lives in `CampaignManager`)

```
Welcome ──Begin──▶ Level[0] ──Victory──▶ (Interstitial?) ──▶ Level[1] ─ … ─▶ Level[9] ──Victory──▶ Closing
   ▲                   │Defeat                                                                        │
   └──────Abandon──────┴──────────────────────── Retry (reload same level) ──────────────            │
   ▲──────────────────────────────── Play Again ──────────────────────────────────────────────────┘
```

## A.7 Persistence

Extend `SaveData` (PlayerPrefs only — consistent with GDD §2.5). Additive keys:

- `corehold.campaign.<id>.furthestStage` — unlock gating / resume.
- `corehold.campaign.<id>.bestScore`, `.bestTime` — campaign records for Welcome.
- Per-level stars reuse the existing `SubmitRecordMax(map, difficulty, stat)`
  store keyed by the emitted `LevelDefinition` name.

## A.8 File summary (Part A)

**New:**
- `Scripts/Data/CampaignDefinition.cs` (+ `ProgressionRules`, `CampaignStage`)
- `Scripts/Core/CampaignManager.cs` (+ `CampaignRunState`)
- `Scripts/Core/CampaignLevelBinder.cs`
- `Scripts/UI/ClosingScreen.cs`
- `Editor/Coplay/Generation/CampaignBuilderWindow.cs`
- Scenes: `Scenes/Campaign/Campaign_Welcome.unity`, `Campaign_Closing.unity`

**Modified (additive / backward-compatible):**
- `Scripts/UI/ResultScreen.cs` (+Continue button, campaign-aware)
- `Scripts/Systems/SaveData.cs` (+campaign keys)

**Reused unchanged:** the entire generation pipeline, `LevelBlueprint`,
`GameManager`, `GameFlow`, `TitleScreen`, `UITheme`, Build Settings registration.

**Risk:** low. The generator is untouched; the only new runtime concept is a
`DontDestroyOnLoad` manager, which is standard.

---

# Part B — Character Generator (Enemies & Towers)

## B.1 Goal

Turn the bespoke-editor-script pattern (`CreateDroneEnemy`, `BuildColossusEnemy`,
the backup turret builders) into **one data-driven generator**: the developer
supplies a prefab (vendor model), fills a recipe, and gets a game-ready prefab +
`EnemyDefinition` / `TowerDefinition` — no per-unit C#.

## B.2 The pattern being generalized

Every existing enemy builder performs the same steps (seen verbatim in
`CreateDroneEnemy` / `BuildColossusEnemy`):

1. Instantiate + **unpack** the vendor prefab (`PrefabUtility.UnpackPrefabInstance`).
2. Add the runtime stack: `Enemy`, `EnemyMover`, `EnemyWeapon`,
   `EnemyAnimatorBridge`, `BlobShadow`.
3. Resolve bones/muzzles by name (`FindDeep("...Barrel_End")`, hand bones), mount
   weapons.
4. Retint materials / build a Walk + Die `AnimatorController`.
5. Set serialized fields via `SerializedObject`.
6. `SaveAsPrefabAsset` → create/link the definition SO.

Towers follow the analogous shape: `basePrefab` chassis + weapon child meshes +
3× `TowerTier` (range / damage / fireRate / projectile / muzzle), driven by
`Tower`, `TowerTargeting`, `TowerWeapon`, `TurretAim`, `TowerHealth`.

## B.3 New data asset: `CharacterRecipe` (ScriptableObject)

**File:** `Scripts/Data/CharacterRecipe.cs`

```csharp
public enum CharacterKind { GroundEnemy, AirEnemy, Tower }

[CreateAssetMenu(menuName = "COREHOLD/Character Recipe", fileName = "Recipe_")]
public class CharacterRecipe : ScriptableObject
{
    public CharacterKind kind;
    public GameObject sourcePrefab;      // ← THE DEVELOPER PROVIDES THIS

    // Identity
    public string id;
    public string displayName;

    // Rig / mounting hints (name-based, matching FindDeep today)
    public string[] muzzleMarkerNames = { "Barrel_End", "Muzzle" };
    public string[] handBoneNames;       // for hand-mounted weapons (bosses)
    public GameObject weaponAttachment;  // optional gun mounted into hands/hardpoints

    // Animation (enemies)
    public AnimationClip walkClip;
    public AnimationClip dieClip;

    // Visuals
    public Material bodyMaterialOverride;
    public Color tint = Color.white;
    public Color emissive = Color.black;
    public float scale = 1f;

    // Combat — exactly one block is used, by kind:
    public EnemyStatBlock enemyStats;    // hp, armour, speed, bounty, leak, altitude, enrage, phase-change
    public TowerStatBlock towerStats;    // damageType, canTargetAir, 3× TowerTier

    // Output (defaulted per kind)
    public string outputPrefabFolder;
    public string outputDefinitionFolder;
}
```

`EnemyStatBlock` / `TowerStatBlock` map 1:1 onto the existing `EnemyDefinition` /
`TowerDefinition` fields — so the recipe is essentially "definition fields +
assembly hints".

## B.4 New engine: `CharacterForge` (editor, static)

**File:** `Editor/Coplay/Generation/CharacterForge.cs`

The single generalized builder. `CharacterForge.Build(recipe)` runs the steps
proven in the existing builders, branching on `kind`:

- **Common:** instantiate + unpack source prefab; apply scale/tint/material; add
  `BlobShadow`; resolve muzzles via `muzzleMarkerNames` (lift the `FindDeep`
  helper out of `BuildColossusEnemy` into a shared util).
- **Enemy path:** add `Enemy` / `EnemyMover` / `EnemyWeapon` /
  `EnemyAnimatorBridge`; build a Walk + Die `AnimatorController` (share the exact
  `BuildController` code from `BuildColossusEnemy`); set `isAir` +
  `flightAltitude` for `AirEnemy`; write all `SerializedObject` fields from
  `enemyStats`; `SaveAsPrefabAsset`; create/link `EnemyDefinition`.
- **Tower path:** add `Tower` / `TowerTargeting` / `TowerWeapon` / `TurretAim` /
  `TowerHealth`; mount weapon child meshes; write 3× `TowerTier` from
  `towerStats`; `SaveAsPrefabAsset`; create/link `TowerDefinition`.
- **Validation gates** (mirroring the generation pipeline's *emit-nothing-on-failure*):
  source prefab present? at least one muzzle resolved for combat units? walk/die
  clips assigned or auto-found? Fail loud with an actionable message and emit
  nothing half-built (delete any created assets, like `GenerationPipeline.Discard`).
- **Icon (optional):** invoke the existing `IconRenderer` so the definition's
  `icon` is populated (definitions note "Null until Ticket 33 generates it").

## B.5 New editor tool: `CharacterForgeWindow`

**File:** `Editor/Coplay/Generation/CharacterForgeWindow.cs`
**Menu:** `Tools/COREHOLD/Characters/Character Forge`

Workflow:

1. Drag a **vendor prefab** into "Source".
2. Pick kind (Ground / Air / Tower). Only fields relevant to that kind show (like
   `GeneratorWindow` hides parity-only fields).
3. **Auto-detect** muzzle markers / hand bones from the prefab hierarchy and list
   them, so the developer confirms rather than types names blind.
4. Optional: pick walk/die clips, or "Find animations" via the existing
   `search_animation_library` flow for humanoid rigs.
5. Fill stats (pre-seeded with sensible per-kind defaults — e.g. the Drone's
   60 HP / speed 8 for a light air unit).
6. **"Forge"** → runs `CharacterForge.Build`, shows a transcript, pings the new
   prefab + definition.
7. **"Add to roster"** → optionally append the new `EnemyDefinition` to a
   `WaveDefinition` group, or the `TowerDefinition` to the build-menu list.

## B.6 Why a recipe SO instead of per-unit scripts

- **Reproducible & inspectable:** a recipe asset can be diffed, re-run, and
  versioned — the current builders bury GUIDs and magic numbers in C#.
- **Feeds the campaign:** a `CampaignDefinition` can reference recipes so
  "generate the whole game" can also populate rosters (10 levels of escalating
  enemy sets) without hand-authoring each unit.
- **Same discipline as the level generator:** validation gates,
  emit-nothing-on-failure, a paste-ready transcript — the level pipeline's proven
  ergonomics applied to characters.

## B.7 File summary (Part B)

**New:**
- `Scripts/Data/CharacterRecipe.cs` (+ `EnemyStatBlock`, `TowerStatBlock`)
- `Editor/Coplay/Generation/CharacterForge.cs`
- `Editor/Coplay/Generation/CharacterForgeWindow.cs`

**Reused:** `EnemyDefinition` / `TowerDefinition`, the full runtime component
stack (`Enemy`/`EnemyMover`/`EnemyWeapon`/`EnemyAnimatorBridge`/`Tower`/`TowerTargeting`/…),
`IconRenderer`, and the assembly steps already written in
`CreateDroneEnemy` / `BuildColossusEnemy`.

**Risk:** low–medium. The main variability is rig/bone naming across vendor packs
— mitigated by auto-detection + confirmation in the window, and by falling back to
a generated muzzle marker (as `BuildColossusEnemy` already does when `Barrel_End`
is missing).

---

# Part C — How the two features connect

`CampaignDefinition` can hold a **roster** (`CharacterRecipe[]` for enemies and
towers). "Generate the whole game" then becomes:

1. `CharacterForge` builds every unit from developer-supplied prefabs → definitions.
2. `CampaignBuilderWindow` generates 10 levels via `GenerationPipeline.RunAll`
   (varying topology / theme / pace / difficulty for variety), wiring the roster's
   enemies into escalating wave tables.
3. Welcome + Closing scenes are built, and everything is registered in Build
   Settings in campaign order.
4. At runtime, `CampaignManager` sequences Welcome → 10 Levels → Closing, carrying
   progression per `ProgressionRules`.

**Result:** one master seed + a handful of prefabs → a complete, reproducible
10-level game, with every map still passing the existing 3 generation gates and
every character built by the same audited forge.

---

# Part D — Suggested build order

1. **B first** (`CharacterForge` + `CharacterRecipe`) — refactor the existing
   builders into the generalized engine; immediately useful and de-risks content.
2. **A core** (`CampaignManager` + `CampaignDefinition` + `ResultScreen`
   "Continue") — the runtime sequencer over existing generated scenes.
3. **A tooling** (`CampaignBuilderWindow` + Welcome/Closing scenes) — the
   one-button "generate the whole game".
4. **C** — wire rosters into campaign generation.

---

# Part E — Open questions / decisions to confirm

1. **Campaign difficulty model** — one difficulty chosen at Welcome for the whole
   run, or per-stage escalation via `CampaignStage.difficultyBias`? (Plan supports
   both; default is Welcome-chosen with optional bias.)
2. **Failure semantics** — does a Defeat end the campaign (roguelike), allow
   unlimited Retry (current single-map behaviour), or a limited-lives model?
   (Plan defaults to unlimited Retry + Abandon, matching today's behaviour.)
3. **Boot scene vs Welcome scene** — whether to introduce a tiny `Boot` scene that
   owns the `CampaignManager` and the `CampaignDefinition` reference, or fold that
   into the Welcome scene. (Plan defaults to folding into Welcome for fewer scenes.)
4. **Roster wiring depth** — should campaign generation regenerate wave tables from
   the roster (larger, ties into roadmap R33), or reuse the existing per-level wave
   tables and only swap enemy definitions? (Plan defaults to the latter as the
   lower-risk first cut.)
