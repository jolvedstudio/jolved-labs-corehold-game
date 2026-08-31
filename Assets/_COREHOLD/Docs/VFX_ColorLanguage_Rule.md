# COREHOLD — VFX Colour-Language Rule (v1)

*One page. Read before selecting or wiring ANY coloured effect (muzzle, tracer,
explosion, shell, overlay, status). Resolves VFX Expansion Proposal v2 · Correction E.*

The player reads COREHOLD's core counter pillar (GDD §7.1) **by colour**. At any
moment up to three independent colour axes are on screen at once. They must never
collide, or the counter read breaks: a Shielded pip must not look like an Energy
kill, an "empowered" tower must not look like a Shielded enemy, and so on.

---

## Axis 1 — Armour identity (LIVE, authoritative, DO NOT REUSE)

Owned by `UI/OverlayManager.cs`. These three colours are **reserved**; nothing
else in the game may read as one of them near a unit.

| Armour type | Colour (RGB, HDR-normalised) | Swatch meaning |
|-------------|------------------------------|----------------|
| Unarmoured  | `(0.75, 0.78, 0.80)` | neutral light grey |
| Plated      | `(0.95, 0.78, 0.25)` | warm gold/amber |
| **Shielded**| **`(0.35, 0.70, 1.00)`** | **cyan-blue — RESERVED** |

**Hard rule:** the Shielded blue is the single most protected colour, because the
whole counter loop hinges on "is this shielded?". No damage-type effect, kill
burst, or status glow may sit in that cyan-blue band **next to an enemy**.

---

## Axis 2 — Damage-type palette (muzzle + tracer + explosion SHARE one palette per type)

A weapon's muzzle flash, tracer, and splash explosion must all read as the **same
family**, so a kill reinforces *which* weapon scored it. Authored per type; kept
clear of Axis 1.

| Damage type | TARGET palette | Collision check |
|-------------|----------------|-----------------|
| Kinetic   | warm amber / white-hot | clear of all armour hues* |
| Energy    | **violet / magenta** (NOT cyan-blue) | **must not drift toward Shielded cyan-blue** |
| Explosive | orange / fire | clear |

\* Kinetic amber is close to Plated gold. That is tolerable because Plated is an
*armour pip on the enemy* and Kinetic fire appears *at the muzzle/impact* — they
are spatially separated and never overlap. Energy-vs-Shielded is the dangerous
pair because an Energy burst can land *on* a Shielded enemy, directly beside its
blue pip; hence Energy is pushed to violet, away from cyan.

> **Reality check (current authored data is NOT yet consistent).** The above is the
> TARGET palette, not the shipped state. Today the per-mount `tracerColor` values
> do not track damage type: the Railgun (Kinetic, type 0) has a violet tracer
> `(3.2, 1.4, 4.5)` while the Arc Node (Energy, type 1) has an amber tracer
> `(3.5, 2.2, 0.8)` — i.e. Energy currently reads amber and a Kinetic tower reads
> violet, the opposite of this rule. Aligning the authored tracer/muzzle colours to
> this table is a follow-up tuning pass (per-tier asset edit), tracked separately
> from wiring the explosion slots. The muzzle prefabs (`MuzzleKinetic/Energy/
> Explosive`) ARE type-routed in code; only the authored tracer hues drift.

**Rule:** when picking an Energy explosion/muzzle, choose a **violet/white-hot**
prefab, never a blue one. If a candidate reads blue in the testbed against a
Shielded enemy, reject it.

---

## Axis 3 — Faction / friendly-state colours

| Element | Colour | Rationale |
|---------|--------|-----------|
| Enemy Shielded shell (`ShieldAura`) | blue `(0.35, 0.7, 1)` — **matches** Axis 1 Shielded | shell + HP-pip reinforce ONE identity (intentional match, the only sanctioned reuse of the reserved blue, and only on the enemy that owns it) |
| Tower shield shell (`TowerShield`) | amber-green `(0.5, 1.0, 0.55)` | "friendly barrier" — deliberately NOT enemy-blue |
| Tower fire (tracer/muzzle) | glowing blue *(side task, managed separately)* | friendly identity |
| Enemy fire (tracer/muzzle) | glowing red *(side task, managed separately)* | hostile identity |

**Rule:** friendly = blue/green family, hostile = red family. The tower shield
shell is green (not blue) specifically so it never collides with the enemy
Shielded read.

---

## Selection checklist (apply in the testbed, both biomes, day + night)

1. Does the candidate sit in the **reserved Shielded cyan-blue band** and appear
   near an enemy? → **reject** (unless it IS the Shielded shell).
2. For a damage-type effect: does it match its **type's palette** (muzzle = tracer
   = explosion)? → if not, retint or reject.
3. Is Energy content **violet/white-hot**, not blue? → required.
4. Screen against **both biomes and the night variant** — a hue that reads fine at
   day can collapse toward white/blue under night grading.

---

## Current status of the axes (v1)

- **Axis 1:** live and authoritative in `OverlayManager`.
- **Axis 3:** shells shipped (enemy blue, tower green). Faction tracers are a
  separate side task, owner-managed.
- **Axis 2 — OPEN:** the three damage-type explosion slots
  (`ExplosionKinetic/Energy/Explosive`, enum id 15–17) are **not yet wired** in
  `VFXDirectorConfig.asset` (only id 0–14 are assigned), so `PlayExplosion(...,
  DamageType)` currently falls back to the neutral size-based explosion. Wiring
  them requires the Step 0 vendor copy first (Blocker A) and must obey the Energy
  ≠ cyan-blue rule above.
