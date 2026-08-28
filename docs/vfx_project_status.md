# VFX Expansion — Project Status & Division of Labor

**Ownership:** systems, seams, policy and balance-truth are handled in the
Claude Code session; **Coplay handles purely aesthetic work** — choosing,
tuning and wiring the *look* of effects through the surfaces listed below.
Coplay: treat this file as the current contract; do not add gameplay
mechanics or new systems from the VFX lane (the tower-shield mechanic from
Tier 1 has been absorbed into the balance model, but future mechanics must go
through the balance side first).

## Ready for aesthetic wiring NOW (all slots are silent until assigned)

Assign prefabs in `CombatVFX_Testbed`, then run
`CopyTestbedVFXToConfigAndScenes`. Every slot no-ops safely while unassigned:

| Slot | Plays | Notes |
|---|---|---|
| `ExplosionKinetic/Energy/Explosive` | on kills by that damage type | falls back to size-based explosion; palette per `VFX_ColorLanguage_Rule.md` |
| `SpawnFlash` | at each unit's spawn position | per-unit, works with staggered group spawns |
| `SpawnPortal` | once per distinct spawner at wave start, facing the spawner | pairs with SpawnFlash |
| `StrikeMarker` | at the Strike Wing's committed point, layered over the telegraph ring | FRIENDLY marker — never a danger/warning visual |

Also aesthetic-lane: `WeatherPreset` content per biome (remember to add new
presets to the theme's `EnvPack.weatherPool` — the seed draws from there),
and shield-shell tint/fresnel tuning (`ShieldShell`, colors bound by the
color rule: blue = Shielded enemy, amber-green = friendly barrier).

## Hard rules for the aesthetic lane

1. **Never reference a vendor-pack prefab from a committed asset.** The packs
   are git-ignored. Copy the specific prefab + its materials/textures into
   `Assets/_COREHOLD/VFX/` first and reference the copy. (Dangling GUIDs from
   ignored folders break every other machine and the build.)
2. Embedded real-time `Light`s are auto-disabled by the director's pool
   (`DisableEmbeddedLights`) — still prefer light-free prefabs.
3. Shuriken only. No VFX Graph on the WebGL target.
4. `VFX_ColorLanguage_Rule.md` governs every color choice.
5. After wiring new textures, run the texture budget pass
   (`EnforceCrunchOnOverrides` / `WebGLBudgetPass`) and check `BuildSizeAudit`.

## Systems-side state (already done — do not redo)

- **Tower shields are in the balance model** (`docs/balance_model.py`) and the
  live exporter: per-tier `shield/regen/delay` ride the towers block, absorbed
  before hp exactly like `TowerHealth` (refill between waves when
  regenerating; mid-fire regen credited only when `delay <= 1.0 s` — the
  authored Autocannon T1 pilot `50 / 2 per s / delay 0` therefore heals under
  fire, and is modeled that way). Certification reads authored shields live;
  editing them re-certifies on the next Verify/Generate.
- Explosion-by-type routing with fallback; pool-level light stripping;
  spawn/portal/marker call sites (WaveManager, StrikeWingAbility); explosion
  trauma on `CameraShake` (`explosionTrauma`, cooldown-guarded).
- `Assets/_Recovery/` (a committed Unity crash-recovery scene) removed and
  gitignored; `Eric VFX Studio` / `Free Slash VFX` untracked and gitignored.

## Parked (systems lane, on request)

- Haptics: browser `.jslib` bridge (`vibrationActuator` / `navigator.vibrate`)
  — Unity's `SetMotorSpeeds`/`Handheld.Vibrate` are no-ops on WebGL.
- Ground danger telegraphs: waiting on an enemy mechanic worth telegraphing.
- Model terms for utility towers (CryoNode slow, SalvageRig income) if
  generated maps ever field them.
