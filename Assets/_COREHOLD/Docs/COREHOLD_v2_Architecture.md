# COREHOLD v2 — Architecture: One Sim, Many Targets

> **Status:** proposal for the next iteration. v1 (`jolved-labs-corehold-game`)
> is designated the vibe-coding experiment: its *algorithms* and *doctrine*
> carry forward, its *plumbing* does not. Desktop first. Web and console are
> tiers of the same build, not ports.
>
> Companion documents: `Desktop_and_Console_Plan.md` (what to do with v1 in
> the meantime), `AssetStore_Readiness_Audit.md` (v1's defects, most of which
> this architecture makes structurally impossible).

---

## 0. What v1 taught, and the structural answer to each lesson

The point of this section is that every lesson maps to something the
**toolchain** enforces — a compiler error, a failing test, a CI gate — rather
than something a person or an AI agent has to remember.

| # | v1 lesson | v2 answer | Enforced by |
|---|-----------|-----------|-------------|
| 1 | The sim was fused to presentation (`Enemy` reaches `VFXDirector.Instance` 6×), so it could not run headless — which forced a *second* implementation of the sim in Python, which drifted from the assets. | The sim is a plain-C# assembly that **cannot reference `UnityEngine`**. Certification runs the real sim. There is no second implementation of anything. | asmdef `noEngineReferences`, arch test |
| 2 | Doctrine ("expand, don't parallel", "never port the model") held socially and eroded one ticket at a time. | Every rule that matters is a compile error, a test, or a blocking CI gate. | Arch tests, CI |
| 3 | The *scene* was the source of truth: generation wrote scenes, gates measured scene objects, campaigns relocated scenes, Build Settings were rewritten wholesale. | **`LevelLayout` is the level.** Scenes are derived from it, per tier, at load or build time. Gates run on data. | Type system: gates take `LevelLayout`, never a scene |
| 4 | `TurretKind` was a four-entry enum with literal ranges; `HardpointSelector` had class→kind arrays; each special behaviour was its own class. The generator could only ever place the original four turrets. | Traits and registries. No per-unit code. A new unit is an asset. | Content validation test, no enum for kinds |
| 5 | One live quality tier (and the tuned one was orphaned). | A `PresentationProfile` ladder from day 1, with a conformance test proving tiers cannot change gameplay. | Conformance test across all profiles |
| 6 | Generation was editor-only, so scenes had to be committed and there could be no daily seed or endless mode. | Generation is **runtime-capable**; editor tooling sits on top of it. | Assembly boundary: `Generation` has no editor refs |
| 7 | ~80 one-shot editor scripts (`Setup*`, `Wire*`, `Migrate*`, `Ticket37*`) — one tool per ticket, fossilised. | One `IGenerator` contract. Tools are instances of it. One-shot scripts are not allowed in the package. | Review rule + package layout |
| 8 | Zero tests; the gate ritual was manual. | Golden replays, a determinism hash, budget gates — blocking in CI. | CI |

---

## 1. Principles

Six statements. Everything in the rest of the document is a consequence.

1. **The simulation is the product's truth.** Plain C#, deterministic, headless,
   tick-based. Everything else — rendering, tooling, certification, platforms —
   derives from it and none of it can reach back in.
2. **Gameplay is invariant across targets.** A tier may change nothing the sim
   reads. This is tested, not promised.
3. **Data is the level.** Layouts, rosters, waves and campaigns are records.
   Scenes, prefab instances and terrains are *outputs*.
4. **One implementation of every mechanism.** The certifier is the sim. The gate
   is the sim. The forge's stat solver is the sim.
5. **Extension without editing.** A unit, a tier, a level, a campaign, a
   platform is a new asset or a new assembly — never a change to an existing
   class.
6. **Rules live in the toolchain, not in memory.** If a rule matters, it is an
   assembly constraint, a test, or a CI gate. Documentation explains the rule;
   it is never the only thing holding it.

---

## 2. Assemblies and the reference graph

The reference graph is the architecture. It is acyclic, one-way, and the
arch test asserts it on every commit.

```
                         ┌──────────────────────┐
                         │     Corehold.App     │  composition root — the ONLY
                         │  (scene bootstrap)   │  place that wires things up
                         └──────────┬───────────┘
              ┌────────────┬────────┴────────┬────────────────┐
              ▼            ▼                 ▼                ▼
     ┌────────────┐ ┌────────────┐  ┌──────────────┐  ┌──────────────────┐
     │  Platform  │ │    View    │  │  Generation  │  │     Certify      │
     │ (+ .Web,   │ │ (present.) │  │ (runtime-    │  │ (headless runs,  │
     │  .Console) │ │            │  │  capable)    │  │  solvers)        │
     └─────┬──────┘ └─────┬──────┘  └──────┬───────┘  └────────┬─────────┘
           │              │                │                   │
           │              ▼                ▼                   ▼
           │        ┌────────────────────────────────────────────────┐
           │        │                Corehold.Content                │
           │        │   authoring SOs · bake → records · registries  │
           │        └───────────────────────┬────────────────────────┘
           │                                ▼
           │        ┌────────────────────────────────────────────────┐
           └───────▶│                  Corehold.Sim                  │
                    │   netstandard · NO UnityEngine · deterministic │
                    │   SimWorld · systems · traits · Fix64 · replay │
                    └────────────────────────────────────────────────┘
```

| Assembly | Contains | May reference | Never references |
|---|---|---|---|
| `Corehold.Sim` | `SimWorld`, systems, trait mechanisms, `Fix64`, RNG streams, replay, records it consumes | nothing (netstandard 2.1) | `UnityEngine` — enforced by `"noEngineReferences": true` |
| `Corehold.Content` | ScriptableObject authoring types, `Bake()` to Sim records, registries, validation | `Sim` | `View`, `Platform` |
| `Corehold.Generation` | Layout, wave, terrain-field, placement generators; gates on data; `IGenerator` | `Sim`, `Content` (records only) | `UnityEditor`, `View` |
| `Corehold.Certify` | Headless runner, build policies, solvers, margin reports | `Sim`, `Generation` | `View`, `Platform` |
| `Corehold.View` | Unit views, interpolation, VFX/audio registries, cameras, terrain writers, overlays, UI | `Sim`, `Content` | `Generation`, `Certify`, `Platform` impls |
| `Corehold.Platform` | Interfaces (`IInput`, `ISaveStore`, `IPlatformServices`, `IQualitySelector`) + desktop impl | `Sim` (for command types) | `View` |
| `Corehold.Platform.Web`, `.Console.<X>` | Per-target impls | `Platform` | anything else |
| `Corehold.App` | Composition root, scene bootstrap, tier selection | all runtime | — |
| `Corehold.Generation.Editor` | Generator window, contact sheets, asset emission | `Generation`, `Content`, `UnityEditor` | `View` |
| `Corehold.Tools.Editor` | Campaign builder, forge, harvester, env-pack tools, icon renderer | all runtime + `UnityEditor` | — |
| `*.Tests` | see §10 | the assembly under test | — |

Two properties of this graph do the heavy lifting:

- **`Sim` is a leaf that cannot see the engine.** Unity's own asmdef flag
  refuses the compile if anyone adds a `using UnityEngine` there. The same
  source compiles under a plain `.csproj`, so sim tests run with `dotnet test`
  in seconds, on any CI runner, with no editor.
- **`View` cannot see `Generation` or `Certify`.** Presentation cannot depend on
  how a level was made — which is what lets a hand-authored layout and a
  generated one be indistinguishable at runtime.

---

## 3. The simulation

### 3.1 Shape

```
SimWorld  state: tick, rng streams, routes (baked), pads, towers, enemies,
                 projectiles, economy, integrity, wave schedule, game state
Step(world, commands[], ) → events[]
```

- **Commands in**: `Build(pad, towerId)`, `Upgrade(pad)`, `Sell(pad)`,
  `StartWave`, `ChainWave`, `SetSpeed(x)`.
- **Events out**: `Spawned`, `Damaged`, `Died`, `Leaked`, `WaveStarted`,
  `WaveCleared`, `SalvageChanged`, `IntegrityChanged`, `StateChanged`,
  `TraitFired`, `ProjectileLaunched/Hit`.
- **No per-frame position events.** Views *read* state and interpolate (§7).
- **Fixed tick: 30 Hz.** Decision, not default: it is enough resolution for a
  14-enemy TD with analytic movement, cheap enough to run at 1000× for
  certification, and the same on a 30 Hz web tab, a 60 Hz console and an
  uncapped desktop.

### 3.2 Systems — ported from v1, in order of proof

Each of these is *proven* in v1 and its math transplants nearly verbatim.
What changes is that state lives in `SimWorld`, not on a component, and
side-effects become events.

| System | v1 source | Notes |
|---|---|---|
| Traffic | `RouteTraffic` + `EnemyMover` scalar | The 1-D car-following `Frontness` model. Provably overlap-free and drain-guaranteed. **Port verbatim.** |
| Targeting | `TowerTargeting` | Registry scan on a staggered tick; range from records. |
| Weapons | `TowerWeapon`, `Projectile`, `ChainTracer` | Hitscan, chain, and *sim* projectiles with deterministic travel. Visuals are separate views of `ProjectileLaunched`. |
| Damage | `DamageTable` | The 3×3 type×armour grid. |
| Traits | *(new — replaces `IsColossus`, `WardenAura`, `SalvageRig`, `Floodlight`, `CryoField`, Roller phase, enrage)* | §3.4 |
| Economy | `GameManager` salvage / bounty / chain bonus | Pure functions over records. |
| Wave schedule | `WaveManager` | Spawn groups, gaps, offsets, live cap, chaining rule, HP scalar, difficulty overlay. |
| Game state | `GameManager` state machine | `Boot → Title → Build ⇄ Wave → Victory/Defeat`. |

### 3.3 Determinism — the product's claim, made a guarantee

"Same seed → same level → same certified margins, on every platform" is the
generator's promise. v1 already hand-rolled its RNG and noise because
`System.Random` and `Mathf.PerlinNoise` "are not a contract across
platforms." v2 finishes that thought:

- **Fixed-point arithmetic in the sim** (`Fix64`, Q32.32) for positions,
  distances, HP, timers and rates. Cross-compiler float determinism is not
  something IL2CPP promises (FMA contraction on ARM, libm differences), and a
  certification product cannot ship "usually the same." Fixed-point makes the
  guarantee unconditional.
- **Generation may use float.** Splines, noise, placement search all run in
  float at generation time — and then **bake** to what the sim needs:
  polylines with fixed-point arc-length tables. The sim never evaluates a
  curve; it walks a table. (This is exactly v1's `TerrainStage` doctrine —
  "solve and gate here, then bake" — applied to the whole layout.)
- **RNG streams owned by `SimWorld`**, one per subsystem, seeded by
  `FNV-1a(seed, "streamName")` — v1's convention, kept. Nothing in `Sim`
  touches `Time`, `Random`, `Mathf`, or `DateTime`. It *cannot*: those live in
  `UnityEngine`/`System` namespaces the arch test forbids.
- **Determinism test**: the same replay must hash identically on the desktop CI
  runner, in a WebGL build driven by Playwright, and on each console as it
  comes online.

*Trade-off stated:* fixed-point costs arithmetic ergonomics and a small
library. It buys the one property the product is named for.

### 3.4 Traits — closed mechanisms, open parameters

A trait is a **mechanism** the sim knows how to apply, with **parameters** an
asset supplies. The set of mechanisms is small and closed; the set of
behaviours is open.

| Mechanism | Parameters | Replaces in v1 |
|---|---|---|
| `StatModifier` | target stat, op (add/mul), value, condition | tier bonuses, difficulty overlay |
| `PhaseTrigger` | trigger (path fraction / HP fraction / wave) → modifier set, one-shot flag | Roller phase, Colossus enrage |
| `Aura` | radius, target filter (friend/foe, air/ground), modifier set | `WardenAura`, Scan Relay |
| `StatusOnHit` | status kind, strength, duration, refresh rule | `CryoField`, stun/slow |
| `EconomyHook` | on kill / on wave / on sell → salvage delta | `SalvageRig` |
| `Reveal` | radius, effect on target acquisition | `Floodlight`, Blackout mutator |
| `BossFlag` | leak damage override, UI bar | `IsColossus` |

- A **new behaviour** = a new asset composing existing mechanisms. No code.
- A **new mechanism** = a change to `Sim`, reviewed, with a golden replay.
  Expected a few times a year, not a few times a week.
- The forge, the certifier and the balance solvers all see the same list,
  because there is only one.

### 3.5 Replay

A run is `seed + command log`. That gives, for free: golden regression tests,
one-file bug reproduction, the determinism test — and, should it ever matter,
the exact substrate lockstep co-op or async ghost races are built on. Zero
cost now; an option kept open.

---

## 4. Content

- **Authoring** is ScriptableObjects: `UnitDefinition` (enemy or tower —
  archetype is a trait bundle, not an enum), `TowerTier`, `TraitAsset`,
  `WaveTable`, `LevelLayout`, `Campaign`, `PresentationProfile`, `EnvPack`,
  `DamageTable`, `DifficultyOverlay`.
- **`Bake()`** converts authoring assets to the plain records `Sim` consumes.
  Baking is where validation gates live: no `Unassigned` role, ranges > 0,
  every reference resolvable, no dangling GUIDs. **Every one of those is a
  test**, so v1's "194 unresolvable GUIDs" cannot recur silently.
- **Registries** discover by type and tag with a `menuOrder` — v1's
  `RosterRegistry` pattern, applied to *every* content type. No list is ever
  hand-maintained.
- **Difficulty is an overlay struct**, not three asset sets. v1 got this right.

---

## 5. Generation — runtime-capable

### 5.1 The one contract

```
IGenerator<TRecipe, TOutput>
    Preflight(recipe)        → refusals[]        (nothing written on refusal)
    Run(recipe, seed)        → candidate
    Gates(candidate)         → failures[] | null
    Emit(candidate) | Discard
```

Every generator implements it. In return, every generator gets the Advisor
(v1's "search for the smallest edit that makes it generate", running the real
generator on throwaway copies), the gate UI, contact sheets, and the campaign
DAG runner, without writing any of them.

### 5.2 Generators

| Generator | Recipe → Output | v1 source |
|---|---|---|
| **Layout** | `LevelBlueprint` → `LevelLayout` | `RouteSynthesizer`, `HardpointSelector`, `TerrainField`, `PropPlacer`, `LookStage`, gates — ported. Shape grammar added later (§5.4). |
| **Waves** | `WaveRecipe` (roster + intensity curve) → `WaveTable` | `WaveSynthesizer` — threat budget over bounty. Ported. |
| **Character** | `CharacterRecipe` (role, chassis, traits, target threat cost) → `UnitDefinition` + prefab | Forge v2's "template + assembly hints" doctrine, plus **solved stats** (§6). |
| **Campaign** | `CampaignRecipe` → `Campaign` (list of layouts + wave tables + seeds) | `CampaignBuilderWindow` — orchestration only, as in v1. Becomes a generic DAG over `IGenerator`s. |
| **Harvest** | vendor scene → `EnvPack` (+ look profile, terrain layers, `redistributable` flag) | `EnvPackTools.BuildFromFolders`, extended to read a scene. |

### 5.3 `LevelLayout` — the level

```
LevelLayout
  seed, blueprintId
  routes[]        polyline knots (float) + baked arc table (Fix64) + lanes
  pads[]          position, class, eligibility
  core, spawners[]
  terrain         field params + seed  (writers derive mesh or TerrainData)
  masks           corridor / envelope / water / keep-out  (as parameters, not textures)
  dressing[]      prefabId, transform, tone variant
  look            profile ref (sun, fog, sky, post)
```

Gates take a `LevelLayout`. The View's `LevelBuilder` derives a scene from
one. The campaign is a list of them. CI certifies each of them. Nothing in the
repository is a generated `.unity` file.

### 5.4 Why runtime-capable

Because `Generation` references only `Sim` and `Content` records — never
`UnityEditor` — the same code runs in the editor window, in CI, and **in the
shipped game**. Certification is a 0.5 s headless run (§6). That turns on, at
no extra architectural cost: daily seeds, endless mode, seed sharing, and
"generate a campaign on first launch." It also makes the Asset Store template
a *runtime* generator, which is a different product from an editor tool.

A shape grammar for routes (straight / fold / S-bend / split / merge / loop,
each with a clearance envelope, composed by seeded rewriting under the same
gates) is the planned replacement for v1's parameterised snake. It is a later
milestone; the contract does not change.

---

## 6. Certification — the sim is the model

`Corehold.Certify` replaces `docs/balance_model.py` outright. It does not port
it.

- **Runner**: given `LevelLayout + WaveTable + roster + BuildPolicy`, run the
  real sim headless at maximum speed and report per-wave margins, flags and
  the worst group. A full 10-wave run at 1000× is roughly half a second.
- **Policies**: greedy (v1's `active_build_priority`, ported), scripted, and
  replay-of-a-real-player.
- **Solvers** over the runner: `hpGrowthPerWave` for a target curve
  (v1 `solve_hp_growth`), unit stats for a target threat cost (new — this is
  what makes the forge a design tool rather than an assembler), pad mix for a
  margin band.
- **Where it runs**: the generation gate (runtime and editor), CI on every PR
  (the shipped campaign is re-certified), the forge, and a balance dashboard.
- **The regression anchor**: v1's Refinery Delta baseline curve (`docs/baseline_today.txt`)
  must reproduce within tolerance on the migrated layout. This is the *first*
  certification test written, before any new content exists.

Because the certifier is the sim, drift is impossible by construction: a
forged enemy with a new trait combination certifies from its own asset, and
there is no roster table anywhere to fall out of date.

---

## 7. Presentation

- **Reads, never writes.** `View` reads `SimWorld` and subscribes to events. It
  holds no gameplay state and cannot reference `Generation` or `Certify`.
- **Interpolation.** The sim ticks at 30 Hz; the renderer runs at whatever the
  target allows. A unit view interpolates `Frontness` between the last two
  sim ticks and places itself on the baked route. Frame rate and gameplay are
  fully decoupled.
- **Unit view = chassis prefab + skin.** Chassis carries mount points (muzzles,
  hit point, overlay anchor); skin carries materials and tone variant.
  Vendor art plugs in here and nowhere else.
- **Effects by event type.** VFX and audio are registries keyed by sim event
  and trait/weapon id — v1's `VFXDirector`/`AudioDirector` turned into
  listeners instead of globals that gameplay code reaches into.
- **Two first-class cameras**: the strategy view (fixed, framed) and the POV
  turret view — the mode in which desktop fidelity actually shows.
- **Terrain writers** derive the visible terrain from `LevelLayout.terrain`:
  a baked mesh (web) or a `TerrainData` with layers, detail and trees
  (desktop, console). Chosen by profile. Both read the same field.
- **`PresentationProfile`** (one asset per tier): URP asset, terrain writer,
  detail density and distance, prop LOD bias, texture size cap, VFX
  concurrency, post stack, shadow policy, target frame rate. A profile may
  change nothing the sim reads — the conformance test runs every golden
  replay under every profile.
- **UI is UI Toolkit**, focus-navigable from the first screen, themed through
  USS. This is the desktop-first decision that costs nothing today and
  everything if deferred to console.

---

## 8. Platform

- **Interfaces** in `Corehold.Platform`: `IInput` (Input System action maps per
  device class), `ISaveStore` (async, failure-aware), `IPlatformServices`
  (presence, achievements, suspend/resume hooks), `IQualitySelector`.
- **`#if UNITY_*` lives here and only here.** The arch test greps for it
  everywhere else and fails the build.
- **Desktop first**: Windows / macOS / Linux, Steam. **Steam Deck is the
  console proxy** — controller-first, 16:10 at 800p, a thermal budget, and a
  suspend/resume model. Build for Deck from day 1 and console readiness is
  most of the way there before a devkit arrives.
- **Console**: one new `Platform.Console.<X>` assembly per target. Nothing else
  changes. A memory ceiling set to the weakest intended target is a CI gate
  from day 1, measured on desktop. Suspend/resume is trivial because the sim
  is tick-based and pausable by construction. A TRC-style checklist lives in
  `/docs/platform/`.
- **Web**: `Platform.Web`, the low profile, the mesh terrain writer. Still a
  tier of the same build — and still runs the generator.

---

## 9. Project layout

```
Assets/
  Corehold/                         ← ONE package root; Asset Store-ready
    Runtime/
      Sim/            Corehold.Sim.asmdef           noEngineReferences: true
      Content/        Corehold.Content.asmdef
      Generation/     Corehold.Generation.asmdef
      Certify/        Corehold.Certify.asmdef
      View/           Corehold.View.asmdef
      Platform/       Corehold.Platform.asmdef
      Platform.Web/   Corehold.Platform.Web.asmdef
      App/            Corehold.App.asmdef
    Editor/
      Generation/     Corehold.Generation.Editor.asmdef
      Tools/          Corehold.Tools.Editor.asmdef
    Tests/
      Sim.Tests/  Content.Tests/  Certify.Tests/  View.Tests/  Arch.Tests/
    Content/          Definitions/ Traits/ Layouts/ Waves/ Campaigns/
                      Profiles/ EnvPacks/ DamageTable.asset
    Art/  Audio/  Prefabs/  Settings/  UI/
    LICENSE  THIRD_PARTY_NOTICES.md  package.json
  Vendor/                           ← git-ignored; harvested INTO Content/EnvPacks
docs/
  adr/                              ← architecture decision records (§11)
  platform/                         ← per-target checklists
AGENTS.md                           ← the rules every human and agent reads first
```

`Corehold.Sim` also has a hand-written `Corehold.Sim.csproj` at the repo root
pointing at the same sources, so `dotnet test` runs the sim suite outside
Unity.

---

## 10. Tests and CI — the enforcers

All blocking. None optional.

| Suite | Runs in | Asserts |
|---|---|---|
| **Arch** | `dotnet test` | `Sim` has no `UnityEngine` reference; the assembly graph matches §2 exactly and is acyclic; no `#if UNITY_` outside `Platform.*`; no `.Instance`, `FindObjectOfType`, `GameObject.Find` anywhere in runtime code. |
| **Sim** | `dotnet test` | Golden replays (seed + commands → event hash); property tests on traffic (never overlaps, always drains); trait mechanism tests. |
| **Determinism** | desktop CI + WebGL-in-Playwright (+ consoles) | Identical replay hashes across targets. |
| **Content** | Unity EditMode | Every SO bakes and validates; every registry resolves; **zero dangling GUIDs**. |
| **Certify** | Unity EditMode / `dotnet` | v1 baseline reproduces; every shipped layout is inside the margin band. |
| **Conformance** | Unity PlayMode | Every golden replay, under every `PresentationProfile`, yields the same sim hash. |
| **View smoke** | Unity PlayMode | Load a layout, build the scene, run three waves at 10× on each tier: no exceptions, frame and memory within budget. |
| **Build** | per target | Player size, texture memory per tier, draw calls on a reference layout — each against a numeric budget. |

---

## 11. Process — "this cannot happen again"

- **ADRs.** Every decision in §13 becomes a numbered file in `docs/adr/`
  (context, decision, consequences). v1's GDD had a decision record; this
  formalises it and keeps it next to the code.
- **`AGENTS.md` at the repo root** — the practical enforcement for a studio that
  builds with AI agents. It states the six principles, the extension points
  ("to add a unit, do X; to add a tier, do Y"), and the nevers. Every human
  and every agent session starts there. Rules that are also tests say which
  test.
- **The nevers**, in one place: no `.Instance` or `Find*`; no `UnityEngine` in
  `Sim`; no scene as source of truth; no editor-only generation logic; no
  hand-typed metadata that a tool can measure; no per-unit classes; no second
  implementation of any math; no `#if` outside `Platform.*`; no one-shot
  setup scripts in the package.
- **Ticket shape.** One extension point per ticket. A ticket that needs a hook
  the architecture lacks *adds the hook* with a test — it does not add a
  sibling. A ticket that would add a "setup" script instead adds a tool on
  `IGenerator` or nothing.
- **Definition of Done.** All suites green; no new suppressions; budgets
  within gate; if a mechanism was added, a golden replay was added with it.

---

## 12. What carries over from v1, and what does not

**Carries over — algorithms** (ported, with golden tests written against v1's
outputs): `RouteTraffic`'s Frontness model, `GenerationGates`,
`RouteSynthesizer`, `HardpointSelector`, `TerrainField`, `PropPlacer`,
`WaveSynthesizer` / `WaveRecipe`, `GenerationAdvisor`'s search, the forge's
assembly steps, `EnvPack`'s schema and measurement, `CameraFramingSetup`'s
solve.

**Carries over — doctrine**: determinism by hand-rolled RNG; gates as
preconditions; nothing emitted on failure; measured metadata over typed;
expand-don't-parallel; template-plus-hints over stat blocks.

**Carries over — content**, via a one-time migration script: definitions,
wave tables, the damage table, the Refinery Delta layout (as the regression
anchor), env packs.

**Does not carry over**: the MonoBehaviour sim, singletons, scene-as-truth,
the Python model, `BuildRealUI`, uGUI, the one-shot script bed, the
`Assets/Editor/Coplay` folder name.

---

## 13. Build order — desktop first

Each phase has an exit criterion that is a test, not a demo.

| Phase | Scope | Exit criterion | Size |
|---|---|---|---|
| **0 Skeleton** | Package root, all asmdefs, arch test, CI, `Sim` with tick + RNG streams + replay, empty `SimWorld` | Arch suite green; a replay of zero commands hashes identically on desktop and web | 1 wk |
| **1 Sim core** | Traffic, targeting, weapons, damage, economy, wave schedule, game state — ported | Golden replays reproduce v1's outcomes on the migrated Refinery Delta layout | 3–4 wk |
| **2 Content + bake** | Authoring SOs, `Bake()`, traits, registries, validation, migration script | Content suite green; zero dangling GUIDs; v1 content migrated | 2 wk |
| **3 Certify** | Runner, greedy policy, margin report, first solver | v1 baseline curve reproduces within tolerance | 2 wk |
| **4 View, one tier** | Unit views + interpolation, VFX/audio registries, strategy camera, UI Toolkit shell with controller focus | Playable on desktop; conformance test green on the one profile | 3–4 wk |
| **5 Generation, runtime** | Layout + wave generators ported onto `IGenerator`; gates on data; certify in the loop | A fresh seed generates, certifies and plays in the shipped build with no editor | 3–4 wk |
| **6 Desktop tier** | Profile ladder, `TerrainData` writer, grass/water masks, Shader Graph pass, POV camera, Steam Deck pass | Conformance green across all profiles; Deck at target FPS; budgets within gate | 4–6 wk |
| **7 Tools** | Generator window, campaign builder, forge with solved stats, harvester, contact sheets — all on `IGenerator` | A designer produces a certified 5-level campaign from a harvested vendor scene without touching code | 4 wk |
| **8 Web tier + console prep** | `Platform.Web`, low profile, mesh writer; console checklist; memory ceiling gate live | Web build runs the generator; determinism hash matches desktop | 2 wk |

Roughly **six months for two engineers plus art**, each phase shippable, every
suite green throughout. Not a rewrite of a game — a rebuild of its plumbing
around algorithms that already work.

---

## 14. Decisions to record now (ADR candidates)

1. **Fixed-point sim** (`Fix64`) — recommended. Reason: §3.3.
2. **30 Hz sim tick** — recommended. Reason: §3.1.
3. **Generation is runtime-capable** — recommended. Reason: §5.4.
4. **UI Toolkit over uGUI** — recommended. Reason: §7, console focus navigation.
5. **Steam Deck as the console proxy** — recommended. Reason: §8.
6. **One package root from day 1**, LICENSE and third-party notices in place,
   vendor art never inside it — recommended. Reason: the Asset Store audit's
   P0.1/P0.3 become impossible rather than deferred.
7. **The certifier replaces the Python model rather than porting it** —
   recommended. Reason: §6. This supersedes the desktop plan's P0.2 for v2.
