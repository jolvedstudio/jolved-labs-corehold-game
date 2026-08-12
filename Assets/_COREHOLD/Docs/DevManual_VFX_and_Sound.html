# COREHOLD — Developer Manual: VFX & Sound

Scope: how visual effects and audio are wired, configured, and extended in COREHOLD.
Everything one-shot goes through two central singletons — **`VFXDirector`** and
**`AudioDirector`** — both living on GameObjects in `Assets/_COREHOLD/Scenes/Game.unity`.
Nothing on a gameplay path calls `Instantiate`/`Destroy` during a wave (GDD §11); all
effects and voices are pooled.

---

## 1. Architecture at a glance

| System | Script | Scene object | Editor setup tool |
|---|---|---|---|
| Visual effects | `Assets/_COREHOLD/Scripts/Systems/VFXDirector.cs` | `VFXDirector` | `Tools/COREHOLD/Setup VFX Director` |
| Audio | `Assets/_COREHOLD/Scripts/Systems/AudioDirector.cs` | `AudioDirector` (+ `TurretRotationAudio`) | `Tools/COREHOLD/Setup Audio Director` |

Both are singletons (`VFXDirector.Instance`, `AudioDirector.Instance`). Every public
`Play*` method **no-ops safely** when its prefab/clip is unassigned, so gameplay never
breaks while art/audio is being wired.

Call sites (who triggers what):
- **Turrets** — `Scripts/Towers/TowerWeapon.cs` (muzzle, impact, tracer, fire SFX), `Scripts/Towers/Projectile.cs` (splash explosion).
- **Enemies** — `Scripts/Enemies/Enemy.cs` (death burst/explosion, core-hit, death SFX), `Scripts/Enemies/EnemyWeapon.cs` (tracer, impact, fire SFX).

---

## 2. VFX (VFXDirector)

### 2.1 The nine pooled effects
Defined by the `VFXDirector.Effect` enum, authored on the component's `effects[]` array
(each entry = `id`, `prefab`, `prewarm`):

| Effect | Used for |
|---|---|
| `MuzzleKinetic` | Autocannon muzzle flash |
| `MuzzleEnergy` | Arc Node muzzle flash |
| `MuzzleExplosive` | Missile / Mortar muzzle flash |
| `ImpactSpark` | Hitscan/projectile strike on a unit |
| `ExplosionSmall` | Missile splash (< 4 m radius) |
| `ExplosionLarge` | Mortar / large splash (≥ 4 m, `LargeSplashThreshold`) |
| `EnemyDeath` | Burst when an enemy dies |
| `CoreHit` | Flash on the Core when a leak lands |
| `BuildPuff` | Puff when a turret is placed |

Prefabs are **Cartoon FX Remaster** (JMO Assets). At spawn each pooled copy is forced to
`ClearBehavior.None` and watched by a `PooledEffect` that returns it to its pool when the
particle system finishes. `CFXR_Effect.GlobalDisableLights = true` is set at boot — **no
effect spawns a light** (GDD §11).

### 2.2 Hitscan tracer
A separate pooled `VfxTracer` (LineRenderer) for the Autocannon and Arc Node. Configured on
the component: `tracerMaterial` (auto-built additive URP material if null), `tracerWidth`,
`tracerPrewarm`, `defaultTracerColor`. Per-shot color comes from the weapon mount (see §2.4).

### 2.3 Public API (call from gameplay)
```csharp
VFXDirector.Instance.PlayMuzzle(DamageType type, Vector3 pos, Vector3 forward);
VFXDirector.Instance.PlayImpact(Vector3 pos);
VFXDirector.Instance.PlayExplosion(Vector3 pos, float splashRadius); // picks small/large
VFXDirector.Instance.PlayEnemyDeath(Vector3 pos);
VFXDirector.Instance.PlayCoreHit(Vector3 pos);
VFXDirector.Instance.PlayBuildPuff(Vector3 pos);
VFXDirector.Instance.DrawTracer(Vector3 from, Vector3 to, Color color);
// Generic: VFXDirector.Instance.Play(Effect e, Vector3 pos[, rotation, scale]);
```

### 2.4 Per-unit VFX authoring (the only non-central VFX)
- **Turret tracer/chain color**: `TowerWeaponMount.tracerColor` and `TowerWeapon.chainColor` / `tracerColor` (`Scripts/Data/WeaponMounts.cs`, `Scripts/Towers/TowerWeapon.cs`).
- **Turret projectile**: `TowerWeaponMount.projectilePrefab` — the projectile's impact/splash routes back through the `VFXDirector`.
- **Tier visuals**: `TowerTier.muzzleVfx`, `weaponVisualIndex` (`Scripts/Data/TowerTier.cs`).
- **Enemy enrage emissive** (Colossus only): `Enemy.enrageEmissiveFrom/To` + `enrageRenderers`.
- **Blob shadow**: `BlobShadow` child on each enemy prefab (fake shadow, not a particle).

### 2.5 Setup / verify VFX
1. `Tools/COREHOLD/Setup VFX Director` — creates/updates `VFXDirector` and assigns all nine CFXR prefabs + prewarm counts (mapping in `Assets/Editor/Coplay/SetupVFXDirector.cs`).
2. `Tools/COREHOLD/Verify VFX Director (Play)` — play-mode pool-stability check.
3. To swap a look, change the prefab path in `SetupVFXDirector.cs` and re-run, or drop a new prefab directly onto the matching `effects[]` slot in the Inspector.

### 2.6 Add a new effect
1. Add a value to the `VFXDirector.Effect` enum.
2. Add a default entry (id + prewarm) to the `effects[]` initializer.
3. Add a convenience `Play*` wrapper if desired.
4. Assign its prefab (Inspector, or extend the `Map` in `SetupVFXDirector.cs`).
5. Call it from the relevant gameplay script.

---

## 3. Sound (AudioDirector)

WebGL-friendly, deliberately minimal: no AudioMixer effects, no reverb/ducking.

### 3.1 Voices & policies (GDD §10)
- **12 pooled one-shot voices** (`voiceCount`). When all are busy, the **oldest** voice is stolen, never refused.
- **50 ms collapse** (`collapseWindow`, unscaled time): identical one-shots inside the window fold into one play, boosted by `collapseVolumeBoost`. Unscaled so the 2× speed toggle doesn't eat distinct shots.
- **Volume groups**: `MasterVolume`, `SfxVolume`, `MusicVolume` (0..1) + `Muted`.
- **Pitch is unscaled**; one-shots get ±`pitchSpread` random pitch.
- **Rotation loop**: up to `rotationLoopVoices` (3) voices, driven each frame by `TurretRotationAudio` via `SetRotationLoud(int)` — only the nearest-to-center slewing turrets sound.
- **Music**: dedicated non-stolen voice; `StartMusic()`/`StopMusic()`/`SetMusic()`.

### 3.2 Fixed one-shot slots (`AudioDirector.Sfx`)
`FireKinetic`, `FireEnergy`, `FireMissile`, `FireMortar`, `Impact`, `Explosion`,
`EnemyDeath`, `UIClick`, `Build`, `CoreAlarm`. Each authored in `sfx[]` with `clip`,
`volume`, `pitchSpread`.

### 3.3 Public API
```csharp
AudioDirector.Instance.PlayFire(DamageType type, bool isProjectile, bool isMortar);
AudioDirector.Instance.PlayImpact();
AudioDirector.Instance.PlayExplosion();
AudioDirector.Instance.PlayEnemyDeath();               // shared clip
AudioDirector.Instance.PlayEnemyDeath(EnemyDefinition);// per-enemy, falls back to shared
AudioDirector.Instance.PlayEnemyFire(EnemyDefinition); // per-enemy, no-op if none
AudioDirector.Instance.PlayUIClick(); PlayBuild(); PlayCoreAlarm();
AudioDirector.Instance.PlayClip(AudioClip clip, float volume, float pitchSpread); // ad-hoc
```

### 3.4 Per-enemy sounds (recently added — see `Scripts/Data/EnemyDefinition.cs`)
Each Enemy SO under `Assets/_COREHOLD/Data/Enemies/` authors its own audio:
- `fireSound`, `fireVolume`, `firePitchSpread` — played from `EnemyWeapon.Fire()` via `PlayEnemyFire`.
- `deathSound`, `deathVolume`, `deathPitchSpread` — played from `Enemy.Die()` via `PlayEnemyDeath(def)`; falls back to the shared `Sfx.EnemyDeath` clip when null.

`PlayClip` reuses the same pooled voices + 50 ms collapse (keyed by clip), so a wiped swarm
reads as one louder burst.

### 3.5 Per-turret fire sound
`TowerWeapon.FireWeapon()` calls `PlayFire(definition.damageType, isProjectile, isMortar)`.
`TowerTier.fireSfx` is available for tier-specific overrides.

### 3.6 Setup / verify audio
1. `Tools/COREHOLD/Setup Audio Director` — creates/updates `AudioDirector` (+ `TurretRotationAudio`), assigns one-shots from Turret SFX / Creepy Cat folders, rotation loop, music, and **forces WebGL import to `CompressedInMemory`** (mapping in `Assets/Editor/Coplay/SetupAudioDirector.cs`).
2. To wire per-enemy clips: select each `Enemy_*.asset` and drop a clip into **Fire Sound** / **Death Sound**.
3. WebGL import rule for any new clip: set the WebGL override to `CompressedInMemory` (Vorbis).

### 3.7 Add a new fixed one-shot
1. Add a value to the `AudioDirector.Sfx` enum.
2. Add a default entry to the `sfx[]` initializer (volume/spread).
3. Add a `Play*` wrapper if desired.
4. Assign the clip (Inspector, or extend `SfxMap` + `DefaultVolume`/`DefaultSpread` in `SetupAudioDirector.cs`).

---

## 4. Quick reference: event → VFX + SFX

| Event | VFX | SFX |
|---|---|---|
| Turret fires | `PlayMuzzle` + `DrawTracer` (or projectile) | `PlayFire` |
| Shot hits unit | `PlayImpact` | `PlayImpact` |
| Splash detonation | `PlayExplosion(radius)` | `PlayExplosion` |
| Enemy fires | `DrawTracer` + `PlayImpact` | `PlayEnemyFire(def)` |
| Enemy dies | `PlayEnemyDeath` + `PlayExplosion` | `PlayEnemyDeath(def)` → per-enemy or shared |
| Enemy leaks (Core hit) | `PlayCoreHit` | `PlayCoreAlarm` |
| Turret built | `PlayBuildPuff` | `PlayBuild` |
| Turret slewing | — | rotation loop via `SetRotationLoud` |

---

## 5. Gotchas
- Effects/clips left unassigned **silently no-op** — check the two Setup tools' console logs for missing-asset errors.
- No effect may spawn a light (`GlobalDisableLights`). Camera shake is separate (`CameraShake`, GDD §3.3).
- Enemy firing was silent before per-enemy `fireSound` existed; assign clips on the Enemy SOs to hear it.
- Tracers must use a URP-native additive shader (built-in legacy particle shaders render invisible/magenta on URP).
