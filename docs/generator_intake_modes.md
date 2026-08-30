# Generator intake modes — one certifier, several front doors

The question: the generator should work three ways — (0) pure procedural from a
blueprint, as today; (a) from a visual source (reference images); (b) from a
PRE-BUILT scene, adapted — props moved, added or removed — until every gate
passes. One tool or two?

## The verdict

**One certifier, several intakes. Never two tools.**

A map is shippable because it passed *the* gates — clearance, coverage,
occlusion, model margins. Build a second tool and you have two implementations
of those gates; they drift, and one of them starts lying. Everything downstream
of "dressing exists in the scene" — GATE 2b, weather, terrain, look, emit,
GATE 3, save, localize — must be one code path shared by every mode.

But do not flatten the modes into one mode-switched monolith either, because
they differ in something deeper than input format:

> **What is precious.**
> In modes 0/a, dressing is *disposable*: placement is cheap and seeded, so a
> failed gate discards the seed and tries again. Reject-and-reseed is correct.
> In mode b, dressing is *authored*: a human composed that scene, and the whole
> point is preserving their composition. Reseeding is forbidden — it would
> throw away the work. The only legitimate response to a gate failure is
> **minimal, reported repair**, and past a budget, honest refusal.

Same gates, different *repair policy*. That is the entire architecture: the
policy is a parameter of the pipeline, not a fork of it.

## The three modes, in this codebase's terms

| Mode | Input | Dressing comes from | On gate failure | Status |
|---|---|---|---|---|
| 0 — Procedural | LevelBlueprint | EnvPack via PropPlacer | reject-and-reseed | shipped |
| a — Visual source | reference images | images → ArtTarget → EnvPack Builder → EnvPack → PropPlacer | reject-and-reseed | **shipped** (the Builder *is* this mode's front half) |
| b — Scene adapt | an authored scene | already standing in the scene | **nudge → remove, logged, capped; never reseed** | this document |

Mode a needs no generator changes at all — it is the EnvPack Builder feeding
mode 0, which is exactly why the Builder was built upstream. The only new
construction is mode b.

**Mode b is not the retired parity path resurrected.** Parity (R26, retired)
tried to make the generator *reproduce* an authored scene from a blueprint.
Adapt does the opposite: the authored scene stays authoritative, and the
generator certifies it *in place*, changing as little as possible and saying
exactly what it changed.

## The scoping decision that makes or breaks mode b

What must the authored scene contain?

- **b1 — scene carries the gameplay anchors**: a Core, spawn points, pad
  positions (the blockout pieces), plus the authored dressing. Routes are still
  *synthesized* between the anchors, exactly as today — and then the dressing
  adapts around them. Every existing test runs unchanged; the only new logic is
  the repair policy.
- **b2 — scene is dressing only**: the generator must *find* a route corridor
  through authored props. That inverts the whole system — today routes are
  drawn freely and props avoid them; b2 makes routes avoid props, which is a
  constrained path-optimization problem none of the current machinery solves.

**Recommendation: b1, firmly.** The cost to the author is placing a Core, two
or three spawners and some pads — minutes, with prefabs that already exist —
and it converts mode b from a research project into a modest extension. A
"Stamp Anchors" helper can drop a default anchor set into any scene to start
from. b2 can be revisited if b1's workflow proves the demand; nothing in b1's
design blocks it.

## Mode b, concretely

New intake stages replace the front of the pipeline; the back half runs as-is.

1. **Intake & inventory.** Verify anchors (Core, ≥1 spawner, pads with
   coverage gizmos) — missing anchors is an immediate, named refusal. Stamp
   `PlacedProp` onto every unmarked prop via the existing measure tool
   (`TryMeasure`), flagged `authored`. The marker is already the contract:
   "a generated scene must stay verifiable after the generator is gone."
2. **Routes from anchors.** The existing `RouteSynthesizer`, seeded by the
   blueprint, between the *authored* spawn/core positions. GATE 1 (clearance)
   runs — and violations by authored props go to the adapt loop instead of
   discarding the seed.
3. **The adapt loop** — the one genuinely new piece. For each violation
   attributable to dressing (lane clearance, pad keep-out, camera sight line,
   route-visibility budget, GATE 2b occlusion):
   - **nudge** first: try a deterministic ring of offsets that clears the
     constraint while moving the prop as little as possible;
   - **remove** only if no nudge within radius R works;
   - **log every intervention**: "moved `Rock_Big_3` 2.4 m south (was inside
     lane clearance)", "removed `Arch_1` (blocked HP_Premium_2's sight line,
     no clear position within 6 m)".
   - **budget**: past N interventions (tune ~12), refuse with the full
     conflict list — "this scene fights the gates" is a legitimate verdict,
     and the author decides, not the tool.
4. **Anchor-caused failures refuse honestly.** Route too short, pad class
   shortfall, GATE 3 margin failures — those are facts about the *anchors*,
   not the dressing. Moving props cannot fix them and the tool must say so:
   "coverage needs 4 premium spans, your pad layout gives 3 — move pads, not
   props."
5. **Optional top-up** (off in v1): after conflicts clear, run `PropPlacer` in
   fill-only mode from the theme's EnvPack to thicken thin bands — "ajouter"
   from the request, as a later switch.
6. **Back half unchanged**: weather, terrain (off by default in v1 — an
   authored scene owns its ground), look (also optional — authored scenes own
   their light), emit, GATE 3, save, localize.

### Invariants carried over from the rest of the tooling

- **Deterministic**: same scene + same blueprint ⇒ same adaptations, ordered
  iteration, seeded nudge rings.
- **Report as data**: the intervention list *is* the product — certification's
  cost to the composition, stated, not implied.
- **Authored is sacred by default**: `authored` props are only ever nudged or
  removed under a named constraint with a logged reason; generated filler
  (top-up) is always sacrificed first.

## What this unblocks

- **R32 — map-2 authoring day** (still open): author freely, then certify,
  instead of authoring inside the generator's constraints.
- **C4 set-pieces**: hand-composed moments (the Petra facades) placed by hand,
  certified by machine.
- **Coplay's natural lane**: it works visually, in-scene; mode b makes that
  work land in shippable levels without Coplay ever touching the pipeline.

## Effort

Modest, because the expensive parts already exist: every gate, the route
synthesizer, `PlacedProp`, the measure tool, the occlusion re-run (which is
already a remove-based repair loop — mode b extends its vocabulary with
"nudge" and widens it to the other constraints). New code: intake/inventory,
the nudge search, the policy seam, a menu entry, and reports. Comparable to
the EnvPack Builder's first cut. ADVISE-FIRST applies — this touches the
pipeline — so it starts on an explicit green light.
