# COREHOLD UX/UI revamp — research digest + plan

Companion to `docs/onboarding_arbor_plan.md` (ARBOR/onboarding) — this file
covers the IN-PLAY interface. Research: five reference TD titles studied for
what their interfaces do right; then a gap analysis against COREHOLD's
current UI; then the revamp, phased, with systems/Coplay lanes.

---

## 1. Research digest — five titles, one takeaway each

| Title | The lesson worth stealing |
|---|---|
| **Kingdom Rush** | The radial pad menu: options grow outward FROM the tapped pad, so eyes never leave the field; circular targets suit thumbs; minimal persistent chrome. The gold standard for pad-based TD interaction. |
| **Plants vs. Zombies** | The seed rail: every buildable always visible with cost, dimmed when unaffordable — affordability is a *glance*, never a calculation. Currency spawns in the world and travels to the counter, making the economy physically legible. One new unit per level IS the tutorial. |
| **Bloons TD 6** | Keep panels OFF the thing being inspected: its upgrade panel docks to the screen side *opposite* the selected tower. Its community's chief complaint — menus covering the field — is the anti-pattern to avoid. |
| **Defense Grid** | The wave QUEUE: upcoming waves shown as icon chips in a row, so players plan two waves ahead, not one. Boss waves visibly marked in the queue. |
| **Defender's Quest** | Design for FOCUS: no scrolling viewport (stress kills thinking), and a wide time-control range so difficulty lives in decisions, not in waiting or panic. |

COREHOLD already banks two of these for free: the fixed no-scroll camera
(Defender's Quest's #1 rule) and an untimed build phase.

## 2. Current state — honest audit

Already good: tap-empty-pad bottom-sheet with icon/name/cost/role and
desaturated unaffordable entries; range-ring preview; event-driven HUD with
segmented integrity bar, "WAVE n/N" plus next-wave composition icons WITH
armour pips; animated salvage counter; Start Wave with live chain bonus;
1×/2× speed; pause. Solid bones — the gaps are speed-of-interaction and
feedback weight, not missing features.

Gaps, by reference title: build flow is a modal bottom sheet (eyes leave the
field — Kingdom Rush); no persistent roster visibility (PvZ); tower panel
placement vs the inspected tower unaudited (BTD6); wave preview shows one
wave, not a queue (Defense Grid); kills pay salvage numerically only — the
economy is invisible in the world (PvZ); wave/boss moments carry no drama
(PvZ's banner); the roster arrives all-at-once on level 1 (PvZ's one-per-level).

## 3. The revamp

**R-UI-1 · Radial pad menu (Kingdom Rush).** Tapping an empty pad grows a
ring of the buildable turrets around the pad itself: icon + cost per node,
unaffordable nodes dimmed and inert, selected node previews the range ring,
second tap builds. The existing bottom sheet remains as the fallback layout
(and for very crowded pad clusters). GDD §9.1 already lists the radial as a
sanctioned nicety. *Systems: interaction + layout math. Coplay: node visuals,
grow/shrink motion (~120 ms), iconography.*

**R-UI-2 · Persistent roster rail (PvZ, approved).** A slim always-visible
rail (top edge, clear of the HUD corners): the level's turrets as chips with
cost, live-dimmed by affordability. Tap a chip → pads that can host it pulse
faintly; then tap a pad to build. Drag chip→pad works as the power-user path.
The rail is also where **per-level turret introductions** land: a new chip
slides in with one ARBOR line (PvZ's one-new-unit-per-level, approved — needs
the roster-gating progression flag in CampaignAuthoring; systems change,
user-approved). *Systems: rail + drag + gating. Coplay: chip design.*

**R-UI-3 · Side-docked tower panel (BTD6).** When inspecting a built tower,
the upgrade/sell panel docks to the screen side OPPOSITE the tower, never
covering it or its range ring. Pure layout rule, cheap.

**R-UI-4 · Wave queue (Defense Grid).** Extend the existing next-wave strip
to the next TWO waves as compact chips (icons + counts + armour pips at
reduced size), current wave emphasized. Boss-class waves get a distinct chip
treatment. Data already exists per wave — display change only.

**R-UI-5 · Salvage made physical (PvZ, approved).** On a kill, a small
salvage pip pops at the crash site and drifts to the counter (pooled sprite,
one per kill, cap concurrent at ~8, merge beyond). No collection tap — this
is COREHOLD, not a clicker; auto-collect keeps hands on strategy. The
counter's existing tick animation becomes the pip's landing.

**R-UI-6 · Wave banner with weight (PvZ, approved).** Wave start, boss
waves, and doctrine (mutator) call-outs get one diegetic banner moment —
short, loud, non-blocking (≤1.5 s, never input-blocking). Reuses the ARBOR
strip styling family; doctrine names come from the narrative bible.

**R-UI-7 · Field guide / Almanac (PvZ, approved).** An optional book —
enemies and turrets seen so far, one card each: portrait, role line, armour
pips, one ARBOR flavour sentence. Opens from pause + between waves. This is
where ALL story depth beyond the one-liners lives; unlocks are
seen-this-campaign flags (shares the ARBOR flag store).

**R-UI-8 · Time control breadth (Defender's Quest — PROPOSAL, needs
sign-off).** Keep 1×/2×; consider a third stop (3× on cleared-content
replays) and/or press-and-hold 0.5× "focus". Gameplay-feel change —
flagged, not assumed.

## 4. Explicitly NOT doing

- No scrolling/zooming battlefield (Defender's Quest rule; we have it free).
- No collection-tapping for salvage (PvZ's is loved on touch; ours would
  fight tower interaction mid-wave).
- No Arknights-style deploy economy or ability layers — different genre arm.
- No modal anything during waves. Banners and pips never take input.

## 5. Phasing

- **P1 (systems): BUILT.** R-UI-3 (`TowerPanel.DockOppositeTo`, wide
  bottom-sheet roots auto-detected and left alone), R-UI-4 (second queue row
  built programmatically in `HUDController.EnsureQueueRow`, fed by
  `WaveManager.PeekWave`; boss counts tinted danger), R-UI-5
  (`GameManager.OnKillSalvage` → pooled amber shards arcing to the counter,
  capped, unscaled time), R-UI-6 (shared `ShowBanner`: doctrine name +
  plain-words clause, HEAVY CONTACT for boss waves, short cyan stamp
  otherwise, wave 1 silent). All widgets self-build — no scene edits needed;
  Coplay may restyle via the serialized [TUNE] fields and UITheme only.
- **P2 (systems): BUILT — Coplay styling open.** R-UI-1 radial menu
  (`RadialBuildMenu`, created at runtime by `BuildMenu` — no scene edits)
  behind Settings → "BUILD MENU: RADIAL/SHEET" (`SaveData.RadialBuildMenu`,
  default SHEET). Tap a node to select + preview range, tap again to build;
  unaffordable/WIP nodes dimmed and inert; ring auto-grows for the 10-slot
  roster, clamps fully on-screen, 120 ms unscaled grow-out. Sheet remains
  the default and the fallback. Coplay lane: node look (plates are
  runtime-generated circles + theme colours today), grow/shrink motion feel,
  iconography — via BuildMenu's radial [TUNE] knobs and UITheme. Settings
  panels in existing scenes show the new toggle after *Build real UI*
  regenerates them.
- **P3 (systems + user sign-off on gating):** R-UI-2 rail + per-level turret
  introductions (progression change), R-UI-7 almanac.
- **P4 (design call):** R-UI-8 speed stops.

Acceptance bar (all phases): 907×510 legibility over sand/night/rain; no new
element blocks input or covers the path; build ≤ +1 MB; skin-driven colours
only (`UISkin`), colour language rules hold; prefab-seam pattern from the
onboarding plan applies to every new surface (Coplay styles prefabs, never
generated scenes).
