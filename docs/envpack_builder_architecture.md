# EnvPack Builder — architecture guide for Coplay

The requested tool: images in → an EnvPack (up to 50 entries + look parameters)
out → ten reviewable test scenes → then the untouched COREHOLD generator
consumes the pack. This document is how to build it without getting stuck where
you are stuck.

> **STATUS: L1–L5 are BUILT** under `Assets/Editor/Coplay/PackBuilder/`
> (`ArtTarget`, `ArtTargetFactory`, `PrefabIndexer`, `PackMatcher`,
> `PackWriter`, `PaletteExtractor`, `LookdevStager`), menu-driven as numbered
> steps under `Tools → COREHOLD → Level → Env Pack Builder`. L6 (a window
> veneer) remains optional. Coplay's lane is now reviewing the picks with eyes
> on the meshes, localizing vendor picks, and feeding better candidates in —
> not building the tool. The sections below stand as the design record; one
> implementation note: dominant color is measured with a synchronous
> `PreviewRenderUtility` render rather than `AssetPreview` (same atlas-safety,
> none of the async pumping a menu-item context cannot do reliably).

## The one architectural move

You are struggling because "here's a picture, figure out which prefabs match"
is being treated as one problem. It is two, and they have opposite natures:

1. **Reading the image** — *what does this world consist of?* Massifs, fluted
   sandstone, rose-orange palette, bare floor, blue distance. Semantic. Needs
   vision.
2. **Matching the inventory** — *which of my prefabs fit that description, and
   what numbers go in the pack?* Measurement and scoring. Deterministic. Needs
   no vision at all.

The trap is putting vision inside the Unity tool — a cloud API or a bundled
model in an editor script. Don't. Instead:

> **Vision produces data. The editor consumes data.**
> The seam between them is a small ScriptableObject: the **ArtTarget**.

Claude reads the reference images and writes the ArtTarget's values (that
already happened for this theme — `docs/art_direction_wadi_rum.md` *is* the
vision step's output, down to exact colors, densities and a scale ladder). The
editor tool takes the ArtTarget and does everything else deterministically.
When a new biome arrives: drop references on Claude, get target values back,
build. No API keys, no model dependency, reproducible forever.

One honest exception: **palette extraction** from an image is pure arithmetic
(dominant colors), so that one piece of image-reading *can* live in the editor
— see L4. That is what makes "takes visual images as input" true without
smuggling a vision model into the project.

Your uncommitted `SandyDesertPackSetup` is this idea with the data hardcoded as
code. Its values become `ArtTarget_SandyDesert.asset`; the class retires. Commit
what you have first so nothing is lost in the reshape.

## What already exists — do not rebuild these

| Need | Exists as |
|---|---|
| Vision step for this theme | `docs/art_direction_wadi_rum.md` — palette, densities, scale bands, counts |
| Prefab size measurement | `EnvPackTools.TryMeasure` (internal, same assembly) — footprint + height from renderer bounds |
| Folder → role conventions | `EnvPackTools.CategoryFolders`, `_Shared` |
| Name → substrate affinity | `SubstrateField.Resolve` — already inferring rock/scrub/debris from names |
| Camera capture | `EditorShot.Capture(cam, w, h)` — just extracted from ContactSheet for you |
| Review of REAL generated levels | `ContactSheet` game view — 9 seeds through the full pipeline, shot through the gameplay camera |
| Vendor-safety | `VendorLocalizer` pattern — vendor folders are git-ignored; committed content must be localized copies |

That last row is a hard rule: **an EnvPack that references prefabs under
`Assets/Vendor/` carries dangling GUIDs on every other machine.** The builder
must either write only already-localized prefabs into the pack, or its report
must list exactly which picks still need localizing before commit.

## The pipeline

```
reference images ──(Claude / palette extractor)──▶ ArtTarget.asset
                                                        │
     Assets/Vendor + committed prefabs ──▶ PrefabIndex  │
                                                 │      │
                                                 ▼      ▼
                                            PackMatcher (pure scoring)
                                                 │
                                    ┌────────────┴────────────┐
                                    ▼                         ▼
                             PackWriter → EnvPack       GAP REPORT =
                             (+ weather pool)           shopping list
                                    │
                                    ▼
                          LookdevStager → 10 scenes + sheet   (fast look review)
                                    │
                                    ▼
                     COREHOLD Generator (UNTOUCHED) → ContactSheet (real levels)
```

Note the two-speed review. The ten lookdev scenes are **staged compositions of
the pack** — no routes, no gates, seconds each — for judging look and scale.
Whether the pack makes an *engaging level* is judged by the existing
ContactSheet game view, which runs the real generator. Do not rebuild that half;
it shipped last week.

## Modules and build order

Suggested layout: `Assets/Editor/Coplay/PackBuilder/`. Every step is a static
method returning its report as data; any window is a veneer over them (house
rule — a check nobody can script protects nobody).

### L1 — `ArtTarget` (build first; unblocks everything)

```csharp
[CreateAssetMenu(menuName = "COREHOLD/Art Target", fileName = "ArtTarget_")]
public class ArtTarget : ScriptableObject
{
    public string themeName;                 // which EnvPack this builds/updates
    public Texture2D[] referenceImages;      // the record, and palette input

    [Header("Palette")]
    public Color sunColor;   public Vector2 sunAngles;   public float sunIntensity = 2f;
    public Color fogColor;   public float fogDensity = 0.002f;
    public Color groundTint; public Color rockTint;      // matching targets, not render values

    [Header("Scale ladder")]
    public Band[] bands;
    [System.Serializable] public struct Band
    {
        public string name;                  // "Massif"
        public EnvPack.PropRole role;        // where its entries land
        public float minHeight, maxHeight;   // authored metres, BEFORE scaleRange
        public Vector2 scaleRange;           // written onto picked entries
        public int wantDistinct;             // e.g. Massif 12
        public float aspectMin, aspectMax;   // height / width window
        public string[] nameTokens;          // score bonus: "mesa","butte","fin"
    }

    [Header("Written to the EnvPack")]
    public float landmarkDensity, midFieldDensity, clutterDensity,
                 silhouetteDensity, outfieldDensity;
    public float clusterChance, scaleJitter, toneVariation,
                 slopeTiltMaxDegrees, groundZoneStrength;
    public WeatherPreset[] weatherPool;      // the clear/dust split lives HERE
    public int maxEntries = 50;
}
```

Then author `ArtTarget_SandyDesert.asset` by transcribing the parameter table in
`docs/art_direction_wadi_rum.md`. One hour, and the "vision" requirement is
satisfied for this theme.

### L2 — `PrefabIndex` (the measured inventory)

Scan a configurable folder list (committed pack folders + `Assets/Vendor/...`).
Per prefab record: path, `TryMeasure` footprint/height, **aspect** =
height / (2 × footprintRadius), source pack (top-level folder), and **dominant
color**.

Dominant color: use `AssetPreview.GetAssetPreview(prefab)` and average the
non-background pixels. Two reasons over sampling the albedo texture directly:
no Read/Write import flag needed, and it is atlas-safe — averaging a texture
atlas gives garbage when the mesh uses one small UV region, while the preview
averages what the mesh actually shows. Previews load async: poll
`AssetPreview.IsLoadingAssetPreview` and finish the scan over editor updates.
Cache the index in an asset keyed by prefab GUID + file hash so rescans are
incremental.

### L3 — `PackMatcher` (pure) + `PackWriter`

Matcher: `(PrefabIndex, ArtTarget) → picks + gaps`. No asset writes inside —
pure function, testable, deterministic. Per band, score every candidate:

```
score = 2.0 × heightFit      // 1 inside [min,max]; linear falloff; 0 beyond 2×
      + 1.5 × colorFit       // 1 − perceptual distance to the band's tint
                             //   (rock roles → rockTint, scrub → groundTint)
      + 1.0 × aspectFit
      + 0.5 × tokenBonus     // name contains a band token
      − 1.0 × duplicatePenalty
```

The duplicate penalty is against **already-picked** entries of the band — same
source pack AND similar color AND similar aspect ⇒ heavy penalty. This is what
prevents rebuilding the current disease (two silhouettes on repeat ×16) with
fifty entries instead of two. Greedy pick per band until `wantDistinct` or the
candidates run out, respecting `maxEntries` overall.

Writer: upsert into the theme's EnvPack — picked entries get role, the band's
`scaleRange` (this alone fixes the everything-at-(1,1) problem), measured
footprint/height, `affinity` left `Auto` (name inference already works); then
the densities, look values and weather pool from the ArtTarget. Preserve
hand-authored values on existing entries, same as `FillMissing` does.

The report is half the product:

```
=== ENVPACK BUILDER — ArtTarget_SandyDesert → EnvPack_SandyDesert ===
  Massif    4/12 picked — UNFILLED (shopping list below)
     MesaRock_A   h 52 m  aspect 1.1  Δcolor 0.08  ×[1.5,3.0]  (Vendor/Mesa — NEEDS LOCALIZING)
  Outcrop  12/14 picked
  ...
  gaps: Massif wants 8 more ≥40 m — see docs/art_direction_wadi_rum.md §shopping
```

The gap section is the automated version of "what should we buy": run the
builder, read what the project cannot supply.

### L4 — `PaletteExtractor` (optional; the honest image input)

Button on the ArtTarget inspector: "Extract palette from references".
Downsample each image (~64×64), k-means k=6 with a fixed seed in linear RGB;
sky = dominant cluster of the top 25% of rows → `fogColor` candidate; ground =
bottom 30% → `groundTint`; brightest warm cluster → `sunColor`; rockTint =
dominant mid-image cluster that is not sky or ground. Write the fields, let the
human overrule. Deterministic, ~200 lines, zero dependencies. `fogDensity` and
`sunAngles` stay authored — they are not reliably readable from a photo.

### L5 — `LookdevStager` (the ten scenes)

Per variant seed (10 by default): new scene → ground plane with the pack's
material/tiling → sun + fog + skybox + post from the pack (imitate what
`LookStage` does; do not call into the generator) → camera at the gameplay pose
(standard pitch/height/FOV) → **staged** dressing with a simplified placer:

- massifs on an arc 200–350 m out across the back 180°;
- outcrops in an edge band 60–120 m;
- mid-field sparse at 30–80 m; clutter sparse near;
- a 12 m corridor axis kept clear so the framing approximates play.

No gates, no routes — this is a look review, not a level. Vary per seed:
arrangement, weather draw from the pool, sun yaw ±8°. Save to
`Assets/_COREHOLD/Lookdev/Lookdev_<theme>_s<seed>.unity`, capture each through
the scene camera with `EditorShot.Capture`, write a 2×5 sheet PNG + md table
(seed, weather, sun, per-band counts). Add a "Delete lookdev scenes" menu item —
they are disposable by design.

### L6 — window veneer (last)

One `EnvPack Builder` window: ArtTarget slot, folder list, buttons for
Scan / Match / Build Pack / Stage Lookdev, report in a scroll view. All of it
calling the L2–L5 statics.

## Acceptance criteria

1. Same ArtTarget + same project state ⇒ **byte-identical EnvPack**. Seeded
   draws, `OrderBy(..., StringComparer.Ordinal)` everywhere (house rule).
2. **Zero changes under `Assets/Editor/Coplay/Generation/`.** The generator is
   a consumer.
3. The built pack references no unlocalized vendor prefab silently — localized,
   or loudly listed in the report.
4. Every step callable headless; reports returned as data.
5. The gap report names each unfilled band with counts and the spec to buy.
6. Ten lookdev scenes + sheet from one click, deletable from one click.

## Traps, from experience in this codebase

- **The editor lies about WebGL.** Run `Tools → COREHOLD → VFX → WebGL Shader
  Audit` after importing any pack you intend to pick from; read its `intake`
  section before authoring on top of a pack.
- **Never type footprint/height by hand** — `TryMeasure` only. A radius short by
  30% puts a prop in a lane.
- **Massifs go in the Silhouette role for now.** A 40 m footprint entered as
  Landmark is also offered to in-field placement, where the gates will reject it
  every attempt and starve the landmark count (recorded follow-up in the art
  doc; an in-field size guard comes later if needed).
- **`allowInFold` defaults false.** Pockets are where hardpoints live.
- **AssetPreview is async and cache-limited** — batch with
  `AssetPreview.SetPreviewTextureCacheSize` bumped during a scan.

## Division of labor

- **Claude**: reads reference images → ArtTarget values (done for Wadi Rum);
  owns the seams touched here (`EditorShot`, and the in-field size guard if
  massif placement needs it); reviews the matcher's scoring if it misbehaves.
- **Coplay**: builds L1–L6; eyes on meshes — the final say on whether a picked
  prefab actually looks like the reference is a judgement the scorer only
  approximates.
