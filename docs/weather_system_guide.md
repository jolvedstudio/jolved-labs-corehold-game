# The COREHOLD Weather System — user guide

Weather is becoming the game's central element: the fiction is a small
terraforming system degrading, and the weather is how the player *sees* the
degradation. This guide documents the system as it stands — the mental model,
every knob, the precedence rules, the recipes — and closes with the framework
for where it goes next (critical events).

Everything here is data-driven: a designer with no code access can author any
weather this document describes.

---

## 1. The mental model

```
   EnvPack.weatherPool  ──(seed draw, per level)──▶  BASE PRESET   "geography"
   WeatherPreset.layers ──(declared composition)──▶  + LAYERS      "character"
   Wave mutators        ──(while the wave runs)───▶  + LINK LAYERS "events"
                                                        │
                                                   FLATTEN + MERGE
                                                        │
                                                 one merged carrier
                                                        │
                       light · fog · surfaces · precipitation · wind ·
                       gusts · lightning · trails · post · audio
```

Four rules carry the whole design:

1. **The base preset is geography.** Drawn once per level from the theme's
   `weatherPool` (the blueprint's pool overrides when set). Duplicates weight
   the draw — `[Clear, Clear, Dust]` is 2:1 clear.
2. **Layers are events.** A preset can declare `layers[]` (its permanent
   character), and the applier stacks `mutatorLinks` layers while a wave with
   the linked mutator runs. Wave ends → mutators clear → base look eases back.
3. **Everything applies from a baseline.** The applier captures the scene's
   authored look once, and every apply starts from it — stacks never stack on
   stacks, so chained waves cannot drift the scene. An empty pool is the R13
   null preset: pixel-identical to the authored scene.
4. **Surfaces ramp, they never pop.** Film, wetness and precipitation ease in
   over `surfaceChangeSeconds` (play mode; the editor snaps so previews don't
   lie). Snow that pops on in a frame reads as a bug; snow that builds over
   ten seconds reads as weather.

---

## 2. Preset reference (every knob, grouped)

All fields live on `WeatherPreset` (Create → COREHOLD → Weather Preset).
`override*` booleans gate their group — off means "leave the scene alone."

### Light
| Field | What it does |
|---|---|
| `overrideAmbient` / `ambientColor` | scene ambient (HDR). Raise for snow — snow bounces light; a dim snow scene reads as night |
| `overrideSun` + `sunTemperatureKelvin` | sun colour by blackbody (5 200 warm → 7 800 cool overcast) |
| `sunFilter` | tint multiplied over the temperature |
| `sunIntensityMult`, `sunShadowStrengthMult` | MULTIPLIERS over the authored sun, never absolutes — dim-authored suns keep their identity |

### Fog
`overrideFog`, `fogColor`, `fogDensity` (ExponentialSquared). ~0.002 = clear
play area with coloured distance; 0.006 = a storm you are inside of.

### Surfaces (the ground and props respond)
| Field | What it does |
|---|---|
| `groundWetness` 0–1 | darkens + desaturates terrain and props. No specular on purpose — at 130–150 m wet reads as *darker*, and a gloss lane costs WebGL bandwidth nobody can resolve |
| `groundSnow` 0–1 | whitens the terrain **by surface normal** (flat accumulates, slopes shed) and tints the props via `PlacedProp` markers |
| `snowColor` | slightly blue-shifted white; pure white reads as blown-out ground |
| `surfaceChangeSeconds` | the ramp length for all of the above (0 → default 10 s) |
| `trailStrength` 0–1 | how hard ground enemies carve tracks through the snow film |
| `trailMeltSeconds` | how long the field remembers — short is a blizzard erasing the army's passage, long is a ledger of every wave |

### Precipitation (what falls)
`precipitation` (None / Rain / Dust / Snow — what FALLS, independent of what
lies), `precipitationRate`, `fallSpeed`, `particleSize`, `streakLength` (rain
only), `particleColor`, optional `precipitationPrefab` (authored systems; the
applier fixes their reach and caps their size).

The sheet classifies **motion, not the enum**: fast fall → top-slab streaks
(rain); slow fall against wind → volume-filled drifting motes (dust, snow).
That is why snow must fall slowly — speed it up and it streaks like rain.

### Wind, gusts, lightning
| Field | What it does |
|---|---|
| `windDirection`, `windStrength` | drift applied to all precipitation |
| `gustStrength`, `gustPeriodSeconds` | two incommensurate sines modulate the horizontal drift — the rhythm never quite repeats. Gusts push sideways; they never make snow fall faster |
| `lightningStrikesPerMinute`, `lightningIntensity`, `lightningColor` | scheduled sun+ambient flashes (0.12 s pulses). The only per-frame work in the whole system, and only while a flash is live |

### Post, audio, composition
`overridePostProfile` + `postProfile` + `postWeight` (layered OVER the scene's
base volume — bloom and tonemapping survive); `ambientLoop` + `ambientVolume`
(synthesized wind when no clip is assigned); `layers[]` (see §4).

---

## 3. Precedence — who wins (the chained-mutators answer)

The stack is flattened in a fixed order:

> **base preset → its `layers[]` (depth ≤ 4, cycle-guarded) → one linked layer
> per active mutator flag, in `mutatorLinks` array order.**

Then merged with two kinds of channel:

| Kind | Channels | Rule |
|---|---|---|
| **Discrete** | ambient, sun, fog, ground tint, post, precipitation, wind, gusts, lightning, audio | **last in the stack wins** (whoever has the override on / a non-zero value) |
| **Accumulative** | `groundSnow` (with its colour + trail knobs), `groundWetness` | **max wins** — snow never melts because a second layer arrived |

Consequences worth knowing:

- **"Incompatible" cannot break anything.** Every channel resolves
  deterministically; there is no error state. Storm's rain will beat the base's
  dust *particles* while the dust *film* persists — mechanically fine,
  visually a designer's call. The order of `mutatorLinks` on the WeatherApplier
  IS the tiebreak among simultaneous mutators, so put the dramatic ones last.
- **Chained waves crossfade.** Wave ends → mutators fire `None` → targets
  return to base → the ramp eases everything back over `surfaceChangeSeconds`.
  Wave N+1's layer then ramps from wherever the surfaces are. Nothing pops.
- **Gameplay mutators are orthogonal by construction** — `[Flags]` bitmask,
  each effect reads its own bit (`Storm|Blackout` = fast air *and* short
  acquisition, plus both weather layers). There are no mechanically exclusive
  pairs today. So yes: composition is the designer's palette, and the
  infrastructure guarantees it can't crash — taste is the only constraint.

---

## 4. The authored library

`Tools → COREHOLD → Scene Setup → Weather` authors all of these (idempotent —
hand edits survive):

| Asset | Role |
|---|---|
| `Weather_Clear` | the null-ish base: authored look, nothing falls |
| `Weather_Rain`, `Weather_Dust`, `Weather_Snow` | the three core weathers |
| `Weather_Overcast` | fog + ambient + weak shadows, zero particles — the cheapest mood in the system |
| `Weather_Sandstorm` | intense dust variant |
| `Weather_HeavySnowStorm` | **the composition showcase**: heavy snow base + `layers = [GustingWind, Lightning]` — three assets, one storm |
| `WeatherLayer_GustingWind` | layer-only: gusts, nothing else |
| `WeatherLayer_Lightning` | layer-only: strikes, nothing else |
| `WeatherLayer_Storm` | the Storm-mutator link layer |

---

## 5. Recipes

**A new weather "off the bat"** — duplicate the nearest preset, tweak, add to
an EnvPack's `weatherPool`. No code, no registration; the merge/ramp/trails
machinery keys off *values*, not identities.

**A composed storm** — make a base preset, then fill its `layers[]` with the
single-purpose layer assets. Layers compose by the §3 rules; ≤ 4 deep.

**Weather as a wave event** — on the scene's WeatherApplier, add a
`mutatorLinks` entry: `{ mutator: Storm, layer: WeatherLayer_Storm }`. Every
wave carrying that flag now *looks* like what it *does*. Multi-flag waves stack
every matching layer.

**Per-level control** — the blueprint's `weatherPool` overrides the theme's;
one entry = deterministic weather for that stage.

---

## 6. The performance contract

First cut is WebGL; this system was built to a hard rule — **no per-frame cost
when idle, no screen textures, no compute.**

Costs as shipped: one throttled tick (4 Hz) for ramps/gusts/scheduling ·
property-block writes only during ramps (cached renderer lists) · one particle
sheet, one shared material · trails = 0.26 MB RT + tiny stamped quads + one
512² multiply blit per 0.5 s + one terrain sampler · lightning = per-frame only
while a 0.12 s flash is live.

Banned without a measured case: screen-texture effects (heat shimmer,
refraction — Desktop tier), per-frame `FindObjects*`, per-instance material
edits, volumetrics, real-time shadow-casting weather lights.

---

## 7. Narrative frame: the failing terraformer

The degradation story gives every knob a diegetic name — weather stops being
decoration and becomes *telemetry of a dying machine*:

| System term | Fiction |
|---|---|
| base preset | the region's **regulator state** (what the terraformer still manages here) |
| mutator layer | a **fault transient** riding an assault (the machines strain the grid) |
| `surfaceChangeSeconds` | how fast the regulator loses grip |
| trails filling in | the system still self-repairing — for now |
| escalating pool per campaign stage | the arc: early maps draw Clear 2:1, late maps draw storms 2:1 — **the campaign IS the pool weighting**, no new tech needed |

That last row is the cheapest storytelling instrument in the project: the
degradation arc across the campaign is authorable *today* by weighting each
stage's `weatherPool`.

## 8. Critical events — the framework (not yet built)

When floods and earthquakes come, they should be **the same shape as
everything above**: *an event = a timed weather layer + one gameplay hook +
one surface response.* The layer machinery already handles look, ramp and
restore; each event adds exactly one new mechanic. Sketches, with the honest
flags:

| Event | Look (existing tech) | New piece | Cert impact |
|---|---|---|---|
| **Flood / flash flood** | wet 1.0, rain layer, fog | a rising water plane + a lane-slow or lane-deny term | **HIGH — touches routes; balance-model term required; ADVISE-FIRST** |
| **Earthquake** | dust puffs, prop topple via jitter | `CameraShake` (already shipped) + brief build-lockout | medium — lockout needs a model term |
| **Regulator surge** | lightning layer at high rate, post flicker | temporary turret buff/debuff | medium |
| **Solar event** | bleached grade, harsh shadows | Blackout-mutator kinship (acquisition penalty exists) | low — reuses R20 |
| **Collapse finale** | HeavySnowStorm++ or Sandstorm++ | scripted sequence of the above | composition only |

Doctrine for all of them: **presentation through the weather stack, gameplay
through the mutator system** — because mutators are already certified,
composable, wave-scoped and cleared on wave end. A critical event is a wave
mutator with a heavier coat.
