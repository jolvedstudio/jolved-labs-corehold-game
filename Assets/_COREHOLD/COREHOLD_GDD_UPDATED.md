# COREHOLD — Updated GDD (As-Built)

**Status:** Reconstructed from the live codebase · Unity `6000.3.21f1` (6.3 LTS — the recommended pin was taken) · URP · WebGL2 target

This is a condensed, *as-built* companion to `COREHOLD_GDD.md`. The original document is the design intent; this one records what the code actually does today, and flags where the implementation diverged from the plan. Where a section here is silent, assume the original GDD still holds.

Read `§9 Divergences from the original GDD` first if you only have time for one section — that is where the surprises are.

---

## 1. What COREHOLD is (unchanged)

Sci-fi mech **tower defense**. Fixed camera, fixed enemy route, fixed hardpoints. Hold a reactor Core (Refinery Delta) against escalating waves of rogue mining machines using five turret types with a real three-way damage/armour counter system. Nine-ish-minute browser session, instant restart, three difficulty tiers. One map, ten waves.

The pillars are intact: **visible counter system**, **scarce hardpoints on a snaking route**, **wave-chaining as a risk/reward gamble**.

---

## 2. Code architecture (as-built)

All gameplay code lives under `Assets/_COREHOLD/Scripts/`, namespaced `Corehold.*`. Roughly 55 runtime scripts (up from the ~34 estimated), organised:

```
Scripts/
  Core/     GameManager, GameFlow, WaveManager, Spawner, PathRoute,
            RouteTraffic, RouteClearance
  Data/     CoreholdEnums, DamageTable (+Editor), TowerDefinition, TowerTier,
            TowerDefinition/EnemyDefinition/WaveDefinition/LevelDefinition,
            WeaponMounts (TowerWeaponMount, EnemyWeaponMount)
  Enemies/  Enemy, EnemyMover, EnemyAnimatorBridge, EnemyWeapon
  Towers/   Tower, TowerHardpoint, TowerTargeting, TurretAim, TowerWeapon,
            Projectile, SupportAura, TowerModifiers, TowerHealth,
            ChainTracer, TurretBarrelSpin, HardpointCoverageGizmo
  Systems/  GameManager events, CoreholdPool, PoolRegistry, AudioDirector,
            VFXDirector, InputRouter, SaveData, DebugConsole, BlobShadow,
            CameraShake, CoreDamageState, CoreDestruction, TurretRotationAudio,
            Wave9MemoryProbe
  UI/       HUDController, BuildMenu, BuildEntryHover, TowerPanel, ResultScreen,
            TitleScreen, PauseScreen, OverlayManager, RangeRing, UITheme,
            WorldHealthBar, RotateDeviceOverlay
```

Editor tooling lives in `Assets/Editor/Coplay/` — a large set of one-shot build/verify/setup scripts (scene builders, icon renderers, audits, ticket-verification scripts). These are development scaffolding, not shipped code.

### 2.1 State machine (`GameManager`)

`Boot → Title → Briefing → Build → Wave → (Build | Victory | Defeat)`, exactly as designed. `GameManager` is a self-healing singleton holding `Salvage`, `Integrity`, `WaveIndex`, `Difficulty`, and `CoreInvulnerable`. It raises `OnSalvageChanged`, `OnIntegrityChanged`, `OnStateChanged`. Everything reacts to events; nothing polls.

- Starting integrity: **20 / 15 / 10** (Normal / Veteran / Nightmare) — matches the design.
- `ConfigureRun(tier)` applies the economy multiplier to starting salvage and sets integrity for the tier.
- Leak → `DamageCore` is wired per-enemy via `RegisterEnemy` / `HandleEnemyLeaked`.

### 2.2 Flow (`GameFlow`)

Replaced the old IMGUI bootstrap. On load: `Title` state, title screen shown, no audio (audio gate honoured). Difficulty pick → `ConfigureRun` → `Build`. It also does scene prep: puts each `TowerHardpoint` on the `Hardpoint` layer, adds a 1.5 m trigger `SphereCollider`, ensures a `TurretMount` child, and guarantees an `InputRouter` exists (created in `Awake` so `BuildMenu` can subscribe in time — a fix for pads not opening the build menu).

---

## 3. Economy, waves & difficulty (as-built)

`WaveManager` owns the schedule and the live registry, and reads rules from `Level_RefineryDelta` (`LevelDefinition`) with hard-coded fallbacks.

- **Wave HP scalar:** `1 + 0.18·(wave − 1)` — matches design.
- **Difficulty HP multiplier:** `1.00 / 1.25 / 1.55` — matches design.
- **Difficulty economy multiplier:** `1.00 / 1.12 / 1.22` — matches design.
- **Concurrency cap:** design's flat "14 live" is now a *derived* ceiling: `min(maxLiveEnemies, RouteTraffic.DerivedCapacity(largestBodyRadius))`, with the flat 14 as the fallback if the traffic manager is absent. Admission is **slot-based** — a unit only enters if its track entrance is clear by min-spacing — otherwise it waits in a pending queue that drains as the field clears (retried ~4×/sec).
- **Wave chaining:** `StartNextWave` is always available; if a wave is on the field it pays `min(liveCount × 8, 80)` salvage (economy-scaled) and stacks the next wave. Matches design.
- **Clear bonus:** authored `clearBonus` if non-zero, else `60 + 18·waveNumber`, economy-scaled.
- **Spawners:** matched by `Index` — 0 = west ground, 1 = north ground, 2 = air.
- **Wave-complete rule:** field must be fully clear (no live, no pending, no group still spawning) before flipping to `Build` / `Victory`.

Wave assets `Wave_01`…`Wave_10` and the ten-wave schedule from the original §8.1 are authored. The full numeric balance model (Appendix A of the original) still governs tuning intent.

---

## 4. Enemies (as-built)

`EnemyDefinition` assets: **Scuttler, Strider, Lancer, Wasp, Roller, Breaker, Colossus** — plus a **`Enemy_Drone`** asset (the Wasp air unit was swapped/duplicated to the Rip Vertices drone; both exist in `Data/Enemies/`).

Runtime enemy stack:
- `Enemy` — health, armour, `TakeDamage(float, DamageType)` through the `DamageTable`, death, leak, registry add/remove, `CullSilently` (used by the stall watchdog). Exposes `Live`, `Mover`, `IsAlive`, `Frontness`, `LeakDamage`, `SetMaxHealth`, `SetBounty`, `SetLeakDamage`.
- `EnemyMover` — route walk / straight-line flight, Roller phase change, body radius. **Movement is now scheduled centrally by `RouteTraffic`, not self-driven** (see §5).
- `EnemyAnimatorBridge` — `Speed`, `Die`, `Animator.speed` foot-slide correction via `animatorClipSpeedRef`.
- `EnemyWeapon` — **NEW, not in original design.** Enemies shoot back at turrets. Multi-mount array (`EnemyWeaponMount[]`), each mount finds the nearest live `Tower` with a `TowerHealth` in its own range via the `Tower.Live` registry (no physics), fires on its own cadence/damage/muzzle/tracer.

`Enemy_Colossus`: HP 2800, Shielded, speed 3, bounty 250, leak 20, `enrageAtHealthFraction 0.5`, `enrageSpeedMultiplier 1.4` — matches design. **Its `prefab` reference is currently null (`fileID: 0`)** — the boss model still needs to be assigned (the §4.5 go/no-go outcome was not yet baked into the asset).

Definitions also carry **per-enemy audio** (fire/death clips, volume, pitch spread) beyond the original spec.

---

## 5. Navigation redesign — the biggest divergence

The original GDD deleted pathfinding entirely: enemies were "waypoint followers," no collision, no negotiation. The as-built code adds a full **1-D car-following traffic model** (`RouteTraffic`, `RouteClearance`, `RouteTraffic`-driven `EnemyMover`) that the original never described.

**`RouteTraffic`** is a scene singleton (auto-created) and the sole owner of enemy longitudinal position:
- Each **track** (a ground `PathRoute`, or the shared air corridor) has fixed **lanes**, each an ordered list front(0)→back.
- Movers tick front-to-back; each is hard-clamped so its progress never exceeds `leader.Frontness − minSpacing`. This clamp is the overlap guarantee. No overtaking, no lane changes → list order equals progress order, no sorting.
- Front unit of each lane has no leader → always advances → always reaches the Core (a sink), so chains are acyclic and every unit drains in bounded time. A wave can always complete.
- **Wide bodies** (radius ≥ `wideBodyRadius`) occupy every lane of their track so nothing clips past them.
- **Air units** share one corridor ordered by remaining-distance-to-Core; scalar spacing on that distance guarantees no world overlap.
- Provides `DerivedCapacity(radius)` and `CanAdmit(route, isAir, radius, entranceFrontness)` used by `WaveManager` for slot-based admission.

**Stall watchdog** (`WaveManager.TickStallWatchdog`): if any live enemy makes no path progress for `stallWatchdogSeconds` (default 8 s) it is culled silently (no Core damage, no bounty) and logged loudly. With the car-following model this should never fire; it's a regression net so a bug logs instead of bricking the session.

**Why it exists:** enemies bunching, clipping, or piling up when waves chained. The traffic model gives deterministic spacing and a provable no-deadlock guarantee at the cost of the extra system.

---

## 6. Towers (as-built)

Five `TowerDefinition` assets: **Autocannon, MissileBattery, ArcNode, SiegeMortar, ScanRelay**. Three tiers each, damage/armour counter table (`DamageTable.asset`) as designed.

Runtime tower stack:
- `Tower` — tier state, `Live` registry, `OnRosterChanged` event (build/upgrade/sell only). **Effective stats are computed properties** (`EffectiveRange/FireRate/Damage`), never cached, so a sold Scan Relay can't leave a stale buff. `IsSupportRelay` detected from `auraRadius > 0`.
- `TowerTargeting` — registry scan (no physics), staggered ~0.2 s tick, `First/Closest/Strongest` priority.
- `TurretAim` — yaw/pitch slew with `IsAimed` gate.
- `TowerWeapon` — fire timer, `IsAimed` gate, dispatch to hitscan / chain / projectile.
- `Projectile` — leading intercept, travel, splash, orphaned-target handling.
- `SupportAura` — non-stacking (strongest relay wins), pushed on build/upgrade/sell only.
- `TowerModifiers` — the buff struct.
- **`TowerHealth` — NEW, not in original design.** Turrets have HP (default 220), take damage from `EnemyWeapon`, and when destroyed free their hardpoint for a rebuild.
- `ChainTracer`, `TurretBarrelSpin`, `HardpointCoverageGizmo` — VFX / editor helpers.

### 6.1 Multi-weapon mounts — divergence

Both towers and enemies moved from single-weapon fields to a **`weapons` array** (`TowerWeaponMount[]` / `EnemyWeaponMount[]`). A turret tier or an enemy can now mount several weapons (twin barrels, cannon + launcher), each firing independently at its own damage/rate/projectile/muzzle/tracer. Range and min-range stay tier-level.

Legacy single-weapon fields are retained and auto-migrate into `weapons[0]` on load, so older definitions still deserialize. `TowerTier` exposes `TotalFireRate`, `TotalDamagePerVolley`, `TotalDps` (sum of per-mount `damage × rate`, not the product of sums). A single weapon can also round-robin multiple muzzles purely for visual flavour without changing DPS.

---

## 7. Level, camera, presentation (as-built)

- **Scene:** `Assets/_COREHOLD/Scenes/Game.unity`, a `RefineryLevel` built from Creepy Cat + Firadzo props, with `Routes/Route_West` and `Routes/Route_North` (WP_0…WP_13 each), `Hardpoints/` (HP_Premium ×3, HP_Standard ×2, HP_Rear ×2, HP_Overwatch), a `Core_Blockout` (Shield Generator core, dome segments, target), spawners (West/North/Air), and a `LightProbeGroup` + `ReflectionProbe`.
- **Hardpoints:** each has `PadMarker` and `PadAura` children (cyan emissive rim).
- **Lighting:** baked, directional shadows off, blob shadows on units (`BlobShadow`).
- **Core presentation:** `CoreDamageState` / `CoreDestruction` mirror integrity physically (dome segments darken, emissive shifts).
- **Camera feel:** `CameraShake` (leak shake with cooldown).
- **Framing:** verified at 16:9 / 16:10 / 20:9 (previews in `Docs/FramingPreviews/`). `RotateDeviceOverlay` + `Canvas_RotatePrompt` handle portrait phones.

---

## 8. Systems & UI (as-built)

**Systems:** `CoreholdPool<T>` + `PoolRegistry` (pooled enemies/projectiles/VFX). `AudioDirector` (mixer groups + one-shot SFX registry, `Group`/`Sfx` enums). `VFXDirector` (pooled effects + `VfxTracer`). `InputRouter` (single-tap raycast to hardpoints). `SaveData` (PlayerPrefs best score, difficulty unlocks, `ComputeScore`, `IsCleared`/`MarkCleared`, `IsUnlocked`). `DebugConsole` (editor/dev key bindings for wave skip, salvage, invuln, kill-all, difficulty jump, on-screen readout). `Wave9MemoryProbe` (diagnostic).

**UI (all present):** `HUDController` (integrity / salvage / wave / start-wave / speed / pause), `BuildMenu` (+`BuildEntryHover`, role tags), `TowerPanel` (upgrade/sell, priority buttons, counter grid), `ResultScreen`, `TitleScreen` (difficulty tiers + best scores), `PauseScreen`, `OverlayManager` (world-space health bars + armour pips, shared-material quads), `RangeRing`, `UITheme`, `WorldHealthBar`, `RotateDeviceOverlay`.

Canvases in the scene: `Canvas_HUD`, `Canvas_Menus`, `Canvas_RotatePrompt`. `EventSystem` present.

---

## 9. Divergences from the original GDD (the important part)

| # | Original design | As-built | Impact |
|---|---|---|---|
| 1 | No pathfinding; simple waypoint followers; no enemy collision | Full **1-D car-following `RouteTraffic`** with lanes, min-spacing clamp, slot-based admission, stall watchdog | Larger system than planned, but deterministic spacing and no pile-ups when chaining waves |
| 2 | "No enemy abilities beyond movement" | Enemies carry **`EnemyWeapon`** and shoot back at turrets | Adds a whole combat direction the design excluded; changes turret survivability tuning |
| 3 | Turrets are indestructible emplacements | Turrets have **`TowerHealth`** and can be destroyed, freeing the pad | Consequence of #2; adds rebuild economy pressure |
| 4 | One weapon per turret tier / enemy | **Multi-weapon mount arrays** on both (with legacy migration) | More authoring flexibility; DPS must be summed per-mount |
| 5 | Flat "14 live enemies" cap | **Derived capacity** from track geometry, `min(14, DerivedCapacity)`, plus pending queue | Cap is now geometry-aware; 14 is a ceiling/fallback |
| 6 | Wasp = Rip Vertices drone | Both `Enemy_Wasp` **and** `Enemy_Drone` assets exist | Confirm which is wired into waves; retire the unused one |
| 7 | Per-enemy audio not specified | `EnemyDefinition` carries fire/death clips, volume, pitch spread | Richer audio than planned |

---

## 10. Known open items / TODO

- **Colossus prefab is unassigned** (`Enemy_Colossus.prefab = null`). Resolve the §4.5 boss go/no-go and assign the model (Humanoid at 1.4× or the Spiders/Tanks fallback at 1.6×).
- **Wasp vs Drone:** confirm which air-unit asset the wave definitions reference; delete or repurpose the other.
- **Tune enemy-weapon damage** against `TowerHealth` — this interaction is new and unmodelled by the original balance spreadsheet; the Appendix A model does not account for turret loss.
- **Re-validate the balance model** now that turrets can die and the concurrency cap is geometry-derived rather than a flat 14.
- Reconcile remaining `[VERIFY]` items in the original GDD against `AssetManifest.md`.

---

## 11. Where to look

- **Design intent & balance model:** `COREHOLD_GDD.md` (original, authoritative for numbers and Appendix A).
- **VFX & sound authoring:** `Docs/DevManual_VFX_and_Sound.md`.
- **As-built truth:** the code under `Assets/_COREHOLD/Scripts/` — this document summarises it but the source is canonical.
