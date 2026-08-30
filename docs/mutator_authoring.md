# Wave mutators — authoring guide and scope (R33)

A **mutator** is a rule that applies to one wave: air units fly faster, turrets
see half as far, everything funnels down one approach. Before R33 there were
exactly four of them and each was an enum bit, so adding one meant editing five
C# files and the balance model. Now a mutator is an **asset**.

This document is both the how-to and the boundary: what you can author, what
still needs code, and why the line sits where it does.

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
6. **Use it** — add it to a `WaveDefinition`'s `mutatorAssets`.
7. **Check it** — `Tools → COREHOLD → Validate → Wave Mutators Audit`.

### Testing without authoring a wave

**⇧T** in play mode cycles the scene's mutator library, adding one to every wave
started from the press. (Plain **T** still cycles the four legacy flags.) The
mutator has to be in the WaveManager's library to be offered — if ⇧T does not
list yours, step 5 has not been run or the scene was not saved.

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

## 4. How the two authoring routes compose

A wave can carry legacy **flags** and authored **assets** at once. They fold into
one `MutatorEffects` vector: multipliers multiply, switches OR. Order does not
matter, and two mutators that both slow the ground compound rather than one
silently winning.

The one rule to know: **an asset bound to a legacy flag via `legacyFlag` does not
double-apply.** When the flag is set, the flag's numbers (the WaveManager's
`[TUNE]` fields) win and the asset contributes only its words and weather. That
is deliberate — scenes have had those values tuned on the WaveManager for a long
time, and an asset must not silently overrule a number a designer set.

So: the four originals keep behaving exactly as they always have, and
`Mutator_Storm.asset` exists to give Storm a banner and a weather layer, not to
re-specify it.

---

## 5. How a mutator reaches the balance model

The exporter writes mutators into the wave table in one of two shapes:

```jsonc
"mutators": ["storm"]                                   // a legacy flag: name only
"mutators": [{"id":"hailfall","ground_speed":0.75,      // an asset: its own numbers
              "hp":1.15,"bounty":1.2,"gap":0.6}]
```

A **name** resolves through `BUILTIN_MUTATORS` in `balance_model.py` — the four
originals and their constants, unchanged, which is what keeps every table
exported before R33 producing the numbers it always did. An **object** carries
its own terms, because the model cannot hold a constant for a mutator that did
not exist when it was written.

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
4. Emit it from `WaveTableExporter.MutatorNames`.
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
| `Scripts/Data/WaveDefinition.cs` | `mutatorAssets[]` beside the legacy flags |
| `Scripts/Core/WaveManager.cs` | `EffectsForWave`, `MutatorAssetsForWave`, application |
| `Scripts/UI/HUDController.cs` | the wave-start banner |
| `Scripts/Systems/WeatherApplier.cs` | stacks each asset's weather layer |
| `Editor/Coplay/SetupWaveMutators.cs` | authoring tool + audit |
| `Editor/Coplay/Generation/WaveTableExporter.cs` | writes mutators into the table |
| `docs/balance_model.py` | `BUILTIN_MUTATORS`, `MUTATOR_TERMS`, `r22_effects` |
