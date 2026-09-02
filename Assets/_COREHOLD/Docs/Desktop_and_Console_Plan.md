# COREHOLD → Desktop & Console: Port Strategy and Plan

> **Status:** proposal, written against the tree at `f548864` on
> `claude/td-game-desktop-port-t5j46f`. Read alongside
> `AssetStore_Readiness_Audit.md` — the two overlap deliberately, and where they
> do, this document defers to the audit's sequencing.
>
> **Verdict up front:** there is no port. There is a *boundary* to draw and a
> *second presentation tier* to build. Do not rewrite; do not open-ended
> "clean up first" either. Three narrow prerequisites, then additive work.

---

## 0. What the audit of the tree actually found

Five facts drive every recommendation below. They are worth stating plainly
because at least two of them contradict the premise of the question.

**0.1 — There is no WebGL-specific code.** A sweep of all 89 runtime scripts
(~21k LOC) for `UNITY_WEBGL`, platform branches, `SystemInfo` capability
queries or `QualitySettings` manipulation returns nothing. The only `Screen.*`
reads are a portrait-warning overlay and two IMGUI debug panels. **"WebGL-light"
is a content-and-settings constraint in this project, not a code constraint.**
Whatever else is true, the runtime does not need porting.

**0.2 — The generator is the product, and it is bigger than the game.** 133
editor scripts / ~27k LOC against 89 runtime scripts / ~21k LOC. The value
concentrated in this repo is `GenerationPipeline`'s twenty gated stages and the
certification chain behind them — not the rendering.

**0.3 — The simulation is analytic, deterministic and certified.** Movement is
a 1-D monotonic `Frontness` scalar along a spline with car-following
scheduling: *"transform-only, no Rigidbody, no collider"* (`EnemyMover`).
Targeting is distance-based. `NavMesh` appears nowhere despite the package
being installed. Physics is used in exactly five files, all of it picking
raycasts for build placement and input. Every generated level is gated against
`docs/balance_model.py` before it is allowed to exist, and even the noise is
hand-rolled specifically to avoid `Mathf.PerlinNoise`, *"whose tables are not a
contract across platforms"* (`TerrainField`). **This project already treats
cross-platform determinism as a contract. That is an unusual asset and the plan
below is built to protect it.**

**0.4 — Terrain is already solved, deliberately, as a bake.** `TerrainStage`'s
doctrine: *"THE MODEL STAYS PLANAR. Terrain is a generation-time constraint,
solved and gated here, then BAKED — into spline knots, transforms and one
relief mesh. The runtime never reads a heightfield."* `TerrainField` is a pure
analytic function `Height(x, z)` with a `CorridorMask(x, z)`. This matters
enormously: a pure height function can emit *any* representation, including a
Unity `TerrainData` heightmap.

**0.5 — The asset-adaptation pipeline already exists.** `EnvPack` holds direct
prefab references plus the two numbers the gates need (`footprintRadius`,
`height`), a `PropRole`, sun/fog/skybox/post-profile overrides, and density
knobs. `EnvPackTools.BuildFromFolders` *measures* prefabs from mesh bounds
rather than asking a human to type numbers, and infers role from asset label →
folder → size heuristic. `Assets/Vendor/` is already the git-ignored drop zone
with a stated policy. **The "grab any asset store scene" feature is ~80%
built.**

**0.6 — The tier scaffolding already exists, and its platform mapping is
already right.** Two quality levels ship. WebGL, Android and Windows Store
default to level 0 ("Mobile"); Standalone, PS4, PS5, Switch and GameCore
default to level 1 ("PC"). Each level names its own pipeline asset:

```
Mobile_RPAsset   render scale 0.8 · 1024 shadowmap · 1 cascade
                 no depth texture · no opaque texture · LDR grading
PC_RPAsset       render scale 1.0 · 2048 shadowmap · 4 cascades
                 depth + opaque on
both             shadow distance 50 · MSAA off · soft shadows off
                 default volume profile: SampleSceneProfile
```

**0.7 — …but the project's own tuned render chain is orphaned.**
`COREHOLD_URP.asset` is referenced by nothing — verified by GUID sweep across
`ProjectSettings/` and all of `Assets/`. `COREHOLD_Renderer` is referenced only
by that dead asset. `COREHOLD_PostFX` is referenced only by
`GameBackup.unity`, which the readiness audit already lists for deletion. Both
*live* pipeline assets name `SampleSceneProfile` — Unity's template volume
profile — as their default.

**So the shipping WebGL build renders at 0.8 render scale under a template
post-processing profile, while the hand-tuned chain sits unreferenced.** This
is hours of work to fix, it needs none of the phases below, and it is the
largest immediate visual win available on the target that already ships.

**0.8 — There are no assembly definitions for project code.** The only two
`.asmdef` files in the tree belong to the Splines samples. Everything compiles
into `Assembly-CSharp` / `Assembly-CSharp-Editor`. Sim, presentation, UI and
generation are separated by folder convention and discipline alone.

**0.9 — The camera is fixed at 38° pitch, framed to the whole playfield.**
There is a second, closer view already (`TurretCamera`, `ManualTurretControl`,
and `EnvPack.groundDetail` explicitly labelled *"near-field ground detail for
POV cameras"*), but it is not the default view. **This is the single biggest
risk to the visual-upgrade budget** and §5 treats it as a first-class decision
rather than a detail.

---

## 1. The answer: refactor the boundary, not the code

### Not a rewrite

A rewrite discards ~27k LOC of gated generation pipeline and, worse, the
*provenance* of the balance model — the reason anyone can claim a generated
level is fair. Nothing on the desktop wish-list requires different simulation
code. A rewrite buys a cleaner history and costs the only defensible claim the
product makes.

### Not an open-ended clean-up either

"Refactor and clean up, then port" has no exit condition, and the readiness
audit already lists enough clean-up (193 hardcoded path literals, 111 editor
scripts to cull, ticket IDs in user-facing strings) to absorb a quarter without
producing a single desktop frame. That work is real but it is *packaging* work,
sequenced by the audit, and it is not on the critical path to a desktop build.

### The actual move

**Draw one boundary, then add a tier.**

- The **simulation** — movement, scheduling, targeting, damage, wave state,
  economy — is platform-invariant and already correct. It gets an assembly of
  its own and is then *frozen* with respect to platform work.
- The **presentation** — rendering, terrain representation, vegetation, water,
  VFX, audio, camera, UI — becomes a tier selected by data.
- The **generation** pipeline keeps its gates, and gains stages that *emit*
  richer representations of the same certified geometry.

Desktop is then not a port but a *tier*, and console is a third tier that
reuses everything the second one established. This is why the boundary comes
first: it is the only work that is a prerequisite for **both** additional
targets, and it is the only work that gets more expensive the longer it waits.

### How a studio would actually run this

Studios that ship one game on five platforms do not port upward from the lowest
target. They author content at or near the **highest** tier and decimate down,
run **one** codebase with a data-driven quality ladder, and hold one line
absolutely: **gameplay is invariant across tiers.** A tier that changes a
hitbox, a spawn count, a navigation result or a timing is not a quality setting
— it is a different game, and it has to be re-certified and re-balanced per
platform, which is how port schedules die.

COREHOLD is unusually well placed to hold that line, because it has something
most projects lack: a model that can *prove* the line held. The C# port of
`balance_model.py` (audit P0.2) plus a headless replay test turns "gameplay is
invariant" from a code-review convention into a CI gate.

---

## 2. The six desires, sorted by whether they touch the certified simulation

This is the discriminator that should govern every scheduling decision. Work
that does not touch the sim is cheap, parallelisable and low-risk. Work that
does touch it costs a re-certification pass. Work that *invalidates* it should
not be done at all.

| # | Wish | Touches the sim? | Verdict |
|---|------|------------------|---------|
| 1 | Adapt owned Asset Store scenes | No (harvest); Yes (play in-place) | Two features, not one — §3.1 |
| 2 | Grass, water, Shader Graph | No, with two rules | Do it — §3.2 |
| 3 | Real Terrain, not meshes | Representation only | Do it, as an emitter swap — §3.3 |
| 4 | Terrain spawning tools | Only if unbounded | Two-band scattering — §3.4 |
| 5 | Colliders for navigation | **Yes — invalidates it** | Do not — §3.5 |
| 6 | 2K textures | No | Needs the tier system first — §3.6 |

---

## 3. Each wish, concretely

### 3.1 Owned Asset Store scenes

These are two different products and conflating them is the main risk here.

**Mode A — Harvest (recommended, cheap).** A new editor tool reads a vendor
scene and *emits an `EnvPack`* — props measured from renderer bounds exactly as
`EnvPackTools` already does, roles inferred by the existing label → folder →
size chain, plus the atmosphere fields `EnvPack` already carries and nothing
currently fills: `skyboxMaterial`, `sunIntensity` / `sunColor` / `sunAngles`
(from the scene's directional light), `fogColor` / `fogDensity`, `postProfile`
(from the scene's global volume), `groundMaterial` and terrain layer set. The
generator then builds a *new, gated, certified* level **in that scene's style**.
This is a straight extension of an existing tool and it composes with every
gate untouched.

**Mode B — Site (possible, gated, more expensive).** Play inside the vendor
scene as authored. A human marks a playable envelope (a rectangle plus a
walkable mask); `RouteSynthesizer` runs inside it; gates 1, 2, 2b and 3 run
exactly as today. If the site cannot host a certified layout, it fails loudly
and is not shipped. Do **not** attempt to make an arbitrary scene playable
without this envelope — the route synthesizer needs a bounded domain and the
clearance gate needs to know what counts as an obstacle.

**Licensing seam (do this in Mode A, immediately).** Add
`EnvPack.redistributable` and a `sourceAttribution` string. The desktop *game*
may use purchased vendor art; the Asset Store *template* may not (audit P0.1),
and the packaging step needs to be able to strip non-redistributable packs
mechanically rather than by memory.

### 3.2 Grass, water, Shader Graph

Presentation-only, with two rules that keep them so:

- **Grass must never be sight-line relevant.** Gate 2b (occlusion) tests
  whether a prop breaks a turret's line to its covered spans, using
  `EnvPack.Entry.height`. Detail grass is not in that test and must not become
  tall enough to need to be. Cap detail height below the gate's clearance
  threshold and keep it out of `EnvPack` entirely — it belongs to the terrain
  layer, not the prop pool.
- **Water is a mask, not a mesh.** Either purely cosmetic and outside the
  corridor, or — better — a genuine *unwalkable / unbuildable* mask fed into
  `TerrainField` alongside the corridor mask and consumed by `RouteSynthesizer`
  and `HardpointSelector`. That makes water a design tool that the gates
  understand, at the cost of one new mask layer in a class that already has
  four.

Shader Graph is otherwise unconstrained — the project has Shader Graph 17.3
installed and currently ships two hand-written shaders, so this is greenfield.
The one caution: `COREHOLD_TerrainLit` is what `TerrainStage` assigns to the
baked relief mesh; if §3.3 lands, its replacement must be a terrain-layer
material, which is a different shader family.

### 3.3 Real Terrain — a bake target, not a doctrine change

The doctrine ("the model stays planar, terrain is baked") is **correct** and
should survive. What should change is only *what gets baked into*.

Because `TerrainField.Height(x, z)` is a pure analytic function, it can sample
a `TerrainData` heightmap (513² or 1025²) exactly as easily as it currently
generates a 96² mesh. Everything downstream is unchanged: spline knots are
still lifted by sampling `TerrainField`, the T3 sight-line gate still samples
`TerrainField`, and the runtime still never reads a heightfield. This is a
**swap of the output writer**, roughly a day's work, and it unlocks terrain
layers/splatmaps, detail instancing (grass), tree instancing, basemap distance
and the whole terrain toolset.

**Both writers survive, selected by tier.** Unity Terrain costs more draw calls
and more memory than one baked 96-cell mesh — fine on desktop, wrong on the
web. So this is not a replacement: `TerrainStage` keeps the mesh writer as the
web tier's output and gains the heightmap writer for desktop and console, with
the `PresentationProfile` choosing. Both are fed by the same `TerrainField`, so
the geometry the gates certified is identical either way.

Three constraints to write into the stage:

- **No terrain collider on the navigation path.** Movement stays spline-driven.
  If a collider is added for build-placement picking, decals or VFX, it goes on
  a layer the simulation does not consult, and `EnemyMover` continues to derive
  height from spline tangents.
- **Splat/detail authority stops at the corridor.** `CorridorMask(x, z)` is
  already the right function; use it to keep the play surface readable
  regardless of how lush the outfield gets.

### 3.4 Spawning / scattering tools

`PropPlacer` is not a scatterer, it is a *gated* scatterer — it is why gate 2b
can be trusted. Third-party vegetation and scatter tools carry no such
guarantee. Rather than choosing between them, split the field:

- **Inside the play envelope** (`CorridorMask` below threshold, plus pad
  keep-outs): `PropPlacer` retains sole authority. Gated, seeded, certified.
- **Outside it**: any tool may run — Unity's own detail/tree instancing,
  Vegetation Studio, Polaris, whatever is owned — driven by masks
  `TerrainField` already computes.

Formalise this by having `TerrainStage` emit the envelope as a texture mask
asset alongside the heightmap. Then the third-party tool consumes a normal
Unity mask and needs to know nothing about COREHOLD's gates.

### 3.5 Colliders for navigation — the one thing to refuse

Replacing spline movement with collider- or NavMesh-driven navigation would:

- **invalidate every certified margin** — the balance model's traverse times,
  exposure windows and per-wave margins are all computed from arc-length along
  known curves;
- **destroy seeded reproducibility** — physics is frame-rate- and
  platform-sensitive; "same seed → same level → same certified margins" is the
  generator's core promise and it does not survive a solver;
- **break the shared-tail invariant** — merged routes are currently *identical
  geometry*, pinned by tangent constraints, which is why corridor maps gate
  cleanly at all.

Use colliders for what they are good at here: build-placement and selection
picking (already the case), decal and VFX raycasts, cosmetic debris and
destructible dressing, and a cursor-to-world ray for the POV camera.

If the goal behind the request is *visual* — units flowing around obstacles
rather than walking through them — the right implementation is **lane offsets
within the corridor**. `EnemyMover` already carries a fixed render lane offset
alongside its monotonic `Frontness`; making that offset vary along the route
(from a baked, seeded, gate-checked profile) buys the appearance of navigation
with none of the certification cost, because `Frontness` — the only thing the
model reads — is untouched.

### 3.6 2K textures

The importers already cap at 2048; the constraint has always been elsewhere.
What is actually needed is a per-platform override policy, a mip-bias and
streaming setup, and a **memory budget per tier** — which is meaningless until
§4.2 exists, and becomes a hard ceiling on console. Sequence this after the
tier system, not before.

---

## 4. The plan

### Phase 0 — The boundary (prerequisite for *both* new targets)

This is the only refactor on the critical path. It is small, and it is the
thing that gets more expensive every week it is deferred.

**0.1 Assembly definitions.** Split `Assembly-CSharp` into:

| Assembly | Contains | May reference |
|---|---|---|
| `Corehold.Data` | ScriptableObjects, definitions, blueprints | — |
| `Corehold.Sim` | `EnemyMover`, `RouteTraffic`, `PathRoute`, `WaveManager`, `TowerTargeting`, `TowerWeapon`, `GameManager` state | `Corehold.Data` |
| `Corehold.Presentation` | `VFXDirector`, `AudioDirector`, `OverlayManager`, `WeatherApplier`, cameras, animator bridges | `Sim`, `Data` |
| `Corehold.UI` | HUD, menus, panels | `Sim`, `Data` |
| `Corehold.Generation.Editor` | the pipeline and its stages | all runtime |
| `Corehold.Tools.Editor` | the rest of `Assets/Editor/Coplay/` | all runtime |

The point is the *reference direction*: `Sim` cannot see `Presentation`, so a
desktop feature physically cannot leak into certified code. The compiler
enforces what discipline currently does. This is also a prerequisite for the
Asset Store package regardless (audit P0.3 asks for asmdefs + `package.json`).

Expect this to surface a handful of genuine violations — the likeliest are
`GameManager` reaching into UI and `Enemy` touching `VFXDirector` directly.
Those are the couplings worth fixing; the rest of the "clean up" list is not.

**0.2 Port `balance_model.py` to C#.** Already audit P0.2, already blocking the
Asset Store listing. It becomes *more* important with tiers, because the gate
needs to run in CI on every target and a Python dependency will not survive a
console build farm. Do audit P1.1 (roster exported from assets) first as the
audit recommends — it shrinks what must be ported.

**0.3 The conformance test.** The project has no tests. It needs exactly one to
begin with: **replay a seeded level headlessly and assert the per-wave outcome
matches the certified model, on every tier.** This single test is what makes
multi-target development safe, because it converts "presentation must not
affect gameplay" into something CI can fail on. Everything else in Phase 0 is
in service of being able to write it.

*Exit criteria: assemblies split and compiling; C# model reproduces the Python
model's report byte-for-byte on the shipped baseline; conformance test green on
the current single tier.*

### Phase 1 — Tiers as data

**1.1 Extend the ladder that already exists.** Phase 1 is smaller than it first
looks, because §0.6's scaffolding is correct — two levels, per-platform mapping
already right. The work is: fold the orphaned `COREHOLD_URP` tuning into the
two live assets (or reconnect the chain and delete the duplicates — either way,
one surviving lineage), then extend to the rungs desktop needs and add the
console level, which today falls through to `PC`. Replace `SampleSceneProfile`
with `COREHOLD_PostFX` as the default volume profile on both.

**1.2 A `PresentationProfile` ScriptableObject** carrying what the URP asset
cannot: grass and detail density, detail draw distance, prop LOD bias, texture
memory budget, VFX concurrency budget, post-FX stack, shadow policy, target
frame rate.

**1.3 The invariant, enforced.** A profile may change nothing the simulation
reads. The conformance test from 0.3 runs across every profile.

*Exit criteria: the existing WebGL build is byte-identical in behaviour when
run through `URP_Web` + the web profile; conformance green on all profiles.*

### Phase 2 — The desktop tier (all additive)

Ordered by payoff per unit of risk:

1. **Camera decision** (§5) — settle this before spending art budget.
2. **Terrain emitter** (§3.3) — heightmap + layers + envelope mask.
3. **Detail/grass layer** (§3.2) with the sight-line rule.
4. **Shader Graph pass** — terrain layer blending, water, near-field detail.
5. **Water masks** into `TerrainField` and the synthesizer.
6. **Two-band scattering** (§3.4).
7. **Scene Harvester, Mode A** (§3.1) plus the redistribution flag.
8. **2K texture pass** against the Phase 1 memory budgets.
9. **Lighting**: real cascades, soft shadows, baked GI where scenes are static,
   MSAA or SMAA — all of it now expressible because tiers exist.

### Phase 3 — Console readiness

If Phase 0 held, nothing here touches the simulation. Budget it as *shell and
platform* work, not gameplay work.

- **Input**: already on the Input System with an `.inputactions` asset — good
  starting position. Needs a controller-first scheme and removal of any
  pointer-only path.
- **UI focus navigation**: the real cost. Three canvases built for
  pointer/touch need explicit focus order, selection state, and no
  hover-dependent affordances. Start it early in Phase 2; it is slower than it
  looks.
- **Safe area / overscan**, TV viewing distance (minimum type sizes),
  **suspend-resume**, **memory ceiling** as a hard fail in CI, load-time
  budgets, no guaranteed mouse cursor, platform certification (TRC/XR/Lotcheck)
  passes.
- **Determinism across compilers** — the hand-rolled FNV noise and the avoidance
  of `Mathf.PerlinNoise` already anticipate this. Keep that discipline; add a
  cross-platform hash check to the conformance test.

---

## 5. What this does for the build that already ships

Phase 0 and Phase 1 are not a tax paid for desktop. Most of Phase 0 is work the
WebGL game needs on its own merits, and one item is a live correctness problem.

**Certification drift is real today.** The readiness audit's P1.1 found the
model has drifted from the assets: 5 of 10 towers modelled, colossus 2800 HP
against 2400 fielded, air groups named `drone` where the assets field Wasp, and
a hand-authored Colossus Sentinel group in `Wave_01` the model never sees. The
shipping WebGL game is therefore certified against a model that does not match
its own content. Exporting the roster from `EnemyDefinition` / `TowerDefinition`
per run fixes it, and it is the audit's own first item.

**The Python dependency already costs a dev day.** Stages 16–17 hard-fail
without Python 3, after the pipeline has done all its other work — a generation
run that produces nothing. The C# port removes the dependency and lets the gate
run in CI on every commit, on the WebGL target, now.

**Zero tests, one manual ritual.** Every gameplay ticket currently ends with
"re-run the balance model before tuning", by hand. The conformance replay
automates exactly that ritual. Its value on WebGL is immediate and does not
wait for a second tier to exist.

**Compile time.** Editing one UI script today recompiles all ~21k runtime LOC
and forces the ~27k-LOC editor assembly to rebuild behind it. After the split,
editing `HUDController` rebuilds `Corehold.UI` and its dependents. That is
daily-life iteration speed on the project as it stands.

**Download size — the dominant WebGL constraint.** A GUID and symbol sweep
finds these installed packages referenced by *no* project script and backed by
*no* asset in the tree:

```
com.unity.visualscripting      0 references
com.unity.postprocessing (v2)  0 references   (URP has its own volume system)
com.unity.multiplayer.center   0 references
com.unity.timeline             0 references
com.unity.visualeffectgraph    0 references   (no .vfx assets)
com.unity.ai.navigation        0 references
com.unity.cinemachine         28 references   ← keep
```

Be precise about the mechanism: **the assembly split does not itself shrink the
WASM.** IL2CPP's linker already does whole-program stripping regardless of how
many assemblies there are. What the split buys is an explicit dependency graph
— which is what makes deleting the six unreferenced packages a safe, verifiable
change rather than a hopeful one. The deletion is the download-size win; the
split is what lets you make it without guessing.

**And before any of it:** §0.7's orphaned render chain. Reconnecting the tuned
pipeline and swapping `SampleSceneProfile` for `COREHOLD_PostFX` is hours of
work, on the live target, with no prerequisites at all.

What Phases 2 and 3 add — terrain, grass, water, Shader Graph, 2K textures,
console shell — does nothing for WebGL and should not be charged to it.

---

## 6. The decision that governs the art budget

The strategy camera is fixed at 38° pitch with a 35° vertical FOV, solved to
frame the whole playfield. At that framing, a 2K texture pass, per-blade grass
and a Shader Graph water surface are largely **invisible** — the pixels are not
there to show them.

There are three coherent answers, and the desktop tier needs one chosen before
Phase 2 item 8:

1. **Keep the framing, spend the budget on silhouette and lighting instead** —
   shadows, cascades, GI, atmospherics, denser outfield dressing. Cheapest, and
   honest about what a fixed strategy camera can show.
2. **Bring the camera closer and give it freedom** — lower pitch, pan/zoom/
   orbit within limits. Highest payoff for the requested features, and the one
   with real design consequences: the whole playfield stops being visible at
   once, which changes how the player reads incoming waves.
3. **Promote the POV turret view to a first-class mode.** `TurretCamera`,
   `ManualTurretControl` and `EnvPack.groundDetail` already exist for it. This
   is where 2K textures, grass and water pay for themselves completely, and it
   is a genuine desktop-and-console *feature* rather than a fidelity bump.

Recommendation: **3, with 1 as the baseline** — the strategy view gets the
lighting and silhouette work, and the POV mode becomes the reason the desktop
version exists. Option 2 is the expensive one and the only one that risks
invalidating the camera-framing stage and the coverage gizmo's assumptions.

---

## 7. What this deliberately does not do

- It does not touch `EnemyMover`, `RouteTraffic` or the gates.
- It does not fork the project or the scenes. One project, one content set,
  tiers on top.
- It does not schedule the readiness audit's P2 polish; that stays sequenced by
  the audit, against a near-final tree.
- It does not adopt NavMesh, Rigidbody navigation, or terrain colliders on the
  movement path — see §3.5.
