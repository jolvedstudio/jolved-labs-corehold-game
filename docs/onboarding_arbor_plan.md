# Onboarding & ARBOR — build plan (systems + Coplay visual brief)

**Goal:** a new player understands COREHOLD in ~15 seconds and never feels
lectured. One helpful voice (ARBOR) does three jobs — teach, advise, narrate —
so there is no extra UI to learn. Story arrives one sentence at a time, always
optional, never blocking.

**Division of labor.** Systems (components, string tables, flags, scene
wiring, triggers) are built in the Claude Code session. **Coplay owns the
LOOK**: layout, the ARBOR emblem, motion, iconography, skin values. Coplay
must not add gameplay mechanics or new systems from this lane.

---

## 1. THE CRITICAL CONSTRAINT — read before touching a scene

`BuildCampaignScenes.BuildWelcome/BuildClosing` create their scenes with
`NewSceneSetup.EmptyScene` **every time they run**. Anything hand-styled
inside `Campaign_Welcome.unity` or `Campaign_Closing.unity` is **destroyed**
the next time anyone presses *Build menu scenes* in the Campaign Builder.

So: **Coplay styles PREFABS, never those scenes.** Systems adds a prefab seam
— the builder instantiates a styled prefab when one exists at the path below,
and falls back to today's programmatic layout when it does not. Prefabs are
durable assets; regeneration then preserves the visual work.

| Prefab (Coplay authors) | Used by |
|---|---|
| `Assets/_COREHOLD/Prefabs/UI/UI_Welcome.prefab` | Welcome scene |
| `Assets/_COREHOLD/Prefabs/UI/UI_HowToPlay.prefab` | Welcome + Pause |
| `Assets/_COREHOLD/Prefabs/UI/UI_ArborStrip.prefab` | every gameplay scene |
| `Assets/_COREHOLD/Prefabs/UI/UI_Briefing.prefab` | Briefing state |
| `Assets/_COREHOLD/Prefabs/UI/UI_Closing.prefab` | Closing scene |

**Binder contract.** Each prefab root carries a binder component (systems
provides them: `WelcomeLayout`, `HowToPlayLayout`, `ArborStripLayout`,
`BriefingLayout`, `ClosingLayout`) with serialized fields Coplay assigns in
the prefab inspector. Logic binds through those fields only — so Coplay may
restructure the hierarchy freely, as long as the fields stay assigned. An
unassigned field logs a warning naming it; it never crashes.

---

## 2. What the player sees

**First launch** — one card, three sentences, tap to dismiss, never again:

> *"The machines we sent to build this world turned on it. They're coming to
> destroy the terraformers that make the air. Hold them — I'll help."* — ARBOR

**Welcome** — existing title / NORMAL / VETERAN / NIGHTMARE / CONTINUE, plus a
new **HOW TO PLAY** button beside Settings.

**How to Play** — 4 cards, ~15 seconds, no story at all:

1. **BUILD** — Tap a pad, pick a turret. It costs salvage.
2. **DEFEND** — Enemies walk the path to your Core. Stop them.
3. **EARN** — Every kill pays salvage. Spend it between waves.
4. **WIN** — Survive every wave. Start a wave early for a bonus.

**Level 1 teaches by doing** — 5 ARBOR lines, each fired by the moment it
matters, once per save, never repeated:

| Trigger | Line |
|---|---|
| First build phase | "Tap a pad, Commander. I'll suggest what fits." |
| After first turret | "It fires on its own. More salvage, more guns." |
| First Start Wave | "They come through the gate. Stop them before the Core." |
| First kill | "Their scrap is your salvage. Spend it." |
| First leak | "That one reached us. At zero integrity, we lose the node." |

**Every later level** — a 2-sentence briefing (tap to skip), then the corner
strip for advice and the occasional story line.

**Pause** — How to Play again, plus the last few ARBOR lines for anyone who
blinked. **Settings** — chatter: *Full / Tactical / Silent*.

---

## 3. Coplay deliverables

### 3.1 The ARBOR emblem — the single most important visual

A **geometric mark, not a face**: no eyes, no portrait, nothing humanoid. A
steward machine; the restraint IS the character. Think a core dot with a
branch/arc motif that reads at 32 px. Requirements:

- Friendly **amber-green** per `VFX_ColorLanguage_Rule.md` (ARBOR is always
  on the player's side; never red, never enemy palette).
- Legible at 32 px (strip) and 96 px (briefing header).
- A subtle **breathing pulse** while a line displays, still when idle —
  reuse the portal pulse idiom (scale ±8%, ~0.9 Hz) for house consistency.
- Vector-ish: a shape built from UI primitives or one small sprite. **Not** a
  large PNG (see budget below).

### 3.2 The ARBOR line strip (`UI_ArborStrip.prefab`)

The only new in-game UI. Non-negotiable behavior (systems enforces; Coplay
styles): lower corner, one line at a time, replaces rather than stacks, fades
after ~4 s, **never takes input, never pauses, never blocks a tap**.

Coplay: emblem placement, type ramp, the scrim/plate behind the text (must
stay readable over sand, night and rain), enter/exit motion (suggest: 120 ms
fade + 8 px rise), and the typing reveal (~40 chars/s, instantly completed on
tap). Must not overlap the HUD's salvage/integrity readouts or the build menu
at 907×510 — that resolution is the legibility bar.

### 3.3 How to Play cards (`UI_HowToPlay.prefab`)

Four cards, paged (swipe/arrows/dots), each = one icon + one bold word + one
line. Big touch targets; readable at 907×510; a persistent **CLOSE** and a
**SKIP** on card 1. Icons should be simple silhouettes reusing existing
turret/enemy iconography where possible.

### 3.4 Welcome, Briefing, Closing (`UI_Welcome`, `UI_Briefing`, `UI_Closing`)

Welcome: give the title real presence, keep the three difficulty buttons
unmistakable, make HOW TO PLAY obvious to a first-timer without competing
with PLAY. Briefing: ARBOR emblem + stage title + briefing body + a clearly
visible skip; this is the story's main surface, so it can be the most
"designed" screen in the game. Closing: quiet, earned, no confetti.

### 3.5 Skin pass

`UISkin` (accent, warm, danger, textMuted, scrim, boss, background,
panelColor, textDim, font, textScale, uiScale, buttonPadding,
cornerRoundness) is applied at **build time**, so after changing it, re-run
*Build menu scenes* + regenerate to see it. Tune it once, globally, rather
than hand-colouring individual objects.

---

## 4. Rules that bound the visual work

1. **Color language** (`VFX_ColorLanguage_Rule.md`) is binding. ARBOR =
   friendly amber-green. Cyan = UI accent. Red/danger stays reserved for
   actual danger — never decoration.
2. **Budget.** The build is ~36 MB and UI textures are already among the
   biggest single assets (one panel PNG is 1.4 MB). Prefer 9-slice sprites,
   solid fills, and TMP text over new full-resolution art. Anything new goes
   through `EnforceCrunchOnOverrides` / `WebGLBudgetPass`, then
   `BuildSizeAudit`.
3. **No vendor-pack references** from committed prefabs — run
   *Tools → COREHOLD → VFX → Localize VFX Config* / the Campaign Builder's
   *Localize vendor assets*, and keep `WebGL Shader Audit` at zero errors.
   Preflight now BLOCKS a build on vendor references.
4. **Never hand-edit the generated menu scenes** (§1).
5. **No VO, ever.** Cost, localization, and download size all say no; the
   silence suits ARBOR. Line reveal + a soft synthesized tick is the voice.
6. **No new gameplay mechanics** from this lane.
7. **Copy meaning is fixed** by `docs/narrative_arbor_terraform.md` §4 voice
   rules; Coplay may tighten wording, never introduce jargon. Player-facing
   text uses PLAIN words — machines, enemies, gates, the Core. The bible's
   vocabulary (Verdance, frames, doctrines, bore gates) is a writer's
   reference and stays out of the player's face.

---

## 5. Sequencing

- **S1 (systems, next):** binder components + prefab seams in the scene
  builders, `ArborVoice` + line strip logic, `ArborLines` string-table asset,
  first-run flags, How to Play controller, chatter setting. Ships with plain
  programmatic fallbacks so everything is playable, if ugly.
- **S2 (Coplay, parallel from the moment S1 lands):** author the five
  prefabs + emblem + motion + skin pass against the binder contract.
- **S3 (systems):** level-1 teaching triggers, first-sighting/defeat-cause
  lines, doctrine names on the existing mutator banner.
- **S4 (systems):** advisor lines on the build-plan ghosts, when
  `--emit-build-plan` lands.

## 6. Acceptance criteria

- A first-time player reaches a built turret without reading anything but
  ARBOR's first line.
- How to Play is understood in ≤ 15 s and closable at any point.
- No ARBOR line ever blocks input, pauses the game, or survives past 4 s.
- Silent chatter mode leaves gameplay fully playable and legible.
- 907×510 legibility holds for every new element, over sand, night and rain.
- Re-running *Build menu scenes* preserves all of Coplay's visual work.
- `WebGL Shader Audit` = 0 errors; campaign preflight = READY; build size
  does not grow more than ~1 MB for the whole onboarding layer.
