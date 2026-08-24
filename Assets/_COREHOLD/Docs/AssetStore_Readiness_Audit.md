# COREHOLD → Asset Store TD Generator: Readiness Audit & Punch List

> **Status:** synthesized from two full-codebase audits (redistribution/packaging,
> and generator-vs-hardcoded), run against the campaign branch. Three defects the
> audits surfaced are already fixed (forge model-row schema, BuildMenu role-tag
> id drift, wave-data generator de-menued as destructive). Part C (wave
> synthesis + `--waves` certification) landed while the audits ran, closing what
> was previously the biggest generator gap — findings below are updated for it.
>
> **Verdict:** not publishable as-is. Two ship-stoppers dominate everything
> else: the entire visual layer is unredistributable vendor art, and the
> certification engine is a Python file outside `Assets/`. Both are fixable;
> neither is small.

---

## P0 — Ship-stoppers (block ANY listing, template or generator)

### P0.1 The art problem — the game's visuals are not ours to ship
Every pack is git-ignored and referenced by GUID: **194 unresolvable GUIDs**.
What breaks without the packs: **11 of 14 scenes** (Game.unity alone: 60
dangling refs), **23 of 24 prefabs** (9 of 10 towers are nested compositions of
vendor turret parts; enemies lose mesh, Avatar AND AnimatorController), 8 of 21
materials, **all 27 EnvPack authoring prefabs** (variants of vendor rocks — a
variant with a missing base has no geometry at all), and the font
(SCI-FI UI Pack Pro's Aldrich SDF — **1,467 references across 11 scenes**).
`_COREHOLD/Audio`, `Prefabs/UI`, `Prefabs/VFX` are `.gitkeep`-empty: audio and
VFX layers exist only as editor scripts that assemble vendor content.

Packs involved: TD Sci-Fi Turrets V2, Mech Constructor Spiders, Creepy Cat Vol 4
(props + SFX), Destructible Humanoid Robot, RipVertices Drone, SCI-FI UI Pack
Pro (UI + font), JMO Cartoon FX Remaster, IndieGameModels Turret SFX, Yoge,
Layer Lab GUI Pro.

**Options** (pick one; this is the big cost decision):
- **(a) Original/commissioned placeholder art set** — full control, real cost.
- **(b) CC0 replacement pass** (Kenney/Quaternius meshes+SFX, Google-Fonts SDF
  font) — cheapest legitimate path; the procedural-primitive approach proven by
  SetupColossus generalizes for enemies/towers.
- **(c) Bring-your-own-art product** — ship primitives + the Character Forge and
  make "drop your models in" the story. Weakest demo, strongest honesty.

### P0.2 The Python engine
`docs/balance_model.py` (~1,230 lines) lives OUTSIDE `Assets/`, is invoked with
a project-root-relative path, and requires a Python 3 install. Pipeline stages
16–17 hard-fail without it — the generator does all its work and then discards
it. **Fix: port to C#** (deterministic sim, mechanical port), regression-gated
by byte-comparing report output against the .py on the shipped baseline. Keep
`--waves` / `--starting-salvage` / `--geometry` semantics as method inputs.

### P0.3 Packaging mechanics
- Single package root (today: 12 top-level entries; 111 editor scripts under
  `Assets/Editor/Coplay/` — a folder named after a third-party AI plugin).
- **`com.coplaydev.coplay` git-branch dependency must not ship**; declare real
  deps (URP 17.5, Splines 2.9, Input System, TMP) via asmdefs + package.json.
  Drop legacy PostProcessing v2, visualscripting, multiplayer.center.
- Delete from the package: `Assets/TextMesh Pro/Examples & Extras` (12 MB),
  `Assets/Samples/Splines` (4.8 MB — contains the repo's only asmdefs),
  `Assets/Scenes/SampleScene.unity`, `GameBackup.unity`, `_RenderTest`/
  `_IconRenderTesting` scenes+materials, root `Backup_TurretPrefabs/` (5 loose
  prefabs outside Assets/), orphan `Assets/Resources.meta`.
- **193 hardcoded `"Assets/_COREHOLD"` literals across 58 files** → one
  PackageRoot constant. 41 `"Assets/Vendor"` literals across 15 files die with
  P0.1.
- Project identity: `productName: _test_coplay`, `companyName: DefaultCompany`,
  no application id. No LICENSE / third-party-notices files anywhere (mandatory
  with any bundled art).
- Build Settings ghost: a git-ignored generated scene ships `enabled: 1`.
- `Gizmos/` and `WebGLTemplates/` only work at Assets root — needs a documented
  post-import step.

---

## P1 — Generator-genericization (the difference between "template" and "generator")

### P1.1 Model roster from assets (kills the hand-mirror AND the drift)
The .py embeds ENEMIES (10), TOWERS (5 of our 10!), pad names, and named-unit
logic (`scan_relay` as THE aura source, colossus/roller phase keys). It has
**already drifted from the shipped assets**: Wave_01.asset carries a
hand-authored Colossus Sentinel group the model never sees; every modeled air
group says drone where the assets field Wasp; the model's colossus is 2800 HP
vs the fielded 2400. **Fix:** export ENEMIES/TOWERS to the model per run from
the actual `EnemyDefinition`/`TowerDefinition` assets — the same injection
pattern already proven three times (`--geometry`, `--starting-salvage`,
`--waves`). This also removes Part C's one remaining coupling (the synthesizer
refusing rosters absent from the .py).

### P1.2 TurretKind → data
`HardpointCoverageGizmo.TurretKind` (4 entries), `RangeFor` (**literal ranges
that disagree with the assets by up to 8 m**), `ColorFor`, Mortar
special-cases, `HardpointSelector`'s class→kind arrays, and the kind→model-id
switch all mean: **the generator can only ever place the original four
turrets.** Fix: pad-class placement driven by `TowerDefinition` data (range from
tier 1, class eligibility as definition fields), registry-discovered like the
build menu already is (B0 got this right — extend the same pattern).

### P1.3 Special behaviors → archetypes
`WardenAura`/`SalvageRig`/`Floodlight`/`CryoField` hooks, `Enemy.IsColossus`,
the HUD's dedicated Colossus bar, Roller-only animator params: promote to
definition flags/archetypes so a buyer's units can opt into aura/boss-bar/
phase behavior without code edits. (The forge's archetype enum is the seed.)

### P1.4 Enemy roster registry
Towers discover via `RosterRegistry`; enemies have no equivalent — the only
enumeration is the retired GDD transcription. Add the enemy half (menuOrder-
style), which P1.1's export then reads.

---

## P2 — Polish for review quality
- Ticket IDs (R29, R33, A2…) appear in user-facing strings and stage titles —
  meaningless to buyers; sweep to plain language.
- Editor tool cull: 111 scripts, most one-shot migrations/probes
  (Ticket37*, Migrate*, *Probe, FinalSetup…) — keep the toolkit set (~30),
  archive the rest out of the package.
- Docs: three copies of the GDD, an internal roadmap, and raw tool output ship
  today; only the Level Generator user guide is customer-shaped. Needs: README,
  getting-started, per-tool guide, third-party notices.
- Menu hygiene: "stub"/"test fixture" wording in shipped menu items
  (Welcome/Closing stub, Test Manifest) → either promote or hide behind a dev
  flag.
- `[TUNE]`/`[MANUAL]` markers (100+) read as unfinished to a reviewer — a doc
  note explaining the convention, or a rename.

---

## Sequencing recommendation
1. **P1.1 model-from-assets** first: smallest of the big items, kills an
   active correctness problem (drift) in the LIVE game today, and Part C's
   refusal-gate makes its absence loud. Prerequisite-free.
2. **P0.2 C# port** second (P1.1 shrinks what must be ported — the tables go).
3. **P1.2/P1.4** third — the generator claim becomes true.
4. **P0.1 art decision** in parallel (longest lead time, mostly non-code).
5. **P0.3 packaging + P2** last, against the near-final tree.
