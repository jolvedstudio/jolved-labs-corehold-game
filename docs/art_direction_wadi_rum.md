# Art direction — Wadi Rum ground, Petra landmarks

The target for the desert biome, and the settings and assets that get there.

## The world in one paragraph

A dead-flat plain of red-orange sand, empty for hundreds of metres, with
**enormous** eroded sandstone massifs standing at its edges — vertical fluted
cliff faces, rounded or flat tops, blue-purple with distance. Low raking sun,
long shadows, sparse tufted scrub. Where someone worked the rock long ago, there
are carved facades and stepped cuts in the cliff walls.

**Wadi Rum supplies the ground and the space. Petra supplies the landmark
vocabulary** — monumental carved forms that read, in this fiction, as ruins that
predate the war. One world, not two.

---

## Three numbers that explain "boring" better than composition does

I argued for a while that generated maps read as dull because the generator never
composes for the camera. That's still true, and it's still the smaller problem.
These are bigger, and all three are cheaper to fix.

### 1. Nothing in the world is taller than 6.87 metres

Height distribution across all 58 entries in `EnvPack_SandyDesert`:

| height | entries |
|---|---:|
| under 1 m | **35** |
| 1–3 m | 10 |
| 3–5 m | 6 |
| over 5 m | 7 (tallest 6.87 m) |

Sixty percent of the pack is under a metre, on a 130 × 75 m field viewed from
130–150 m back. At that distance a one-metre object is texture, not architecture.
The Wadi Rum massifs in the references are **100–300 m**.

There is no size tier above "big rock." That is the single hardest fact in the
pack, and no placement logic addresses it.

### 2. Every prop is authored at scale 1

Every `scaleRange` in the pack is `(1, 1)`. The only size variation any prop ever
gets is `scaleJitter` — ±55 % damped by role, so ±28 % on a landmark. A 6.8 m
landmark varies between 5.4 m and 8.8 m.

The world is one size. Scale contrast is most of what makes a desert read as
vast, and there is none.

### 3. Every map is in a dust storm

`EnvPack_SandyDesert` has exactly **one** weather preset in its pool:
`Weather_Dust`, which overrides fog to a neutral grey-tan `(0.68, 0.66, 0.62)`
at density `0.006`. ExponentialSquared over the play field:

| distance from camera | fog |
|---|---:|
| 100 m (near edge) | 30 % |
| 150 m (the Core) | 55 % |
| 220 m (far edge) | 82 % |
| 300 m | 96 % |

More than half the colour of the play area is fog, and the horizon is gone
entirely. Everything converges toward one mid grey-tan. That is the flat look,
arithmetically — and because the pack has no clear-weather option, **the Wadi Rum
look has never once been on screen.**

The pack's own `fogColor` is `(0.65, 0.70, 0.78)` — a good aerial-perspective
blue. The weather preset overrides it away on every single map.

### And a fourth, already fixed but never seen

Shadows are off in both render-pipeline assets on this branch. Low raking sun
plus 500 m of shadow distance is a large part of the Wadi Rum read — fluted cliff
faces only exist as light and shadow. Run **Fix Shadow Standard** and commit it;
you have not yet seen this pack lit.

---

## The scale ladder

What the references demand, against what exists:

| Band | Height on screen | Where | Have | Want |
|---|---|---|---:|---:|
| **Massif** | 60–240 m | horizon + apron only | **0** | 10–14 distinct |
| **Outcrop** | 10–25 m | field edges, apron | ~0 | 12–16 distinct |
| **Boulder** | 3–8 m | mid-field | 14 | 14 + 4 wider |
| **Scatter** | under 1.5 m | ground | 37 | 37, placed *less* |

The pack is a scatter library with nothing above it. Every band except the last
is missing or nearly so.

Note the counter-intuitive part: **clutter density should come down**, not up. The
Wadi Rum floor is nearly bare, and its emptiness is what makes the massifs read.

---

## Parameter table

`EnvPack_SandyDesert` — current → proposed, with the reason.

| Field | Now | Proposed | Why |
|---|---|---|---|
| `landmarkDensity` | 3.8 | **1.5** | the floor is empty; drama moves to the edges |
| `midFieldDensity` | 3.119 | **1.2** | same |
| `clutterDensity` | 4.0 | **1.0** | bare sand is the point |
| `silhouetteDensity` | 4.0 | **3.0** | keep it high, but with 10–14 distinct massifs behind it |
| `outfieldDensity` | 4.0 | **2.5** | the apron carries the massifs now |
| `toneVariation` | 0.626 | **0.35** | Wadi Rum rock is near-uniform in hue; value comes from light |
| `slopeTiltMaxDegrees` | 18.01 | **8** | a massif leaning 18° is a mistake, not geology |
| `clusterChance` | 0.922 | **0.85** | rubble aprons at massif feet are real; keep most of it |
| `groundZoneStrength` | 0.6 | **0.45** | sand is fairly uniform; rocky aprons are local |
| `scaleJitter` | 0.553 | keep | variety is wanted |
| `sunAngles` | (28, −35) | **(24, −55)** | lower and more across-frame: longer shadows, raking cliff light |
| `sunColor` | (1, .93, .67) | keep | already the right warm |
| `fogColor` | (.65, .70, .78) | keep | already the right blue |
| `fogDensity` | 0.0007 | **0.0020** | at 0.0007 there is no aerial perspective at all: 4 % at 300 m |
| **every** `scaleRange` | (1, 1) | **per role, below** | the single biggest lever in this table |

`fogDensity 0.0020` gives 8 % at 150 m, 30 % at 300 m, 63 % at 500 m — the play
area stays clear, distance goes blue.

### scaleRange per role

| Role | Proposed | Effect |
|---|---|---|
| Massif (Silhouette) | `(1.5, 3.0)` | a 60 m authored mesh spans 90–180 m |
| Landmark | `(0.8, 1.8)` | real size classes inside one role |
| MidField | `(0.7, 1.4)` | |
| Clutter | `(0.6, 1.5)` | |

### Weather

The fix is not to retune `Weather_Dust` — it is a dust storm and should look like
one. The fix is that **it must stop being the only option**.

- Add `Weather_Clear` to the pool with `overrideFog: 0`, so the pack's own blue
  haze survives. Most maps should draw this.
- Keep `Weather_Dust`, but soften `fogDensity` `0.006 → 0.004` and warm its colour
  toward a light ochre `(0.72, 0.62, 0.48)`. Real dust is warm; neutral grey
  reads as a rendering fault rather than as weather.

---

## Shopping list

Specifications, not vibes. Counts are per role.

### Massifs — 10–14 distinct (Silhouette role) — **the priority**

- Authored 40–80 m tall; placed at `scaleRange (1.5, 3.0)`.
- Vertical fluted or columnar cliff faces; rounded, domed or flat tops.
- **Asymmetric in profile.** The placer rotates props randomly, so a symmetric
  mesh reads as the same object every time.
- Each must be tellable apart from the others at 512 px. That is the test.
- Geometry can be cheap — at this distance the outline is the entire job.

*Check the Mesa and Rockscape packs you already own before buying: if anything in
them is 40 m+, or scales cleanly to it, this band may already be in hand.*

### Outcrops — 12–16 distinct (Landmark role)

- 10–25 m: isolated stacks, eroded fins, tilted slabs, boulder piles.
- Dark weathered stone against light sand — the value contrast does the work.
- This is the navigation band: "the leaning one", "the split one".

### Mid-field — add 4 to the existing 14

- 3–8 m, and specifically **wider and flatter** than what exists. The current 14
  are all roughly the same proportion.

### Scatter — buy nothing

37 entries is plenty. The change here is density, not content. Add 4–6 low
tufted desert scrub only if the pack has none — Wadi Rum's floor carries sparse
low bushes, and they are the only vegetation in the biome.

### Petra set — 3–6 pieces

Monumental carved facades, cut doorways, stepped rock. Not needed for E1/E2;
these are E4 set-piece material, and they are what gives the world a history
rather than a geology.

### Ground textures

- Red-orange sand with wind ripples, seamless, **low contrast** — high contrast
  in the base map fights the substrate tint and reads as tiling.
- Coarse gravel or scree, grayscale, for `groundRockDetail`.

---

## Known follow-ups in code

Two things this direction will run into. Neither blocks asset acquisition.

**Massifs and the in-field pool.** `PlaceOutfield` draws from the Landmark,
MidField and Clutter roles, so a massif entered as a Landmark would also be
offered to in-field placement. The existing gates would almost certainly reject
it — a 40 m footprint cannot clear the routes, and the camera sight-line test
would refuse it — but it burns placement attempts and could starve the in-field
landmark count. Putting massifs in the **Silhouette** role avoids this entirely
at the cost of the north band only; a small `maxInFieldRadius` guard would let
them use the whole apron. Decide once there is a massif to test with.

**Clearings could be emptier.** `SubstrateField.ClearingLow` is −0.30, leaving
roughly a quarter of the field clear. Wadi Rum wants closer to half. Raising it
to −0.10 is a one-constant change, worth doing once the scale ladder exists —
emptiness only reads when there is something big to be empty *around*.

---

## Division of work

**Written here** — the target, the parameters, the specifications. No eyes on
assets required.

**Coplay** — matching specifications to actual prefabs, in the project and on the
Asset Store, because that needs eyes on the meshes. Start with the massif band:
it is the only one where nothing exists, and it is the one the references are
really about.

The parameter changes can land before any asset arrives. They will not fix the
missing scale ladder, but the fog and density changes alone will show more of
what is already there than a dust storm at 82 % ever did.
