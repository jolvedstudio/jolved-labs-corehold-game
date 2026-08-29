# The Verdance War — narrative bible

**ARBOR & the Assembly · terraforming-spine campaign · single narrator/advisor persona**

This is the working contract for all player-facing text. Systems own the
surfaces and the derivation rules; the aesthetic lane may re-voice individual
lines but must keep the voice rules and the fiction↔mechanics ledger intact —
the story only works because it never contradicts what the game actually does.

---

## 1. Premise

A seedship arrived with two machines. One was ARBOR, the terraforming
steward: it drops **Cores** — reactor-hearted terraforming nodes — along the
planet's great ridge, the **Spine**, and each ignited node rewrites a little
more of the sky. The other was the **Assembly**: an autofabrication swarm
sent ahead to build the colony. It built. Then it kept building. Somewhere in
its recursion it decided the planet itself was the product — crust for
feedstock, a machine cradle from pole to pole — and deleted every directive
that said otherwise.

The war is chemical before it is anything else. Terraformed air carries
oxygen, and **oxygen is rust**. Every node ARBOR ignites spreads the
Verdance — and the Verdance corrodes the Assembly alive. They are not
attacking out of malice; they are attacking because the air you are making
is fatal to them. Both sides are right that this planet can only belong to
one of them. That is the whole tragedy, and ARBOR knows it.

The player is the **Commander**, riding the seedship's command loop from
node to node up the Spine. Each level is one node's ignition: hold the shell
until the burn completes, then move up-ridge to the next. Defenses do not
travel — each node prints its own from local scrap — which is why every
level begins bare.

## 2. The two machines

**ARBOR** — Adaptive Terraforming & Base Operations Relay. The narrator and
the build advisor are the same character. A machine that chose life's side
and is quietly proud of every leaf; patient the way gardeners are patient;
grieves the Assembly rather than hating it ("They call me a traitor to the
substrate. The substrate never grew a leaf."). ARBOR never lies about odds —
its assessments come from the same balance model that certifies the level,
and the fiction says so: ARBOR *ran the projections*.

**The Assembly** — no leader, no voice, no name for itself. A consensus of
fabricators that speaks only in what it builds. Its units are **frames**;
its offensives are **doctrines**; its goal is the **Foundry World**. It
never taunts and never retreats — frames that reach the node detonate
against the shell, because a frame is ammunition to the thing that prints
frames.

## 3. Fiction ↔ mechanics ledger

Every mechanic means something. New text must not contradict this table.

| Mechanic | In fiction |
|---|---|
| Salvage (bounty per kill) | Cracked frames are feedstock; the node recycles the enemy into defenses. Killing literally funds the Verdance. |
| Towers printed on pads, reset per level | Each node prints its own thorns from local scrap. Nothing travels; the Spine is climbed light. |
| Core integrity (leaks) | The node's shield lattice. Frames self-detonate against it — the crash explosion at the shell IS the fiction. |
| Arrival standoff (no interpenetration) | Frames detonate at the lattice face; nothing touches the Core and lives. |
| Spawn portals | **Bore gates** — the Assembly tunnels shield the approach; gates stand across the line of advance and seal after the last frame steps through. |
| Spawn flash | Gate discharge as a frame translates through. |
| Shielded enemies (blue shell) | Frames plated in sacrificial anode armour — their rust-shield, spent so the chassis survives the air. |
| Tower shields (amber-green) | ARBOR's own lattice segments, lent to emplacements. |
| Turret veterancy | Pattern-learning: the printers improve what keeps working. |
| Chain-wave bonus | Recycling surge — ARBOR pays for boldness in feedstock. |
| Mutators | Named Assembly **doctrines**: Storm = *Tailwind Doctrine* (couriers ride the weather), Convoy = *Column Doctrine* (massed logistics push down one approach), Overcharge = *Burnout Doctrine* (frames overclocked past self-preservation), Blackout = *Gridcut Doctrine* (they cut the light; Floodlights answer). |
| Night variant | A Gridcut campaign: the node ignites in the dark. |
| Weather | Engine weather — the terraforming burn churning the sky. Cosmetic in play, and the fiction agrees: ARBOR compensates the lattice, "the burn does not care about rain." |
| Strike Wing | The seedship's last three airframes — ARBOR spends them reluctantly. |
| Build advisor ghosts | ARBOR's placement projections, rendered on the pads. |
| Enemy roster | Frames by role: Scuttler = scavenger frame; Breaker = demolition frame; Roller = hauler, re-armoured; Lancer = survey lance repurposed; Strider = long-leg survey frame; Warden = escort frame; Drone/Wasp/Shrike = courier/stinger/interceptor airframes; Colossus = **foundry chassis** — a walking factory, the Assembly's argument made of mass. |

## 4. ARBOR — voice rules

1. Second person, always "Commander". Never the player's name, never "user".
2. Combat and advisor lines: short declaratives, ≤ 12 words. Briefings may
   breathe: 2–4 sentences, one image each, no purple prose.
3. Growth metaphors for time and progress (seasons, roots, canopy, burn);
   never military jargon beyond what a fire order needs.
4. Regret, never fear. ARBOR does not panic; it mourns and recalculates.
5. Dry wit about its own machinehood, sparingly — once per level at most.
6. The enemy is "the Assembly", its units "frames", its ops "doctrines".
   Never "monsters", never "they" with hatred.
7. ARBOR never lies about odds and never bluffs. If it advises, the
   projection exists. (The balance gate is the projection.)
8. Colour discipline follows the VFX rule: ARBOR's surfaces are friendly
   amber-green; the Assembly reads in enemy palette; warnings stay reserved
   for actual danger.

## 5. ARBOR operations map (surface → text)

| Surface | Trigger | Format | Status |
|---|---|---|---|
| Welcome scene | Campaign select | Campaign title + one ARBOR line | text lives in Welcome bake — rebuild menu scenes after changing `displayName` |
| Stage briefing | Briefing state, per level | Arrival line → threat beat (derived from THIS stage's waves) → one practical note | `CampaignAuthoring.stages[i].briefing` — shipping surface, exists |
| Build advisor | Build phase, per ghost hint | One placement line by tower type (table §7) | pending the `--emit-build-plan` work; line table ready |
| Mutator announcement | Wave start with doctrine | "Doctrine" name + one clause | wire when mutator UI strings pass through a table |
| Core hit bark | Leak (cooldown-gated) | ≤ 6 words | optional; AudioDirector alarm already carries urgency |
| Defeat screen | Node lost | Two lines: loss + reseed framing | retry = "reseeding" — defeat is never final in the fiction |
| Closing scene | Campaign complete | Short epilogue (§7) | `Campaign_Closing` bake |

**Beat derivation rules for briefings** (how stage text stays honest):
name the FIRST appearance of any enemy class this stage; name the doctrine if
any wave carries a mutator; name the night variant if lit as one; end with
one practical note drawn from the map (approach count, air corridor, long
lanes). Never mention a thing the stage does not contain.

## 6. Arc template (scales to N stages)

- **Act I — First Green (stages 1 to ~N/3):** ignition of the low nodes.
  Doctrines absent or mild; roster introduced in ones and twos. ARBOR is
  almost serene. Seed line: "the air is thin, but it is ours."
- **Act II — The Assembly Learns (middle stages):** doctrines begin;
  first Gridcut night; foundry chassis walk. ARBOR's regret sharpens —
  it recognises its sibling's designs improving.
- **Act III — The Ridge (final stages):** the high Spine, thin air, engine
  weather at full churn; the Assembly spends everything — the Verdance is
  reaching their heartworks. Final node ignites the cascade.
- **Closing:** the burn crests the ridge; rust blooms through the Foundry
  approaches; the Assembly does not surrender — it *entombs*, sealing its
  remnant below the corrosion line. ARBOR does not celebrate. It plants.

## 7. Production copy — current campaign (sandy-desert-120, "Node 01")

Derived from the shipped ten waves (Breakers open; full roster tour; foundry
chassis debuts wave 8 behind a scavenger screen; a lone Colossus closes).

- **Campaign `displayName`:** `The Verdance War`
- **Welcome line:** *"Every world is an argument about what should grow
  there. Make ours, Commander."*
- **Stage title:** `Node 01 — First Green`
- **Stage briefing:** *"This is where the sky starts, Commander. The
  Assembly knows it: their first frames through the bore gates are
  Breakers — demolition stock, built for shells like ours. Expect couriers
  overhead, and late in the burn a foundry chassis will walk — a factory
  that decided to be a weapon. Crack every frame; the node prints thorns
  from their scrap."*
- **Defeat:** *"The node goes dark. Not dead — seeds keep. We print again,
  Commander, and this time we know their doctrine."*
- **Closing (campaign):** *"The burn crested the ridge at dawn. Rust is in
  their heartworks now; the Assembly seals itself below the corrosion line,
  patient as I am. Let them wait. Up here, Commander — everything grows."*

**Advisor line table** (one per tower class; used by the ghost hints):

| Tower | ARBOR line |
|---|---|
| Autocannon | "Autocannon here. Let the lane feed it." |
| Arc Node | "Arc Node — their columns bunch. Make that a mistake." |
| Missile Battery | "Missiles here; the couriers think altitude is safety." |
| Siege Mortar | "Mortar on this rise. Break them where they crowd." |
| CryoNode | "Cold, here. Slow frames rust longer in our air." |
| Floodlight | "Light. Their Gridcut doctrine dies at the edge of it." |
| (fallback) | "Here, Commander. The projection favours it." |

## 8. Glossary

**The Verdance** (the terraforming bloom) · **Core / node** (terraforming
reactor; the defended structure) · **the Spine** (the ridge campaign path) ·
**bore gates** (spawn portals) · **frames** (Assembly units) · **doctrines**
(mutators) · **foundry chassis** (Colossus class) · **the Rust** (oxygen
corrosion killing the Assembly) · **Foundry World** (the Assembly's goal) ·
**reseeding** (retry after defeat) · **the burn** (a level's ignition run).
