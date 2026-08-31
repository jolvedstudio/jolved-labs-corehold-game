# Wave mutators — authoring guide and scope

A **mutator** is a rule that applies to one wave: air units fly faster, turrets
see half as far, everything funnels down one approach. A mutator is an **asset**
— there were once four of them hardcoded as enum bits, and adding one meant
editing five C# files and the balance model.

This document is both the how-to and the boundary: what you can author, what
still needs code, and why the line sits where it does.

**New here?** [`wave_setup_guide.md`](wave_setup_guide.md) is the one-page tour
of the whole chain — level → wave → mutator. This doc is the deep reference for
the mutator layer itself.

---

## 1. The two halves of a mutator

| Half | Where it lives | Who owns it |
|---|---|---|
| **Words and weather** | `WaveMutatorDefinition` asset | design — fully authorable |
| **Mechanical effect** | the asset's *closed* effect list | design — the numbers are yours |
| **The kind of effect** | `MutatorEffects` + the balance model | code — a gated change |

The first two are why the asset exists. The third is why the asset does not
have a free-form script field, and it is the whole safety argument:

> Every effect field maps 1:1 onto a term the balance model computes. A mutator
> built from these fields is a mutator the model can **price**. A free-form
> effect would let you author something the model cannot see — and the gate
> would then certify a wave that does not exist.

---

## 2. Authoring one

1. **Create** — `Assets → Create → COREHOLD → Wave Mutator`, into
   `Assets/_COREHOLD/Data/Mutators/`.
2. **Fill the identity** — `id` (lowercase, stable: it is the key the wave table
   and every gate report use), `title` and `clause` in plain words. These are
   what the player reads at wave start; no code change needed for the banner.
3. **Set the effects** (below). Leave anything at 1 that this mutator does not
   touch.
4. **Optionally attach a weather layer** — the asset carries it, so the look
   follows the mutator into any scene with no per-scene wiring.
5. **Register it** — `Tools → COREHOLD → Scene Setup → Wave Mutators` adds every
   mutator asset to the open scene's WaveManager library, then **save the scene**.
6. **Use it** — add it to a `WaveDefinition`'s `poolMutators` (see §3b). For a
   wave that always carries it, that means a pool of one with
   `poolNothingWeight` at 0.
7. **Check it** — `Tools → COREHOLD → Validate → Wave Mutators Audit`.

### Testing without authoring a wave

**T** in play mode cycles the level's mutator library, adding one to every wave
started from the press. The mutator has to be in the WaveManager's library to be
offered — if T does not list yours, step 5 has not been run or the scene was not
saved.

---

## 3. The effect list (closed)

| Field | Meaning | Model term | Shipped example |
|---|---|---|---|
| `airSpeedMultiplier` | speed of air units | air traverse **and** exposure | Storm 1.3 |
| `groundSpeedMultiplier` | speed of ground units | ground traverse and exposure | — |
| `healthMultiplier` | enemy max HP | effective HP per group | Overcharge 1.3 |
| `bountyMultiplier` | salvage per kill | wave income | Overcharge 1.5 |
| `turretRangeMultiplier` | every turret's reach | pad coverage intervals | Blackout 0.5 |
| `singleApproach` | ground groups funnel to one route | spawner collapse | Convoy |
| `spawnGapMultiplier` | gap between spawns in a group | wave duration → firing budget | — |

Three things worth knowing before you turn a dial:

- **Range is area.** `turretRangeMultiplier` 0.5 leaves a quarter of the ground
  covered. Blackout's 0.5 is the harshest value shipped, and the audit warns
  below 0.4.
- **Speed cuts both ways.** Faster units cross sooner *and* spend less time
  under every tower — it is sharper than the number looks. Slower ground units
  are not simply easier: they linger, and lingering units shoot back.
- **`spawnGapMultiplier` moves the gap, not the offset.** Offsets are the wave's
  composition (the air group arrives eight seconds in); gaps are its tempo.
  Scaling offsets would rearrange a wave rather than pace it.

### What is deliberately absent

Armour piercing, per-enemy-type targeting, shields, spawn-count changes, new
status effects, anything that changes *which* enemies spawn. Each of these needs
a new model term, so each is a code change plus a gate run — see §6.

---

## 3b. Draw pools — the same wave, a different fight

A wave carries **one** mutator list:

- `poolMutators` — the wave draws **one** of these each run.
- `poolNothingWeight` — how many "nothing drawn" slots sit in the hat. 1
  alongside a 3-member pool makes a plain wave a 1-in-4 outcome; 0 means the
  wave always carries one.

There is no separate always-on list, because it would be a second way to say
something the pool already says: **a pool of one with a nothing-weight of 0 is
an always-on mutator**, at runtime and in the model alike (`wave_variants` only
emits the plain variant when the weight is above zero, so such a wave prices as
the single fight it actually is).

The one shape this cannot express is a guaranteed rule stacked under a drawn one
on the same wave. That is deliberate — author it as one mutator asset that does
both, since the model prices the composed vector either way.

The draw is derived from `(run seed, wave number)` and never stored. A unit
admitted from the pending queue thirty seconds late reads the same answer as one
that spawned instantly, and the HUD banner agrees with both. The run seed is
fresh per run, so a replay **and a retry** draw a different shape of fight.

The wave inspector lists every outcome with its odds and its composed effect,
and marks the one the gate will certify against.

### The guarantee that makes this safe to ship

> The model evaluates the wave **once per outcome** and gates on the **worst**.

So a run can never be harder than what certification signed off. Measured on a
contested wave with a pool of {overcharge, blackout, nothing}:

| Wave authored as | margin | gate flags |
|---|---|---|
| plain | 1.50 | — |
| pool `[overcharge]`, none 0 | 1.21 | — |
| pool `[blackout]`, none 0 | 0.97 | **LOW** |
| **pool of both, none 1** | **0.97** | **DRAW[blackout/3 ±0.53], LOW** |

The pooled wave certifies at its hardest member's margin and flags exactly as
the single-member wave carrying that member does. Randomising cannot be used to
slip past the gate.

### Reading the band

`DRAW[blackout/3 ±0.53]` means: three possible outcomes, the worst is
`blackout`, and the best draw is 0.53 margin easier. **That spread is the
learnability number.** A narrow band is a wave that varies in shape while
staying the same problem; a wide band is a wave that is a different problem
each run, and the level ends up tuned for a worst case it rarely draws.

Keep pools narrow — two or three members, close in severity. Pool width *is*
the variance.

### What worst-case certification does and does not promise

It bounds the **ceiling**, not the load. A retry can be easier or harder within
the pool; it simply can never exceed what was certified. Note also that this is
per-wave worst case, not worst-case-*run*: each wave is certified against the
hardest draw it can produce, carrying that same draw's economy. Searching every
sequence of draws would be exponential, and the per-wave bound is the one that
matters.

### Testing

**⇧R** re-rolls the run's mutator sequence in play mode (the counterpart of
**⇧W** for weather). Waves already started keep what they drew; the new
sequence lands at the next wave start.

---

## 4. How mutators compose on one wave

A wave draws one mutator, but the fold still has to compose several: the debug
override stacks on top of a drawn one, and a single mutator asset may move
several terms at once. They fold into one `MutatorEffects` vector:
**multipliers multiply, switches OR**. Order does not matter, identity is 1, and
two mutators that both slow the ground compound rather than one silently
winning.

That rule is not a convenience. It is the only rule under which "any two
mutators can share a wave" is true without a table of special cases, and it is
exactly what the balance model does with the same numbers — which is what keeps
the two implementations from drifting.

One list drives everything. `WaveManager.MutatorAssetsForWave` answers "which
mutators are on this wave?", and the HUD banner, the weather stack and
`EffectsForWave` all read it. A mutator the player is told about is by
construction a mutator that is applied and lit; they cannot disagree, because
there is nothing to disagree about.

> **Historical note.** There used to be three authoring routes: four enum flags
> on the wave, whose numbers lived on the WaveManager, whose weather lived on the
> WeatherApplier and whose banner words lived in a switch in the HUD; a fixed
> asset list; and the pool. Every consumer carried de-duplication logic to decide
> whether an asset was standing in for a flag. That is gone: one list, no
> cross-checks.

---

## 5. How a mutator reaches the balance model

The exporter writes each mutator as an **object carrying its own numbers**:

```jsonc
"mutator_pool": [{"id":"hailfall","ground_speed":0.75,   // one of these is drawn
                  "hp":1.15,"bounty":1.2,"gap":0.6},
                 {"id":"blackout","range":0.5}],
"mutator_pool_none": 1                                   // …or nothing, 1 slot
```

The numbers travel with the wave because the model cannot hold a constant for a
mutator that did not exist when it was written. (`balance_model.py` still
resolves a **bare name** through `BUILTIN_MUTATORS` for hand-written tables; the
exporter no longer emits that shape.)

Unknown terms are **rejected**, not ignored:

```
mutator 'bogus': unknown term(s) armour_pierce. Known terms: air_speed,
bounty, convoy, gap, ground_speed, hp, range
```

That rejection is the mechanism this whole design rests on. A mutator the model
cannot price cannot reach a gate run silently — it fails loudly instead.

---

## 6. Adding a new KIND of effect (gated)

This is still a code change, in this order:

1. Add the field to `WaveMutatorDefinition` and the term to `MutatorEffects`
   (`Fold` must handle it — multiply or OR).
2. Apply it in `WaveManager` at the spawn site.
3. Add it to `MUTATOR_TERMS` (or `MUTATOR_SWITCHES`) in `balance_model.py` **and
   apply it in `compute_wave`**. A term in the dict that nothing applies is
   worse than no term: it parses, so it looks priced, and it is not.
4. Emit it from `WaveTableExporter.MutatorObject`.
5. Run the gate and diff the baseline. A term that changes no authored wave must
   reproduce the baseline byte-for-byte.

**This is ADVISE-FIRST work.** Step 3 is the certified balance path; it is the
same rule that governs the tower-loss term and every other R22 extension.

---

## 7. Design notes — what makes a good mutator

- **One sentence.** If the clause needs a comma-spliced second clause, it is two
  mutators.
- **Pay for what you take.** A mutator that is only harder reads as a punishment.
  Overcharge asks for 1.3× health and pays 1.5× salvage; the audit nudges you
  when a mutator takes without paying.
- **Change a decision, not a number.** The good ones make the player build
  differently: Convoy rewards stacking, Blackout makes the Floodlight worth its
  pad, Storm punishes a defence with no air cover. A flat +15% health changes
  nothing about how anyone plays.
- **Look like what you do.** Attach the weather layer. A wave that plays
  differently and looks identical reads as a difficulty spike, not as an event.

---

## 8. Files

| File | Role |
|---|---|
| `Scripts/Data/WaveMutatorDefinition.cs` | the asset + `MutatorEffects` |
| `Scripts/Data/WaveDefinition.cs` | `poolMutators[]`, `poolNothingWeight`, `DrawablePool()` |
| `Scripts/Core/WaveManager.cs` | `MutatorAssetsForWave`, `EffectsForWave`, `DrawnMutatorForWave` |
| `Scripts/UI/HUDController.cs` | the wave-start banner |
| `Scripts/Systems/WeatherApplier.cs` | stacks each asset's weather layer |
| `Editor/Coplay/SetupWaveMutators.cs` | authoring tool + audit |
| `Editor/Coplay/WaveDefinitionInspector.cs` | the per-wave outcome table |
| `Editor/Coplay/WaveMutatorInspector.cs` | banner + effect preview, usage |
| `Editor/Coplay/Campaign/WaveRecipe.cs` | a level's pool, stamped onto generated waves |
| `Editor/Coplay/Generation/WaveTableExporter.cs` | writes mutators into the table |
| `docs/balance_model.py` | `BUILTIN_MUTATORS`, `MUTATOR_TERMS`, `r22_effects`, `wave_variants` |
