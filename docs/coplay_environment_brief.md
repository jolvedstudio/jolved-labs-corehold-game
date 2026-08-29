# Coplay brief — environment generation (E1–E4)

**You own the aesthetics. Pick and prepare the assets; the systems are handled.**

This brief exists so you can choose environment assets for E1 and E2 knowing
exactly what the placement system does with them, what makes an asset good or
useless to it, and what WebGL will refuse to render.

Read `docs/environment_generation_plan.md` for the full rationale. This document
is the operational half: what to look for and what to do.

---

## 0. Lanes

**Yours.** Choosing assets. Preparing prefabs (pivots, scale, materials, LODs).
Folder placement. EnvPack knobs — densities, cluster chance, jitter, tone,
`groundZoneStrength`. Setting `affinity` on entries. Ground textures. Skyboxes.
Colour, light and sun angles on the pack.

**Not yours — do not edit.**
`SubstrateField.cs`, `PropPlacer.cs`, `TerrainStage.cs`, `TerrainField.cs`,
`COREHOLD_TerrainLit.shader`, the balance model and its data, and any generated
`Blueprint_*.unity` scene (they are rebuilt from the generator; edits are lost).
If one of those needs to change to fit an asset, say so — don't work around it.

---

## 1. How dressing works now, in one page

Levels are generated. A `LevelBlueprint` seeds routes, pads and a Core; then
`PropPlacer.Dress` fills the field from the theme's `EnvPack`.

Placement is a **deterministic rejection sampler**. For each slot it makes up to
24 attempts; each attempt redraws a random prefab from the role's pool and a
random position, then tests it. The first candidate that survives every test is
placed. Same seed ⇒ same map, prop for prop.

The tests a candidate must survive:

| Test | What it protects |
|---|---|
| Route clearance | lane half-width + widest enemy body + the prop's own radius |
| Pad keep-out | 6 m around every hardpoint (3 m in a hairpin fold) |
| Core keep-out | 10 m |
| Camera sight line | the player must be able to SEE each pad — at a shallow pitch a 12 m landmark hides ~15 m of ground behind it |
| Route visibility budget | only a bounded fraction of the route may be hidden |
| Prop spacing | no overlapping footprints |
| **Substrate (E1)** | does this prop belong on this ground? |

**Substrate (E1)** is the new one. A set of cheap deterministic fields say what
the ground under each square metre *is*: rockiness, fertility as its
**anti-correlate** (from the same noise — that's what makes zones read as zones),
clearings, disturbance near the corridor, slope, and plain patchiness.

Two terms, with different authority:

- **Affinity relaxes.** A prop that wants stony ground will hold out for it on
  early attempts and take anything by the last. So zoning costs no fill rate.
- **Openness does not relax.** Clearings stay empty. Landmarks are 60 % exempt
  (open ground with one strong silhouette in it is a composition, not a hole),
  mid-field 20 %, clutter not at all.

**E2** puts the same zoning in the ground: a cool-grey/warm-ochre tint split, a
pale scuffed band along every route, and a coarse-vs-fine detail crossfade driven
by vertex alpha.

### Why this matters for your choices

The system can only compose with what the pack gives it. If every prefab in a
role looks the same, perfect zoning still produces a boring map — it just
produces it in tidier patches.

---

## 2. The measured problem: the packs are inverted

This is the concrete finding, and it is the most useful thing in this document.

`EnvPack_SandyDesert` has 58 entries. Multiply the role densities (all near the
4.0 ceiling) by the standard 130×75 field and you get roughly this:

| Role | Distinct prefabs | Slots placed | Repeats per prefab | Visibility |
|---|---|---|---|---|
| Landmark | **5** | ~11 | 2.2× | highest — navigation anchors |
| MidField | 14 | ~25 | 1.8× | high |
| Clutter | **37** | ~68 | 1.8× | lowest — small scatter |
| Silhouette | **2** | ~32 | **16×** | the entire horizon |

`EnvPack_RockyDesert` is worse: 2 Landmarks, 1 MidField, 22 Clutter, 0 usable
Silhouettes.

**The variety is inverted.** Maximum variety sits where it is least visible
(small scatter on the ground), minimum variety where it is most visible (the
readable big shapes and the horizon line). Two silhouette prefabs repeated
sixteen times across the skyline is wallpaper, and no amount of placement
cleverness fixes it.

**So the shopping list is Landmarks and Silhouettes, not more clutter.**
Target roughly:

- **Landmark: 12–16 distinct**, so ~11 slots rarely repeat.
- **Silhouette: 10–14 distinct**, so the horizon reads as a range, not a pattern.
- **MidField: 14 is fine.** Add variety in SIZE more than in count.
- **Clutter: 37 is already plenty.** Do not add more.

---

## 3. What makes a good asset, per role

The camera sits **130–150 m back** at a shallow pitch. That distance decides
everything: fine surface detail is invisible, and **silhouette is the only thing
that reads.**

**Landmark** — large, distinct, navigable. The player should be able to say
"the tall leaning one" and be understood. Wildly different silhouettes from each
other; height 8–20 m. Mesas, arches, spires, wrecked superstructures. Avoid
symmetric blobs: a symmetric rock looks identical from every angle, and the
placer rotates props randomly, so it will read as the same prop every time.

**MidField** — 2–6 m, fills the space between routes. Its job is to break up
flat ground without hiding anything. Variety of *proportion* matters more than
variety of shape: a squat wide one, a tall narrow one, a low sprawling one.

**Clutter** — under 1.5 m, cheap and numerous, never sight-line relevant. Also
what clusters are drawn from. Already well stocked.

**Silhouette** — the far band beyond the field's north edge, seen only against
the sky. Only the outline matters, so these can be extremely cheap. Distinct
profiles and varied heights are the whole job. This is the most under-served
role in the project.

### Prefab hygiene that the placer depends on

- **Pivot at the base, centred.** The placer positions by pivot, sinks each prop
  0.06–0.15 × scale into the ground, then the terrain stage lifts it by the local
  height. An off-centre or mid-body pivot floats or buries the prop.
- **No baked ground plane, no base disc.** The prop sits on generated terrain.
- **Tight bounds.** `footprintRadius` and `height` are measured from the mesh; a
  prefab with a huge empty bounding box reserves space it does not use, and the
  map comes back sparse for no reason.
- **Everything must cast a shadow.** Blob shadows are retired. Run
  `Tools → COREHOLD → Look → Fix Shadow Standard` after importing.
- **Materials must be URP.** A built-in-pipeline material renders magenta.
- **LODs are welcome**, but LOD0 must be reasonable — the far band never gets close.

---

## 4. Affinity: name your prefabs and it is free

`EnvPack.Entry.affinity` defaults to `Auto`, which infers from the **prefab
name**. Matching is case-insensitive substring, checked in this order:

- **Scrub** — tree, bush, shrub, grass, plant, cactus, cacti, scrub, fern, weed,
  flower, palm, agave, yucca, sage, foliage, vegetation, stump, hedge, reed,
  moss, vine
- **Rock** — rock, stone, boulder, cliff, mesa, crag, scree, gravel, butte,
  spire, outcrop, pebble, granite, sandstone, geode, formation, monolith, hoodoo
- **Debris** — wreck, debris, rubble, scrap, ruin, crate, barrel, container,
  barricade, sandbag, girder, junk, husk, carcass, chassis, cable, pallet,
  canister
- **Neutral** — everything else. Scatters with density variation but no
  ground preference.

Generic words that read both ways (wall, tank, post, pipe, panel) are
deliberately **not** matched — a wrong guess puts boulders in the scrub, which is
worse than no guess.

**So: keep or rename to descriptive names.** `Mesa_Large_A` classifies itself;
`SM_Prop_017` does not. Where a name lies, set `affinity` by hand on the entry.

What each affinity seeks:

| Affinity | Goes where |
|---|---|
| Rock | stony ground, and always on steep faces |
| Scrub | the ground rock did not take; thins near routes; can't hold a slope |
| Debris | **along** the corridor — wreckage gathers where things happen |
| Neutral | varied density, no preference |

---

## 5. E2 — what the ground needs

Two texture slots on the EnvPack, both optional:

- `groundMaterial` / `groundTilingPerMetre` — the base. Currently 0.2 on
  SandyDesert. Wants a **seamless, low-contrast** desert base. High contrast in
  the base map fights the substrate tint and reads as tiling.
- `groundDetail` — a small **grayscale** map tiled ~9× denser, overlay-multiplied
  where **0.5 is neutral**. Cracks, ripples, grain.
- `groundRockDetail` *(new)* — the same idea but **coarser and higher contrast**,
  used where the ground is stony. Grain size is most of what separates gravel
  from sand at this camera distance. Empty = a generated coarse noise, which
  already works, so this is an upgrade not a blocker.
- `groundZoneStrength` (0.6) — how strongly zones show in the ground. Turn it
  down if the tint fights your art; 0 restores a uniform ground.

A sand / gravel / cracked-pan set is the ideal buy here.

---

## 6. WebGL — the hard rules

**The current ship target is WebGL.** The editor runs the PC quality tier and the
browser runs the Mobile tier, so **the editor will lie to you**. This asymmetry
has already cost this project days.

Absolutely not:

- **VFX Graph** (`VisualEffect`) — needs compute shaders. WebGL has none.
- **Soft particles** — URP fades them against the depth texture, which the Mobile
  tier does not render. The particle renders **fully transparent**: invisible in
  the build, perfect in the editor.
- **Geometry or tessellation shaders** (`#pragma geometry`, `hull`, `domain`) —
  not in GLES 3.0 at all. Many stylized grass and fur packs use them.
- **Shaders reading `_CameraOpaqueTexture` or `_CameraDepthTexture`** —
  refraction, heat haze, depth-faded water. The Mobile tier renders neither.
- **Built-in-pipeline materials** — magenta under URP.
- **GrabPass** — unsupported.

Note this covers two of the four recent purchases: **Stylized Water 3** (needs
both screen textures) and possibly **Stylized Grass Shader** (may need a geometry
stage). Both are fine on Desktop and are queued as Desktop-tier features — do not
build WebGL content that depends on them.

**Run `Tools → COREHOLD → VFX → WebGL Shader Audit` right after any import.**
It resolves each target's quality tier and reports per target, with an `intake`
section listing exactly which shaders in a newly imported pack cannot run on
WebGL. Read that before spending authoring time on a pack.

---

## 7. Workflow

1. **Drop prefabs** into
   `Assets/Authoring/EnvPack/<Theme>/<Category>/`
   where `<Category>` is one of `Landmarks`, `MidField`, `Clutter`,
   `Silhouettes`. `_Shared/` holds props every theme can use.
   *(This tree is the scan root and is currently empty — the committed packs
   under `Assets/_COREHOLD/Authoring/EnvPack/` are localized copies.)*
2. `Tools → COREHOLD → Level → Build Env Packs From Folders` — builds or
   refreshes the pack. Authored values on existing entries survive.
3. `Tools → COREHOLD → Level → Measure Env Pack Metadata` — measures
   `footprintRadius` and `height`. **Do not type these by hand**: a radius short
   by 30 % looks fine in the inspector and puts a prop in the lane.
4. `Tools → COREHOLD → Look → Fix Shadow Standard`.
5. `Tools → COREHOLD → VFX → WebGL Shader Audit`.
6. Generate a level and read the console.

Vendor packs stay git-ignored and machine-local. Anything a committed scene needs
gets localized into `_COREHOLD` — code is never vendored.

---

## 8. What "done" looks like

Two lines in the dressing log tell you whether the pack is working:

```
affinity:  22 rock, 3 scrub, 0 debris, 11 neutral (0 set by hand, the rest inferred from prefab names)
substrate: 41/47 prop(s) on PREFERRED ground (87%), 6 placed on relaxed attempts; …
```

- Mostly **neutral** ⇒ the prefab names carry no signal. Rename, or set
  `affinity` by hand.
- A collapse toward **relaxed** ⇒ the map is too crowded for zoning to have room;
  lower a density knob.

And the per-role line:

```
Landmark   11/11 placed, +14 satellite(s) (5 distinct of 5 in the pack's pool)
```

**`distinct` is the variety truth.** "11/11 placed" can still be three prefabs on
repeat, and only that number shows it. Getting `distinct` up on Landmark and
Silhouette is the goal of this whole pass.

---

## 9. Where this is going

- **E3 — terrain that reads from above.** Mesas, escarpments, dry riverbeds
  outside the corridor. The mesa pack feeds this. Gated: it touches the
  line-of-sight gates and certification, so it gets a design pass first.
- **E4 — composition rules.** Guaranteed clearings on the approach side, ridge
  silhouettes placed against the camera rather than at random, deliberate framing
  (sparser inside the action so the fight stays readable), lane shoulders.

Assets chosen for E1/E2 with **strong silhouettes and varied heights** stay
useful in both. Assets chosen for surface detail will not.
