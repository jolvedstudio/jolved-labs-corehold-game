# Procedural environment: why it failed, and the four things that fix it

The generator places props correctly and the maps still look boring. Those are
two different problems, and only the first one was ever solved.

## The diagnosis

`PropPlacer.Dress` drew every candidate position from a uniform distribution
over the playfield and accepted the first one that cleared geometry:

```csharp
Vector3 pos = new Vector3(rng.Range(-halfW, halfW), 0f, rng.Range(-halfD, halfD));
```

Everything downstream of that line is careful and correct — route clearance, pad
keep-outs, camera sight lines, a route-occlusion budget, scale jitter, tone
variants, slope settle. None of it can rescue the sampler, because uniform
scatter has three properties that read as "generated" no matter how good the art
is:

1. **Constant density everywhere.** Every square metre fills at the same rate,
   so there is no thick and no thin, and therefore no composition.
2. **No correlation between what and where.** Rocks and plants interleave at
   random, so no patch of ground looks like it has a geology or a history.
3. **No empty ground.** Emptiness has to be *decided*; a uniform sampler can
   only leave gaps by accident, and accidental gaps read as missing props rather
   than as open ground.

Cranking the density knobs — which is where the desert pack already sits, at the
`4.0` ceiling on clutter, silhouettes and outfield — makes all three worse. That
is why "more props" kept not helping.

## E1 — substrate fields *(built)*

`Assets/Editor/Coplay/Generation/SubstrateField.cs`. A handful of cheap
deterministic fields that say what the ground under a given square metre *is*,
drawn from `TerrainField.Fbm` (one noise generator for the whole generator, on
separate seed streams, so nothing can drift):

| Field | What it decides |
|---|---|
| `Rockiness` | exposed stone vs soil, hard-clamped at the tails so zones are unambiguous |
| `Fertility` = `1 - Rockiness` | **anti-correlated by construction** — the whole trick |
| `Openness` | mid-scale noise thresholded into **clearings** with visible edges |
| `Disturbance` | distance to the play corridor: traffic thins plants, gathers wreckage |
| `Slope01` | gradient of the terrain height: steep ground sheds soil, exposes rock |
| `Patchiness` | plain density modulation, for packs whose names carry no signal |

Deriving fertility from the *same* noise as rockiness rather than a second
independent field is what makes zones read as zones. Two independent fields give
back the mush we started with.

`EnvPack.Entry` gained `affinity` (`Auto` / `Rock` / `Scrub` / `Neutral` /
`Debris`). `Auto` is value 0, so **every pack already on disk gets zoned dressing
with no re-authoring**: the affinity is inferred from the prefab name,
conservatively — only unambiguous tokens match, and anything unrecognised falls
to Neutral. Generic words that read both ways (wall, tank, post, pipe, panel) are
deliberately not matched: a wrong guess puts boulders in the scrub, which is
worse than no guess.

Placement then weights each candidate by two terms with **deliberately different
authority**:

- **Affinity relaxes.** As a slot burns through its 24 attempts the preference
  lerps toward 1 (quadratically, so most attempts stay picky). The early attempts
  do the composition, the late ones guarantee the fill — placement counts hold at
  roughly what uniform scatter gave. Composition that cost props would just trade
  one kind of empty map for another.
- **Openness does not relax.** A clearing is a decision. An attempt budget that
  eventually filled it in would erase the only genuinely empty ground on the map.
  Props refused by a clearing land in the dressable ground instead, so the same
  count arrives as *thicker cover plus real open pans*.

Roles obey clearings differently: landmarks are 60% exempt, mid-field 20%,
clutter not at all. The oldest composition in landscape painting is open ground
with one strong silhouette in it, and a rule that pushed landmarks out of every
clearing would forbid exactly that shot.

Two lines in the dressing log make the result checkable without opening the
scene:

```
affinity:  22 rock, 3 scrub, 0 debris, 11 neutral (0 set by hand, the rest inferred from prefab names)
substrate: 41/47 prop(s) on PREFERRED ground (87%), 6 placed on relaxed attempts; …
```

`36 neutral` on a desert pack means the prefab names carry no signal and those
entries want their affinity set by hand. A collapse toward "relaxed" means the
map is too crowded for the zoning to have room to work.

**Regeneration note.** The substrate test consumes RNG draws, so existing seeds
now dress differently. The geometry gates all run *before* dressing and are
unaffected; gate 2b (occlusion) runs after, and dressing's own self-repair plus
the pipeline's reject-and-reseed absorb it. Scenes already baked on disk do not
change — only new generations.

## E2 — ground that is not one flat texture *(built)*

Zoned props standing on a uniform sheet only get half the effect. The eye reads
the ground first, and a single flat texture tells it the whole field is one
material no matter what is standing on it.

The relief mesh already baked vertex colours (valley darkening, slope
desaturation) and already had a vertex-colour-aware shader,
`COREHOLD/Terrain Lit` — so E2 extends both rather than adding a lane:

- **Tint.** `TintAt` now multiplies in a substrate zone tint: cool grey where
  the ground is stony, warm ochre where it is not, picked by the *same*
  anti-correlated field the props obey. Everything can only darken (the tint
  multiplies albedo and vertex colours clamp at 1), so the look pass's exposure
  survives intact — "bleached" is expressed as darkening *less*.
- **The worn band.** Disturbance pulls the tint back toward pale, drawing a
  scuffed strip along every route. It is the prettiest part of this and the most
  gameplay-legible: the corridor now reads as a road in the ground itself.
- **Grain.** Vertex **alpha** carries the rock weight, and the shader crossfades
  the near-field detail between the existing fine noise and a coarser, stronger
  one. Grain size is most of what separates gravel from sand at this camera
  distance, and the coarse map *generates* the same way the fine one always has
  — so this needs no new art at all. `EnvPack.groundRockDetail` takes a real
  gravel texture when one is bought.

`EnvPack.groundZoneStrength` (default 0.6) scales all three per theme; 0 restores
a single uniform ground.

Safety: `_RockDetailBlend` defaults to **0** in the shader. A scene baked before
E2 has vertex alpha 1 across the whole mesh, so without that gate it would come
back wearing gravel everywhere. Only a freshly generated material turns the lane
on, and old scenes render byte-identically.

Limitation: this rides the relief mesh, and `TerrainStage` skips entirely when
`terrainRelief` is off. Blueprints default it on, so this is the normal path —
but a deliberately flat map still gets a single-material ground.

## E3 — terrain that reads from above *(advise first)*

Mesas, escarpments and dry riverbeds outside the corridor mask, instead of the
current gentle rolling. This is where the purchased mesa and cliff packs pay off.

Flagged advise-first because it touches the LOS gates and certification: taller
relief near the corridor changes what a turret can see, and the balance model's
high-ground term measures against `TerrainField`. Any change here gets a gate
re-run before it ships.

## E4 — composition rules *(after E2)*

Guaranteed clearings on the approach side, ridge silhouettes placed against the
camera rather than at random, deliberate framing (sparser inside the action so
the fight stays readable), and lane shoulders that make the corridor legible from
the air. E1 supplies the fields these rules need; E4 is the art direction on top.

## Asset dependencies

Bought or being bought in parallel — nothing above is blocked on them except
where noted:

- Lowpoly mesa / desert rock packs → E3, plus better Landmark entries for E1.
- Stylized rocks and cliffs → E3.
- Desert ground texture set (sand, gravel, cracked pan) → E2's blend targets.

Vendor packs stay git-ignored and machine-local; committed content is made
self-contained through `VendorLocalizer`, as with every other pack.
