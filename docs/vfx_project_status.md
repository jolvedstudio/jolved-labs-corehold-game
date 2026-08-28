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

Weather presets can now also grade the **sun** (opt-in `overrideSun`):
colour temperature in Kelvin × a filter tint, plus intensity and
shadow-strength *multipliers* over the authored sun — relative on purpose,
so one overcast preset reads correctly on a bright map and the R23 night
variant alike. The applier resolves `RenderSettings.sun` (else the brightest
active directional light), and restores every field exactly on clear.
Cosmetic only, as ever — Blackout's gameplay darkness stays a wave mutator.

## Hard rules for the aesthetic lane

1. **Never reference a vendor-pack prefab from a committed asset.** The packs
   are git-ignored. Copy the specific prefab + its materials/textures into
   `Assets/_COREHOLD/VFX/` first and reference the copy. (Dangling GUIDs from
   ignored folders break every other machine and the build.) This is now
   *enforced and repairable*: campaign preflight ERRORs on any stage whose
   closure reaches a vendor pack, and `VendorLocalizer` copies the used subset
   into `Assets/_COREHOLD/Vendored/` and remaps the references (see below).
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
- **Weather tint & coverage fixed:** `WeatherApplier` now captures each
  renderer's authored base colour and applies `groundTint` multiplicatively
  (clearing a preset restores the exact authored look), and the tint reaches
  the whole readable ground plane — Floor, `TerrainRelief` children, the
  silhouette band and `PlacedProp`s in the Silhouette role — not just the
  floor quad. Stale fog-authority comments corrected (LookStage bakes the
  baseline; the applier saves/restores around presets).
- **Vendor localization (`Assets/Editor/Coplay/VendorLocalizer.cs`):**
  copies every git-ignored vendor asset a scene/definition/config actually
  references into committed `Assets/_COREHOLD/Vendored/` (structure kept,
  deduplicated) and remaps all GUIDs in the closure to the copies, so a fresh
  clone builds what the author saw. Wired in four places:
  1. the Level Generator runs a *Localize vendor assets* stage after Save;
  2. Campaign Builder Generate + Adopt localize each accepted stage;
  3. the Ship step has a **Localize vendor assets (all stages)** button for
     already-generated campaigns (e.g. sandy-desert-110);
  4. campaign preflight ERRORs (blocks Build) while any stage still
     references a pack.

## ACTION NEEDED on a machine that has the packs

`VFXDirectorConfig.asset` at HEAD references machine-local Cartoon FX
prefabs — on a fresh clone every effect slot dangles and a clean build ships
with **no combat VFX**. On a machine where the packs exist, run
**Tools → COREHOLD → VFX → Localize VFX Config (vendored copies)** once,
then commit `Assets/_COREHOLD/Vendored/` plus the updated config. For the
existing campaign, also press **Localize vendor assets (all stages)** in the
Campaign Builder's Ship step and commit the result. (On a pack-less clone
the tools report nothing to do — the GUIDs are unresolvable there; they must
run where the sources exist.)

## Parked (systems lane, on request)

- Haptics: browser `.jslib` bridge (`vibrationActuator` / `navigator.vibrate`)
  — Unity's `SetMotorSpeeds`/`Handheld.Vibrate` are no-ops on WebGL.
- Ground danger telegraphs: waiting on an enemy mechanic worth telegraphing.
- Model terms for utility towers (CryoNode slow, SalvageRig income) if
  generated maps ever field them.
