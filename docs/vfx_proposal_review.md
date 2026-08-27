# VFX Expansion Proposal — Review & Required Amendments

**For:** Coplay, to incorporate into the VFX expansion proposal.
**Verified against:** branch `claude/campaign-manager-a0` at commit `46626d9`
(includes the provider-agnostic `VFXDirector` refactor and the CoPlay-Wip merge).
Every code claim below was checked against these files — re-verify locally if
the tree has moved.

Overall verdict: the proposal's structure, priorities and WebGL awareness are
sound. Keep its shape. Amend the items below: A–D are blockers, E–H are
corrections, the confirmations at the end are claims verified true (keep them
as written).

---

## A. BLOCKER — vendor-reference policy (add as a new "Step 0" section)

**Fact:** the effect packs are git-ignored, machine-local content. `.gitignore`
now lists `Assets/Vendor/`, `Assets/Yoge/`, `Assets/Layer Lab/`, **`Assets/Eric
VFX Studio/`, `Assets/Free Slash VFX/`** (the last two were untracked in
`46626d9` after being accidentally committed). Meanwhile every Tier 1–2 item
wires pack prefabs into **committed** assets: `Assets/_COREHOLD/Data/
VFXDirectorConfig.asset`, WeatherPreset assets, enemy prefabs (ShieldAura).

**Consequence if unamended:** committed assets referencing ignored prefabs =
dangling GUIDs on every fresh clone and every machine without the packs;
effects silently missing; remote builds broken.

**Amendment:** before any wiring, copy the *actually used* prefabs — plus the
materials/textures/shaders they depend on — into a committed
`Assets/_COREHOLD/VFX/` folder, and reference **only** those copies. This also
bounds WebGL build size to used content, which `WebGLBudgetPass`,
`EnforceCrunchOnOverrides` and `BuildSizeAudit` can then police. Add a
dependency audit step (same idea as the Character Forge's out-of-repo
reference audit) so a copied prefab never references back into an ignored
folder.

## B. BLOCKER — haptics section describes a dead path on WebGL

**Fact:** on the WebGL backend, `Gamepad.current.SetMotorSpeeds(...)` (Input
System 1.20.0) is a no-op — Unity does not route rumble to the browser
Gamepad API. `Handheld.Vibrate()` is Android/iOS-native and also inert on
WebGL. As written, the HapticDirector ships dead code on the primary target.

**Amendment:** keep the HapticDirector shape (cooldowns mirroring
`CameraShake`, SaveData-persisted toggle — that part is right), but the
mechanism must be a small `.jslib` browser bridge:
`navigator.getGamepads()[i].vibrationActuator.playEffect("dual-rumble", {...})`
for gamepads, `navigator.vibrate(pattern)` for mobile browsers, both
feature-detected and fail-silent. Re-scope the item as "jslib spike first; if
the spike is unsatisfying, drop haptics" and demote it to last in sequencing.

## C. BLOCKER — the agnostic refactor opened a lights gap the proposal's own guardrail misses

**Fact:** the proposal's guardrail "`GlobalDisableLights` stays on" only
covers **CFXR** effects. Post-refactor, `VFXDirector` pools ANY
Shuriken-based prefab (`CoreholdPool<Transform>`), and the new doc string in
`VFXDirector.cs` says precisely "no **CFXR** effect spawns a light". ETFX and
similar packs frequently embed real-time point `Light` components in their
prefabs; nothing in the pool path disables them today.

**Amendment:** add to the plan (before any ETFX adoption): in the director's
prefab-preparation step, find all `Light` components in the pooled instance's
hierarchy and disable/strip them, mirroring what `CFXR_Effect.
GlobalDisableLights` guarantees for CFXR content. One small change; without
it, three ETFX explosions put per-pixel lights back on WebGL.

## D. BLOCKER — telegraph section targets mechanics that do not exist

**Facts:** Strike Wing (R19) is the **player's** active ability
(`StrikeWingButton.cs`) — a `WarningBolt`/`WarningSkull` danger telegraph
under it inverts the visual vocabulary (danger telegraphs warn the player
about *incoming* threats; a friendly strike wants a target/impact marker).
The Colossus has **no AoE or footfall attack** — it walks and fires through
`EnemyWeapon` like everything else; there is nothing to telegraph.
Additionally, "detach on shield-break": no shield-break mechanic exists —
armour type is static per enemy (`Enemy.SetArmourType` at spawn only).

**Amendment:** rewrite item 6 as: (a) a **target marker** (not a warning) at
the Strike Wing impact point during its wind-up; (b) hold ground-warning
telegraphs in reserve until an enemy gains a telegraphable threat (none
exists today — flag this as a design dependency, not a VFX task). Change the
ShieldAura lifecycle to "attach on spawn, detach on death" only.

## E. Explosion-by-DamageType needs a color-language rule, not just enum slots

**Fact:** two color codes already run on screen: armour identity
(`OverlayManager.Shielded = (0.35, 0.7, 1)` etc.) and per-weapon
tracer/muzzle colors. Adding damage-type-colored explosions is a third axis;
an Energy kill's blue lightning burst beside Shielded-blue pips can misread
as "shielded".

**Amendment:** add a one-page color-language rule to the proposal and choose
pack effects against it: each damage type's muzzle + tracer + explosion share
one palette; the three damage-type palettes stay distinct from the three
armour-identity colors. Then the feature genuinely deepens the counter
language instead of muddying it.

## F. Prefer mesh shells over looping particle shields

**Fact:** Shielded enemies include swarm waves (e.g. 5× Lancer) and the boss
variants — that is 5–8 concurrent looping particle systems layered under
existing `WorldHealthBar`s at a fixed camera, on a fill-rate-bound target.

**Amendment:** make the Eric-style **mesh shell with a rim/fresnel material**
the primary shield visual (one draw, no particle churn, cleaner read at the
38° camera), with the ETFX particle bubble as a boss-only upgrade if the
testbed shows it earns the cost. Keep the ShieldAura component design as
proposed — only the visual payload changes.

## G. Portal timing must match how waves actually spawn

**Fact:** `WaveManager` spawns groups over a long window (per-group
`startOffset` + per-unit `spawnGap`; waves run tens of seconds). A one-shot
portal at wave start followed by enemies popping in 30 s later reads broken.

**Amendment:** pick one explicitly: (a) a **looping** portal held open for
the spawner's active window — which lands in the attach/detach bucket
alongside shields and weather (the proposal's "everything else is a pooled
one-shot" undersells this), or (b) a small pooled per-spawn flash on each
unit (cheap, works with staggered spawns, no new lifetime code). (b) is the
lower-risk default; (a) is the better look if the attach/detach path is being
built for shields anyway.

## H. Minor precision fixes

- "Trauma is unused" → imprecise. `ShakeFootfall`/`ShakeCoreHit` already use
  the trauma system (TowerHealth deaths, core hits). The real gap — and the
  proposal's point stands — is that `KickExplosion` is kick-only; adding
  trauma to explosions is a good, small change.
- Testbed screening: the project already has `CombatVFX_Testbed` and
  `CopyTestbedVFXToConfigAndScenes` — make "screen every candidate effect in
  the testbed against both biomes before wiring" an explicit step 0.5, since
  the loop already exists.
- Weather presets: new `WeatherPreset` assets must also be added to each
  theme's `EnvPack.weatherPool` (the seed draws from the pack) — one line of
  wiring the proposal omits.

---

## Confirmations — verified TRUE, keep as written

- `VFXDirector` is provider-agnostic as of `46626d9`: pools any prefab with a
  Shuriken `ParticleSystem`; CFXR clear-behaviour neutralised when present;
  pooling/prewarm as described. (This was NOT true before that commit.)
- `WeatherApplier`: `MaxAlphaLayers = 3` (R14, warn-not-block), authored
  prefab source supported, weather is a level property by doctrine — the
  proposal's fairness framing is exactly right.
- `OverlayManager.Shielded = (0.35, 0.7, 1)` — the blue-matching argument holds.
- `Enemy.SetArmourType` exists and is the right hook for ShieldAura.
- `CameraShake` is the right home for more feel; do not add a second system.
- Input System 1.20.0; `CompressAllTextures512` exists; SaveData persists
  per-channel volumes (`GetVolume`/`SetVolume`) — a `haptics` flag fits there.
- Skipping VFX Graph (Vefects) for WebGL: correct, keep.
- Shield visibility is the single highest-value item in the proposal — the
  counter pillar becomes visible *before* the first wrong shot.

## Amended sequencing (replaces "Recommended sequencing")

0.  Vendor policy: copy used prefabs into `Assets/_COREHOLD/VFX/`; add the
    Light-stripping guard to the director's prefab prep (item C).
0.5 Testbed screening pass on all candidates + write the color-language rule
    (item E).
1.  Shielded visibility (mesh shell first) + explosion-by-type within the
    color rule.
2.  Spawn portals with the timing decision made explicitly (item G).
3.  Weather/fog presets per biome (+ `EnvPack.weatherPool` wiring).
4.  Trauma on explosions now; haptics only as a `.jslib` spike (item B).
