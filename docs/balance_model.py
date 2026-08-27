#!/usr/bin/env python3
"""COREHOLD balance model (roadmap R1) — the universal gate.

Geometry-parameterized per-wave margin model for the shipped Refinery Delta
map. For every wave it computes total incoming effective HP vs. the damage the
built towers can deliver while enemies are inside their coverage (time-in-range
derived from covered arc-length / enemy speed, per pad), producing a per-wave
margin (deliverable damage / required damage). Defaults equal today's live map
and live ScriptableObject data; re-run this before tuning ANYTHING.

Run:
    python3 docs/balance_model.py                      # print the margin table
    python3 docs/balance_model.py --difficulty veteran
    python3 docs/balance_model.py --report docs/baseline_today.txt
    python3 docs/balance_model.py --json out.json      # machine-readable dump

Exit code is non-zero when any wave is flagged outside the band, so the model
can gate CI / generation (roadmap R29, R30).

LINEAGE. The shipped tuning was validated against the GDD's Appendix A model
(COREHOLD_GDD.md), which converts banked salvage to DPS (DPS_PER_SALVAGE),
applies a slew/aim efficiency (SLEW) and an ABSTRACT exposure line
(0.30 + 0.018*(wave-1)). This model keeps Appendix A's accepted curve as its
acceptance spec — "opens near 1.5, holds 1.2-1.5 through the midgame, closes
1.0-1.1 on the boss" — but replaces the abstract exposure line with exposure
DERIVED from the live geometry (routes, pad positions, per-tier ranges), which
is what the roadmap needs from R6/R10 (splines change Length) through R27-R30
(generated maps). Appendix A itself says its 0.80 slew factor was a
measure-later estimate; the calibration constants below are this model's
equivalent, anchored so the live map reproduces the accepted curve.

DATA PROVENANCE (extracted 2026-08-12 from the live repo — the code wins):
  * Routes, pad positions, spawners, core: Assets/_COREHOLD/Scenes/Game.unity
    (pads were hand-moved after the RefineryDeltaBlockout build during the
    clearance pass, so the SCENE, not the builder constants, is ground truth).
  * Pad intended turrets / classes: HardpointCoverageGizmo components in scene.
  * Towers: Assets/_COREHOLD/Data/Towers/*.asset   (tiers, weapons arrays).
  * Enemies: Assets/_COREHOLD/Data/Enemies/*.asset (+ prefab bodyRadius).
  * Waves: Assets/_COREHOLD/Data/Waves/Wave_01..10.asset.
  * Damage table: Assets/_COREHOLD/Data/DamageTable.asset.
  * Rules: Assets/_COREHOLD/Data/Levels/Level_RefineryDelta.asset and
    WaveManager.cs (wave HP scalar 1 + 0.18*(wave-1), difficulty multipliers,
    clear bonus 60 + 18*wave fallback, chain bonus 8/80).

LIVE-DATA OBSERVATIONS (true today, worth knowing when tuning):
  * Tier-1 ranges in the assets are 20 m for Autocannon, Arc Node AND Scan
    Relay, while the GDD table and the HardpointCoverageGizmo authoring rings
    say 12 / 10 m; tier-2 ranges then DROP to 13 / 12 m. Upgrading those
    towers today trades away most of their coverage (63 m -> 21 m of route for
    the Premium_2 Autocannon) and ALL of some pads' anti-air. The model uses
    the live values and its build simulation refuses coverage-destroying
    upgrades unless the DPS gain outweighs the loss.
  * Air: the live Spawner_Air -> Core line is 55.5 m; the GDD's Appendix A
    still says 95 m. The live waves field the Drone (60 HP, 8 m/s, bounty 12,
    via SwapAirToDrone); the GDD's tables still say Wasp. That is exactly the
    GDD's 3,515 "total earnable" vs. the live 3,461.
  * Scan Relay tier 1 carries an authored weapon (5 dmg @ 1/s, chain 2); its
    tier 2/3 weapons arrays are empty with legacy damage 0, so an upgraded
    relay stops shooting. Live truth, modeled as-is (the baseline build never
    buys a relay — no pad intends one).
  * The Colossus definition has no prefab reference in this snapshot
    (bodyRadius falls back to 0.6 like WaveManager.ResolvePrefabRadius does).
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass, field

# =============================================================================
#  MODEL ASSUMPTIONS — named constants (the knobs; everything else is live data)
# =============================================================================

# Fraction of theoretical DPS that lands as useful damage against ordinary
# swarm targets: TurretAim's 6-degree fire gate, target switching, projectile
# travel and overkill on dying units. Appendix A's SLEW=0.80 is the ancestor.
FOCUS_SWARM = 0.80
# Against a single heavy target (per-enemy effective HP >= HEAVY_HP) there is
# no switching or overkill waste — the turret stays locked on.
FOCUS_HEAVY = 0.95
HEAVY_HP = 800.0

# Ground exposure amplification over raw free-flow transit: car-following
# queue compression (followers bunch to ~1.6-2.8 m behind slow leaders inside
# coverage), wide-body reach (a Breaker/Colossus body spans metres beyond its
# point sample), and the play the model does not simulate (the telegraphed
# wave-10 armour check steering builds, chain-bonus income, building during a
# wave). Calibrated so the live map reproduces Appendix A's accepted curve —
# for reference, Appendix A's own abstract exposure line (0.30+0.018*(w-1))
# implied ~2.3x over raw free-flow geometry, so 1.8 is the more conservative
# anchor. Re-measure after the spline swap (roadmap R10).
QUEUE_DWELL_FACTOR = 1.80   # ground groups only; air never queues

# Splash: extra average targets per metre of splash radius against grouped
# waves, applied when a group has >= SPLASH_PACK_MIN units.
SPLASH_PACK_BONUS_PER_M = 0.25
SPLASH_PACK_MIN = 3

# Chain (Arc Node): fraction of shots that find their extra chain targets.
CHAIN_UPTIME_PACK = 0.70    # group of >= SPLASH_PACK_MIN
CHAIN_UPTIME_SPARSE = 0.25  # smaller groups / lone boss

# Damage delivered to one group is capped at this multiple of the group's
# effective HP — surplus on a cheap group must not subsidise the wave margin.
# 1.5 also lets healthy early waves print the "opens near 1.5" shape instead
# of clipping it.
OVERKILL_CAP = 1.50

# 3D range check (GDD section 7.2): horizontal reach vs a flyer at altitude a
# is sqrt(R^2 - a^2) (matches the GDD's 10m -> 9.2m at 4 m altitude example).
COVERAGE_SAMPLE_STEP_M = 0.25   # arc-length sampling step for coverage

# The Colossus enrages (+40% speed) below 50% HP. A defense that is winning
# burns it down gradually, so the model assumes the speed-up starts this far
# along its path.
ENRAGE_ASSUMED_PATH_FRACTION = 0.70

# Margin band = Appendix A's accepted curve. Every wave must clear >= BAND_MIN.
# The CLOSING wave (the boss) must sit in [BAND_MIN, BAND_CLOSE_MAX] — above it
# the finale is too easy and the gate ritual's +0.01..0.02 wave-HP scalar bump
# applies. Mid-game waves above BAND_MID_MAX get an advisory flag.
BAND_MIN = 1.00
BAND_CLOSE_MAX = 1.20
BAND_MID_MAX = 1.55
CLOSE_WAVE = 10

# Per-group starvation check. The wave margin nets all groups, so a single
# group may sit slightly below 1.0 while cross-group slack still clears it —
# the designed-tight boss does exactly this. The group flag exists to catch a
# STARVED group (e.g. air with no anti-air built), which shows up far below
# the wave margin, not a few points under it.
GROUP_MIN = 0.90

# Build simulation: deterministic greedy. Purchases happen in the build phase
# before each wave from banked salvage (starting 300 + full-clear income; chain
# bonuses are NOT assumed). All eight pads are built with their intended turret
# in this priority order first; after that, upgrades are bought best
# value-gain-per-salvage first, where value = tier DPS x (ground + air covered
# route length) — so a "bigger gun, smaller ring" upgrade that would strand a
# pad (see LIVE-DATA OBSERVATIONS) is only taken when it still adds throughput.
# A two-step lookahead lets the sim cross a bad middle tier when tier 3 wins.
BUILD_PRIORITY = [
    "HP_Premium_2",   # Autocannon 100 — the workhorse core
    "HP_Premium_1",   # Missile 150
    "HP_Premium_3",   # ArcNode 120
    "HP_Standard_1",  # Autocannon 100
    "HP_Standard_2",  # Missile 150
    "HP_Rear_1",      # Autocannon 100
    "HP_Rear_2",      # ArcNode 120
    "HP_Overwatch",   # Mortar 200
]
UPGRADE_MIN_VALUE_GAIN = 1.02   # tier must beat the current one by >= 2%
SALVAGE_RESERVE = 0             # keep nothing banked

# =============================================================================
#  LIVE DATA — extracted from the repo (see provenance above). Do not "fix"
#  values here to make the table look nicer; change the game, then re-extract.
# =============================================================================

# ---- Geometry (Game.unity, world XZ; y=0 ground plane) ----------------------

ROUTE_WEST = [
    (-60.0, 18.0), (-45.0, 18.0), (-30.0, 18.0), (-19.0, 18.0), (-19.0, 9.0),
    (-9.0, 9.0), (-9.0, 18.0), (2.0, 18.0), (2.0, 8.0), (13.0, 8.0),
    (13.0, 18.0), (23.0, 18.0), (23.0, 6.0), (34.5, -6.5),
]
ROUTE_NORTH = [
    (-6.0, 36.0), (-18.0, 27.0), (-30.0, 18.0), (-19.0, 18.0), (-19.0, 9.0),
    (-9.0, 9.0), (-9.0, 18.0), (2.0, 18.0), (2.0, 8.0), (13.0, 8.0),
    (13.0, 18.0), (23.0, 18.0), (23.0, 6.0), (34.5, -6.5),
]
AIR_SPAWN = (0.0, 37.0)     # Spawner_Air at (0, 4, 37); flies straight to Core
AIR_TARGET = (34.5, -6.5)   # Core_Target XZ; flight is level at flightAltitude

# Pads: name -> (x, z, intended tower id, pad class). Scene positions (moved
# after the blockout during the clearance pass) + gizmo intendedTurret fields.
PADS = {
    "HP_Premium_1":  (-3.488, 4.5,   "missile_battery", "Premium"),
    "HP_Premium_2":  (7.5,    1.5,   "autocannon",      "Premium"),
    "HP_Premium_3":  (18.024, 1.5,   "arc_node",        "Premium"),
    "HP_Standard_1": (-25.767, 11.231, "autocannon",    "Standard"),
    "HP_Standard_2": (-13.0,  2.5,   "missile_battery", "Standard"),
    "HP_Rear_1":     (32.579, 5.542, "autocannon",      "Rear"),
    "HP_Rear_2":     (22.551, -3.379, "arc_node",       "Rear"),
    "HP_Overwatch":  (24.0,  -8.0,   "siege_mortar",    "Overwatch"),
}

# ---- Rules (Level_RefineryDelta.asset + WaveManager.cs) ---------------------

# ---- Adopted geometry (roadmap R10) ----------------------------------------
#
# The model's STANDING baseline is the spline geometry, matching PathRoute's
# useSpline default (ON since R9). These are the lengths R9's gate measured on
# the live map; the pre-spline polyline was 149.985 m on both routes, still
# reachable with --polyline for comparison. Re-measure and update these two
# numbers whenever route geometry changes — they are a model input, not a
# derived value. Margins were verified insensitive to Route_North anywhere in
# 153.89-154.52 m (wave 10 lands at 1.07 throughout), so the pending merge
# re-pin does not require a refresh.
SPLINE_ROUTE_LENGTHS = {0: 153.742, 1: 154.518}

STARTING_SALVAGE = 300
CORE_INTEGRITY = 20
HP_GROWTH_PER_WAVE = 0.18
CHAIN_BONUS_PER_LIVE_ENEMY = 8   # not assumed in the baseline income
CHAIN_BONUS_CAP = 80
MAX_LIVE_ENEMIES = 14

# ---- R22 extension terms (roadmap R22) --------------------------------------
#
# The live game grew four systems the R1 model could not see: kill-streak
# income (R2), the Strike Wing active (R19), wave mutators (R20) and turret
# veterancy (R21). Streak and veterancy are UNCONDITIONALLY part of live play,
# so they are ON by default — the default report models the game as shipped.
# `--r22-off` reproduces the pre-R22 report byte-for-byte (the legacy check).
#
#   • STREAK income: bounty payouts escalate with kill streaks. Dense waves
#     (total spawns >= DENSE_WAVE_COUNT) chain streaks reliably: +15% on the
#     bounty component; sparse waves +5%. Clear bonuses are unaffected.
#   • VETERANCY: fleet-average damage ramp standing in for kill-accumulated
#     ranks — +2% per wave from wave 3, capped at +12% (rank 3 by wave 8).
#   • STRIKE WING: each use buys STRIKE_ENEMY_SECONDS extra enemy-seconds of
#     engagement, credited to the wave's WORST group at that group's observed
#     delivery rate (a strike on an uncovered group credits nothing — stun
#     only helps where towers already shoot), and costs STRIKE_COST salvage.
#     "auto" policy: 1 use on dense-or-boss waves, 0 otherwise; --strike-uses
#     overrides it flat.
#   • MUTATORS: per-wave flags (none authored on the shipped table; force with
#     --mutate for tuning). overcharge: xMUTATOR_OC_HP effective HP and
#     xMUTATOR_OC_BOUNTY bounty. storm: air speed xMUTATOR_STORM_AIR_SPEED
#     (shorter traverse AND shorter exposure). convoy: every ground group
#     funnels onto the primary ground route.
STREAK_INCOME_DENSE = 0.15
STREAK_INCOME_SPARSE = 0.05
DENSE_WAVE_COUNT = 12
VETERANCY_PER_WAVE = 0.02
VETERANCY_CAP = 0.12
VETERANCY_FROM_WAVE = 3
STRIKE_ENEMY_SECONDS = 4.5
STRIKE_COST = 120
MUTATOR_OC_HP = 1.30
MUTATOR_OC_BOUNTY = 1.50
MUTATOR_STORM_AIR_SPEED = 1.30
MUTATOR_BLACKOUT_RANGE = 0.50   # distance counts x2 -> range halves; the model
                                # has no floodlight term (the policy never buys
                                # one), so this is the WORST-case blackout

# Run-scoped R22 state (CLI-mutated): on/off, strike policy, forced mutators.
R22 = {
    "on": True,
    "strike_uses": None,        # None = auto policy; int = flat per-wave uses
    "forced_mutators": {},      # wave_number -> set of flags, from --mutate
}


def r22_mutators(wave: dict, wave_number: int) -> frozenset:
    """The mutator flags in force for a wave: authored on the table + forced."""
    if not R22["on"]:
        return frozenset()
    authored = wave.get("mutators", ())
    forced = R22["forced_mutators"].get(wave_number, ())
    return frozenset(authored) | frozenset(forced)


def r22_dense(wave: dict) -> bool:
    return sum(count for _, count, _, _, _ in wave["groups"]) >= DENSE_WAVE_COUNT


def r22_strike_uses(wave: dict, wave_number: int) -> int:
    """Auto policy: one use on dense-or-boss waves (the panic clusters)."""
    if not R22["on"]:
        return 0
    if R22["strike_uses"] is not None:
        return max(0, R22["strike_uses"])
    scalar = wave_scalar(wave_number)
    boss = any(ENEMIES[eid]["hp"] * scalar >= HEAVY_HP
               for eid, _, _, _, _ in wave["groups"])
    return 1 if (r22_dense(wave) or boss) else 0


def r22_veterancy_mult(wave_number: int) -> float:
    if not R22["on"]:
        return 1.0
    return 1.0 + min(VETERANCY_CAP,
                     VETERANCY_PER_WAVE * max(0, wave_number - (VETERANCY_FROM_WAVE - 1)))

# ---- Tower loss (return fire) -----------------------------------------------
#
# Every live enemy prefab carries an EnemyWeapon: enemies shoot the nearest
# turret in range while they walk, and a turret at 0 HP explodes, frees its
# pad, and refunds NOTHING (TowerHealth.Die -> Tower.Sell deregisters only).
# Nothing repairs a turret mid-run — not even an upgrade — so damage
# accumulates for the whole level. Until this term, the model ignored all of
# it, which was its one anti-conservative asymmetry: every other term rounds
# against the defender, this one silently credited towers with 100% uptime.
#
# The term, per wave, per built pad:
#   • Incoming fire is sampled along each armed group's own path. Each sample's
#     fire goes to the NEAREST built pad within that enemy's weapon range —
#     the live EnemyWeapon.FindNearestTower rule — for the seconds the enemy
#     spends on that sample (Roller phase / enrage speeds honoured; air groups
#     sample the corridor with 3D slant range at altitude).
#   • Units fire only while ALIVE, and delivery vs return fire is coupled: a
#     full-uptime delivery pass measures how m-fold the towers out-deliver
#     each group, the group's fire is discounted to ~1/m of its walk (floored
#     at TOWER_LOSS_SURVIVAL_FLOOR — enemies out-range most turret rings and
#     get approach shots in even when they die on entry), and a second
#     delivery pass books what the standing towers actually land.
#   • A pad that soaks less than its remaining HP keeps 100% uptime and CARRIES
#     the damage to the next wave. A pad that soaks its remaining HP dies
#     partway: its deliverable damage this wave scales by remaining/incoming
#     (the fraction of the wave it stood), and the NEXT build phase must re-buy
#     it from tier 1 at full price — upgrades and position in the greedy queue
#     are lost, exactly as in play.
#
# Enemy gun stats ride the ENEMIES rows as gdps (sum of mount damage x
# fireRate) / grange (longest mount); rows without them are unarmed. Tower HP
# rides TOWERS as hp — the runtime TowerHealth default of 220 everywhere,
# because no tower prefab authors an override today. `--tower-loss-off`
# reproduces the pre-term report byte-for-byte.
TOWER_HP_DEFAULT = 220.0

# Alive-fraction floor for out-delivered groups: even a group shredded on
# entry fires on approach (Scuttler guns reach 20 m; live T2/T3 turret rings
# are 13-14 m), so return fire never discounts to zero. [TUNE]
TOWER_LOSS_SURVIVAL_FLOOR = 0.15

# Full-model evaluations the counts-only tuner may spend (suggest_fix). Each
# is one complete run (~0.2 s); the chunked cuts converge in a handful per
# flagged wave, so this cap is a runaway stop, not a working budget.
MAX_FIX_RUNS = 60

# Run-scoped switch (CLI-mutated), --r22-off pattern.
TOWER_LOSS = {"on": True}


def _nearest_pad_in_range(geom: Geometry, built: dict, x: float, z: float,
                          reach: float):
    """Name of the nearest BUILT pad within reach of (x, z), or None."""
    best = None
    best_d = reach
    for pad in built:
        px, pz = geom.pads[pad][0], geom.pads[pad][1]
        d = math.hypot(px - x, pz - z)
        if d <= best_d:
            best_d = d
            best = pad
    return best


def tower_incoming(geom: Geometry, built: dict, groups: list,
                   survival: list):
    """Return fire soaked per pad over one wave. Returns (incoming, by_group):
    pad name -> damage, and the same damage re-summed per group index (the
    advice lines name the top shooter from it). `survival` is the per-group
    alive fraction (parallel to groups)."""
    by_group = [0.0] * len(groups)
    if not TOWER_LOSS["on"] or not built:
        return {}, by_group
    incoming: dict = {}
    for gi, g in enumerate(groups):
        enemy = g["enemy"]
        gdps = enemy.get("gdps", 0.0) * survival[gi]
        grange = enemy.get("grange", 0.0)
        if gdps <= 0.0 or grange <= 0.0:
            continue
        # Seconds ONE unit of this group fires at each pad, walking its path.
        per_enemy: dict = {}
        if enemy["air"]:
            reach_sq = grange * grange - enemy["altitude"] * enemy["altitude"]
            if reach_sq <= 0.0:
                continue
            reach = math.sqrt(reach_sq)
            ax, az = geom.air_spawn
            bx, bz = geom.air_target
            length = math.hypot(bx - ax, bz - az)
            n = max(2, int(length / COVERAGE_SAMPLE_STEP_M))
            dt = (length / n) / (enemy["speed"] * g["air_speed_mult"])
            for i in range(n):
                t = (i + 0.5) / n
                pad = _nearest_pad_in_range(geom, built,
                                            ax + (bx - ax) * t, az + (bz - az) * t, reach)
                if pad is not None:
                    per_enemy[pad] = per_enemy.get(pad, 0.0) + dt
        else:
            route = g["route"]
            n = max(2, int(route.polyline_length / COVERAGE_SAMPLE_STEP_M))
            step = route.polyline_length / n
            for i in range(n):
                s = (i + 0.5) * step
                x, z = route.sample(s)
                pad = _nearest_pad_in_range(geom, built, x, z, grange)
                if pad is not None:
                    # Time on this sample: step / speed, scaled to spline time
                    # like every other ground duration (R10).
                    dt = step / enemy_speed_at(enemy, s, route.polyline_length) * route.scale
                    per_enemy[pad] = per_enemy.get(pad, 0.0) + dt
        for pad, seconds in per_enemy.items():
            dmg = g["count"] * gdps * seconds
            incoming[pad] = incoming.get(pad, 0.0) + dmg
            by_group[gi] += dmg
    return incoming, by_group

# ---- Generator overrides (roadmap R30) --------------------------------------
#
# The generator parameterizes the model per generated map: its own geometry
# (--geometry), a candidate or solved HP growth (--hp-growth /
# --solve-hp-growth), the derived live-enemy cap (--max-live) and a build
# priority ordered for its own pad names. ACTIVE carries the values one process
# run actually uses; the constants above remain the live-map defaults, so a
# bare run still reports the shipped baseline byte-for-byte.
ACTIVE = {
    "hp_growth": HP_GROWTH_PER_WAVE,
    "max_live": MAX_LIVE_ENEMIES,
    "build_priority": None,   # None -> BUILD_PRIORITY (the shipped names)
    # A2 campaign carry: an ABSOLUTE entry bank. None -> the tunable default
    # (STARTING_SALVAGE x difficulty economy). When set, the value is used
    # as-is: it represents what a campaign run actually walks in with, and the
    # caller (Campaign Builder) has already settled any difficulty economics —
    # re-applying the multiplier here would double-count it.
    "starting_salvage": None,
}


def active_build_priority():
    return ACTIVE["build_priority"] if ACTIVE["build_priority"] else BUILD_PRIORITY

DIFFICULTY_HP_MULT = {"normal": 1.00, "veteran": 1.25, "nightmare": 1.55}
DIFFICULTY_ECO_MULT = {"normal": 1.00, "veteran": 1.12, "nightmare": 1.22}

# ---- Damage table (DamageTable.asset): rows Kinetic/Energy/Explosive x
#      columns Unarmoured/Plated/Shielded ------------------------------------
DAMAGE_MULT = [
    [1.0, 0.5, 1.25],   # Kinetic
    [1.0, 1.25, 0.5],   # Energy
    [1.3, 0.65, 0.65],  # Explosive
]

# ---- Enemies (Enemy_*.asset; armour 0=Unarmoured 1=Plated 2=Shielded) -------
#
# gdps / grange are the enemy's RETURN FIRE (tower-loss term): the sum of the
# prefab's EnemyWeapon mount damage x fireRate, and the longest mount range,
# measured from Assets/_COREHOLD/Prefabs/Enemies/*.prefab. Rows without them
# are unarmed. NOTE these rows are hand-copies and the live assets HAVE
# drifted since (Shrike is now a plated 4.5 m/s ground unit, the Wasp is
# grounded, the wave assets themselves were re-authored) — every REAL
# certification therefore passes `--waves` with an `enemies` override block
# exported from the actual assets, and this embedded table remains only the
# frozen reference map for the bare regression run.
ENEMIES = {
    "scuttler": dict(hp=45,   armour=0, speed=7.5,  bounty=8,   leak=1, air=False,
                     gdps=6.0,  grange=20.0),
    "strider":  dict(hp=110,  armour=1, speed=5.0,  bounty=12,  leak=1, air=False,
                     gdps=5.4,  grange=12.0),   # twin 4.5 dmg @ 0.6/s
    "drone":    dict(hp=60,   armour=0, speed=8.0,  bounty=12,  leak=2, air=True, altitude=4.0,
                     gdps=5.4,  grange=20.0),
    "wasp":     dict(hp=70,   armour=0, speed=9.0,  bounty=14,  leak=2, air=False,
                     gdps=4.0,  grange=13.0),   # asset says GROUND now (isAir 0)
    "lancer":   dict(hp=190,  armour=2, speed=4.6,  bounty=18,  leak=2, air=False,
                     gdps=7.0,  grange=14.0),
    "roller":   dict(hp=150,  armour=0, speed=11.0, bounty=20,  leak=2, air=False,
                     phase_at=0.6, phase_speed=4.6, gdps=21.0, grange=15.0),
    "breaker":  dict(hp=420,  armour=1, speed=3.75, bounty=35,  leak=3, air=False,
                     gdps=19.8, grange=25.0),
    # The original boss row, kept for the frozen wave-10 below. No asset holds
    # id "colossus" any more — the live roster split it into _b/_c; guns are
    # the orphaned Colossus_A prefab's (28 dmg @ 1.2/s, 30 m).
    "colossus": dict(hp=2800, armour=2, speed=3.0,  bounty=250, leak=20, air=False,
                     enrage_mult=1.4, gdps=33.6, grange=30.0),
    # Live roster (asset-true 2026-08-27). The Warden's ally damage-reduction
    # bubble is deliberately UNMODELED (the model stays conservative; when it
    # matters, add a per-group protection factor next to the mutators).
    "shrike":     dict(hp=55,   armour=1, speed=4.5, bounty=16,  leak=2, air=False,
                       gdps=10.8, grange=20.0),
    "warden":     dict(hp=520,  armour=1, speed=3.4, bounty=45,  leak=3, air=False,
                       gdps=8.0,  grange=18.0),
    "colossus_b": dict(hp=2400, armour=2, speed=3.4, bounty=220, leak=18, air=False,
                       enrage_mult=1.4, gdps=35.2, grange=26.0),
    "colossus_c": dict(hp=3200, armour=2, speed=2.6, bounty=280, leak=24, air=False,
                       enrage_mult=1.4, gdps=30.6, grange=34.0),
}

# ---- Towers (Tower_*.asset): damage type 0=Kinetic 1=Energy 2=Explosive.
#      Tier dicts mirror TowerTier: authored weapons array (or legacy fields
#      when empty), tier-level range/minRange, aura fields for the relay.
#      hp: what return fire must chew through (tower-loss term) — the runtime
#      TowerHealth default of 220, added by Tower.Build; no tower prefab
#      authors an override and nothing scales it per tier. ---------------------
TOWERS = {
    "autocannon": dict(type=0, air=True, hp=220.0, tiers=[
        dict(cost=100, range=20.0, min_range=0.0, dps=10 * 2.0,   chain=0, falloff=0.0, splash=0.0),
        dict(cost=130, range=13.0, min_range=0.0, dps=15 * 2.8,   chain=0, falloff=0.0, splash=0.0),
        dict(cost=200, range=14.0, min_range=0.0, dps=25 * 3.6,   chain=0, falloff=0.0, splash=0.0),
    ]),
    "missile_battery": dict(type=2, air=True, hp=220.0, tiers=[
        dict(cost=150, range=13.0, min_range=0.0, dps=45 * 0.6,   chain=0, falloff=0.0, splash=2.5),
        dict(cost=180, range=14.0, min_range=0.0, dps=80 * 0.7,   chain=0, falloff=0.0, splash=3.0),
        dict(cost=270, range=15.0, min_range=0.0, dps=140 * 0.8,  chain=0, falloff=0.0, splash=3.5),
    ]),
    "arc_node": dict(type=1, air=True, hp=220.0, tiers=[
        dict(cost=120, range=20.0, min_range=0.0, dps=14 * 1.5,   chain=2, falloff=0.7, splash=0.0),
        dict(cost=140, range=12.0, min_range=0.0, dps=22 * 1.8,   chain=3, falloff=0.7, splash=0.0),
        dict(cost=200, range=14.0, min_range=0.0, dps=34 * 2.2,   chain=4, falloff=0.7, splash=0.0),
    ]),
    "siege_mortar": dict(type=2, air=False, hp=220.0, tiers=[
        dict(cost=200, range=20.0, min_range=6.0, dps=90 * 0.35,  chain=0, falloff=0.0, splash=4.0),
        dict(cost=240, range=22.0, min_range=6.0, dps=160 * 0.4,  chain=0, falloff=0.0, splash=4.5),
        dict(cost=300, range=24.0, min_range=6.0, dps=260 * 0.45, chain=0, falloff=0.0, splash=5.0),
    ]),
    "scan_relay": dict(type=0, air=True, hp=220.0, tiers=[
        dict(cost=90,  range=20.0, min_range=0.0, dps=5 * 1.0,    chain=2, falloff=0.7, splash=0.0,
             aura_radius=10.0, aura_fire=0.15, aura_range=0.10, aura_dmg=0.0),
        dict(cost=110, range=12.0, min_range=0.0, dps=0.0,        chain=0, falloff=0.0, splash=0.0,
             aura_radius=12.0, aura_fire=0.25, aura_range=0.15, aura_dmg=0.0),
        dict(cost=160, range=14.0, min_range=0.0, dps=0.0,        chain=0, falloff=0.0, splash=0.0,
             aura_radius=14.0, aura_fire=0.35, aura_range=0.20, aura_dmg=0.10),
    ]),
}

# ---- Waves (frozen reference table). spawner: 0=west ground, 1=north ground,
#      2=air. clear = authored clearBonus (WaveManager falls back to
#      60 + 18*wave when zero).
#
#      This is the Wave_01..10 table AS FIRST EXTRACTED — the regression
#      baseline the bare run reports. The live Wave_*.asset files have been
#      re-authored since (wave 1 fields Breakers, wave 8 fields Colossus B
#      packs, ...) and are NOT mirrored here on purpose: hand-mirroring is the
#      drift this file suffered once already. Certification of any real level
#      passes `--waves` with waves + enemies exported from the actual assets
#      (WaveTableExporter), so edits to the assets move the verdict
#      immediately; this literal only keeps the bare run stable. -------------
WAVES = [
    dict(clear=78,  groups=[("scuttler", 5, 2.6, 0.0, 0)]),
    dict(clear=96,  groups=[("scuttler", 8, 2.2, 0.0, 0)]),
    dict(clear=114, groups=[("strider", 5, 3.4, 0.0, 0), ("drone", 1, 0.0, 12.0, 2)]),
    dict(clear=132, groups=[("scuttler", 8, 1.9, 0.0, 0), ("strider", 4, 3.6, 5.0, 1)]),
    dict(clear=150, groups=[("drone", 8, 2.8, 0.0, 2)]),
    dict(clear=168, groups=[("strider", 8, 3.2, 0.0, 0), ("scuttler", 5, 2.2, 4.0, 1),
                            ("drone", 4, 4.0, 10.0, 2)]),
    dict(clear=186, groups=[("lancer", 5, 4.4, 0.0, 0), ("roller", 3, 6.0, 5.0, 1)]),
    dict(clear=204, groups=[("lancer", 5, 4.6, 0.0, 0), ("scuttler", 10, 2.2, 4.0, 1),
                            ("drone", 5, 3.4, 12.0, 2)]),
    dict(clear=222, groups=[("breaker", 3, 8.0, 0.0, 0), ("strider", 8, 3.0, 5.0, 1),
                            ("drone", 5, 3.4, 10.0, 2)]),
    dict(clear=240, groups=[("colossus", 1, 0.0, 4.0, 0), ("scuttler", 8, 2.4, 0.0, 1),
                            ("drone", 4, 3.6, 14.0, 2)]),
]

# =============================================================================
#  Geometry engine
# =============================================================================

@dataclass
class Route:
    """
    A ground route as a polyline with arc-length sampling.

    `scale` (roadmap R10) is the measured spline length divided by the polyline
    length. Sampling and coverage are always computed in POLYLINE space — the
    curve passes through the same knots and the same hairpin pockets, so it
    covers the same spans; R9's gate confirmed this empirically, reporting an
    identical covered-span count on chords and on the curve for all eight pads.
    What the curve changes is how long a unit spends walking those spans, so the
    scale is applied to TIME, not to the geometry. That keeps pad-to-route
    distances honest (scaling coordinates would move the pads relative to the
    route) while still crediting the extra time-in-range a longer curve buys.
    """
    name: str
    points: list
    scale: float = 1.0

    def __post_init__(self):
        self.cum = [0.0]
        for i in range(1, len(self.points)):
            ax, az = self.points[i - 1]
            bx, bz = self.points[i]
            self.cum.append(self.cum[-1] + math.hypot(bx - ax, bz - az))
        self.polyline_length = self.cum[-1]

    @property
    def length(self) -> float:
        """Effective route length: the measured spline length when scaled (R10)."""
        return self.polyline_length * self.scale

    def sample(self, s: float):
        """Position at arc length s in POLYLINE space (clamped)."""
        s = max(0.0, min(s, self.polyline_length))
        for i in range(1, len(self.points)):
            if s <= self.cum[i] or i == len(self.points) - 1:
                seg = self.cum[i] - self.cum[i - 1]
                t = 0.0 if seg <= 1e-9 else (s - self.cum[i - 1]) / seg
                ax, az = self.points[i - 1]
                bx, bz = self.points[i]
                return (ax + (bx - ax) * t, az + (bz - az) * t)
        return self.points[-1]


@dataclass
class Geometry:
    """The map, as parameters (roadmap R10 swaps in spline lengths here)."""
    routes: dict = field(default_factory=lambda: {
        0: Route("Route_West", ROUTE_WEST),
        1: Route("Route_North", ROUTE_NORTH),
    })
    air_spawn: tuple = AIR_SPAWN
    air_target: tuple = AIR_TARGET
    pads: dict = field(default_factory=lambda: dict(PADS))

    #: Deal ground groups out across every ground route instead of obeying the
    #: spawner index the wave table names (roadmap R40). Siege maps have up to
    #: five approaches while the tables address two, so the game rotates groups
    #: across them — and the model has to rotate identically or it is scoring a
    #: map nobody plays. Off for the shipped map and for corridor synthesis.
    spread_ground_groups: bool = False

    #: Per-pad high-ground damage bonus (M-b terrain), pad name → fraction
    #: (0.05 = +5%). Written by the generator's terrain stage from the pad's
    #: height over the nearby lane; composes ADDITIVELY with the aura damage
    #: bonus because the live TowerWeapon folds both into one (1 + Σbonus)
    #: factor. Empty on flat maps ⇒ every dps figure is bit-identical to the
    #: pre-terrain model.
    pad_hg: dict = field(default_factory=dict)

    def ground_indices(self) -> list:
        """Ground spawner indices, ascending — the rotation order."""
        return sorted(self.routes)

    def air_length(self) -> float:
        return math.hypot(self.air_target[0] - self.air_spawn[0],
                          self.air_target[1] - self.air_spawn[1])

    def apply_measured_lengths(self, lengths: dict):
        """
        Adopt measured route lengths (roadmap R10) — e.g. the spline lengths
        filed by R9's gate — keyed by spawner index. The air corridor is a
        straight flight and is unaffected by the spline work, so it is not
        scalable here by design.
        """
        for spawner, measured in lengths.items():
            route = self.routes[spawner]
            if measured and measured > 0.001:
                route.scale = measured / route.polyline_length


def covered_intervals(route: Route, px: float, pz: float,
                      reach: float, min_reach: float):
    """Arc-length intervals of `route` horizontally within (min_reach, reach]
    of the pad. Sampled at COVERAGE_SAMPLE_STEP_M."""
    step = COVERAGE_SAMPLE_STEP_M
    n = max(2, int(route.polyline_length / step))
    intervals = []
    start = None
    for i in range(n + 1):
        s = route.polyline_length * i / n
        x, z = route.sample(s)
        d = math.hypot(x - px, z - pz)
        inside = (d <= reach) and (d >= min_reach)
        if inside and start is None:
            start = s
        elif not inside and start is not None:
            intervals.append((start, s))
            start = None
    if start is not None:
        intervals.append((start, route.polyline_length))
    return intervals


def air_covered_length(geom: Geometry, px: float, pz: float,
                       range3d: float, altitude: float) -> float:
    """Length of the level flight segment within 3D range of the pad."""
    reach_sq = range3d * range3d - altitude * altitude
    if reach_sq <= 0.0:
        return 0.0
    reach = math.sqrt(reach_sq)
    ax, az = geom.air_spawn
    bx, bz = geom.air_target
    length = math.hypot(bx - ax, bz - az)
    step = COVERAGE_SAMPLE_STEP_M
    n = max(2, int(length / step))
    covered = 0.0
    for i in range(n):
        t = (i + 0.5) / n
        x = ax + (bx - ax) * t
        z = az + (bz - az) * t
        if math.hypot(x - px, z - pz) <= reach:
            covered += length / n
    return covered


def enemy_speed_at(enemy: dict, s: float, route_len: float) -> float:
    """Ground speed at arc position s, honouring the Roller phase change and
    the Colossus enrage (assumed at ENRAGE_ASSUMED_PATH_FRACTION)."""
    if "phase_at" in enemy and s >= enemy["phase_at"] * route_len:
        return enemy["phase_speed"]
    if "enrage_mult" in enemy and s >= ENRAGE_ASSUMED_PATH_FRACTION * route_len:
        return enemy["speed"] * enemy["enrage_mult"]
    return enemy["speed"]


def time_in_intervals(enemy: dict, route: Route, intervals) -> float:
    """
    Seconds one enemy spends inside the covered intervals (free-flow). Intervals
    are in polyline space; the result is scaled by the route's measured-length
    factor, which is where a longer spline earns its extra time-in-range (R10).
    """
    total = 0.0
    for a, b in intervals:
        steps = max(1, int((b - a) / 2.0))
        for k in range(steps):
            s0 = a + (b - a) * k / steps
            s1 = a + (b - a) * (k + 1) / steps
            mid = 0.5 * (s0 + s1)
            total += (s1 - s0) / enemy_speed_at(enemy, mid, route.polyline_length)
    return total * route.scale


def traverse_time(enemy: dict, route: Route) -> float:
    return time_in_intervals(enemy, route, [(0.0, route.polyline_length)])


# =============================================================================
#  Build simulation
# =============================================================================

@dataclass
class TowerInstance:
    pad: str
    tower_id: str
    tier: int = 0        # index into tiers (0 = tier 1)
    hp_lost: float = 0.0  # return fire soaked so far — nothing repairs it


_value_cache: dict = {}


def tier_value(geom: Geometry, pad: str, tower_id: str, tier_idx: int) -> float:
    """Throughput proxy for a (pad, tower, tier): DPS x covered route metres
    (ground, worst-case per route de-duplicated by using the west route, which
    shares the snake) + DPS x covered air metres. This is what makes the sim
    coverage-aware: a tier whose bigger gun loses more ring than it gains is
    scored lower and not bought."""
    key = (pad, tower_id, tier_idx)
    if key in _value_cache:
        return _value_cache[key]
    px, pz = geom.pads[pad][0], geom.pads[pad][1]
    tower = TOWERS[tower_id]
    tier = tower["tiers"][tier_idx]
    ground = sum(b - a for a, b in covered_intervals(
        geom.routes[0], px, pz, tier["range"], tier["min_range"]))
    air = air_covered_length(geom, px, pz, tier["range"], 4.0) if tower["air"] else 0.0
    value = tier["dps"] * (ground + air)
    _value_cache[key] = value
    return value


def run_build_phase(geom: Geometry, salvage: int, built: dict) -> int:
    """Deterministic greedy spend. 1) Build unbuilt pads (intended turret,
    BUILD_PRIORITY order). 2) Buy the upgrade with the best value gain per
    salvage; a two-step lookahead lets the sim cross a weak middle tier when
    the top tier justifies the combined cost. Upgrades that lose throughput
    (see LIVE-DATA OBSERVATIONS on tier-1 ranges) are never bought."""
    progressed = True
    while progressed:
        progressed = False

        # 1) unbuilt pads first
        for pad in active_build_priority():
            if pad in built:
                continue
            tower_id = geom.pads[pad][2]
            cost = TOWERS[tower_id]["tiers"][0]["cost"]
            if salvage - cost >= SALVAGE_RESERVE:
                built[pad] = TowerInstance(pad, tower_id, 0)
                salvage -= cost
                progressed = True
                break
        if progressed:
            continue
        if any(pad not in built for pad in active_build_priority()):
            break  # a pad is unaffordable; bank for it rather than upgrading

        # 2) best-value upgrade (with two-step lookahead)
        best = None  # (gain_per_salvage, pad, target_tier, cost)
        for pad in active_build_priority():
            inst = built[pad]
            tiers = TOWERS[inst.tower_id]["tiers"]
            cur_value = tier_value(geom, pad, inst.tower_id, inst.tier)
            for target in (inst.tier + 1, inst.tier + 2):
                if target >= len(tiers):
                    continue
                cost = sum(tiers[k]["cost"] for k in range(inst.tier + 1, target + 1))
                if salvage - cost < SALVAGE_RESERVE:
                    continue
                value = tier_value(geom, pad, inst.tower_id, target)
                if value < cur_value * UPGRADE_MIN_VALUE_GAIN:
                    continue
                gain = (value - cur_value) / cost
                if best is None or gain > best[0]:
                    best = (gain, pad, target, cost)
        if best is not None:
            _, pad, target, cost = best
            built[pad].tier = target
            salvage -= cost
            progressed = True
    return salvage


def aura_bonuses(geom: Geometry, built: dict, pad_name: str):
    """Strongest-per-axis Scan Relay aura folding (SupportAura doctrine).
    The baseline build never places a relay; implemented for completeness."""
    fire = rng = dmg = 0.0
    px, pz = geom.pads[pad_name][0], geom.pads[pad_name][1]
    for inst in built.values():
        if inst.tower_id != "scan_relay" or inst.pad == pad_name:
            continue
        tier = TOWERS["scan_relay"]["tiers"][inst.tier]
        radius = tier.get("aura_radius", 0.0)
        rx, rz = geom.pads[inst.pad][0], geom.pads[inst.pad][1]
        if math.hypot(px - rx, pz - rz) <= radius:
            fire = max(fire, tier.get("aura_fire", 0.0))
            rng = max(rng, tier.get("aura_range", 0.0))
            dmg = max(dmg, tier.get("aura_dmg", 0.0))
    return fire, rng, dmg


# =============================================================================
#  Wave margin computation
# =============================================================================

def wave_scalar(wave_number: int) -> float:
    return 1.0 + ACTIVE["hp_growth"] * (wave_number - 1)


def compute_wave(geom: Geometry, built: dict, wave_number: int,
                 wave: dict, difficulty: str):
    hp_mult = DIFFICULTY_HP_MULT[difficulty]
    scalar = wave_scalar(wave_number)
    mutators = r22_mutators(wave, wave_number)
    oc_hp = MUTATOR_OC_HP if "overcharge" in mutators else 1.0
    storm = MUTATOR_STORM_AIR_SPEED if "storm" in mutators else 1.0
    convoy = "convoy" in mutators
    blackout_rng = MUTATOR_BLACKOUT_RANGE if "blackout" in mutators else 1.0

    groups = []
    wave_duration = 0.0
    ground_ordinal = 0
    for enemy_id, count, gap, offset, spawner in wave["groups"]:
        enemy = ENEMIES[enemy_id]
        if enemy["air"]:
            route = None
            # Storm (R20/R22): air flies faster — shorter traverse AND, below,
            # shorter time under every covering tower.
            traverse = geom.air_length() / (enemy["speed"] * storm)
        else:
            if geom.spread_ground_groups:
                # Mirrors WaveManager.StartWaveGroups exactly, rotation included.
                # Two implementations of one rule is the drift this file exists
                # to prevent, so if one of them changes, change both.
                indices = geom.ground_indices()
                spawner = indices[(ground_ordinal + wave_number) % len(indices)]
            if convoy:
                # Convoy (R20/R22): every ground group funnels onto the primary
                # ground route, mirroring WaveManager's spawner collapse.
                spawner = min(geom.routes)
            ground_ordinal += 1
            # A wave may reference a ground spawner the map does not have (a
            # 1-leg generated map running the shipped wave table): those groups
            # walk the primary route instead, mirroring a single-entrance map.
            route = geom.routes.get(spawner) or geom.routes[min(geom.routes)]
            traverse = traverse_time(enemy, route)
        eff_hp = enemy["hp"] * scalar * hp_mult * count * oc_hp
        groups.append(dict(id=enemy_id, enemy=enemy, count=count, gap=gap,
                           offset=offset, route=route, traverse=traverse,
                           eff_hp=eff_hp, delivered=0.0, exp_s=0.0,
                           lane=None if enemy["air"] else spawner,
                           air_speed_mult=storm if enemy["air"] else 1.0))
        wave_duration = max(wave_duration, offset + max(0, count - 1) * gap + traverse)

    # Deliverable damage, pad by pad. Each pad has a continuous-fire budget of
    # the wave duration; when the raw exposures across groups exceed it they
    # are scaled down proportionally (a pad shoots one thing at a time).
    #
    # Delivery and return fire are COUPLED — a wave the towers shred barely
    # gets a shot off, a wave that lingers chews the towers down — so this
    # runs in two passes: pass 1 at full uptime measures how fast each group
    # dies, that discounts the group's return fire, the discounted fire sets
    # each pad's uptime, and pass 2 books what the standing fraction lands.
    # The geometry work (coverage intervals) is cached from pass 1.
    vet = r22_veterancy_mult(wave_number)
    plan = []   # (inst, tower, tier, dps, exposures[], scale)
    for inst in built.values():
        tower = TOWERS[inst.tower_id]
        tier = tower["tiers"][inst.tier]
        a_fire, a_range, a_dmg = aura_bonuses(geom, built, inst.pad)
        rng = tier["range"] * (1.0 + a_range) * blackout_rng
        # Veterancy (R21/R22): the fleet-average damage ramp rides the same
        # multiplier stack the live TowerWeapon uses ((1+aura)×(1+rank·4%)).
        # High ground (M-b) joins the damage factor ADDITIVELY, mirroring the
        # weapon's single (1 + aura_dmg + hg) funnel — a separate (1+hg)
        # multiplier would over-credit the defender whenever both are nonzero.
        dps = (tier["dps"] * (1.0 + a_fire)
               * (1.0 + a_dmg + geom.pad_hg.get(inst.pad, 0.0)) * vet)
        if dps <= 0.0:
            continue
        px, pz = geom.pads[inst.pad][0], geom.pads[inst.pad][1]

        exposures = []
        for g in groups:
            enemy = g["enemy"]
            if enemy["air"] and not tower["air"]:
                exposures.append(0.0)
                continue
            if enemy["air"]:
                covered = air_covered_length(geom, px, pz, rng, enemy["altitude"])
                t_per = covered / (enemy["speed"] * g["air_speed_mult"])
                dwell = 1.0
            else:
                intervals = covered_intervals(g["route"], px, pz, rng,
                                              tier["min_range"])
                t_per = time_in_intervals(enemy, g["route"], intervals)
                dwell = QUEUE_DWELL_FACTOR
            if t_per <= 0.0:
                exposures.append(0.0)
                continue
            window = max(0, g["count"] - 1) * g["gap"] + t_per * dwell
            exposures.append(min(g["count"] * t_per * dwell, window))

        total_exposure = sum(exposures)
        scale = 1.0 if total_exposure <= wave_duration else wave_duration / total_exposure
        plan.append((inst, tower, tier, dps, exposures, scale))

    def deliver(pad_uptime_map):
        for g in groups:
            g["delivered"] = 0.0
            g["exp_s"] = 0.0
        for inst, tower, tier, dps, exposures, scale in plan:
            up = pad_uptime_map.get(inst.pad, 1.0) if pad_uptime_map else 1.0
            for g, exposure in zip(groups, exposures):
                if exposure <= 0.0:
                    continue
                enemy = g["enemy"]
                mult = DAMAGE_MULT[tower["type"]][enemy["armour"]]
                per_enemy_hp = g["eff_hp"] / g["count"]
                focus = FOCUS_HEAVY if per_enemy_hp >= HEAVY_HP else FOCUS_SWARM
                factor = 1.0
                if tier["chain"] >= 2:
                    potential = sum(tier["falloff"] ** k for k in range(tier["chain"]))
                    uptime = (CHAIN_UPTIME_PACK if g["count"] >= SPLASH_PACK_MIN
                              else CHAIN_UPTIME_SPARSE)
                    factor *= 1.0 + (potential - 1.0) * uptime
                if tier["splash"] > 0.0 and g["count"] >= SPLASH_PACK_MIN:
                    factor *= 1.0 + SPLASH_PACK_BONUS_PER_M * tier["splash"]
                g["delivered"] += dps * mult * factor * exposure * scale * focus * up
                g["exp_s"] += exposure * scale * up

    deliver(None)   # pass 1: full uptime — how the wave WOULD be shredded

    # Tower loss (return fire): units fire only while ALIVE, so a group's
    # return fire is discounted by its expected alive fraction — a group the
    # towers out-deliver m-fold lives ~1/m of its walk, floored because
    # enemies out-range most turret rings and fire on approach before any
    # tower answers. A pad that soaks less than its remaining HP keeps 100%
    # uptime and CARRIES the damage (nothing repairs); a pad that soaks it
    # all dies partway and delivers only remaining/incoming of its plan.
    pad_uptime = {}
    pad_damage = {}
    towers_lost = []
    group_soak = [0.0] * len(groups)
    if TOWER_LOSS["on"] and built:
        survival = [1.0 if g["eff_hp"] <= 0.0 or g["delivered"] <= g["eff_hp"]
                    else max(TOWER_LOSS_SURVIVAL_FLOOR, g["eff_hp"] / g["delivered"])
                    for g in groups]
        incoming, group_soak = tower_incoming(geom, built, groups, survival)
        for pad, inst in built.items():
            soak = incoming.get(pad, 0.0)
            if soak <= 0.0:
                pad_uptime[pad] = 1.0
                continue
            remaining = max(0.0, TOWERS[inst.tower_id].get("hp", TOWER_HP_DEFAULT)
                            - inst.hp_lost)
            if soak < remaining:
                pad_uptime[pad] = 1.0
                pad_damage[pad] = soak
            else:
                pad_uptime[pad] = remaining / soak if soak > 0.0 else 0.0
                towers_lost.append(pad)
        # Pass 2 only when something actually went down — otherwise pass 1
        # already IS the answer, bit for bit.
        if towers_lost:
            deliver(pad_uptime)

    # Strike Wing (R19/R22): each use buys extra enemy-seconds of engagement,
    # credited to the WORST group at that group's OBSERVED delivery rate —
    # a strike over an uncovered group credits nothing, because stunning an
    # enemy no tower can shoot kills nothing. The salvage cost lands on this
    # wave's income in run_model.
    strike_uses = r22_strike_uses(wave, wave_number)
    if strike_uses > 0 and groups:
        target = min(groups, key=lambda g: (g["delivered"] / g["eff_hp"])
                     if g["eff_hp"] > 0 else float("inf"))
        if target["exp_s"] > 0.0:
            rate = target["delivered"] / target["exp_s"]
            target["delivered"] += rate * STRIKE_ENEMY_SECONDS * strike_uses

    required = sum(g["eff_hp"] for g in groups)
    deliverable = sum(min(g["delivered"], g["eff_hp"] * OVERKILL_CAP)
                      for g in groups)
    margin = deliverable / required if required > 0 else float("inf")
    worst = min(groups, key=lambda g: (g["delivered"] / g["eff_hp"])
                if g["eff_hp"] > 0 else float("inf"))
    worst_margin = worst["delivered"] / worst["eff_hp"] if worst["eff_hp"] > 0 else float("inf")

    # Peak concurrent enemies (event sweep, capped by the live-enemy ceiling).
    events = []
    for g in groups:
        for k in range(g["count"]):
            t0 = g["offset"] + k * g["gap"]
            events.append((t0, 1))
            events.append((t0 + g["traverse"], -1))
    events.sort()
    live = peak = 0
    for _, delta in events:
        live += delta
        peak = max(peak, live)
    peak = min(peak, ACTIVE["max_live"])

    return dict(margin=margin, required=required, deliverable=deliverable,
                worst_group=worst["id"], worst_margin=worst_margin,
                peak_live=peak, duration=wave_duration,
                strike_uses=strike_uses, towers_lost=towers_lost,
                _pad_damage=pad_damage,
                # Light per-group summary for the advice/tune machinery —
                # stripped from rows before any JSON dump (see main).
                _group_stats=[dict(id=g["id"], count=g["count"], eff_hp=g["eff_hp"],
                                   delivered=g["delivered"], air=g["enemy"]["air"],
                                   lane=g["lane"]) for g in groups],
                _group_soak=group_soak)


def wave_income(wave: dict, wave_number: int, difficulty: str) -> int:
    eco = DIFFICULTY_ECO_MULT[difficulty]
    bounties = sum(ENEMIES[eid]["bounty"] * count
                   for eid, count, _, _, _ in wave["groups"])
    if R22["on"]:
        # Streak income (R2/R22): kill payouts escalate with streaks; dense
        # waves chain them reliably. Bounty component only — clears are flat.
        streak = STREAK_INCOME_DENSE if r22_dense(wave) else STREAK_INCOME_SPARSE
        bounties *= 1.0 + streak
        if "overcharge" in r22_mutators(wave, wave_number):
            bounties *= MUTATOR_OC_BOUNTY
    clear = wave["clear"] if wave["clear"] > 0 else 60 + 18 * wave_number
    return round(bounties * eco) + round(clear * eco)


# =============================================================================
#  Advice & counts-only tune (the gate's actionable half)
# =============================================================================

def advise_wave(wave_number: int, flags: list, result: dict) -> list:
    """
    First-order fix suggestions for a flagged wave — starting points, not
    solutions: every edit shifts exposure and economy second-order, so the
    loop is always edit → re-run. These lines ride the JSON rows into both
    editor tools' messaging panes; the numbers exist only here, per R1.
    """
    if not flags:
        return []
    stats = result["_group_stats"]
    soak = result["_group_soak"]
    margin = result["margin"]
    required = result["required"]
    deliverable = result["deliverable"]
    advice = []

    def cut_or_hp(g, excess):
        """'cut Z N→M or lower its baseHealth ~P%' for removing `excess` eff HP."""
        per = g["eff_hp"] / g["count"]
        k = math.ceil(excess / per)
        if k < g["count"]:
            pct = min(90, math.ceil(excess / g["eff_hp"] * 100))
            base = ENEMIES[g["id"]]["hp"]
            return (f"cut {g['id']} {g['count']}→{g['count'] - k}, or lower its "
                    f"baseHealth ~{pct}% ({base:g}→{base * (100 - pct) / 100:.0f})")
        return (f"even removing the whole {g['count']}×{g['id']} group (−{g['eff_hp']:.0f} "
                "eff HP) does not close it — remove it, re-run, and fix what remains")

    starved = [g for g in stats
               if g["eff_hp"] > 0 and g["delivered"] / g["eff_hp"] < GROUP_MIN]
    if any(f.startswith("GROUP-STARVED") for f in flags):
        for g in starved:
            q = g["delivered"] / g["eff_hp"]
            where = ("the air corridor — anti-air coverage does not reach enough of it"
                     if g["air"] else
                     f"spawner {g['lane']}'s lane — its route is under-covered")
            need = max(0.0, g["eff_hp"] - g["delivered"] / GROUP_MIN)
            k = math.ceil(need / (g["eff_hp"] / g["count"]))
            advice.append(f"{g['count']}×{g['id']} only gets {q:.2f}× of its HP shot: coverage "
                          f"problem on {where}. Re-pad/reseed for coverage, or cut "
                          f"{g['id']} {g['count']}→{max(0, g['count'] - k)}")

    if "LOW" in flags:
        excess = required - deliverable
        covered = [g for g in stats
                   if g["eff_hp"] > 0 and g["delivered"] / g["eff_hp"] >= GROUP_MIN] or \
                  [g for g in stats if g["eff_hp"] > 0]
        if covered:
            g = max(covered, key=lambda s: s["eff_hp"])
            advice.append(f"margin {margin:.2f} < {BAND_MIN:.2f} — shortfall "
                          f"{excess:.0f} eff HP: " + cut_or_hp(g, excess))
        if wave_number == 1:
            advice.append("wave 1 meets the opening bank (~2 turrets built) — it has to "
                          "stay light; heavies belong from wave 3 on")

    if result.get("towers_lost") and TOWER_LOSS["on"] and soak and max(soak) > 0:
        gi = max(range(len(soak)), key=lambda i: soak[i])
        g = stats[gi]
        advice.append(f"return fire killed {','.join(result['towers_lost'])} — "
                      f"{soak[gi]:.0f} of the pad damage is from {g['id']}: lower its "
                      "weapon damage/fireRate on the prefab, field fewer, or raise TowerHealth")

    if "HIGH-CLOSE" in flags or "HIGH-MID" in flags:
        cap = BAND_CLOSE_MAX if wave_number == CLOSE_WAVE else BAND_MID_MAX
        room = deliverable / cap - required
        pool = [g for g in stats if g["eff_hp"] > 0]
        if pool and room > 0:
            g = max(pool, key=lambda s: s["eff_hp"])
            per = g["eff_hp"] / g["count"]
            k = max(1, int(room / per))
            advice.append(f"margin {margin:.2f} above the {cap:.2f} cap — room for "
                          f"~{room:.0f} eff HP: add ~{k}×{g['id']}, or raise its baseHealth")

    return advice


# =============================================================================
#  Report
# =============================================================================

def load_geometry(path: str) -> Geometry:
    """
    A GENERATED map's geometry (roadmap R30), written by the Unity pipeline:
    routes as knot polylines with their measured spline lengths, the air
    corridor, the pad set, and the build priority for the sim (the shipped
    BUILD_PRIORITY names mean nothing on a generated map). One model, two
    geometries — the margin math itself is untouched, which is the point.
    """
    with open(path) as f:
        data = json.load(f)

    geom = Geometry(
        routes={int(r["spawner"]): Route(r["name"], [tuple(p) for p in r["points"]])
                for r in data["routes"]},
        air_spawn=tuple(data["air_spawn"]),
        air_target=tuple(data["air_target"]),
        pads={p["name"]: (float(p["x"]), float(p["z"]), p["tower"], p.get("cls", "?"))
              for p in data["pads"]},
        spread_ground_groups=bool(data.get("spread_ground_groups", False)),
        # High ground (M-b): absent key ⇒ 0 ⇒ dps math identical to pre-terrain.
        pad_hg={p["name"]: float(p["hg"]) for p in data["pads"] if p.get("hg")},
    )
    geom.apply_measured_lengths(
        {int(r["spawner"]): float(r["measured_length"]) for r in data["routes"]
         if r.get("measured_length")})

    priority = data.get("build_priority")
    if not priority:
        order = {"Premium": 0, "Standard": 1, "Rear": 2, "Overwatch": 3}
        priority = sorted(geom.pads, key=lambda n: (order.get(geom.pads[n][3], 9), n))
    ACTIVE["build_priority"] = priority
    return geom


def run_model(difficulty: str, measured_lengths: dict = None, polyline: bool = False,
              geometry: Geometry = None):
    if geometry is not None:
        geom = geometry
        _value_cache.clear()      # positions differ from any earlier geometry
    else:
        geom = Geometry()
        # Sanity: the live map (fails loudly if the embedded data drifts).
        assert len(geom.pads) == 8, "expected the 8 shipped hardpoints"
        assert len(WAVES) == 10, "expected the 10 shipped waves"
        for r in geom.routes.values():
            assert 145.0 <= r.polyline_length <= 155.0, \
                f"{r.name} polyline {r.polyline_length:.1f} m off the ~150 m live map"

        # Spline geometry is the standing baseline (R10); --polyline recovers the
        # pre-spline map, and an explicit override wins over both.
        lengths = None if polyline else (measured_lengths or SPLINE_ROUTE_LENGTHS)
        if lengths:
            geom.apply_measured_lengths(lengths)

    built: dict = {}
    if ACTIVE.get("starting_salvage") is not None:
        salvage = int(ACTIVE["starting_salvage"])
    else:
        salvage = round(STARTING_SALVAGE * DIFFICULTY_ECO_MULT[difficulty])
    rows = []
    build_log = []
    for i, wave in enumerate(WAVES):
        wave_number = i + 1
        before = {p: (built[p].tower_id, built[p].tier) for p in built}
        salvage = run_build_phase(geom, salvage, built)
        after = {p: (built[p].tower_id, built[p].tier) for p in built}
        changes = [f"{p}:{after[p][0]}@T{after[p][1] + 1}"
                   for p in after if before.get(p) != after[p]]
        build_log.append(changes)

        result = compute_wave(geom, built, wave_number, wave, difficulty)
        income = wave_income(wave, wave_number, difficulty)
        # Strike Wing cost (R22): a use is salvage the build phase never sees.
        income -= STRIKE_COST * result["strike_uses"]

        # Tower loss: bank the soak on survivors, remove the dead. The next
        # build phase re-buys a dead pad from tier 1 ("unbuilt pads first"),
        # which is exactly the salvage the player loses in play.
        for pad, dmg in result.pop("_pad_damage").items():
            built[pad].hp_lost += dmg
        for pad in result["towers_lost"]:
            built.pop(pad, None)

        # NOTE: flags is the GATE verdict (exit code + in_band in --json) —
        # informational markers like Strike Wing usage must never enter it.
        flags = []
        if result["margin"] < BAND_MIN:
            flags.append("LOW")
        if wave_number == CLOSE_WAVE and result["margin"] > BAND_CLOSE_MAX:
            flags.append("HIGH-CLOSE")
        if wave_number < CLOSE_WAVE and result["margin"] > BAND_MID_MAX:
            flags.append("HIGH-MID")
        if result["worst_margin"] < GROUP_MIN:
            flags.append(f"GROUP-STARVED({result['worst_group']})")

        result["advice"] = advise_wave(wave_number, flags, result)

        rows.append(dict(wave=wave_number, salvage_before=salvage,
                         income=income, flags=flags, **result))
        salvage += income

    return geom, rows, build_log


def suggest_fix(difficulty: str, measured_lengths=None, polyline=False,
                geometry: Geometry = None):
    """
    Counts-only tune toward the band — the gate's actionable half, computed
    HERE because only the model can iterate its own verdict (R1: one brain).
    Greedy and deterministic: while any wave sits under band, trim the guilty
    group of the worst one by the same chunk arithmetic the advice prints,
    re-running the full model each step so build economy, coverage, return
    fire and the following waves all react; then feed the over-cap waves the
    same way. Counts are the only knob touched — gaps, offsets, mutators and
    enemy stats are design, counts are load.

    Returns (changes, in_band, note): changes as dicts
    {wave, group, enemy, prev, next} against the CURRENT tables (next 0 =
    drop the group). WAVES is restored before returning — the tune is a
    suggestion, never a silent rewrite of this run's own verdict.
    """
    original = [dict(w, groups=list(w["groups"])) for w in WAVES]
    runs = 0
    note = ""

    def evaluate():
        nonlocal runs
        runs += 1
        _, rows, _ = run_model(difficulty, measured_lengths, polyline, geometry)
        return rows

    def set_count(wi, gi, count):
        eid, _, gap, off, sp = WAVES[wi]["groups"][gi]
        WAVES[wi]["groups"][gi] = (eid, count, gap, off, sp)

    try:
        rows = evaluate()

        # Phase 1 — cuts, until nothing is under band. Converges: every step
        # removes load from the worst cuttable wave, and a wave whose only
        # remaining move would empty it entirely is frozen instead — an empty
        # wave is not a fix, it is dead air with a free clear bonus.
        uncuttable: set = set()
        while runs < MAX_FIX_RUNS:
            under = [r for r in rows
                     if r["wave"] not in uncuttable
                     and any(f == "LOW" or f.startswith("GROUP-STARVED")
                             for f in r["flags"])]
            if not under:
                break
            r = min(under, key=lambda x: x["margin"])
            wi = r["wave"] - 1
            stats = r["_group_stats"]
            starved = [i for i, g in enumerate(stats)
                       if g["count"] > 0 and g["eff_hp"] > 0
                       and g["delivered"] / g["eff_hp"] < GROUP_MIN]
            live = [i for i, g in enumerate(stats) if g["count"] > 0 and g["eff_hp"] > 0]
            if starved:
                gi = max(starved, key=lambda i: stats[i]["eff_hp"])
                g = stats[gi]
                need = max(0.0, g["eff_hp"] - g["delivered"] / GROUP_MIN)
            else:
                if not live:
                    uncuttable.add(r["wave"])
                    continue
                gi = max(live, key=lambda i: stats[i]["eff_hp"])
                g = stats[gi]
                need = r["required"] - r["deliverable"]
            per = g["eff_hp"] / g["count"]
            k = max(1, min(g["count"], math.ceil(need / per)))
            if len(live) == 1 and live[0] == gi:
                k = min(k, g["count"] - 1)   # never empty the whole wave
                if k <= 0:
                    uncuttable.add(r["wave"])   # one unit left and still failing:
                    continue                    # counts can't fix this wave
            set_count(wi, gi, g["count"] - k)
            rows = evaluate()

        # Phase 2 — adds for over-cap waves. Adding load also ADDS delivery
        # (longer engagement windows, more income), so the cap cannot be chased
        # arithmetically: each wave gets two conservative attempts, capped at
        # +100% of the group per step; an attempt that breaks any wave under
        # band is reverted and the wave is left to the ADVICE lines. A harder-
        # but-still-over result is kept — strictly closer to the cap.
        attempts: dict = {}
        while runs < MAX_FIX_RUNS:
            over = [r for r in rows
                    if attempts.get(r["wave"], 0) < 2 and r["required"] > 0
                    and ("HIGH-CLOSE" in r["flags"] or "HIGH-MID" in r["flags"])]
            if not over:
                break
            r = max(over, key=lambda x: x["margin"])
            wave_no = r["wave"]
            attempts[wave_no] = attempts.get(wave_no, 0) + 1
            wi = wave_no - 1
            stats = r["_group_stats"]
            cap = BAND_CLOSE_MAX if wave_no == CLOSE_WAVE else BAND_MID_MAX
            live = [i for i, g in enumerate(stats) if g["count"] > 0 and g["eff_hp"] > 0]
            if not live:
                attempts[wave_no] = 99
                continue
            gi = max(live, key=lambda i: stats[i]["eff_hp"])
            g = stats[gi]
            per = g["eff_hp"] / g["count"]
            k = max(1, min(g["count"],
                           int(0.5 * (r["deliverable"] / cap - r["required"]) / per)))
            before = g["count"]
            set_count(wi, gi, before + k)
            new_rows = evaluate()
            if any(f == "LOW" or f.startswith("GROUP-STARVED")
                   for row in new_rows for f in row["flags"]):
                set_count(wi, gi, before)   # the add broke a wave — take it back
                rows = evaluate()
                attempts[wave_no] = 99
            else:
                rows = new_rows

        in_band = not any(row["flags"] for row in rows)
        if not in_band and not note:
            remaining = ", ".join(f"wave {row['wave']} [{','.join(row['flags'])}]"
                                  for row in rows if row["flags"])
            note = (f"still flagged after tuning: {remaining} — needs a non-count fix "
                    "(coverage, stats, geometry, or re-authoring the wave); see ADVICE")

        changes = []
        for wi, w in enumerate(original):
            for gi, (eid, prev, _, _, _) in enumerate(w["groups"]):
                cur = WAVES[wi]["groups"][gi][1]
                if cur != prev:
                    changes.append(dict(wave=wi + 1, group=gi, enemy=eid,
                                        prev=prev, next=cur))
        return changes, in_band, note
    finally:
        WAVES[:] = original


def format_report(difficulty: str, geom: Geometry, rows, build_log) -> str:
    out = []
    w = out.append
    w("COREHOLD balance model — baseline report")
    w(f"difficulty={difficulty}  focus={FOCUS_SWARM}/{FOCUS_HEAVY}(heavy)  "
      f"dwell={QUEUE_DWELL_FACTOR}  band=[>={BAND_MIN:.2f} all, "
      f"close<={BAND_CLOSE_MAX:.2f} @w{CLOSE_WAVE}, mid<={BAND_MID_MAX:.2f}]")
    scaled = any(abs(r.scale - 1.0) > 1e-9 for r in geom.routes.values())
    route_bits = ", ".join(f"{geom.routes[k].name} {geom.routes[k].length:.3f} m"
                           for k in sorted(geom.routes))
    w(f"geometry: {route_bits}, "
      f"air corridor {geom.air_length():.2f} m, {len(geom.pads)} pads"
      + ("  [spline geometry — the adopted baseline, R10]" if scaled
         else "  [pre-spline polyline — comparison only]"))
    w("")
    w(f"{'wv':>2} {'requiredHP':>10} {'deliverable':>11} {'margin':>6} "
      f"{'worst-group':>16} {'live':>4} {'salv-pre':>8} {'income':>6}  flags / builds")
    for r, changes in zip(rows, build_log):
        worst = f"{r['worst_group']}={r['worst_margin']:.2f}"
        flags = ",".join(r["flags"]) if r["flags"] else "-"
        # Strike Wing usage and tower losses are informational — shown in the
        # flags cell but never stored in r["flags"], which is the gate verdict.
        # (A lost tower already punishes the margin through its downtime and
        # the rebuild spend; flagging it too would double-judge one event.)
        if r.get("towers_lost"):
            flags = f"LOST({','.join(r['towers_lost'])})" + ("," + flags if flags != "-" else "")
        if r.get("strike_uses"):
            flags = f"SW×{r['strike_uses']}" + ("," + flags if flags != "-" else "")
        builds = (" | " + ", ".join(changes)) if changes else ""
        w(f"{r['wave']:>2} {r['required']:>10.0f} {r['deliverable']:>11.0f} "
          f"{r['margin']:>6.2f} {worst:>16} {r['peak_live']:>4} "
          f"{r['salvage_before']:>8} {r['income']:>6}  {flags}{builds}")
    w("")
    flagged = [r for r in rows if r["flags"]]
    if flagged:
        w("FLAGGED WAVES: " + ", ".join(
            f"wave {r['wave']} [{','.join(r['flags'])}]" for r in flagged))
        advised = [r for r in flagged if r.get("advice")]
        if advised:
            w("")
            w("ADVICE (first-order starting points — every edit shifts exposure "
              "and economy, so the loop is: apply one, re-run):")
            for r in advised:
                for a in r["advice"]:
                    w(f"  wave {r['wave']}: {a}")
    else:
        w("All waves in band — baseline healthy (opens ~1.5, mid 1.2-1.5, "
          "closes 1.0-1.2 on the boss; Appendix A's accepted shape).")
    w("")
    w("Assumptions: focus %.2f swarm / %.2f heavy (>=%.0f effHP), queue dwell "
      "x%.2f (ground), splash +%.2f targets/m (packs >= %d), chain uptime "
      "%.2f/%.2f, overkill cap %.2f, enrage from %.0f%% path." % (
          FOCUS_SWARM, FOCUS_HEAVY, HEAVY_HP, QUEUE_DWELL_FACTOR,
          SPLASH_PACK_BONUS_PER_M, SPLASH_PACK_MIN,
          CHAIN_UPTIME_PACK, CHAIN_UPTIME_SPARSE, OVERKILL_CAP,
          ENRAGE_ASSUMED_PATH_FRACTION * 100))
    w("Income assumes full clears, no chain bonuses. Builds happen between "
      "waves; upgrades are coverage-aware (live T1 rings are LARGER than "
      "T2/T3 — see the model header).")
    if TOWER_LOSS["on"]:
        w("Tower loss ON: enemies return fire (per-row gdps/grange) at the "
          "nearest built pad while walking, discounted to each group's alive "
          "fraction (out-delivered m-fold -> ~1/m of the walk, floor %.2f); "
          "%d-HP turrets carry damage across waves (nothing repairs), die at 0 "
          "(LOST(pad) markers) and cost a fresh tier-1 rebuild. "
          "--tower-loss-off reproduces the pre-term report." % (
              TOWER_LOSS_SURVIVAL_FLOOR, int(TOWER_HP_DEFAULT)))
    if R22["on"]:
        w("R22 terms ON: streak income +%.0f%%/+%.0f%% (dense>=%d), veterancy "
          "+%.0f%%/wave from w%d cap +%.0f%%, Strike Wing %s (+%.1f enemy-s to "
          "the worst group, -%d salvage; SW× flags mark uses), mutator flags "
          "honoured (none authored on this table). --r22-off reproduces the "
          "pre-R22 report." % (
              STREAK_INCOME_DENSE * 100, STREAK_INCOME_SPARSE * 100,
              DENSE_WAVE_COUNT, VETERANCY_PER_WAVE * 100, VETERANCY_FROM_WAVE,
              VETERANCY_CAP * 100,
              ("auto" if R22["strike_uses"] is None else f"x{R22['strike_uses']}"),
              STRIKE_ENEMY_SECONDS, STRIKE_COST))
    if difficulty != "normal":
        w("NOTE: sub-1.0 closes are BY DESIGN on Veteran/Nightmare — the model "
          "is conservative, so sub-1.0 means 'requires above-model play' "
          "(GDD Appendix A). The gate difficulty is Normal.")
    return "\n".join(out)


def format_delta_table(difficulty: str, base_rows, new_rows,
                       measured: dict, geom: Geometry) -> str:
    """
    The per-wave margin delta table R10 owes: polyline baseline vs measured
    spline geometry, flagging every wave that moved more than the gate's 0.15.
    """
    out = []
    w = out.append
    w("=== R10 — per-wave margin delta: polyline baseline -> measured spline geometry ===")
    w(f"difficulty={difficulty}")
    for spawner, route in geom.routes.items():
        if spawner in measured:
            delta_pct = (route.length - route.polyline_length) / route.polyline_length
            w(f"  {route.name}: {route.polyline_length:.3f} m -> {route.length:.3f} m "
              f"({delta_pct:+.2%})")
    w(f"  air corridor: {geom.air_length():.3f} m (straight flight — unchanged by splines)")
    w("")
    w(f"{'wv':>2} {'baseline':>9} {'spline':>8} {'delta':>7}  {'band':<12} note")

    movers = []
    for base, new in zip(base_rows, new_rows):
        delta = new["margin"] - base["margin"]
        if abs(delta) > 0.15:
            movers.append((new["wave"], delta))
        band = ",".join(new["flags"]) if new["flags"] else "in band"
        note = ""
        if abs(delta) > 0.15:
            note = "**MOVED >0.15 — explain**"
        elif new["wave"] == CLOSE_WAVE and new["margin"] > BAND_CLOSE_MAX:
            note = "close wave above band — apply +0.01..0.02 wave-HP scalar"
        w(f"{new['wave']:>2} {base['margin']:>9.2f} {new['margin']:>8.2f} "
          f"{delta:>+7.2f}  {band:<12} {note}")

    w("")
    if movers:
        w("WAVES MOVED >0.15: " + ", ".join(f"wave {n} ({d:+.2f})" for n, d in movers))
    else:
        w("No wave margin moved more than 0.15 — the geometry change is absorbed.")

    capped = sum(1 for r in new_rows if r["margin"] >= OVERKILL_CAP - 1e-6)
    if capped:
        w(f"Note: {capped} wave(s) sit at the {OVERKILL_CAP:.2f} overkill cap and CANNOT show "
          f"movement — those waves are over-killed either way, so extra time-in-range")
        w("changes nothing about the outcome. The waves with real headroom are the ones to read.")
    return "\n".join(out)


def parse_measured(spec: str) -> dict:
    """Parse --measured-lengths WEST,NORTH into the spawner-indexed dict."""
    parts = [p.strip() for p in spec.split(",")]
    if len(parts) != 2:
        raise argparse.ArgumentTypeError("expected two lengths: WEST,NORTH")
    return {0: float(parts[0]), 1: float(parts[1])}


def solve_hp_growth(difficulty: str, geometry: Geometry):
    """
    Solve hpGrowthPerWave so the closing wave's margin lands mid-band (~1.10)
    on THIS geometry (roadmap R30). Growth raises every wave's required HP, so
    the close margin is monotonically decreasing in it — a bisection, using the
    same run_model the gate uses, so there is exactly one implementation of the
    margin math anywhere. Returns (growth, rows, build_log); band flags at the
    solved value are the caller's verdict, because some geometries cannot be
    brought into band by growth alone and that must FAIL, not fudge.
    """
    target = 1.10
    lo, hi = 0.02, 0.60

    def close_margin(g):
        ACTIVE["hp_growth"] = g
        _, rows, log = run_model(difficulty, geometry=geometry)
        return rows[CLOSE_WAVE - 1]["margin"], rows, log

    m_lo, rows, build_log = close_margin(lo)
    if m_lo <= target:
        # Even minimal growth closes at/below target — weakest usable growth.
        ACTIVE["hp_growth"] = lo
        return lo, rows, build_log

    for _ in range(24):
        mid = 0.5 * (lo + hi)
        m, rows, build_log = close_margin(mid)
        if m > target:
            lo = mid          # too easy — grow harder
        else:
            hi = mid
    ACTIVE["hp_growth"] = hi
    _, rows, build_log = run_model(difficulty, geometry=geometry)
    return hi, rows, build_log


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="COREHOLD per-wave balance model (R1, R10, R30, R22)")
    ap.add_argument("--difficulty", choices=list(DIFFICULTY_HP_MULT),
                    default="normal")
    ap.add_argument("--measured-lengths", metavar="WEST,NORTH", type=parse_measured,
                    help="override the adopted ground-route lengths in metres — "
                         "the model then reports the per-wave margin delta vs the polyline")
    ap.add_argument("--polyline", action="store_true",
                    help="run the PRE-SPLINE polyline geometry (149.985 m routes) for comparison")
    ap.add_argument("--geometry", metavar="PATH",
                    help="generated-map geometry JSON (R30): routes, air corridor, pads, "
                         "build priority — replaces the embedded live map")
    ap.add_argument("--hp-growth", type=float, metavar="X",
                    help="run with this hpGrowthPerWave instead of the live 0.18")
    ap.add_argument("--solve-hp-growth", action="store_true",
                    help="bisect hpGrowthPerWave so the close wave lands mid-band (R30); "
                         "reported as solved_hp_growth in --json")
    ap.add_argument("--max-live", type=int, metavar="N",
                    help="live-enemy cap for the peak sweep (the generator derives this "
                         "from route capacity, R30)")
    ap.add_argument("--report", metavar="PATH",
                    help="also write the table to this file")
    ap.add_argument("--json", metavar="PATH",
                    help="also dump rows as JSON (for delta tooling and the generator gate)")
    ap.add_argument("--r22-off", action="store_true",
                    help="disable every R22 term (streak, veterancy, Strike Wing, mutators) "
                         "— reproduces the pre-R22 report byte-for-byte")
    ap.add_argument("--strike-uses", type=int, metavar="N",
                    help="flat Strike Wing uses per wave (default: auto — 1 on "
                         "dense-or-boss waves, 0 otherwise)")
    ap.add_argument("--mutate", action="append", default=[], metavar="W:FLAGS",
                    help="force mutator flags onto wave W for a tuning run, e.g. "
                         "--mutate 8:overcharge --mutate 5:storm,convoy (repeatable)")
    ap.add_argument("--starting-salvage", type=int, metavar="N",
                    help="campaign entry bank (A2): ABSOLUTE starting salvage, used as-is "
                         "(no difficulty economy multiplier — the caller already settled it); "
                         "omit for the tunable default")
    ap.add_argument("--waves", metavar="PATH",
                    help="replace the embedded wave tables with THIS LEVEL'S (live certification): "
                         "either a JSON list of waves, each {clear, mutators?, groups:[{enemy,count,"
                         "gap,offset,spawner}]}, or an object {enemies:{id:{hp,armour,speed,bounty,"
                         "leak,air,...}}, waves:[...]} whose enemies block overrides/extends the "
                         "embedded rows with the stats the assets actually carry (what "
                         "WaveTableExporter writes). Omit for the frozen reference tables")
    ap.add_argument("--tower-loss-off", action="store_true",
                    help="disable the return-fire tower-loss term — reproduces the pre-term "
                         "report byte-for-byte")
    args = ap.parse_args(argv)

    if args.waves:
        try:
            with open(args.waves, encoding="utf-8") as f:
                data = json.load(f)
            # Object form carries the level's OWN enemy stats; list form is the
            # legacy waves-only file and leans on the embedded rows.
            enemy_rows = {}
            wave_list = data
            if isinstance(data, dict):
                enemy_rows = data.get("enemies", {})
                wave_list = data["waves"]
            for eid, row in enemy_rows.items():
                parsed_row = dict(hp=float(row["hp"]), armour=int(row["armour"]),
                                  speed=float(row["speed"]), bounty=int(row["bounty"]),
                                  leak=int(row["leak"]), air=bool(row["air"]))
                if parsed_row["hp"] <= 0 or parsed_row["speed"] <= 0:
                    raise ValueError(f"enemy '{eid}': hp and speed must be positive")
                if not 0 <= parsed_row["armour"] <= 2:
                    raise ValueError(f"enemy '{eid}': armour must be 0..2")
                if parsed_row["air"]:
                    parsed_row["altitude"] = float(row["altitude"])
                    if parsed_row["altitude"] <= 0:
                        raise ValueError(f"enemy '{eid}': air unit with no altitude "
                                         "— fix flightAltitude on the definition")
                if float(row.get("phase_speed", 0)) > 0:
                    parsed_row["phase_at"] = float(row.get("phase_at", 0))
                    parsed_row["phase_speed"] = float(row["phase_speed"])
                if float(row.get("enrage_mult", 0)) > 0:
                    parsed_row["enrage_mult"] = float(row["enrage_mult"])
                if float(row.get("gdps", 0)) > 0 and float(row.get("grange", 0)) > 0:
                    parsed_row["gdps"] = float(row["gdps"])
                    parsed_row["grange"] = float(row["grange"])
                # Wholesale replace — the exporter writes complete rows, so a
                # stat edit on the asset lands here whole, never half-merged.
                ENEMIES[eid] = parsed_row

            parsed = []
            for w in wave_list:
                groups = [(g["enemy"], int(g["count"]), float(g["gap"]),
                           float(g["offset"]), int(g["spawner"])) for g in w["groups"]]
                d = dict(clear=int(w.get("clear", 0)), groups=groups)
                muts = {str(m).strip().lower() for m in w.get("mutators", []) if str(m).strip()}
                bad_m = muts - {"storm", "convoy", "overcharge", "blackout"}
                if bad_m:
                    raise ValueError(f"unknown mutator(s): {', '.join(sorted(bad_m))}")
                if muts:
                    d["mutators"] = muts
                parsed.append(d)
        except (OSError, ValueError, KeyError, TypeError) as e:
            print(f"--waves '{args.waves}': {e}", file=sys.stderr)
            return 2

        unknown = {g[0] for w in parsed for g in w["groups"]} - set(ENEMIES)
        if unknown:
            print(f"--waves references enemies missing from ENEMIES and from the file's own "
                  f"enemies block: {', '.join(sorted(unknown))} — export with WaveTableExporter "
                  "(it writes every referenced row) or add the rows.",
                  file=sys.stderr)
            return 2

        # In place, so every module-level reference sees the level's tables.
        WAVES[:] = parsed

    if args.hp_growth is not None:
        ACTIVE["hp_growth"] = args.hp_growth
    if args.max_live is not None:
        ACTIVE["max_live"] = args.max_live

    if args.starting_salvage is not None:
        ACTIVE["starting_salvage"] = args.starting_salvage

    R22["on"] = not args.r22_off
    TOWER_LOSS["on"] = not args.tower_loss_off
    R22["strike_uses"] = args.strike_uses
    for spec in args.mutate:
        try:
            wave_str, flag_str = spec.split(":", 1)
            flags = {f.strip().lower() for f in flag_str.split(",") if f.strip()}
            bad = flags - {"storm", "convoy", "overcharge", "blackout"}
            if bad:
                raise ValueError(f"unknown mutator(s): {', '.join(sorted(bad))}")
            R22["forced_mutators"][int(wave_str)] = flags
        except ValueError as e:
            print(f"--mutate '{spec}': {e} (expected W:flag[,flag])", file=sys.stderr)
            return 2

    geometry = load_geometry(args.geometry) if args.geometry else None

    solved = None
    if args.solve_hp_growth:
        if geometry is None:
            print("--solve-hp-growth requires --geometry (solving against the live map "
                  "would overwrite tuned data)", file=sys.stderr)
            return 2
        solved, rows, build_log = solve_hp_growth(args.difficulty, geometry)
        geom = geometry
    else:
        geom, rows, build_log = run_model(args.difficulty, args.measured_lengths,
                                          args.polyline, geometry)
    report = format_report(args.difficulty, geom, rows, build_log)
    if solved is not None:
        report += f"\n\nSOLVED hpGrowthPerWave = {solved:.4f} (close-wave margin targeted at 1.10)"

    # Flagged run → also compute the counts-only tune, so the gate message can
    # OFFER the fix, not just the diagnosis. Evaluated at this run's final
    # growth (the solver leaves ACTIVE at the solved value).
    fix_changes, fix_in_band, fix_note = None, False, ""
    if any(r["flags"] for r in rows):
        fix_changes, fix_in_band, fix_note = suggest_fix(
            args.difficulty, args.measured_lengths, args.polyline, geometry)
        lines = [f"  wave {c['wave']}: {c['enemy']} {c['prev']}→{c['next']}" +
                 (" (drop the group)" if c["next"] == 0 else "")
                 for c in fix_changes]
        report += "\n\nCOUNTS-ONLY TUNE " + (
            "that passes the gate:" if fix_in_band else "(best effort — did NOT converge):")
        report += "\n" + ("\n".join(lines) if lines
                          else "  (no counts-only change helps — see ADVICE)")
        if fix_note:
            report += f"\n  {fix_note}"
        report += ("\n  Counts only — gaps, offsets, mutators and enemy stats untouched. "
                   "Campaign Builder step 6 can apply this to campaign-owned stages.")

    if args.measured_lengths:
        _, base_rows, _ = run_model(args.difficulty, polyline=True)
        report += "\n\n" + format_delta_table(
            args.difficulty, base_rows, rows, args.measured_lengths, geom)

    print(report)

    if args.report:
        # The baseline file carries all three tiers, like Appendix A's own
        # run()/run(veteran)/run(nightmare) printout. Normal is the gate. Any
        # measured geometry applies to every tier, so the file describes one map.
        sections = [report if d == args.difficulty else
                    format_report(d, *run_model(d, args.measured_lengths, args.polyline))
                    for d in ("normal", "veteran", "nightmare")]
        with open(args.report, "w") as f:
            f.write(("\n\n" + "=" * 78 + "\n\n").join(sections) + "\n")
    if args.json:
        with open(args.json, "w") as f:
            json.dump(dict(difficulty=args.difficulty,
                           hp_growth_used=ACTIVE["hp_growth"],
                           solved=args.solve_hp_growth,
                           solved_hp_growth=solved if solved is not None else -1.0,
                           max_live=ACTIVE["max_live"],
                           in_band=not any(r["flags"] for r in rows),
                           suggested_changes=fix_changes or [],
                           suggested_in_band=fix_in_band,
                           suggested_note=fix_note,
                           # _-prefixed keys are working state, not output.
                           rows=[{k: v for k, v in r.items()
                                  if not k.startswith("_")} for r in rows]),
                      f, indent=1)

    return 1 if any(r["flags"] for r in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
