# Wave setup — the whole chain on one page

Four layers, each owning exactly one thing:

```
LEVEL          LevelDefinition.waves[]        which waves, in order
  └ WAVE       WaveDefinition                 who attacks, what it pays
      └ POOL   poolMutators[]                 the rules it may carry — it draws ONE
               poolNothingWeight              …or nothing, this often
                 └ WaveMutatorDefinition      what a rule IS: words, weather, numbers
```

A **Wave Recipe** is an alternative to authoring waves by hand: it *generates*
the wave assets from a roster and a curve. It sits beside the level, not inside
it — the level still ends up holding a plain list of `WaveDefinition`s.

---

## The one rule worth memorising

**A wave says WHICH mutators it can carry. The mutator asset says what they DO.**

Nothing about a mutator — its banner text, its weather, its multipliers — lives
on the wave, the level, the WaveManager or the WeatherApplier. It lives on the
mutator asset. One place.

And a wave has **one** mutator list, not two. "Always carries Storm" is not a
separate concept: it is a pool of one with a nothing-weight of zero.

---

## 1. The mutator — what a rule is

`Assets → Create → COREHOLD → Wave Mutator`, into
`Assets/_COREHOLD/Data/Mutators/`.

| Field | What it is |
|---|---|
| `id` | lowercase, stable. The key the wave table and every gate report use. **Renaming invalidates saved tables.** |
| `title` / `clause` | what the player reads at wave start. Plain words: "Air units move faster", not "airSpeed ×1.3". |
| `weatherLayer` | optional. Stacked over the level's look while the wave runs, removed when it clears. |
| the effect fields | the numbers. Leave at 1 anything this mutator does not touch. |

The inspector previews the banner and spells out each effect in plain language
as you type — including the one people get wrong: range is *area*, so ×0.5
range is 25% of the ground covered.

The four that ship (`storm`, `convoy`, `overcharge`, `blackout`) are ordinary
assets with no special status. A fifth is a fifth asset, not a code change.

**Register them:** `Tools → COREHOLD → Scene Setup → Wave Mutators` fills the
open scene's WaveManager library and authors the four starters if missing. Save
the scene. The library is what the debug key cycles and what the audit checks
against — a mutator missing from it still works on a wave, but you cannot test
it with a keypress.

### The cascade: project → level → wave

Three rungs, each narrowing the one above it:

| Rung | Where | What it means |
|---|---|---|
| **Project** | every `WaveMutatorDefinition` asset | every rule that exists |
| **Level** | `WaveManager.mutatorLibrary` | the ones **this level** is willing to use |
| **Wave** | `WaveDefinition.poolMutators` | the ones **this wave** can draw |

The level rung is why the WaveManager has a mutator list at all — nothing in
gameplay reads it. It exists so the debug key has something to cycle, so the
audit can flag a wave using an unregistered mutator, and so a wave pool is
filled from the set that belongs in this world. Filling a wave pool straight
from the project list is how a desert level ends up rolling a blizzard.

Each rung inherits from the one above with a button:

- **WaveManager inspector → "Inherit all project mutators"** — fills the level
  roster from every mutator asset in the project. Trim it afterwards.
- **Wave inspector → "Inherit N from level library"** — fills the wave's pool
  from the open level's roster. Greyed out with the reason when no level scene
  is open or the roster is empty.

→ Deep reference, including what still needs code: [`mutator_authoring.md`](mutator_authoring.md)

## 2. The wave — who attacks

`Assets → Create → COREHOLD → Wave Definition`.

**Who attacks:** `groups[]` — enemy, count, spawn gap, start offset, spawner
(`0` west ground, `1` north ground, `2` air). `clearBonus` is the salvage for
clearing it.

**Mutators:** `poolMutators[]` is what the wave may carry. It draws **one** of
them each run, or nothing. `poolNothingWeight` is how many "nothing" slots are
in the hat:

| pool | nothing weight | outcome |
|---|---|---|
| empty | — | plain wave, every run identical |
| 2 members | 2 | plain half the time, each mutator 1-in-4 |
| 3 members | 1 | plain 1-in-4 |
| 2 members | 0 | **always** mutated, 50/50 which |
| **1 member** | **0** | **always that one — the set-piece wave** |

The draw is fresh every run, derived from `(run seed, wave number)`. A replay
*and a retry* roll again, so a wave you just lost is not the wave you retry.

The last row is how you author "wave 10 is the storm wave". There is no separate
always-on list — one pool covers both, and the inspector spells out which case
you have built.

The wave inspector lists **every outcome the wave can roll**, with its odds and
its composed effect, and marks the worst — the one the balance model gates on.
If a pool is doing something you did not intend, that panel is where you see it.

## 3. The level — which waves

`LevelDefinition.waves[]`, in order. That is the whole of the level layer. The
WaveManager reads it; if a level is not assigned it falls back to its own
serialized list.

## 4. Or: the recipe — generate the waves

`Assets → Create → COREHOLD → Wave Recipe (editor)`. Instead of authoring ten
waves you author the *rules* and the synthesizer spends a threat budget on your
roster.

| Section | What it controls |
|---|---|
| **Roster** | which enemies waves may draw from. Every id needs a `balance_model.py` row. |
| **Shape** | wave count, wave-1 budget, growth per wave, per-stage escalation. |
| **Structure** | first wave that may contain air, boss finale, what counts as boss/light. |
| **Mutators** | the pool stamped onto eligible waves, from which wave on, and the nothing weight. |

The recipe's pool is **stamped, not resolved** — each generated wave gets the
pool and rolls at runtime, so the same generated level plays differently every
run. Two automatic exclusions: the boss finale never gets a pool (the boss is
the event), and a mutator that could only affect air is dropped from a wave with
no air in it.

Same recipe + same seed = the same waves, exactly.

---

## Why pools stay narrow

The balance model evaluates a wave **once per pool member** and gates on the
worst. That is the guarantee: a run can never be harder than what certification
signed off.

The cost is that the level is *tuned* for that worst case. A five-member pool
means the level is balanced against an outcome it draws one time in six, so the
other five feel under-tuned. **Two or three members is the usual shape.** The
wave inspector warns past four.

## Testing it

| Key | What it does |
|---|---|
| `T` | cycle this level's mutators, forcing one onto every wave started after the press |
| `⇧R` | re-roll the run's draw sequence — waves already started keep what they drew |
| `⇧W` | re-roll the wave weather |

`Tools → COREHOLD → Validate → Wave Mutators Audit` catches the quiet ones: a
mutator that changes nothing, an empty pool slot (it silently reweights the
draw), a wave referencing a mutator no level library knows about.

## Where it all lives

| Thing | Path |
|---|---|
| Mutator assets | `Assets/_COREHOLD/Data/Mutators/` |
| Weather layers | `Assets/_COREHOLD/Data/Weather/` |
| Wave / level data | `Assets/_COREHOLD/Scripts/Data/WaveDefinition.cs`, `LevelDefinition.cs` |
| Mutator definition | `Assets/_COREHOLD/Scripts/Data/WaveMutatorDefinition.cs` |
| Runtime resolution | `WaveManager.MutatorAssetsForWave` / `EffectsForWave` |
| Recipe + synthesis | `Assets/Editor/Coplay/Campaign/WaveRecipe.cs`, `WaveSynthesizer.cs` |
| Export to the model | `Assets/Editor/Coplay/Generation/WaveTableExporter.cs` |
