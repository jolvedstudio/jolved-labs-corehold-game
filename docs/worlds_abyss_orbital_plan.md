# Worlds: ABYSS (underwater) & ORBITAL (space) — Coplay asset brief + systems plan

Two new visual worlds for the campaign, built the way this project builds
everything: **an EnvPack + WeatherPreset skin over unchanged mechanics**,
with any genuinely new mechanic proposed separately and gated on the
balance model. Coplay owns asset selection and look (this doc is the
shopping brief — the packs are on Coplay's machine, invisible from here);
systems owns wiring, presets, and any mechanic that survives sign-off.

Narrative home (writer reference only, never player-facing vocabulary):
the Verdance War runs planet-wide — ocean terraforming nodes (ABYSS) and
the orbital relay ring (ORBITAL) are campaign chapters, not modes.

---

## 1. Shared rules (both worlds — read first)

1. **Vendor policy.** Packs stay git-ignored on Coplay's machine. Anything a
   committed scene/prefab references must go through *Localize VFX Config* /
   the Campaign Builder's *Localize vendor assets* so the repo stays
   self-contained. Preflight blocks builds on vendor references.
2. **WebGL shader constraints.** URP-compatible shaders only; run
   *WebGL Shader Audit* to zero errors before calling a pack usable. Reject
   assets whose materials use custom included shaders unless they compile
   clean; prefer URP Lit/SimpleLit. No GrabPass, no HDRP materials.
3. **Budget.** The build is ~36 MB. Each world may add **≤ 3 MB** after
   `EnforceCrunchOnOverrides` / `WebGLBudgetPass`; check `BuildSizeAudit`.
   Prefer few textures reused with `toneVariation` over many uniques.
4. **Colour language holds.** Danger red stays reserved; friendly cyan/amber
   stays UI/ally; enemy palette unchanged. A world recolours the STAGE, not
   the actors.
5. **How an EnvPack plugs in** (fields Coplay fills):
   - `entries[]`: prefab + `role` (Landmark / MidField / Clutter /
     Silhouette / Outfield) + **honest** `footprintRadius`/`height` (the
     clearance and occlusion gates measure with these — lie and the gate
     either blocks a good map or ships a sightline blocker), `scaleRange`,
     `allowInFold`.
   - Ground: `groundPrefab`/`groundMaterial` + `groundTilingPerMetre` +
     optional `groundDetail` texture.
   - Densities per role (`landmarkDensity` … `outfieldDensity`) and the
     jitter set (`scaleJitter`, `toneVariation`, `uprightJitterDegrees`)
     — tuned numbers, not new systems.
   - `weatherPool`: the world's presets; the seed picks one per map.
6. **Workflow per world.** (1) Coplay assembles the EnvPack asset +
   weather preset(s) from the starting values below → (2) add the pack to a
   blueprint's `envPackPool` → (3) Generate; gates + shader audit + budget
   pass → (4) localize → commit. No scene hand-edits.

---

## 2. ABYSS — underwater

**Read target:** deep shelf at dusk — dense blue water column, drifting
marine snow, light shafts, silhouettes of a trench wall. Enemies read as
salvage machines crawling a seabed; air units read as swimmers.

### 2.1 Coplay asset shopping list (by EnvPack role)

| Role | Look for | Selection criteria |
|---|---|---|
| Landmark | coral towers, wreck hull sections, hydrothermal chimneys | ≤8k tris, one material, reads at 60 m, height honest (occlusion gate) |
| MidField | kelp clumps, rock outcrops, anchor/chain debris | ≤3k tris; kelp WITHOUT per-frame vertex shaders (WebGL cost) — pick static or lightweight sway |
| Clutter | shells, small corals, silt mounds, scattered plating | ≤800 tris, `allowInFold` true for small pieces |
| Silhouette | trench wall slabs, distant wreck masts | dark, low-detail, big `height` values |
| Ground | sand/silt material | tileable, works with `groundDetail` ripple overlay |

Avoid: transparent foliage jungles (overdraw), animated shader packs,
anything HDRP-only.

### 2.2 Weather_Deep — starting values (systems provides the asset; Coplay tunes)

- Sun: `overrideSun` on, `sunTemperatureKelvin 9000`, `sunFilter` pale
  cyan, `sunIntensityMult 0.55`, `sunShadowStrengthMult 0.35`.
- Fog: `overrideFog` on, `fogColor #0A2233`, `fogDensity 0.018` (the
  legibility bar: enemies readable at gameplay camera range, outfield
  swallowed).
- Ambient: `overrideAmbient` on, deep blue-green.
- **Marine snow via the rain channel:** `precipitationRate 60`,
  `fallSpeed 0.8`, `particleSize 0.05`, `streakLength 1`, pale
  `particleColor` — the existing precipitation machinery does drifting
  motes for free. No new system.
- Audio: `ambientLoop` = low underwater rumble, `ambientVolume 0.3`.
- Post: blue-green grade + gentle vignette profile, `postWeight 0.8`.
- Wind: `windStrength 0.5` (slow drift).

**Systems addition (small, mine):** a `sunCookie` texture slot on
WeatherPreset + applier, so a caustics texture Coplay picks projects
rippling light over the seabed. One field, one apply line, editor-safe.

### 2.3 Mechanics proposals (NOT in scope until signed off — balance covenant)

- **M-A1 · Doctrine reskin only.** Storm reads as "UNDERTOW" on ABYSS maps.
  Cosmetic wording; cheap banner-table extension. *No model impact.*
- **M-A2 · Murk pockets** — authored fog volumes where turret range drops
  (Gridcut's machinery, zonal instead of wave-wide); Floodlight cancels
  murk inside its radius, giving it a starring world. *Needs a balance-model
  term + authoring tool — ADVISE FIRST, then build.*
- **M-A3 · Buoyant wreckage** — salvage pips drift upward before flying to
  the counter. Pure display, free, ships with the world.

---

## 3. ORBITAL — space

**Read target:** a station platform against a black starfield — hard white
sun, no atmosphere, long crisp shadows, hull plating underfoot. Cheapest
world: **the vendored 3D Sci-fi Kit Vol 4 already carries most of it.**

### 3.1 Coplay asset shopping list

| Role | Look for (Sci-fi Kit first) | Selection criteria |
|---|---|---|
| Landmark | comm towers, cranes, reactor domes | as ABYSS; strong silhouettes against black |
| MidField | container stacks, pipe runs, solar panel arrays | modular kit pieces; `allowInFold` false for tall ones |
| Clutter | crates, canisters, cable spools, vents | ≤800 tris |
| Silhouette | distant gantries, antenna farms | dark against the skybox |
| Ground | deck plating material | tileable metal, subtle emissive strips OK (WebGL-safe emissive only) |
| Skybox | starfield | 6-sided or equirect **≤ 2 MB crunched**, near-black, no baked flares; passes the shader audit's skybox check |

### 3.2 Weather_Vacuum — starting values

- Sun: `overrideSun` on, `sunTemperatureKelvin 5400`, white filter,
  `sunIntensityMult 1.35`, `sunShadowStrengthMult 1.0` (vacuum = hard
  shadows; this is the world's signature).
- Fog: `overrideFog` on, `fogDensity 0.0002`, near-black — effectively off.
- Ambient: `overrideAmbient` on, very dark blue (starlight).
- Precipitation: **None**. Wind: 0.
- Audio: `ambientLoop` = station hum + faint structure ticks.
- Post: high-contrast profile, restrained bloom (tracers/portals already
  glow; vacuum should make them read MORE, not wash).

### 3.3 Mechanics proposals (same covenant)

- **M-O1 · Meteor dressing** — occasional outfield-only impact flashes on a
  timer. Pure VFX-lane dressing, never on the playfield, free.
- **M-O2 · Solar flare windows** — a periodic ~10 s "flare" during which
  shielded enemies regenerate slightly, telegraphed by a warm post pulse +
  banner ("FLARE — shields feeding"). Teaches kinetic switching under
  time pressure. *Timed enemy modifier = needs a balance-model term —
  ADVISE FIRST.*
- **M-O3 · Low-gravity detonations** — Core-crash and kill explosions
  linger/expand slightly larger on ORBITAL maps. Display-layer scale knob.

---

## 4. Sequencing

1. **ORBITAL first** (assets in hand): Coplay assembles
   `EnvPack_Orbital` + skybox pick → systems adds Weather_Vacuum + wires a
   blueprint → generate/gate/localize. Free mechanics (M-O1, M-O3) ride in.
2. **ABYSS second** (needs a pack purchase): same flow + the `sunCookie`
   caustics slot; M-A3 rides in.
3. **Mechanics with model impact** (M-A2 murk, M-O2 flare): separate
   advise-first proposals with the balance-term sketch, only after both
   worlds LOOK right.

## 5. Acceptance (per world)

- WebGL Shader Audit: 0 errors. Campaign preflight: READY (fully localized).
- Build size: ≤ +3 MB (BuildSizeAudit before/after).
- 907×510 legibility: enemies, range rings, pips and banners readable over
  the new palette — including ABYSS fog at gameplay range.
- Balance model byte-identical (worlds are skins; certified margins hold).
- Re-running generation reproduces the map from its seed (packs are inputs,
  determinism unchanged).
