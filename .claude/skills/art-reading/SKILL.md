---
name: art-reading
description: Turn biome/environment reference images into an art reading (ArtTarget JSON) for the EnvPack Builder. Use when the user shares landscape/mood reference images for a level theme, asks for an "art reading", a new ArtTarget, or to configure a new biome/environment theme.
---

# Art reading: reference images → ArtTarget JSON

You are performing the VISION step of the EnvPack Builder pipeline — the one
step that needs eyes. Your output is a reading JSON the user imports in Unity
(`Tools → COREHOLD → Level → Env Pack Builder → Import Reading (JSON)…`),
after which everything is deterministic tooling.

## Authority order

1. **Schema**: the `Reading` / `ReadingBand` DTOs in
   `Assets/Editor/Coplay/PackBuilder/ReadingImporter.cs` are the contract.
   Read them before writing JSON — the doc may lag the code.
2. **Method**: `docs/art_reading_prompt.md` (guidance + worked desert example).
3. **Precedent**: `docs/art_direction_wadi_rum.md` shows what a full reading's
   reasoning looks like.

## Facts that must shape every reading

- Fixed camera, ~130–150 m back, ~38° pitch, over a 130×75 m playfield. A
  one-metre object at that distance is texture; **silhouette is the only thing
  that reads**.
- Bands are a SCALE LADDER read out of the references: what is enormous, what
  is human-scale, what is ground scatter.
- Any tier over ~25 m authored height takes role **Silhouette** — the
  generator places that role beyond the playfield, where a huge footprint
  costs nothing. In-field roles would reject it on every attempt.
- `wantDistinct` is VARIETY, not quantity — a horizon repeating two meshes
  sixteen times is the failure this pipeline exists to prevent. 10+ for any
  highly visible tier.
- If the references show bare ground, densities go **down** (clutter
  especially), never up. Empty ground is a decision.
- `groundTint` / `rockTint` are matching targets for scoring prefab colors,
  not render tints. `fogColor` is what distance fades to; keep `fogDensity`
  low enough that the play area stays clear (≈0.002).

## Procedure

1. Read the reference images the user attached. If none are attached, ask for
   3–6 WIDE shots (wide beats beautiful) before proceeding.
2. Determine `themeName`: it must equal an existing EnvPack's `themeName`
   (check `Assets/_COREHOLD/Data/EnvPacks/`). If no pack exists yet, say so —
   the pipeline's validate gate will refuse until one is created — and use the
   intended name.
3. Write the reading as `docs/readings/<themeName>.json`, matching the DTO
   exactly. Band `role` values are strings and must be one of `Landmark`,
   `MidField`, `Clutter`, `Silhouette` — the importer fails loudly on anything
   else, by design.
4. Sanity-check your own output: every band's height window non-empty, scale
   ranges positive, colors in 0–1 floats with `"a": 1`.
5. Alongside the JSON, record the reasoning briefly (either in the chat reply
   or, for a major theme, a `docs/art_direction_<theme>.md` in the style of
   the Wadi Rum one): what each band corresponds to in the images, and any
   counter-intuitive calls (e.g. densities lowered).
6. Commit per the repo's rules and tell the user the handoff:
   pull → `Import Reading (JSON)…` → select the created ArtTarget →
   `Run Full Pipeline (gated)`.

## What NOT to do

- Do not edit the Builder's code, the generator, or any EnvPack asset directly
  — the reading is data; the pipeline applies it.
- Do not invent a weather pool or reference-image assignments in JSON — those
  are Unity object references the importer defaults or the user assigns.
- Do not soften a shortfall: if the references demand a tier the project's
  prefabs cannot fill, say so plainly — the matcher's gap report will turn it
  into a shopping list, and pretending otherwise hides the purchase decision.
