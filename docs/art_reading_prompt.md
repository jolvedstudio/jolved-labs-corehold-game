# Art reading prompt — turn reference images into an ArtTarget, from any Claude

The EnvPack Builder's step 1 needs an **art reading**: the semantic half of
"look at these references and configure the generator". That reading is JSON,
and three producers all feed the same import slot:

1. **A Claude Code session on this repo** — just ask; it can write the JSON (or
   ship the reading as factory code, as the desert one is).
2. **Any claude.ai / Claude app chat** — paste your reference images plus the
   prompt below, save the reply as `reading.json`, then in Unity:
   `Tools → COREHOLD → Level → Env Pack Builder → Import Reading (JSON)…`
3. **A future in-editor API call** — the Anthropic Messages API accepts image
   blocks; a caller would send the images in a project folder plus this same
   prompt, and pipe the reply into `ReadingImporter.Import(json)`. Nothing
   downstream changes. Build it when theme volume justifies it; until then,
   path 2 costs nothing and needs no key in the project.

The importer is strict about band roles and theme name, and defaults anything
missing, so a slightly imperfect reply fails loudly or lands safely — never
silently.

---

## The prompt (paste everything below, with your images attached)

You are configuring a procedural environment generator for a fixed-camera
tower-defense game. The camera sits about 130–150 m back at a ~38° pitch over a
130×75 m playfield; a one-metre object at that distance is texture, not
architecture, and silhouette is the only thing that reads. Study the attached
reference images and produce ONE JSON object — no prose, no markdown fences —
matching exactly this schema:

```
{
  "themeName": "<name of the EnvPack this configures, e.g. SandyDesert>",
  "sunColor":   {"r":0-1,"g":0-1,"b":0-1,"a":1},
  "sunAngles":  {"x":<pitch above horizon, deg>, "y":<yaw, deg>},
  "sunIntensity": <float>,
  "fogColor":   {"r":..,"g":..,"b":..,"a":1},
  "fogDensity": <ExponentialSquared density; 0.002 keeps play clear, colors distance>,
  "groundTint": {"r":..,"g":..,"b":..,"a":1},
  "rockTint":   {"r":..,"g":..,"b":..,"a":1},
  "bands": [
    {
      "name": "<tier name>",
      "role": "<Landmark | MidField | Clutter | Silhouette>",
      "minHeight": <authored metres>, "maxHeight": <authored metres>,
      "scaleMin": <float>, "scaleMax": <float>,
      "wantDistinct": <how many DIFFERENT prefabs this tier needs>,
      "aspectMin": <height/width>, "aspectMax": <height/width>,
      "nameTokens": ["<substrings that suggest a matching prefab name>"]
    }
  ],
  "landmarkDensity": 0-4, "midFieldDensity": 0-4, "clutterDensity": 0-4,
  "silhouetteDensity": 0-4, "outfieldDensity": 0-4,
  "clusterChance": 0-1, "scaleJitter": 0-0.6, "toneVariation": 0-1,
  "slopeTiltMaxDegrees": 0-20, "groundZoneStrength": 0-1,
  "maxEntries": 50
}
```

Guidance for your choices:

- **Bands are the scale ladder.** Read the size tiers out of the references —
  what is enormous, what is human-scale, what is ground scatter — and give each
  a band. Enormous horizon forms (mesas, spires, wrecks seen against the sky)
  take role **Silhouette**: the generator places that role beyond the playfield,
  where a huge footprint costs nothing. Never give a >25 m tier Landmark —
  in-field placement would reject it every attempt.
- **wantDistinct is variety, not quantity.** The horizon repeating two meshes
  sixteen times is the failure this exists to prevent; 10+ distinct for any
  highly visible tier.
- **Empty ground is a decision.** If the references show bare floors, densities
  go DOWN (clutter especially), not up.
- **groundTint / rockTint are matching targets** — what the ground and the
  stone read as in the images — used to score prefab colors, not to tint pixels.
- **fogColor is the color distance fades to** (aerial perspective). Keep
  fogDensity low enough that the near field stays clear.
- Colors are linear 0–1 floats. Output only the JSON object.

---

## Worked example — the desert reading

This is the shipped reading (also available as menu step 1), usable as a
format reference or as an import test:

```json
{
  "themeName": "SandyDesert",
  "sunColor": {"r": 1.0, "g": 0.9266, "b": 0.6745, "a": 1},
  "sunAngles": {"x": 24, "y": -55},
  "sunIntensity": 2,
  "fogColor": {"r": 0.65, "g": 0.70, "b": 0.78, "a": 1},
  "fogDensity": 0.002,
  "groundTint": {"r": 0.80, "g": 0.55, "b": 0.38, "a": 1},
  "rockTint": {"r": 0.72, "g": 0.50, "b": 0.40, "a": 1},
  "bands": [
    { "name": "Massif", "role": "Silhouette", "minHeight": 40, "maxHeight": 80,
      "scaleMin": 1.5, "scaleMax": 3.0, "wantDistinct": 12,
      "aspectMin": 0.4, "aspectMax": 2.5,
      "nameTokens": ["mesa", "butte", "massif", "cliff", "mountain", "plateau"] },
    { "name": "Outcrop", "role": "Landmark", "minHeight": 8, "maxHeight": 25,
      "scaleMin": 0.8, "scaleMax": 1.8, "wantDistinct": 14,
      "aspectMin": 0.5, "aspectMax": 4,
      "nameTokens": ["rock", "outcrop", "spire", "fin", "stack", "boulder", "crag"] },
    { "name": "Boulder", "role": "MidField", "minHeight": 2.5, "maxHeight": 8,
      "scaleMin": 0.7, "scaleMax": 1.4, "wantDistinct": 14,
      "aspectMin": 0.3, "aspectMax": 2.5,
      "nameTokens": ["rock", "boulder", "stone"] },
    { "name": "Scatter", "role": "Clutter", "minHeight": 0.05, "maxHeight": 1.8,
      "scaleMin": 0.6, "scaleMax": 1.5, "wantDistinct": 10,
      "aspectMin": 0, "aspectMax": 99,
      "nameTokens": ["rock", "stone", "pebble", "bush", "scrub", "grass", "plant"] }
  ],
  "landmarkDensity": 1.5, "midFieldDensity": 1.2, "clutterDensity": 1.0,
  "silhouetteDensity": 3.0, "outfieldDensity": 2.5,
  "clusterChance": 0.85, "scaleJitter": 0.55, "toneVariation": 0.35,
  "slopeTiltMaxDegrees": 8, "groundZoneStrength": 0.45,
  "maxEntries": 50
}
```

## Notes on the future API path

If in-editor calls become worth building: one chokepoint class, key from an
environment variable (never a project asset — anything under `Assets/` risks a
commit), images base64-encoded into Messages-API image blocks with this
document's prompt as the text block, and the reply piped into
`ReadingImporter.Import`. The GTM apps' rule applies here too: all Anthropic
access through a single budgeted seam.
