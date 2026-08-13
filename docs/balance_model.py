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

STARTING_SALVAGE = 300
CORE_INTEGRITY = 20
HP_GROWTH_PER_WAVE = 0.18
CHAIN_BONUS_PER_LIVE_ENEMY = 8   # not assumed in the baseline income
CHAIN_BONUS_CAP = 80
MAX_LIVE_ENEMIES = 14

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
ENEMIES = {
    "scuttler": dict(hp=45,   armour=0, speed=7.5,  bounty=8,   leak=1, air=False),
    "strider":  dict(hp=110,  armour=1, speed=5.0,  bounty=12,  leak=1, air=False),
    "drone":    dict(hp=60,   armour=0, speed=8.0,  bounty=12,  leak=2, air=True, altitude=4.0),
    "wasp":     dict(hp=70,   armour=0, speed=9.0,  bounty=14,  leak=2, air=True, altitude=4.0),
    "lancer":   dict(hp=190,  armour=2, speed=4.6,  bounty=18,  leak=2, air=False),
    "roller":   dict(hp=150,  armour=0, speed=11.0, bounty=20,  leak=2, air=False,
                     phase_at=0.6, phase_speed=4.6),
    "breaker":  dict(hp=420,  armour=1, speed=3.75, bounty=35,  leak=3, air=False),
    "colossus": dict(hp=2800, armour=2, speed=3.0,  bounty=250, leak=20, air=False,
                     enrage_mult=1.4),
}

# ---- Towers (Tower_*.asset): damage type 0=Kinetic 1=Energy 2=Explosive.
#      Tier dicts mirror TowerTier: authored weapons array (or legacy fields
#      when empty), tier-level range/minRange, aura fields for the relay. -----
TOWERS = {
    "autocannon": dict(type=0, air=True, tiers=[
        dict(cost=100, range=20.0, min_range=0.0, dps=10 * 2.0,   chain=0, falloff=0.0, splash=0.0),
        dict(cost=130, range=13.0, min_range=0.0, dps=15 * 2.8,   chain=0, falloff=0.0, splash=0.0),
        dict(cost=200, range=14.0, min_range=0.0, dps=25 * 3.6,   chain=0, falloff=0.0, splash=0.0),
    ]),
    "missile_battery": dict(type=2, air=True, tiers=[
        dict(cost=150, range=13.0, min_range=0.0, dps=45 * 0.6,   chain=0, falloff=0.0, splash=2.5),
        dict(cost=180, range=14.0, min_range=0.0, dps=80 * 0.7,   chain=0, falloff=0.0, splash=3.0),
        dict(cost=270, range=15.0, min_range=0.0, dps=140 * 0.8,  chain=0, falloff=0.0, splash=3.5),
    ]),
    "arc_node": dict(type=1, air=True, tiers=[
        dict(cost=120, range=20.0, min_range=0.0, dps=14 * 1.5,   chain=2, falloff=0.7, splash=0.0),
        dict(cost=140, range=12.0, min_range=0.0, dps=22 * 1.8,   chain=3, falloff=0.7, splash=0.0),
        dict(cost=200, range=14.0, min_range=0.0, dps=34 * 2.2,   chain=4, falloff=0.7, splash=0.0),
    ]),
    "siege_mortar": dict(type=2, air=False, tiers=[
        dict(cost=200, range=20.0, min_range=6.0, dps=90 * 0.35,  chain=0, falloff=0.0, splash=4.0),
        dict(cost=240, range=22.0, min_range=6.0, dps=160 * 0.4,  chain=0, falloff=0.0, splash=4.5),
        dict(cost=300, range=24.0, min_range=6.0, dps=260 * 0.45, chain=0, falloff=0.0, splash=5.0),
    ]),
    "scan_relay": dict(type=0, air=True, tiers=[
        dict(cost=90,  range=20.0, min_range=0.0, dps=5 * 1.0,    chain=2, falloff=0.7, splash=0.0,
             aura_radius=10.0, aura_fire=0.15, aura_range=0.10, aura_dmg=0.0),
        dict(cost=110, range=12.0, min_range=0.0, dps=0.0,        chain=0, falloff=0.0, splash=0.0,
             aura_radius=12.0, aura_fire=0.25, aura_range=0.15, aura_dmg=0.0),
        dict(cost=160, range=14.0, min_range=0.0, dps=0.0,        chain=0, falloff=0.0, splash=0.0,
             aura_radius=14.0, aura_fire=0.35, aura_range=0.20, aura_dmg=0.10),
    ]),
}

# ---- Waves (Wave_01..10.asset). spawner: 0=west ground, 1=north ground,
#      2=air. clear = authored clearBonus (all non-zero in the live assets;
#      WaveManager falls back to 60 + 18*wave when zero). ---------------------
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
    """A ground route as a polyline with arc-length sampling."""
    name: str
    points: list

    def __post_init__(self):
        self.cum = [0.0]
        for i in range(1, len(self.points)):
            ax, az = self.points[i - 1]
            bx, bz = self.points[i]
            self.cum.append(self.cum[-1] + math.hypot(bx - ax, bz - az))
        self.length = self.cum[-1]

    def sample(self, s: float):
        """Position at arc length s (clamped)."""
        s = max(0.0, min(s, self.length))
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

    def air_length(self) -> float:
        return math.hypot(self.air_target[0] - self.air_spawn[0],
                          self.air_target[1] - self.air_spawn[1])


def covered_intervals(route: Route, px: float, pz: float,
                      reach: float, min_reach: float):
    """Arc-length intervals of `route` horizontally within (min_reach, reach]
    of the pad. Sampled at COVERAGE_SAMPLE_STEP_M."""
    step = COVERAGE_SAMPLE_STEP_M
    n = max(2, int(route.length / step))
    intervals = []
    start = None
    for i in range(n + 1):
        s = route.length * i / n
        x, z = route.sample(s)
        d = math.hypot(x - px, z - pz)
        inside = (d <= reach) and (d >= min_reach)
        if inside and start is None:
            start = s
        elif not inside and start is not None:
            intervals.append((start, s))
            start = None
    if start is not None:
        intervals.append((start, route.length))
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
    """Seconds one enemy spends inside the covered intervals (free-flow)."""
    total = 0.0
    for a, b in intervals:
        steps = max(1, int((b - a) / 2.0))
        for k in range(steps):
            s0 = a + (b - a) * k / steps
            s1 = a + (b - a) * (k + 1) / steps
            mid = 0.5 * (s0 + s1)
            total += (s1 - s0) / enemy_speed_at(enemy, mid, route.length)
    return total


def traverse_time(enemy: dict, route: Route) -> float:
    return time_in_intervals(enemy, route, [(0.0, route.length)])


# =============================================================================
#  Build simulation
# =============================================================================

@dataclass
class TowerInstance:
    pad: str
    tower_id: str
    tier: int = 0  # index into tiers (0 = tier 1)


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
        for pad in BUILD_PRIORITY:
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
        if any(pad not in built for pad in BUILD_PRIORITY):
            break  # a pad is unaffordable; bank for it rather than upgrading

        # 2) best-value upgrade (with two-step lookahead)
        best = None  # (gain_per_salvage, pad, target_tier, cost)
        for pad in BUILD_PRIORITY:
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
    return 1.0 + HP_GROWTH_PER_WAVE * (wave_number - 1)


def compute_wave(geom: Geometry, built: dict, wave_number: int,
                 wave: dict, difficulty: str):
    hp_mult = DIFFICULTY_HP_MULT[difficulty]
    scalar = wave_scalar(wave_number)

    groups = []
    wave_duration = 0.0
    for enemy_id, count, gap, offset, spawner in wave["groups"]:
        enemy = ENEMIES[enemy_id]
        if enemy["air"]:
            route = None
            traverse = geom.air_length() / enemy["speed"]
        else:
            route = geom.routes[spawner]
            traverse = traverse_time(enemy, route)
        eff_hp = enemy["hp"] * scalar * hp_mult * count
        groups.append(dict(id=enemy_id, enemy=enemy, count=count, gap=gap,
                           offset=offset, route=route, traverse=traverse,
                           eff_hp=eff_hp, delivered=0.0))
        wave_duration = max(wave_duration, offset + max(0, count - 1) * gap + traverse)

    # Deliverable damage, pad by pad. Each pad has a continuous-fire budget of
    # the wave duration; when the raw exposures across groups exceed it they
    # are scaled down proportionally (a pad shoots one thing at a time).
    for inst in built.values():
        tower = TOWERS[inst.tower_id]
        tier = tower["tiers"][inst.tier]
        a_fire, a_range, a_dmg = aura_bonuses(geom, built, inst.pad)
        rng = tier["range"] * (1.0 + a_range)
        dps = tier["dps"] * (1.0 + a_fire) * (1.0 + a_dmg)
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
                t_per = covered / enemy["speed"]
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
            g["delivered"] += dps * mult * factor * exposure * scale * focus

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
    peak = min(peak, MAX_LIVE_ENEMIES)

    return dict(margin=margin, required=required, deliverable=deliverable,
                worst_group=worst["id"], worst_margin=worst_margin,
                peak_live=peak, duration=wave_duration)


def wave_income(wave: dict, wave_number: int, difficulty: str) -> int:
    eco = DIFFICULTY_ECO_MULT[difficulty]
    bounties = sum(ENEMIES[eid]["bounty"] * count
                   for eid, count, _, _, _ in wave["groups"])
    clear = wave["clear"] if wave["clear"] > 0 else 60 + 18 * wave_number
    return round(bounties * eco) + round(clear * eco)


# =============================================================================
#  Report
# =============================================================================

def run_model(difficulty: str):
    geom = Geometry()
    # Sanity: the live map (fails loudly if the embedded data drifts).
    assert len(geom.pads) == 8, "expected the 8 shipped hardpoints"
    assert len(WAVES) == 10, "expected the 10 shipped waves"
    for r in geom.routes.values():
        assert 145.0 <= r.length <= 155.0, f"{r.name} length {r.length:.1f} m off the ~150 m live map"

    built: dict = {}
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

        flags = []
        if result["margin"] < BAND_MIN:
            flags.append("LOW")
        if wave_number == CLOSE_WAVE and result["margin"] > BAND_CLOSE_MAX:
            flags.append("HIGH-CLOSE")
        if wave_number < CLOSE_WAVE and result["margin"] > BAND_MID_MAX:
            flags.append("HIGH-MID")
        if result["worst_margin"] < GROUP_MIN:
            flags.append(f"GROUP-STARVED({result['worst_group']})")

        rows.append(dict(wave=wave_number, salvage_before=salvage,
                         income=income, flags=flags, **result))
        salvage += income

    return geom, rows, build_log


def format_report(difficulty: str, geom: Geometry, rows, build_log) -> str:
    out = []
    w = out.append
    w("COREHOLD balance model — baseline report")
    w(f"difficulty={difficulty}  focus={FOCUS_SWARM}/{FOCUS_HEAVY}(heavy)  "
      f"dwell={QUEUE_DWELL_FACTOR}  band=[>={BAND_MIN:.2f} all, "
      f"close<={BAND_CLOSE_MAX:.2f} @w{CLOSE_WAVE}, mid<={BAND_MID_MAX:.2f}]")
    w(f"geometry: Route_West {geom.routes[0].length:.2f} m, "
      f"Route_North {geom.routes[1].length:.2f} m, "
      f"air corridor {geom.air_length():.2f} m, {len(geom.pads)} pads")
    w("")
    w(f"{'wv':>2} {'requiredHP':>10} {'deliverable':>11} {'margin':>6} "
      f"{'worst-group':>16} {'live':>4} {'salv-pre':>8} {'income':>6}  flags / builds")
    for r, changes in zip(rows, build_log):
        worst = f"{r['worst_group']}={r['worst_margin']:.2f}"
        flags = ",".join(r["flags"]) if r["flags"] else "-"
        builds = (" | " + ", ".join(changes)) if changes else ""
        w(f"{r['wave']:>2} {r['required']:>10.0f} {r['deliverable']:>11.0f} "
          f"{r['margin']:>6.2f} {worst:>16} {r['peak_live']:>4} "
          f"{r['salvage_before']:>8} {r['income']:>6}  {flags}{builds}")
    w("")
    flagged = [r for r in rows if r["flags"]]
    if flagged:
        w("FLAGGED WAVES: " + ", ".join(
            f"wave {r['wave']} [{','.join(r['flags'])}]" for r in flagged))
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
    if difficulty != "normal":
        w("NOTE: sub-1.0 closes are BY DESIGN on Veteran/Nightmare — the model "
          "is conservative, so sub-1.0 means 'requires above-model play' "
          "(GDD Appendix A). The gate difficulty is Normal.")
    return "\n".join(out)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="COREHOLD per-wave balance model (R1)")
    ap.add_argument("--difficulty", choices=list(DIFFICULTY_HP_MULT),
                    default="normal")
    ap.add_argument("--report", metavar="PATH",
                    help="also write the table to this file")
    ap.add_argument("--json", metavar="PATH",
                    help="also dump rows as JSON (for delta tooling, R10)")
    args = ap.parse_args(argv)

    geom, rows, build_log = run_model(args.difficulty)
    report = format_report(args.difficulty, geom, rows, build_log)
    print(report)

    if args.report:
        # The baseline file carries all three tiers, like Appendix A's own
        # run()/run(veteran)/run(nightmare) printout. Normal is the gate.
        sections = [report if d == args.difficulty else
                    format_report(d, *run_model(d))
                    for d in ("normal", "veteran", "nightmare")]
        with open(args.report, "w") as f:
            f.write(("\n\n" + "=" * 78 + "\n\n").join(sections) + "\n")
    if args.json:
        with open(args.json, "w") as f:
            json.dump(dict(difficulty=args.difficulty,
                           rows=[{k: v for k, v in r.items()} for r in rows]),
                      f, indent=1)

    return 1 if any(r["flags"] for r in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
