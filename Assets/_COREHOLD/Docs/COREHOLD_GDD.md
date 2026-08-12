# COREHOLD — Game Design Document

**Version 2.0 · 8 August 2026 · Ship target: Sunday 16 August 2026**
Sci-fi mech tower defense · Unity URP · Web (WebGL2) + mobile browser · Free to play

---

## 0. How to read this document

This is a build spec, written to be handed to CoPlay (or any Unity coding agent) one ticket at a time. It is deliberately over-specified: literal numbers, literal class names, literal field names, literal prefab hierarchies. Values marked **[TUNE]** are knobs, not facts. Items marked **[VERIFY]** must be confirmed inside the Unity Editor before they are trusted, and every one has an entry in §17.

The governing principle behind every decision: **simplicity and playability beat technical wizardry.** Anything that cannot be built, tested and tuned inside the window is cut or explicitly deferred.

Two things to know about provenance. All asset-pack facts in §4 come from vendor documentation, store pages and review threads researched on 8 August 2026 — reliable, but not the same as reading the files. I attempted to read the actual project on the laptop to extract real prefab paths, and the desktop bridge disconnected mid-session, so **every asset path here is a pattern, not a confirmed path.** §4.7 contains a script that dumps the real manifest; it is Ticket 4, and it runs before any gameplay code, for a reason.

And the balance in §7 and §8 is not vibes. It was modelled numerically, then adversarially reviewed, then re-derived after the review found that the first model's wave durations disagreed with its own wave table. The model is in Appendix A and it runs in a second. When you tune, tune the model first.

---

## 1. Decision record

These are the choices that shape everything else, recorded so they are not silently relitigated on day four.

**The name is COREHOLD.** One word, all caps in logo lockups, "Corehold" in prose. Chosen from thirty candidates because it is the only strong option that is completely unoccupied: zero results on Steam, zero on itch.io, zero on Google Play or the App Store, no live trademark near the games classes. The only mark ever filed was Mitsubishi Electric's for electrical-discharge machining equipment, cancelled June 2020. It also does real work — reactor *core* plus *hold* the line is the whole game in eight letters — and it keeps the stressed-first-syllable, hard-consonant shape of HOLDFAST without the *Nations At War* collision. Register `corehold.io`; the `.com` belongs to an unrelated small business and is not worth fighting for. Be aware the genre already contains *Core Defense* and *Hold the Core*, so lean on a distinctive logo and always ship the name as one word.

**Fixed-path, fixed-hardpoint tower defense.** Not free placement, not a maze-builder, not a hero hybrid. Enemies walk a designer-authored route; the player builds on pre-placed pads. This deletes pathfinding, placement validation, path recalculation and blocking logic from the project — two days of work and the largest single class of bugs in the genre. It also makes the game playable with one finger.

**Fixed camera.** No pan, rotate or zoom in the MVP. The level fits one 16:9 screen with UI margins. Second-largest scope saving in the document, guarantees identical behaviour on phone and desktop, and lets the level be composed like a diorama.

**Single-touch input.** One tap, one raycast. Unity's multi-touch on mobile browsers is unreliable — open reports of edge-of-screen phantom contacts and index-overwriting producing wrong deltas — and a TD needs none of it.

**Asset-locked art.** No custom 3D content, no custom animation, no grey boxes. Any mechanic requiring art we do not own is not in this document. UI is assembled from Sci-Fi UI Pack Pro sprites plus flat fills and TextMeshPro; turret and enemy icons are rendered from the prefabs themselves by a script (§9.5), not drawn by hand.

**Factions read by colour, not silhouette.** The single most important art decision, and the assets hand it to us. Every Slava Z. pack ships the same models in seven to thirteen albedo colour variants. Player structures use the cyan variant with cyan emissive; hostiles use the orange/rust variant with orange emissive. Both sides drawn from the same packs sharing one atlas per pack costs nothing in memory and helps batching. It also reads correctly at phone size, which silhouette alone would not.

### 1.1 The Unity version decision — settle this before creating the project

You specified Unity 6.5. Here is the situation, and a recommendation you can overrule.

Unity 6.5 is real and current: `6000.5.7f1`, released 15 June 2026, patched 5 August 2026. But Unity classifies it as a *"Supported release,"* not LTS — it gets fixes only until 6.6 ships, and 6.6 has been in beta since 24 June 2026. Unity 6.3 LTS (`6000.3.21f1`, patched 29 July 2026) is supported to December 2027, has twenty-one patch releases behind it against 6.5's seven, and is what Unity's own guidance points at for *"developers who are about to lock in production on a specific version."*

The risk is specific, not abstract. Unity 6.5 swapped the Emscripten toolchain from 3.1.38 to 4.0.19 and enabled WebAssembly 2023 by default. A toolchain change is exactly what produces a browser-only, hard-to-diagnose failure — surfacing on day six, when there is no time to absorb it.

> **Recommendation: pin `6000.3.21f1` (Unity 6.3 LTS).** It clears the Creepy Cat URP floor (that pack requires 6.3.11f1 or newer), clears every other pack, and is the boring option known to work.
>
> **If 6.5 is non-negotiable**, pin `6000.5.7f1` exactly and treat Ticket 5 — empty project built to Web and opened on a real phone — as a hard day-zero gate. Any anomaly, drop to 6.3 LTS the same day, while the cost is an hour rather than a day.

Never start on Unity 6.0 LTS: it reaches end of support 16 October 2026.

### 1.2 Scope contract

The review of version 1.0 of this document concluded, correctly, that its scope was roughly twelve to fourteen days of work in eight. The largest hidden cost is hand-assembly: these packs ship no constructor tool, so every turret and every enemy is built by dragging parts into bone containers in the Hierarchy. That is five to eight hours per category, and version 1.0 treated it as a fragment of two days.

So the scope is pre-cut here, on paper, rather than discovered on day three.

**Core scope — this ships.** One map. One merged ground route with two spawn legs. Eight hardpoints. Five turrets (four weapons plus one support) at three tiers each. Six enemy types plus one boss. Ten waves. Three difficulty tiers. Roughly nine minutes per run.

**Upside — added only if a day ends early.** Prime Node sixth turret. Second independent ground lane. Two more hardpoints. Waves eleven and twelve. Unique boss model. Damage numbers.

Treat the upside list as upside. Do not start any of it before day six.

---

## 2. Game overview

### 2.1 Premise

Refinery Delta is an automated ore-processing installation on a dead world. Its reactor core keeps the site's shield generator lit, and the site's own mining machines have gone rogue — reactivated by something in the deep seams, now grinding back toward the plant in waves.

You are the defence controller. You have no units. You have salvage, a network of prefabricated turret hardpoints, and about nine minutes.

That is the whole story: four lines on the title screen, two on the results screen. No cutscenes, no dialogue, no codex. The setting exists to justify sci-fi turrets shooting sci-fi mechs on an industrial map, and it stops there.

### 2.2 The pitch

Hold a reactor core against ten escalating waves of rogue mining machines, using five turret types with real rock-paper-scissors counters, in a nine-minute session that runs in a browser tab.

### 2.3 Session shape

Ten waves. Combat time is 449 seconds. Because the player controls when each wave starts (§8.4), total session length varies from about eight minutes for an aggressive player who chains waves to about eleven for a deliberate one. Losing is fast and restarting is instant — one tap, no penalty, no load screen. That is the right shape for a free web game where the median player decides inside ninety seconds whether to continue.

Three difficulty tiers give replay value at almost no content cost: Normal unlocked from the start, Veteran on completing Normal, Nightmare on completing Veteran. A star rating on remaining core integrity gives a reason to replay a tier already beaten.

### 2.4 What makes it good

Three things, and they are what to defend when time runs short.

**The counter system is real and it is visible.** Three damage types meet three armour types in a multiplier table shown in the UI, and every enemy carries a persistent armour pip — visible on the unit, and on the next-wave preview *before* the wave starts. Choosing the wrong turret is a legible mistake the player can see coming and correct. Weighted across the full run, the three damage types score 0.954, 0.871 and 0.845 in effectiveness, so no type is a safe default and each has exactly one bad matchup.

**Placement matters because hardpoints are scarce and the route snakes.** Eight hardpoints, one turret each, on a route authored so every pad covers at least two path segments and three premium pads cover four or more. What goes where — and which support aura reaches which cluster — is the strategic core.

**Chaining waves is a real gamble.** The player may start the next wave while the current one is still on the field, and the bonus scales with how many enemies are still alive when they do. Overlap two waves and the salvage is significant; misjudge it and both waves reach the core together. This is the skill-expression ceiling and it costs about twenty lines.

### 2.5 Explicitly out of scope

No campaign or multiple maps. No meta-progression, unlocks, currencies or accounts. No mid-run save. No backend or leaderboards beyond a local best score in PlayerPrefs. No monetisation in v1 — free, ad-free, IAP-free, which is also fastest to ship. No app store submission; the mobile target is the mobile *browser*, which needs no review. No procedural generation. No enemy abilities beyond movement, armour, the Roller's phase change and one boss speed phase.

---

## 3. Core loop

### 3.1 Moment to moment

The player sits in a build phase with a known salvage balance, a visible next-wave composition including armour types, and a set of empty and occupied hardpoints. Tapping an empty pad opens a menu of five turrets with costs, unaffordables greyed. Picking one places it immediately — no build timer. Tapping an occupied pad opens an upgrade/sell panel showing exactly what the next tier costs and changes.

Tapping Start Wave begins the assault. Enemies stream from two ground entrances that merge, and from wave three onward from an air corridor. Turrets acquire and fire automatically. The player's only in-wave inputs are building, upgrading, chaining the next wave, and toggling game speed.

When the last enemy of the final active wave is dead or has leaked, a clear bonus is paid and the build phase resumes. Anything reaching the core removes integrity and despawns. At zero integrity the run ends.

### 3.2 State machine

`Boot → Title → Briefing → Build → Wave → (Build | Victory | Defeat)`

`Build` and `Wave` alternate ten times — one Build→Wave transition per wave. `Wave` exits to `Victory` after wave ten resolves with integrity above zero, and to `Defeat` from either state the instant integrity reaches zero. `Briefing` is a two-second establishing shot on first entry only, skippable by tap.

Because waves can be chained (§8.4), `Wave` is not exclusive: `WaveManager` may have two waves active simultaneously. `GameManager` remains in `Wave` until the live-enemy count reaches zero **and** no wave remains unstarted in the queue.

### 3.3 Failure and feedback

Core integrity starts at 20 on Normal, 15 on Veteran, 10 on Nightmare. A leaker deals its `leakDamage`: 1 for light units, 2 for mediums and air, 3 for the Breaker, 20 for the Colossus — the boss reaching the core is an instant loss regardless of tier. The integrity bar flashes red on every leak; the camera shakes on leaks only when at least 1.5 seconds have passed since the last shake, so a twelve-Scuttler breach does not turn into a seizure.

Star thresholds are **fractional, not absolute**, because maximum integrity differs per tier: three stars at ≥ 90% of starting integrity, two at ≥ 50%, one above zero. On Normal that is 18+, 10+, 1+; on Nightmare, 9+, 5+, 1+.

---

## 4. Asset lock manifest

### 4.1 What we own and what it actually contains

The critical finding, which shapes the entire technical plan: **not one of these packs contains gameplay code.** No aiming scripts, no firing scripts, no projectile logic, no health systems, no VFX in the mech packs. The only runtime scripts in the whole vendor tree are Creepy Cat's door and light behaviours and Cartoon FX's `CFXR_Effect` / `CFXR_Settings`. Everything in §12 is written from scratch. That is fine — but it must be planned for, not discovered on day three.

| Vendor folder | Product | Publisher | Role |
|---|---|---|---|
| `Mech_Constructor_Turrets` | Sci-Fi Turret Constructor (112002) | Slava Z. | **All five buildable turrets, all tiers** |
| `Mech_Constructor_LtMed` | Mech Constructor: Light and Medium Robots (39969) | Slava Z. | Strider, Lancer |
| `Mech_Constructor_Spiders` | Mech Constructor: Spiders and Tanks (54074) | Slava Z. | Scuttler, Roller, Breaker |
| `Mech_Constructor_Humanoids` | Mech Constructor: Humanoid Robots (80255) | Slava Z. | Colossus boss — **conditional, §4.5** |
| `Mech_Constructor_Vehicles` | Sci-Fi Vehicle Constructor (174229) | Slava Z. | Unused (4–8k tris/unit is too heavy) |
| `RipVerticesStudio/…Drone…` | Sci-Fi Drone: Stylized Enemy Unit (325729) | Rip Vertices Studio | Wasp |
| `Destructible_Humanoid_Robot` | Destructible Humanoid Robot (137017) | Slava Z. | Static wreck prop only — **§4.6** |
| `Creepy_Cat/3D Scifi Kit Vol 4` | 3D Scifi Kit Vol 4 (231805) | Creepy Cat | The level environment |
| `TD_Sci-Fi_Turrets_Pack_V2` | TD Sci-Fi Turrets Pack v2 (309721) | Firadzo Assets | Core structure + landmark props — **§4.4** |
| `TD_Sci-Fi_Turret1_Example` | TD Sci-Fi Turret FREE (246331) | Firadzo Assets | **Delete** — Built-in RP only, redundant |
| `SCI-FI UI Pack Pro` | SCI-FI UI Pack Pro (149421) | D.F.Y. Studio | UI sprites |
| `JMO Assets/Cartoon FX Remaster` | Cartoon FX Remaster (4010) | Jean Moreno | All VFX |
| `IndieGameModels/SFX/Turret SFX` | Turret SFX (19689) | Indiegamemodels | Weapon and turret audio |

Thirteen folders; twelve after deleting the free Firadzo pack.

### 4.2 Why Slava Z.'s Turret Constructor is the turret art, not the TD turret pack

The highest-leverage asset decision here, and it runs against the obvious choice.

The Firadzo pack is a turret pack *for tower defense*, so it looks like the answer. It is not, for one reason: its turrets are **8,175 to 17,961 vertices each**. Eight placed turrets is 65,000 to 144,000 vertices of turrets alone, before an enemy or a wall. The mobile frame budget in §13.1 is 130,000 *triangles* — the two units are not interchangeable, but for meshes of this kind they are the same order of magnitude, and that is the point: the turrets alone would consume most or all of a frame. It does not fit, and optimising elsewhere will not make it fit.

Slava Z.'s Sci-Fi Turret Constructor runs **500 to 3,000 triangles per assembled turret**. Eight of those is 4,000 to 24,000. An order of magnitude of headroom.

It is also structurally the better fit. The pack is organised as seven turret bases, five tower bases, eight cockpits, five shoulders, nine half-shoulders, nine back parts, and six weapon families — big mortars, two-barrel cannons, rayguns, radars, plasma guns, rocket launchers — **each in three levels**. Six families in three tiers is a TD roster and upgrade tree, pre-built, in the right style. Our five turrets map onto five of the six families directly.

And because the enemies come from sibling packs by the same author — "all the parts are fully compatible," "the textures match" — turrets and enemies share one visual language and a small set of materials, which is a direct SRP Batcher win.

The detail that makes it work: assembly is base → cockpit/shoulder → weapon, each a separate GameObject parented into a named bone container. **The cockpit/shoulder GameObject is the yaw pivot and the weapon GameObject is the pitch pivot, for free.** `TurretAim` needs two transform references and no rigging. **[VERIFY]** that the weapon prefab's pivot sits at its mount rather than behind it; if pitch swings the barrel through the housing, wrap it in an empty at the trunnion. Budget two hours across all five chassis, not one.

### 4.3 Turret assembly is real work — schedule it

Five chassis, each needing a base, tower base, cockpit, shoulders, back part, three weapon-tier meshes, muzzle locators, pivot verification and a cyan material variant. At forty-five to ninety minutes each that is four to seven and a half hours. It sits on day two alongside eight code tickets, and it is the single most likely cause of a day-two slip.

Mitigation: build one chassis end to end on day one as part of the vertical slice, so the workflow is known before four more are attempted, and assemble the remaining four as the *first* thing on day two, before any code.

### 4.4 What the TD Turrets Pack v2 is for

Not wasted — its turrets are hero pieces, which is right for objects that appear **once**.

The **Shield Generator** becomes the Core, the thing being defended. Thematically perfect, visually distinct from anything the player builds, and one instance at 12k verts is affordable. The **Radar** becomes a skyline landmark. If day six ends early, the **Tesla Turret** becomes the Prime Node: a sixth turret unlocked at wave six, limited to one instance, expensive and powerful. One instance, one hero mesh. Upside, not MVP.

Delete `TD_Sci-Fi_Turret1_Example` on day zero — Built-in RP only, and it duplicates art we already carry.

### 4.5 The boss: a conditional with a pre-decided fallback

The Colossus should be a Mech Constructor Humanoid; a giant biped is the right final-wave silhouette. But that pack carries real risk: version 2.1, last republished September 2023, compatibility table listing **only Unity 2020.3.40f1** while all four siblings carry a Unity 6 row — and its description text claims Unity 6 testing, contradicting the table. Separately, only its "crouched" variant ships animations at all.

**Gate on day two, ninety minutes, hard stop.** Import, drop a crouched-variant mech in, convert materials, play Walk and Death. If it works, the Colossus is that mech at 1.4× scale in a unique dark-red variant with heavy orange emissive.

**If it does not, take the fallback** — pre-decided so nobody makes the call under pressure. The Colossus becomes the heaviest Spiders and Tanks chassis at 1.6× scale, unique colour, emissive core, slower heavier walk. That pack is confirmed working in URP on Unity 6 as of a July 2026 review, uses animations already wired for the Breaker, and costs nothing to stand up. The boss reads as a boss through scale, colour, a dedicated health bar and a music change — not through its mesh.

### 4.6 The Destructible Humanoid Robot

Despite the name, no fracture system, no destruction scripts, no demo. "Destructible" means it ships either as a single 16k-triangle skinned mesh or as 20k triangles of separate parts on the skeleton, so you can detach limbs yourself. It ships **no animations**.

An unanimated 20k-triangle character has no place in a wave. It makes an excellent **static wreck** — a downed titan half-buried at the map edge, limbs scattered, telling the player what happened before they arrived. One instance, no animation, no scripts, real storytelling. Place it on day four. Cheapest narrative in the project.

### 4.7 Ticket 4 — dump the real asset manifest

The vendor packs are already consolidated under `Assets/Vendor/` in the existing project; if a fresh import scatters them to Asset Store default roots, move them under `Assets/Vendor/` first — the script guards against a missing root but cannot find packs that are elsewhere.

Create `Assets/Editor/AssetManifestDumper.cs`, run **Tools → COREHOLD → Dump Asset Manifest**. On a full unpruned tree expect five to thirty minutes, so run it after the coarse deletion pass, not before.

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class AssetManifestDumper
{
    const string Root = "Assets/Vendor";
    static int _processed;

    [MenuItem("Tools/COREHOLD/Dump Asset Manifest")]
    public static void Dump()
    {
        if (!AssetDatabase.IsValidFolder(Root))
        {
            Debug.LogError($"[COREHOLD] '{Root}' does not exist. Move the vendor packs under {Root} first, " +
                           "or change the Root constant. Aborting — no manifest written.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# COREHOLD Asset Manifest");
        sb.AppendLine($"Generated {System.DateTime.Now:yyyy-MM-dd HH:mm} · Unity {Application.unityVersion}");
        sb.AppendLine();

        int prefabCount = 0;
        try
        {
            prefabCount = Section(sb, "Prefabs", "t:Prefab", path =>
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) return null;
                int tris = 0;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                    tris += CountTris(mf.sharedMesh);
                foreach (var sr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    tris += CountTris(sr.sharedMesh);
                var rends = go.GetComponentsInChildren<Renderer>(true);
                var mats = new HashSet<string>(rends.SelectMany(r => r.sharedMaterials)
                                                    .Where(m => m != null).Select(m => m.name));
                int skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                return $"{tris} tris · {rends.Length} renderers ({skinned} skinned) · mats: {string.Join(", ", mats)}";
            });

            Section(sb, "Animation clips", "t:AnimationClip", path =>
            {
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                              .Where(c => !c.name.StartsWith("__preview")).ToArray();
                return clips.Length == 0 ? null
                     : string.Join(", ", clips.Select(c => $"{c.name} ({c.length:0.00}s, {(c.isLooping ? "loop" : "once")})"));
            });

            Section(sb, "Materials", "t:Material", path =>
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) return null;
                var shader = m.shader == null ? "<MISSING SHADER>" : m.shader.name;
                bool builtIn = shader.StartsWith("Standard") || shader.StartsWith("Legacy") || shader.StartsWith("Mobile/");
                return $"shader: {shader}{(builtIn ? "   <-- BUILT-IN, needs URP conversion" : "")}";
            });

            Section(sb, "Textures", "t:Texture2D", path =>
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) return null;
                ti.GetSourceTextureWidthAndHeight(out int sw, out int sh);
                bool mult4 = sw % 4 == 0 && sh % 4 == 0;
                return $"source {sw}x{sh} · maxSize {ti.maxTextureSize}"
                     + (mult4 ? "" : "   <-- NOT multiple of 4, blocks block compression");
            });

            Section(sb, "Audio clips", "t:AudioClip", path =>
            {
                var a = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (a == null || ai == null) return null;
                var web = ai.GetOverrideSampleSettings("WebGL");
                return $"{a.length:0.00}s · {a.frequency}Hz · {a.channels}ch · WebGL loadType: {web.loadType}";
            });
        }
        finally { EditorUtility.ClearProgressBar(); }

        var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../AssetManifest.md"));
        File.WriteAllText(outPath, sb.ToString());

        if (prefabCount == 0)
            Debug.LogError($"[COREHOLD] Manifest written to {outPath} but found ZERO prefabs under '{Root}'. " +
                           "This is a failure, not a success — check the vendor packs are actually there.");
        else
            Debug.Log($"[COREHOLD] Manifest written to {outPath} — {prefabCount} prefabs catalogued.");
    }

    static int CountTris(Mesh mesh)
    {
        if (mesh == null) return 0;
        int t = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) t += (int)(mesh.GetIndexCount(i) / 3);
        return t;   // GetIndexCount avoids the big int[] copy that mesh.triangles allocates
    }

    static int Section(StringBuilder sb, string title, string filter, System.Func<string, string> describe)
    {
        var paths = AssetDatabase.FindAssets(filter, new[] { Root })
                        .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).ToList();
        if (paths.Count == 0) { sb.AppendLine($"## {title}").AppendLine().AppendLine("_none found_").AppendLine(); return 0; }

        sb.AppendLine($"## {title}  ({paths.Count} assets scanned)").AppendLine();
        string lastFolder = null;
        int written = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (EditorUtility.DisplayCancelableProgressBar($"Manifest: {title}", path, (float)i / paths.Count)) break;

            string info;
            try { info = describe(path); }
            catch (System.Exception e)
            {
                sb.AppendLine($"- `{Path.GetFileName(path)}` — **ERROR: {e.Message}**");
                continue;   // surface broken assets; they are exactly what we are hunting for
            }
            if (info == null) continue;

            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (folder != lastFolder) { sb.AppendLine().AppendLine($"### {folder}").AppendLine(); lastFolder = folder; }
            sb.AppendLine($"- `{Path.GetFileName(path)}` — {info}");
            written++;

            if (++_processed % 200 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
        }
        sb.AppendLine();
        return written;
    }
}
```

Then reconcile every `[VERIFY]` path in this document against `AssetManifest.md`.

### 4.8 Import settings, day zero

The vendor tree is roughly 3.2 GB, dominated by Creepy Cat's 2.0 GB and the UI pack's 447 MB.

**Delete these on day zero:** every demo scene not in use; every `.PSD`; every 4096 and 8192 texture; unused colour variations from every Slava Z. pack, keeping cyan-family, orange-family and one boss variant; the realistic/PBR map set if the hand-painted path is chosen; the whole `TD_Sci-Fi_Turret1_Example` folder.

**Do not delete unused meshes on day zero.** The level is not authored until day four, so you cannot know which are unused, and deleting after day four breaks live scene references. Unreferenced meshes cost import time and Library size, not download size — Unity only ships what a build scene references. Skip it entirely.

Texture max sizes: environment albedo 1024, environment normal 512, character atlases 1024, UI 512. Nothing ships above 1024. Note that the Slava Z. packs author a single 4K atlas per pack, so clamping to 1024 is a 16× pixel reduction — that is an art decision, not just an import setting. **Eyeball one mech at 1024 on day zero** before committing; 2048 for characters is the fallback if it reads badly.

Two specific traps. The UI pack contains **many source textures whose dimensions are not multiples of four**, silently blocking block compression; the manifest script flags them from source dimensions. And Cartoon FX Remaster ships as both a free subset and a paid 50-effect pack, **both installing to the same path** — check for `Readme Cartoon FX Remaster FREE.html`. **[VERIFY]**

Material conversion: every Slava Z. pack and the Firadzo pack ship Built-in RP materials. Run **Edit → Rendering → Materials → Convert All Built-In Materials to URP** once, then re-hook emission manually — the converter frequently misses it. Creepy Cat ships a native URP package on the 6.3+ branch; use that branch rather than converting its Built-in variant. Cartoon FX is natively URP but needs **Depth Texture enabled in the URP Asset** or its soft particles will not render.

### 4.9 The audio gap

There is no music in the vendor list. Turret SFX covers weapons (twenty files: rotation start/stop/loop, missile fire and impact, energy shots, bullet shots) and Creepy Cat ships ambience and UI sounds. Nothing covers a menu or combat track.

Decide this on **day one**, not day six. Two tracks minimum — an ambient build loop and a driving combat loop — plus optionally a boss variant. In preference order: buy a small sci-fi music pack for under $20; use a permissively licensed track with clear commercial terms; or ship ambience only, which is materially worse but survivable. **[VERIFY]** the Creepy Cat `Sound` folder first, since it may already hold usable beds.

---

## 5. Level design — Refinery Delta

### 5.1 Framing

One map, entirely visible on one 16:9 screen with no camera movement. Playfield approximately **130 × 75 metres**. Perspective camera at 38° pitch and 35° vertical FOV, positioned so the play area fills the centre with roughly 12% HUD margin at the top and 18% at the bottom. At that framing the camera sits around 100 m out and 60 m up, and nearest visible ground is roughly 40 m away — a number that matters in §5.5.

Landscape only. A portrait mobile browser gets a full-screen rotate prompt; there is no portrait layout.

### 5.2 The route

Two ground entrances — left edge and upper edge — feed short 30 m legs that **merge at roughly 20% of the route** into a single shared 120 m snake ending at the Core in the lower right. Total ground route length is **150 metres**.

Merging early rather than running two full parallel lanes is a deliberate cost decision: it reads visually as two approaches, costs one route to author and tune instead of two, and keeps the balance model one-dimensional. The second independent lane is on the upside list, not the plan.

**The route must snake, and this is load-bearing.** The entire tower balance assumes each turret gets meaningful time on target, which only happens when one turret's radius covers multiple path segments. The authoring rule:

> Every hardpoint must cover at least **two** route segments at that hardpoint's intended turret's tier-1 range. At least **three** hardpoints ("premium") must cover **four or more**. Verify with per-turret-type gizmo rings — 12 m for Autocannon, 13 m for Missile, 10 m for Arc Node, 20 m for Mortar — not a single generic sphere, because a 12 m check over-validates the Arc Node by 20% and under-validates the Mortar by 40%.

If the rule is not met the game becomes unwinnable from wave six regardless of the numbers. Check it on day four before anything else on the level.

A 150 m route inside a 130 × 75 m box implies real switchbacks — the straight diagonal is 150 m, so the route is effectively a folded diagonal. That is the intent. Author it as three or four hairpins around fixed refinery structures, so the geometry is justified by the set rather than looking like a racetrack.

Air enemies ignore the route entirely: they spawn from a single air corridor at the top edge and fly a straight line to the Core, roughly **95 metres**, at a fixed altitude of **4 metres**. That altitude is deliberately low. At 8 m a turret with 10 m range would have under 6 m of horizontal reach and effectively no air coverage at all; at 4 m the same turret reaches 9.2 m horizontally. Altitude is a balance number, not set dressing.

### 5.3 Hardpoints

Eight in the MVP, all available from wave one, gated only by salvage.

Three are **premium**, covering four or more segments — contested, and the natural home for high-tier single-target turrets. Two are **standard**, covering two or three. Two are **rear**, near the Core, covering only the final approach and the air corridor's terminal leg — the natural anti-air and last-resort slots. One is **overwatch**, set back from the route with poor close coverage but a clear line to most of the map; it exists specifically to make the Siege Mortar, with its 20–24 m range and 6 m minimum, worth building.

Each pad is a visible circle — a Creepy Cat floor plate or decal — with a cyan emissive rim that pulses gently when empty and goes dark when occupied. Empty pads are the primary call to action during a build phase and must read at phone resolution. The tap collider is a 1.5 m sphere on a 1.0 m visual, because a fingertip is imprecise and a missed tap on a pad feels like a broken game.

### 5.4 The Core and set dressing

The Core is the Firadzo Shield Generator on a raised Creepy Cat platform, cyan emissive dome, slow rotation. Integrity is mirrored physically as well as in the HUD: at 66% one dome segment goes dark and sparks, at 33% a second, below 20% the whole structure flickers and the emissive shifts cyan to amber. Three lines of code, more tension than any UI element. Because the amber shift is colour-only, it is always paired with the darkened segments and the numeric HUD value.

Everything else is Creepy Cat: refinery towers, pipe runs, containers, walkways, floor plates, cranes. The Destructible Humanoid Robot lies as a dismembered wreck against the left edge. The Firadzo Radar sits on the skyline.

### 5.5 Lighting — fully baked, no realtime shadows

This is a change worth explaining, because the obvious setup does not work at this camera.

With the camera roughly 100 m from the play area, a conventional 30 m shadow distance puts *every* object beyond the shadow cutoff — nothing would cast a shadow at all. Raising shadow distance to cover a 100–160 m range means a shadow atlas stretched across the whole map, plus a full extra render pass and a re-submission of every caster, which is precisely the draw-call budget we cannot afford (§13.1).

So: **directional light shadows are disabled entirely.** The environment is fully baked — everything static, lightmapped, with light probes for the units. Enemies and turrets get a **blob shadow**: a single small quad with a soft radial texture, one shared material, drawn under each unit. Blob shadows batch into effectively one draw call for all units, cost nothing, and at this camera angle and art style are visually indistinguishable from the real thing.

The rest: no HDR. No bloom by default — it is the effect this art style wants, but it costs multiple full-screen passes; it stays off unless day-six profiling shows it under 1.5 ms, and it is off unconditionally on mobile. Colour grading via LUT plus a vignette is nearly free and carries most of the mood. A handful of non-shadow-casting point lights give emissive pools at the Core and hardpoints.

---

## 6. Enemy roster

Seven units — six plus the boss. Every one maps to a pack with **confirmed animation clips**; that constraint eliminated several otherwise-attractive designs and it is why this roster will actually be moving on screen by day three.

### 6.1 Stats

| Unit | Source | HP | Armour | Speed | Bounty | Leak | Air | Role |
|---|---|---:|---|---:|---:|---:|:-:|---|
| **Scuttler** | Spiders — small spider | 45 | Unarmoured | 7.5 | 8 | 1 | – | Swarm filler. Punishes gaps. |
| **Strider** | LtMed — light robot | 110 | Plated | 5.0 | 12 | 1 | – | Baseline. Teaches kinetic-vs-plate. |
| **Lancer** | LtMed — medium robot | 190 | Shielded | 4.6 | 18 | 2 | – | Punishes an all-energy build. |
| **Wasp** | Rip Vertices drone | 70 | Unarmoured | 9.0 | 14 | 2 | ✈ | Ignores the route. Forces AA. |
| **Roller** | Spiders — roller form | 150 | Unarmoured | 11.0 → 4.6 | 20 | 2 | – | Sprints 60% of route, then unpacks. |
| **Breaker** | Spiders — tank chassis | 420 | Plated | 3.75 | 35 | 3 | – | Sponge. Needs focused high-tier fire. |
| **Colossus** | Humanoids (conditional §4.5) | 2800 | **Shielded** | 3.0 | 250 | 20 | – | Boss. Reaching the Core is an instant loss. |

HP is multiplied by the wave scalar in §8.2. Speeds are metres per second; the route is 150 m and the air line is 95 m, so a Strider traverses in 30 s, a Scuttler in 20 s, a Breaker in 40 s, and the Colossus in 50 s.

Two deliberate choices in that table. The **Wasp leaks 2**, not 1 — at 1, a player with no anti-air survives the all-air wave five and only discovers the problem around wave eight, three waves after the lesson was supposed to land. At 2, wave five costs 16 of 20 integrity and the message is unmissable. And the **Colossus is Shielded, not Plated** — that balances the run's armour mix to 30% Unarmoured, 29% Plated, 41% Shielded, which is what keeps the three damage types within 0.11 of each other in weighted effectiveness (§7.1). It also gives the Kinetic Autocannon, weak against the Plated mid-game, its payoff moment at the boss. The boss's armour type is shown in the wave-ten preview so this is a telegraphed check, not a gotcha.

### 6.2 Behaviour

The **Roller** came from the asset rather than the other way round. Spiders and Tanks ships `Transform to Roller`, `Roller_Idle`, `Roller_Roll`, `Roller_Turn while rolling` and `Transform to Spider` as authored clips. So it travels the first 60% of the route in roller form at 11.0 m/s, plays `Transform to Spider` at the 60% mark, and walks the rest at 4.6 m/s. A defence weighted toward the entrance barely touches it. It cost nothing to invent because the animation already existed.

The **Colossus** gains +40% speed below 50% health and its emissive shifts orange to white. That is the only enemy ability in the game — two lines in `Enemy.OnDamaged`, and it is the climax beat.

No enemy has a hit reaction; no pack ships flinch clips. Damage feedback is a brief white emissive flash on the material, a Cartoon FX spark at the hit point, and health bar movement. At this camera distance, flinch animation on a swarm would read as noise anyway.

### 6.3 Animation wiring

**Disable root motion on every enemy.** The Slava Z. packs ship root motion on nearly all locomotion clips — correct for a character controller, wrong for a waypoint follower, and it will fight `EnemyMover` and produce drift.

The consequence is foot-sliding if scripted speed does not match the clip's authored speed. Fix it once per enemy: measure the clip's implied ground speed, store it as `animatorClipSpeedRef` on the `EnemyDefinition`, and set `Animator.speed = moveSpeed / animatorClipSpeedRef`. Budget roughly one hour per enemy for the measurement — it is fiddly editor work, not a one-liner, and it interacts with the 2× speed toggle (§9.6).

Each enemy needs a four-state Animator: Locomotion, Death, plus Roll and Transform for the Roller only. Parameters `Speed` (float) and `Die` (trigger). That is the whole animation system.

Two performance settings are mandatory and free: Animator **Culling Mode = Cull Completely**, and SkinnedMeshRenderer **Update When Offscreen = off**. On Web there are no C# worker threads, so animation evaluation, skinning and draw submission all serialise on the main thread — a scene costing 2 ms natively can cost 6–10 ms in a browser.

A third setting, **Optimize Game Objects**, is the biggest win of the three and is **[VERIFY], not mandatory**. It strips the bone transform hierarchy — and §4.2 established that Slava Z. parts are parented into named bone containers. If the parts are skinned to the shared skeleton it is safe; if any are mesh renderers parented to bones, they will detach or vanish. Test it on one enemy on **day three**, not day six. If it breaks assembly, either expose the carrying bones (which negates most of the benefit) or skip it and rely on the concurrency cap in §8.1.

---

## 7. Tower roster

Five turrets, three tiers each, all assembled from Sci-Fi Turret Constructor parts. A tier upgrade swaps the weapon child mesh and leaves the base untouched — exactly how the pack is designed.

### 7.1 Damage and armour

| | vs Unarmoured | vs Plated | vs Shielded |
|---|---:|---:|---:|
| **Kinetic** | ×1.00 | ×0.50 | ×1.25 |
| **Energy** | ×1.00 | ×1.25 | ×0.50 |
| **Explosive** | ×1.30 | ×0.65 | ×0.65 |

Legible without a tutorial: plating is physical armour, so it stops mass and conducts energy; shields absorb energy and do nothing against mass; explosives shred soft targets and disappoint against anything hardened.

Weighted against the actual HP mix of a full run, the three types score **Kinetic 0.954, Energy 0.871, Explosive 0.845**. No safe default, and each type has exactly one bad matchup — Explosive's weakness is that it has two mediocre matchups rather than one terrible one, which is the price of being the swarm-clearer.

This table must be visible: a three-by-three grid on the tower panel with the current turret's row highlighted, an armour pip on every enemy that is **always visible and not tied to the health bar**, and armour icons on the next-wave preview. A counter system the player cannot see before committing is not a counter system.

### 7.2 The turrets

| Turret | Weapon family | Damage | Targets | Range | Special |
|---|---|---|---|---:|---|
| **Autocannon** | 2-barrel cannon | Kinetic | Ground + Air | 12–14 | Hitscan, single target. The workhorse. |
| **Missile Battery** | Rocket launcher | Explosive | Ground + Air | 13–15 | Leading projectile, 2.5–3.5 m splash. |
| **Arc Node** | Plasma gun | Energy | Ground + Air | 10–14 | Hitscan chain, 30% falloff per jump. |
| **Siege Mortar** | Big mortar | Explosive | **Ground only** | 20–24 | 6 m minimum, 4–5 m splash, arcing shell. |
| **Scan Relay** | Radar | — | — | 10–14 aura | No damage. Buffs turrets in radius. |

Ranges are deliberately compressed relative to a first draft that gave the Missile Battery 16–18 and the Mortar 26–30. At those numbers, range silently decided the game: adjusted for time-on-target, the Missile Battery dominated the Autocannon at every tier while also splashing and hitting air, and the correct play was Missiles everywhere plus a Mortar. With the Autocannon and Missile within 1 m of each other, raw DPS efficiency decides between them again — which is what the table is supposed to do.

**All range checks are 3D distance** from the turret's `RangeOrigin` transform to the enemy's `HitPoint`. This matters for air: at 4 m altitude, a 10 m turret has 9.2 m of horizontal reach, a 12 m turret 11.3 m, a 14 m turret 13.4 m.

### 7.3 Tier data

| Turret | Tier | Cost | Cumulative | Damage | Rate/s | Range | DPS | DPS/salv | Notes |
|---|:-:|---:|---:|---:|---:|---:|---:|---:|---|
| Autocannon | 1 | 100 | 100 | 10 | 2.0 | 12 | 20.0 | 0.200 | |
| | 2 | 130 | 230 | 15 | 2.8 | 13 | 42.0 | 0.183 | |
| | 3 | 200 | 430 | 25 | 3.6 | 14 | 90.0 | 0.209 | |
| Missile Battery | 1 | 150 | 150 | 45 | 0.6 | 13 | 27.0 | 0.180 | splash 2.5 |
| | 2 | 180 | 330 | 80 | 0.7 | 14 | 56.0 | 0.170 | splash 3.0 |
| | 3 | 270 | 600 | 140 | 0.8 | 15 | 112.0 | 0.187 | splash 3.5 |
| Arc Node | 1 | 120 | 120 | 14 | 1.5 | 10 | 21.0 | 0.175 | 2 targets → ×1.70 vs group |
| | 2 | 140 | 260 | 22 | 1.8 | 12 | 39.6 | 0.152 | 3 targets → ×2.19 |
| | 3 | 200 | 460 | 34 | 2.2 | 14 | 74.8 | 0.163 | 4 targets → ×2.53 |
| Siege Mortar | 1 | 200 | 200 | 90 | 0.35 | 20 | 31.5 | 0.158 | splash 4.0, min 6 |
| | 2 | 240 | 440 | 160 | 0.40 | 22 | 64.0 | 0.145 | splash 4.5, min 6 |
| | 3 | 300 | 740 | 260 | 0.45 | 24 | 117.0 | 0.158 | splash 5.0, min 6 |
| Scan Relay | 1 | 90 | 90 | — | — | 10 | — | — | +15% rate, +10% range |
| | 2 | 110 | 200 | — | — | 12 | — | — | +25% rate, +15% range |
| | 3 | 160 | 360 | — | — | 14 | — | — | +35% rate, +20% range, +10% dmg |

Splash damage falls off linearly to 40% at the edge. Arc chain damage falls to 70% of the previous target per jump. Scan Relay auras **do not stack** — a turret inside two relays takes the strongest only; without that rule, relay-clustering is a degenerate dominant strategy.

Reading the efficiency column: every turret dips at tier 2 and recovers at tier 3, though the Arc Node and Mortar recover only to roughly their tier-1 value rather than above it. That "upgrade valley" is deliberate — with eight hardpoints, the standing question is always whether to widen coverage or deepen it. The Arc Node's raw column understates it badly: at tier 3 its effective output against a tight group is 0.163 × 2.53 ≈ 0.41 per salvage, by far the best in the game, while against a lone target it is the worst. That is the intended shape.

**The table is a theoretical maximum.** With 180°/s yaw, a 6° aim gate and First-priority retargeting on a snaking route, a turret loses time re-slewing after every kill. The balance model applies a flat 0.80 slew-efficiency factor to account for it. Measure the real figure on day two and correct the model.

Selling returns **60%** of cumulative invested salvage, with no cooldown. See §8.5 for the one exploit this opens and the mitigation.

### 7.4 Targeting — no physics

Targeting uses a **live-enemy registry, not physics.** `WaveManager` maintains a `List<Enemy>` of everything alive; `TowerTargeting` iterates it and compares squared distances.

This matters more than it looks. The obvious implementation — trigger colliders on enemies plus `Physics.OverlapSphere` from turrets — has a trap: a collider that moves without a Rigidbody is a *static* collider, and moving it forces PhysX to rebuild its static AABB tree every frame, for every enemy, on a single-threaded WebAssembly main thread. Meanwhile, with at most fourteen live enemies and eight turrets, the registry costs 112 squared-distance comparisons per re-acquire tick — cheaper than a single `OverlapSphere`, and it needs no physics at all. **Enemies carry no collider and no Rigidbody.** Tap-to-inspect is not a feature.

Re-acquire runs on a **0.2 second timer, not per frame**, staggered across turrets by index so they do not all tick on the same frame.

Default priority is **First** — furthest along the route — which is the correct default and what players expect. The tower panel offers Closest and Strongest. Priority is per-turret and persists.

A turret holds its target until it dies, leaves range, or a re-acquire tick finds a strictly better one. `TurretAim` slews yaw at 180°/s and pitch at 120°/s and reports `IsAimed` when both are within 6°; `TowerWeapon` will not fire until then. That gate is what makes turrets feel mechanical rather than magical, and it is why the Autocannon's rate of fire feels different from the Mortar's. Yaw uses shortest-arc interpolation and is normalised each frame so it cannot unwind past 360°.

Projectiles **lead their target**: `Projectile` solves a first-order intercept against the target's current velocity. Missile speed is 22 m/s, Mortar shell speed 18 m/s on a 6 m arc apex. Without leading, a 22 m/s missile against a 9 m/s Wasp misses essentially always, which would silently make the Missile Battery ground-only and break the anti-air plan. If a projectile's target dies mid-flight, it continues to the last known position and detonates there — splash weapons still do useful work, and it looks correct.

---

## 8. Waves

### 8.1 The schedule

Ten waves. Each entry is count × unit @ spawn gap in seconds, +start offset.

| W | Composition | Units | Spawn window | Duration | Peak concurrent |
|:-:|---|:-:|---:|---:|:-:|
| 1 | 5× Scuttler @2.6 | 5 | 10.4 s | 30.4 s | 5 |
| 2 | 8× Scuttler @2.2 | 8 | 15.4 s | 35.4 s | 8 |
| 3 | 5× Strider @3.4 · 1× Wasp +12 | 6 | 13.6 s | 43.6 s | 6 |
| 4 | 8× Scuttler @1.9 · 4× Strider @3.6 +5 | 12 | 15.8 s | 45.8 s | 12 |
| 5 | 8× Wasp @2.8 | 8 | 19.6 s | 30.2 s | 4 |
| 6 | 8× Strider @3.2 · 5× Scuttler @2.2 +4 · 4× Wasp @4.0 +10 | 17 | 22.4 s | 52.4 s | 16 |
| 7 | 5× Lancer @4.4 · 3× Roller @6.0 +5 | 8 | 17.6 s | 50.2 s | 8 |
| 8 | 5× Lancer @4.6 · 10× Scuttler @2.2 +4 · 5× Wasp @3.4 +12 | 20 | 25.6 s | 51.0 s | 18 |
| 9 | 3× Breaker @8.0 · 8× Strider @3.0 +5 · 5× Wasp @3.4 +10 | 16 | 26.0 s | 56.0 s | 14 |
| 10 | **1× Colossus** +4 · 8× Scuttler @2.4 · 4× Wasp @3.6 +14 | 13 | 24.8 s | 54.0 s | 11 |

Total combat time 449 seconds.

The "peak concurrent" column is the **worst case where nothing dies**, which is the honest number — the earlier draft of this document reported peaks assuming 55% of units die en route, which conveniently understated exactly the waves designed to be survivable. Waves six and eight peak at sixteen and eighteen, above the fourteen the frame budget wants, and they peak highest precisely when the player is losing.

So `WaveManager` enforces a **hard cap of 14 live enemies**. When the cap is reached the spawn queue holds and resumes as units die. This makes the performance budget deterministic instead of aspirational, and it has a pleasant side effect: a player who is losing gets a slightly slower assault, which is a gentle rubber band nobody will notice.

### 8.2 Difficulty scaling

Enemy HP is multiplied by **`1.0 + 0.18 × (wave − 1)`**, reaching ×2.62 at wave ten. Bounties and leak damage do not scale with wave.

Difficulty tiers scale HP **and the economy**, which the first draft did not — it scaled HP up and starting salvage *down*, which made Veteran and Nightmare unwinnable by this document's own model at every wave past three.

| Tier | Enemy HP × | Economy × | Starting salvage | Core integrity |
|---|---:|---:|---:|---:|
| Normal | 1.00 | 1.00 | 300 | 20 |
| Veteran | 1.25 | 1.12 | 336 | 15 |
| Nightmare | 1.55 | 1.22 | 366 | 10 |

The economy multiplier applies to starting salvage, kill bounties and clear bonuses alike.

### 8.3 The difficulty curve

Difficulty margin — damage a competent player can deliver divided by wave HP — modelled conservatively (85% of salvage spent, 0.19 DPS per salvage, 0.80 slew efficiency, exposure ramping 0.30 → 0.46):

| Tier | W1 | W2 | W3 | W4 | W5 | W6 | W7 | W8 | W9 | W10 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Normal | 1.57 | 1.43 | 1.30 | 1.30 | 1.52\* | 1.28 | 1.48 | 1.40 | 1.28 | **1.04** |
| Veteran | 1.41 | 1.28 | 1.16 | 1.17 | 1.36\* | 1.14 | 1.33 | 1.25 | 1.14 | **0.93** |
| Nightmare | 1.24 | 1.12 | 1.02 | 1.02 | 1.19\* | 1.00 | 1.17 | 1.10 | 1.00 | **0.82** |

Normal opens forgiving because it is teaching, holds between 1.28 and 1.52 through the midgame, and closes at 1.04 — a player who has built badly loses the boss and a player who has built well wins with visible margin. Veteran runs about 0.15 tighter throughout and closes at 0.93; Nightmare sits at or barely above 1.0 for most of the run and closes at 0.82. Those sub-1.0 endings are intentional: the model is conservative on three separate axes, so a below-1.0 margin means "requires above-model play," which is exactly what those tiers are for. Nightmare additionally halves core integrity, a difficulty lever the model does not capture at all.

Total earnable across a full run is 3,515 on Normal, 3,934 on Veteran and 4,283 on Nightmare.

\* Wave five reads soft and is not. It is entirely airborne, and air ignores the route — a player who has built only Siege Mortars has literally zero effective DPS and loses 16 of 20 integrity. The single scout Wasp in wave three exists to telegraph this, and should be accompanied by a wave-three briefing line: **"Airborne contact detected. Mortars cannot elevate."**

Expect to spend day seven tuning. When tuning, move the wave HP scalar first — it is one number and reshapes the whole curve — then individual enemy counts, and only then tower numbers, which ripple through every wave simultaneously.

### 8.4 Economy and wave chaining

Starting salvage is 300 on Normal: three Autocannons, or a Missile Battery and an Autocannon with change, or an Arc Node and a Scan Relay. Kill bounties are in §6.1. Wave clear pays **60 + 18 × waveNumber**, so 78 for wave one rising to 240 for wave ten. Total earnable on a perfect Normal run is 3,515.

**Wave chaining replaces the early-call bonus.** The first draft paid a bonus for starting the next wave early during a build phase, which is a free +30 salvage per wave with no downside — the build phase confers no capability the wave phase lacks, since building during a wave is allowed and the next wave's composition is always visible. That is not a decision, it is a button.

Instead: the Start Wave button remains available **while the current wave is still on the field**, and pressing it starts the next wave immediately on top of the current one. The bonus is **8 salvage per enemy still alive at the moment of the call, capped at 80**. Chaining two waves is worth real money and is genuinely dangerous — the concurrency cap in §8.1 does not save you, it just delays the arrival of units that are all still coming.

There is no build timer. The player takes as long as they want between waves, which is why session length varies with play style rather than being fixed.

### 8.5 The one exploit, and what to do about it

Selling at 60% is not arbitrage — it is a strict loss, so there is no salvage loop. But combined with a full next-wave preview, no build timer and instant placement, the patient optimal play is to **hot-swap the entire loadout between waves**: sell the ground turrets before the all-air wave five, rebuild after. A 40% tax buys a perfect counter every time, which flattens the counter system into a chore.

Three mitigations, in order of preference. Show the next wave's composition but **only one wave ahead**, so a swap is a bet rather than a certainty — already the plan, and it costs nothing. Reduce the refund to **50%** if playtesting shows swapping is still worth it. And note that wave chaining (§8.4) creates real time pressure that punishes long between-wave micromanagement, since a chained wave arrives while the player is still shopping.

Do not add a sell cooldown. It punishes correcting a genuine mistake, which is the thing the counter system depends on the player being able to do.

---

## 9. UI and UX

### 9.1 Screens

**Title.** Logo, three difficulty buttons with locked states, best score from PlayerPrefs, mute toggle. This screen is also the **audio gate** — browsers refuse audio without a user gesture, so nothing plays until Play is tapped. Do not attempt an auto-playing menu track; it will silently fail everywhere.

**HUD.** Top-left, core integrity as a segmented bar with a numeric value. Top-centre, "WAVE 4 / 10" plus the next wave's composition as unit icons with counts **and armour pips**. Top-right, salvage with an animated counter. Bottom-right, the Start Wave button — showing the chain bonus when a wave is live — and a 1× / 2× speed toggle. Bottom-left, pause.

**Build menu.** Opens on tapping an empty hardpoint. Five entries showing icon, name, cost and a one-word role tag; unaffordables desaturated and non-interactive. The selected turret's range previews as a ground ring. Tapping elsewhere dismisses.

Radial on wide viewports, bottom sheet on narrow ones. **[TUNE]** — if day five is tight, ship the bottom sheet on both. Two layouts is a real cost and the radial is a nicety.

**Tower panel.** Current tier, damage type, DPS, range, next tier's cost and deltas, sell value, targeting priority selector, and the 3×3 counter grid with the current row highlighted.

**Pause, Victory, Defeat.** Victory: waves survived, integrity remaining, star rating, score, any new unlock. Defeat: wave reached and a single prominent Retry. Both have Retry and Main Menu. Neither has more than four elements.

### 9.2 Score

`score = wavesCleared × 1000 + integrityRemaining × 250 + salvageUnspent + difficultyBonus`, where `difficultyBonus` is 0 on Normal, 2500 on Veteran, 6000 on Nightmare. Best score persists per difficulty in PlayerPrefs. This is a tuning knob, not a load-bearing system — it exists so the Victory screen has a number and the Title screen has a reason to try again.

### 9.3 Input

One tap, one result. `InputRouter` first calls `EventSystem.current.IsPointerOverGameObject(fingerId)` and returns if UI consumed the tap — **uGUI is not in the physics scene, so a `Physics.Raycast` layer mask can never hit a Canvas element**, and the `fingerId` overload matters because the parameterless version is mouse-only and is a known mobile bug. If UI did not consume it, one `Physics.Raycast` against the Hardpoint layer only.

No drag, no pinch, no long-press, no multi-touch. Touch targets are a minimum of 48 device-independent pixels.

### 9.4 Health bars and armour pips

Only damaged enemies show a health bar, fading two seconds after the last damage. **The armour pip is separate and always visible** — a small coloured chevron above every enemy from spawn, because the counter system is worthless if the player learns an enemy is Plated only after shooting it.

Bars and pips are world-space quads on one shared material, billboarded by a single manager iterating all of them in one loop rather than each running its own `LateUpdate`. Bar and pip share a texture atlas, so an enemy's overlay is one batched draw.

The Colossus gets a dedicated screen-space bar across the top of the HUD with a name label.

### 9.5 Icons

Twelve icons are needed — five turrets, seven enemies — and no pack ships them. Do not draw them. Write a ~40-line editor script that instantiates each prefab in front of a temporary camera against a transparent background, renders to a 256×256 RenderTexture, and writes a PNG to `Assets/_COREHOLD/Art/Icons/`. Half an hour once, consistent results, and re-runnable when a prefab changes. This is Ticket 33 and it is not optional — §12.2's `TowerDefinition.icon` field is otherwise null.

### 9.6 The speed toggle

`Time.timeScale = 1` or `2`. Three consequences that must be handled or they become bugs:

Spawn coroutines use `WaitForSeconds`, which scales with `timeScale` — that is correct and intended, since the whole wave should run at double speed. Anything that must *not* scale — UI animations, the results screen — uses `WaitForSecondsRealtime`. Audio pitch is left unscaled; doubling pitch sounds like a fault, not a feature. And `AudioDirector`'s one-shot collapse window (§10) is specified in unscaled time, or at 2× it starts eating distinct shots.

### 9.7 Building the UI from the pack

Sci-Fi UI Pack Pro is a sprite library, not a framework — no dialog system, no health-bar logic, no scripts. Two documented problems to design around: its buttons have only Normal and Pressed states with no Hover, so add hover via Unity's colour tint rather than sprite swap; and many composite elements distort badly when resized, so **use only nine-sliced panel and frame sprites for anything whose size varies** and use decorative composites only at authored size.

Pick one visual family — the cyan V2 set matches the player faction — and use no more than a dozen sprites total. All text is TextMeshPro, one font, two sizes. Canvas Scaler in Scale With Screen Size at 1920×1080 reference, match 0.5.

---

## 10. Audio

Audio on Web is Web Audio, not FMOD, which imposes hard limits that belong in the design rather than in day-six debugging. **No AudioMixer effects work** — only volume via `SetFloat`. No filter sweep on pause, no ducking, no reverb zones. Volume and clip choice are the entire toolkit.

All audio is gated behind a user gesture, which the title screen's Play button provides.

Every AudioClip uses **CompressedInMemory** on the WebGL platform override. This is the correct load type for Web regardless — streaming and decompress-on-load do not behave usefully there, and CrazyGames measured a background track down to about 5 MB of RAM this way. Separately, there are reports that clips will not play on iOS with the ringer switch off; that is a WebKit behaviour rather than something load type controls, so **treat "audio works on iOS with silent mode on" as a thing to test and report, not a thing to guarantee.** Do not let day six disappear into it.

The sound map is small. Turret fire uses the pack's bullet, energy and missile shots, one per turret type, with ±8% random pitch. Turret rotation uses the rotation loop, gated to play only while slewing and only for the three turrets nearest screen centre — ten turrets slewing at once is a wall of noise. Impacts and explosions from the same pack. UI clicks and the Core alarm from Creepy Cat. Music is the open gap in §4.9.

`AudioDirector` owns twelve pooled AudioSources. When all twelve are busy it **steals the oldest voice** rather than refusing the new sound — a refused shot reads as a bug, a stolen tail does not. It also collapses identical one-shots fired within 50 ms of unscaled time into one play at slightly raised volume, which matters when four Autocannons fire on the same frame.

---

## 11. Visual effects

All VFX come from Cartoon FX Remaster, which is Shuriken-based rather than VFX Graph — the right answer, since VFX Graph is not reliably supported on WebGL.

Nine effects, all pooled through `VFXDirector`, none spawning lights, all using `CFXR_Effect`'s auto-deactivate rather than `Destroy`: three muzzle flashes (one per damage type), an impact spark, two explosion sizes for splash weapons, an enemy death burst, a Core hit flash, and a build-placement puff.

Two configuration requirements: Cartoon FX soft particles need **Depth Texture enabled in the URP Asset**, and `CFXR_Effect`'s global switches should have lights off and camera shake on at low intensity, reserved for Core hits and Colossus footfalls — with the 1.5 second cooldown from §3.3.

Autocannon and Arc Node are hitscan and need a visible tracer to communicate firing: a pooled `LineRenderer` on a two-frame fade, one shared additive material. Missile and Mortar have travel time and need none.

---

## 12. Technical architecture

### 12.1 Project layout

```
Assets/
  _COREHOLD/
    Art/            project-authored material variants, UI atlas, generated Icons/
    Audio/
    Data/
      Towers/  Enemies/  Waves/  Levels/
    Prefabs/
      Towers/  Enemies/  Projectiles/  VFX/  UI/
    Scenes/         Boot.unity  Game.unity
    Scripts/
      Core/  Towers/  Enemies/  Data/  UI/  Systems/
    Settings/       URP assets, renderer, quality
  Editor/
    AssetManifestDumper.cs   IconRenderer.cs
  Vendor/           untouched vendor packs
```

Two scenes. `Boot` initialises settings and loads `Game`; `Game` holds everything and reloads on retry. No additive loading, no Addressables — the build is small enough to ship in one bundle, and Addressables is a day of integration for a benefit this project does not need.

**Source control from minute one.** `git init`, Unity `.gitignore`, Editor Settings → Asset Serialization **Force Text**, Version Control Mode **Visible Meta Files**. A seven-day from-scratch Unity build driven by an AI agent with no VCS is one bad refactor from total loss. This is Ticket 1.

### 12.2 ScriptableObjects

`TowerDefinition` — `id`, `displayName`, `icon`, `description`, `damageType`, `canTargetAir`, `basePrefab`, `TowerTier[3] tiers`.

`TowerTier` — `cost`, `damage`, `fireRate`, `range`, `minRange`, `splashRadius`, `chainTargets`, `chainFalloff`, `projectilePrefab` (null = hitscan), `projectileSpeed`, `weaponVisualIndex`, `muzzleVfx`, `fireSfx`, and for the relay `auraRadius`, `auraFireRateBonus`, `auraRangeBonus`, `auraDamageBonus`.

`EnemyDefinition` — `id`, `displayName`, `icon`, `prefab`, `baseHealth`, `armourType`, `moveSpeed`, `bounty`, `leakDamage`, `isAir`, `flightAltitude`, `animatorClipSpeedRef`, plus `hasSecondPhase` / `phaseChangeAtPathFraction` / `secondPhaseSpeed` for the Roller and `enrageAtHealthFraction` / `enrageSpeedMultiplier` for the Colossus.

`WaveDefinition` — `SpawnGroup[] groups`, `clearBonus`. `SpawnGroup` — `enemy`, `count`, `spawnGap`, `startOffset`, `spawnerIndex`.

**`spawnerIndex` assignments**, which the first draft omitted entirely and an agent would otherwise guess: index 0 is the west ground entrance, 1 is the north ground entrance, 2 is the air corridor. Air units always use 2. Ground groups alternate: in any wave with two or more ground groups, the first uses 0 and the second uses 1; single-group ground waves use 0. Wave 10's Scuttlers use 1 so the Colossus has the west approach to itself.

`LevelDefinition` — wave array, `startingSalvage`, `coreIntegrity`, `hpGrowthPerWave`, `chainBonusPerLiveEnemy`, `chainBonusCap`, `maxLiveEnemies`.

`DamageTable` — the 3×3 grid, exposing `float Multiplier(DamageType, ArmourType)`.

Difficulty is a struct applied over `LevelDefinition` at run start, not three duplicated asset sets.

### 12.3 Runtime classes

**Core** — `GameManager` (singleton state machine: integrity, salvage, wave index, difficulty; raises `OnSalvageChanged`, `OnIntegrityChanged`, `OnStateChanged`). `WaveManager` (spawn coroutines, the 14-live cap, the live-enemy registry, `OnWaveComplete`). `Spawner`. `PathRoute` (ordered `Transform[]`, `GetPoint(int)`, `Length`, and an `OnDrawGizmos` drawing the route plus cumulative length — write that gizmo, because §5.2's coverage rule is checked visually).

**Enemies** — `Enemy` (health, armour, damage-table application in `TakeDamage(float, DamageType)`, death, leak, registry add/remove). `EnemyMover` (route walk or straight-line flight; Roller phase change). `EnemyAnimatorBridge` (`Speed`, `Die`, the `Animator.speed` correction).

**Towers** — `TowerHardpoint`. `Tower` (tier state; `EffectiveRange` / `EffectiveFireRate` / `EffectiveDamage` as computed properties, not cached values — recomputing three floats beats the stale-cache bugs when a Scan Relay is sold). `TowerTargeting` (registry scan on a staggered 0.2 s tick). `TurretAim`. `TowerWeapon` (fire timer, `IsAimed` gate, dispatch to hitscan / chain / projectile). `Projectile` (leading, travel, hit, splash, orphaned-target handling). `SupportAura` (registers and pushes modifiers on build, upgrade and sell only — never per frame).

**Systems** — `CoreholdPool<T>` (named to avoid colliding with `UnityEngine.Pool.ObjectPool<T>`; prewarmed; enemies, projectiles, VFX, tracers, health bars all go through it — **nothing calls `Instantiate` or `Destroy` during a wave**, because Web's GC only runs at end of frame and cannot run while managed code executes, so per-frame allocation causes visible hitching). `AudioDirector`. `VFXDirector`. `InputRouter`. `SaveData`. `DebugConsole`.

**UI** — `HUDController`, `BuildMenu`, `TowerPanel`, `ResultScreen`, `TitleScreen`, `OverlayManager` (bars and pips). All event-driven; none poll in `Update`.

Roughly 25 classes plus 5 SO types and 2 editor scripts — call it 34 files and 2,500 to 4,000 lines. Nothing should exceed 200 lines.

### 12.4 Debug tooling — Ticket 12, not an afterthought

Day seven is a tuning day and there is no way to tune wave nine if reaching it takes eight minutes. `DebugConsole`, active only in the Editor and in development builds, bound to keys: `]` skip to next wave, `[` previous, `M` grant 1000 salvage, `I` toggle core invulnerability, `K` kill all live enemies, `1`–`3` jump to a difficulty, `F1` toggle an on-screen readout of live enemy count, draw calls and frame time. An hour on day one that pays back several on days six and seven.

### 12.5 Prefab structures

```
Tower_Autocannon          [Tower, TowerTargeting, TowerWeapon, AudioSource]
  RangeOrigin             (empty at turret centre — all range checks measure from here)
  Base                    (Slava Z. turret base, static)
  YawPivot                [TurretAim yaw target]
    Cockpit
    PitchPivot            [TurretAim pitch target]
      Weapon_T1           (active)   + MuzzlePoint_T1
      Weapon_T2           (inactive) + MuzzlePoint_T2
      Weapon_T3           (inactive) + MuzzlePoint_T3
  BlobShadow              (shared quad)
  RangeRing               (flat quad, inactive by default)
```

Tier upgrades toggle the three `Weapon_T*` children — no instantiation, no loading. Slava Z. source files name barrel-end locators `Barrel_end` or `Barrel_end_1..n` **[VERIFY]**; use them directly if present.

```
Enemy_Strider             [Enemy, EnemyMover, EnemyAnimatorBridge, Animator]
  Model                   (assembled mech)
  HitPoint                (empty at centre mass — range target and VFX origin)
  OverlayAnchor           (empty above model — health bar and armour pip)
  BlobShadow
```

Layer `Enemy`. **No collider, no Rigidbody** (§7.4).

### 12.6 Scene hierarchy — `Game.unity`

```
--- SYSTEMS ---   GameManager  WaveManager  Pools  AudioDirector  VFXDirector  InputRouter  DebugConsole
--- LEVEL ---     Environment (static, lightmapped)
                  Route_Ground  Route_Air          [PathRoute]
                  Spawners/  Hardpoints/  Core
--- LIGHTING ---  DirectionalLight (shadows OFF)  ReflectionProbe  LightProbeGroup
--- CAMERA ---    MainCamera (fixed)
--- UI ---        Canvas_HUD  Canvas_Menus  Canvas_WorldOverlay
```

---

## 13. Performance and platform

### 13.1 Budgets

| | Desktop browser | Mobile browser |
|---|---:|---:|
| Frame rate | 60 | 30–40 |
| Draw calls | ≤ 300 | ≤ 140 |
| Triangles | ≤ 300k | ≤ 130k |
| GPU texture memory | ≤ 150 MB | ≤ 70 MB |
| **SkinnedMeshRenderers** (not units) | ≤ 40 | ≤ 28 |
| Unique materials on units | ≤ 8 | ≤ 5 |
| WASM heap | ≤ 512 MB | ≤ 256 MB |
| Compressed download | ≤ 25 MB | ≤ 25 MB |

The skinned budget is stated **per renderer, not per unit**, because a Mech Constructor mech is many parts and each part is its own renderer. Fourteen live enemies at two to six renderers each is 28 to 84 — the top of that range blows the mobile budget on its own.

### 13.2 Draw calls are the binding constraint, and the usual answers do not apply

Every GL call on Web crosses the WebAssembly-to-JavaScript boundary; Unity's own docs note CPU-side dispatch is slower than native. A realistic count for the specified scene:

| | Mobile draw calls |
|---|---|
| Static environment batches | 25–40 |
| 8 turrets × 3–5 renderers | 24–40 |
| 14 enemies × 2–6 skinned renderers | 28–84 |
| Blob shadows (one shared material) | 1–2 |
| Health bars, pips, tracers, VFX | 15–35 |
| 3 canvases | 10–25 |
| **Total** | **103–226** vs a 140 budget |

Note what is *not* in that table: a shadow pass. Disabling directional shadows (§5.5) removes a full re-submission of every caster, and it is the single largest saving available.

Note also what does **not** help. SRP Batcher reduces SetPass and constant-buffer rebinding, not the number of draws. GPU instancing does not apply to `SkinnedMeshRenderer` at all. Sharing a material does not merge renderers. All three are worth having and none of them reduces the count.

The one thing that does is reducing renderers per prefab. **Ticket 24 on day three: combine each enemy prefab's parts into a single SkinnedMeshRenderer**, either with `Mesh.CombineMeshes` plus bone remapping in a small editor utility, or by baking each assembled mech once in Blender. Target ≤ 2 renderers per enemy and ≤ 3 per turret. This also makes Optimize Game Objects (§6.3) safe, so the two problems have one solution.

Measure with the Frame Debugger on **day three**, right after the enemies exist — not day six, when the only remaining lever is cutting concurrency, which cuts the game.

### 13.3 Player settings

```
Graphics API:           WebGL2 only — remove WebGPU from the list
Rendering path:         Forward   (NOT Forward+, NOT Deferred)
Compression:            Brotli
Decompression fallback: Off
Data caching:           On
Name files as hashes:   On
Managed stripping:      High  (+ link.xml for anything reflection-based)
IL2CPP code gen:        Faster (smaller) builds
WASM code optimization: DiskSize      (try DiskSizeLTO once on day 6, with rollback)
Exceptions:             Full With Stacktrace through day 5, then
                        Explicitly Thrown Exceptions Only for the ship build
Threads support:        Off
WebAssembly 2023:       Off for the day-0 gate; try On on day 6 with rollback
Initial memory:         measure first, then set to observed steady state (~192-256 MB)
Maximum memory:         512 MB
Memory growth:          Linear once Initial Memory is measured
Target frame rate:      -1 (let the browser drive requestAnimationFrame)
Splash screen:          Off
Input handling:         legacy Input Manager only
```

Three of these need justifying. **WebGPU is experimental and currently slower than WebGL2** — a reported benchmark showed roughly 71 ms per frame against 36 — so remove it from the list so nothing auto-selects it. **Deferred does not work on WebGL2 at all**, and Unity silently falls back to Forward with no warning, which is why teams set it, see no change, and conclude it did nothing. And the **legacy Input Manager** rather than the Input System package saves about 2.4 MB — a tenth of the entire download budget, for a game whose input is one tap.

`WebAssembly 2023` gets SIMD and cheaper exceptions but raises the browser floor to Safari 16.4+, which costs reach on exactly the platform being targeted. It is a day-six experiment, not a day-zero default. Same reasoning as `DiskSizeLTO`, which is known to break packages using unmanaged memory and can push build times past thirty minutes.

Do not enable the GPU Resident Drawer or GPU occlusion culling — both need compute shaders, unavailable on WebGL2.

**Texture compression is a measurement, not a setting.** DXT is the desktop format and ASTC the mobile one; a single build must pick one and let the other decompress to RGBA32 at load, roughly four to eight times the memory. Against a 70 MB mobile budget that is potentially fatal. Ship DXT through day five, **measure actual on-device texture memory at Ticket 38**, and if it exceeds budget, do Unity's documented two-build split with JS capability detection — about two hours with their sample.

### 13.4 Hosting

itch.io. Its documented limits are 500 MB extracted, 200 MB per file, 1000 files — comfortable, though **[VERIFY]** current values, since the whole hosting plan rests on them. Its CDN handles Brotli when files carry `.br`, which is Unity's default output. Tick **Mobile friendly** so phones launch fullscreen rather than fighting page chrome for viewport.

Two itch specifics. Safari does not support IndexedDB in iframes, so Data Caching will not persist in an embedded player, only in the fullscreen launch. And set `WebGLInput.captureAllKeyboardInput = false`, or the game swallows keystrokes meant for the comment box below the embed.

Avoid a fullscreen toggle on iOS entirely: entering or leaving fullscreen on iOS Safari and Chrome produces a two-to-five second window where the app appears unfocused and accepts no input, reported across Unity 6.0, 6.1 and 6.2 with no fix. Make the canvas fill the viewport responsively instead.

The stock Unity web templates are not mobile-responsive and do not scale the canvas to the viewport. A custom template with a real progress bar driven by `createUnityInstance()`'s progress callback is required, and it also covers the known black-screen-before-splash. Half a day, on day zero.

### 13.5 Quality settings

There is one URP asset and one scene, so the mobile column above is the real budget and the desktop column is headroom. The only runtime difference is **Render Scale**, set at boot from a simple device check: 1.0 on desktop, 0.75 on mobile. Bloom, if it ever ships, is desktop-only via the same switch. Anything more elaborate is not worth the day.

---

## 14. Implementation tickets

Ordered and atomic. Hand these to CoPlay one at a time. Ticket numbers here are canonical; anywhere else in this document that cites a ticket refers to this list.

**Day 0 — Pipeline**

1. `git init`, Unity `.gitignore`, Force Text serialization, Visible Meta Files. *Accept: a clean initial commit.*
2. Create the URP project on the pinned Unity version, Linear colour space, folder structure per §12.1.
3. Import vendor packs. Coarse deletion pass per §4.8 — demo scenes, PSDs, 4K/8K textures, unused colour variants, the free Firadzo pack. Apply texture max sizes. Convert Built-in materials to URP.
4. Run `AssetManifestDumper`. Reconcile every `[VERIFY]` path. *Accept: manifest lists ≥ 200 prefabs with non-zero triangle counts. An empty manifest is a failure, not a pass.*
5. Build empty scene to Web with §13.3 settings and a custom responsive template; upload to itch.io; open on a real phone. *Accept: loads, shows the progress bar, compressed size ≤ 8 MB.* **Day-zero gate — do not proceed past it.**
6. Configure the URP asset: Forward, no HDR, **directional shadows off**, Depth Texture on, SRP Batcher on.
7. Eyeball one mech at 1024 atlas. Decide 1024 or 2048 for characters.

**Day 1 — The loop runs**

8. `PathRoute` with route gizmos and cumulative length readout.
9. `Enemy` + `EnemyMover`: one enemy walks the route and damages the Core.
10. `GameManager` — integrity, salvage, events.
11. `CoreholdPool<T>`; route enemies through it.
12. `DebugConsole` per §12.4.
13. Assemble **one** turret chassis from Sci-Fi Turret Constructor parts; `TurretAim` on real yaw/pitch transforms. *Accept: turret visibly tracks a walking enemy.*
14. `TowerTargeting` (registry-based) + `TowerWeapon` hitscan + `Enemy.TakeDamage`. *Accept: turret kills enemy, salvage increments.*
15. Resolve the music decision (§4.9).

**Day 2 — Towers and data**

16. Assemble the remaining **four** turret chassis. Verify pivots. *Do this before any code today.*
17. All ScriptableObject types and the `DamageTable`.
18. Author all fifteen tier entries from §7.3, including the three Scan Relay rows.
19. `Projectile` with leading, travel and splash; wire Missile and Mortar.
20. Arc Node chain targeting with falloff.
21. `SupportAura`, non-stacking.
22. `TowerHardpoint`, `InputRouter`, build and sell. *Accept: five turret types buildable, upgradeable, sellable via placeholder UI.*
23. **Colossus go/no-go gate (§4.5). Ninety minutes, hard stop.**

**Day 3 — Enemies, waves, and the draw-call check**

24. Assemble six enemy prefabs. **Combine parts to ≤ 2 SkinnedMeshRenderers each** (§13.2). Test Optimize Game Objects on one (§6.3).
25. Disable root motion; wire Animators; measure `animatorClipSpeedRef` per enemy; set Cull Completely and Update When Offscreen off.
26. Roller two-phase; Colossus enrage; air movement and corridor.
27. `WaveManager` with the 14-live cap and the enemy registry; all ten `WaveDefinition` assets from §8.1; wave chaining per §8.4.
28. Win/lose conditions; placeholder `ResultScreen`.
29. **Frame Debugger pass in the Editor.** *Accept: a full ten-wave run completes, and enemy draw calls are within §13.2.*

**Day 4 — The level**

30. Block out Refinery Delta; author the 150 m route; **verify the §5.2 coverage rule with per-turret gizmo rings before proceeding.**
31. Place eight hardpoints per §5.3; Core, wreck, landmark props.
32. Fix the camera; verify framing at 16:9, 16:10 and 20:9. Bake lighting; blob shadows on units. *Accept: playable end to end on the real level at target frame rate in the Editor.*

**Day 5 — Feel**

33. `IconRenderer` editor script; generate all twelve icons (§9.5).
34. Nine VFX via `VFXDirector`; hitscan tracers.
35. `AudioDirector`; all SFX; music in.
36. Real UI: HUD, build menu, tower panel, title, results. Armour pips always visible.
37. Core damage states; camera shake with cooldown; speed toggle per §9.6. *Accept: no placeholder UI remains.*

**Day 6 — Platform and persistence**

38. Web build. Profile heap; set Initial Memory. **Measure on-device texture memory**; decide DXT vs the two-build split.
39. Frame Debugger on device; get draw calls under budget.
40. Real-device test, iOS Safari and Android Chrome; fix touch targets and framing.
41. `CompressedInMemory` WebGL overrides on all clips; report iOS silent-mode behaviour (§10).
42. Difficulty tiers, unlocks, star rating, score, PlayerPrefs. Title screen polish.
43. One attempt each at `DiskSizeLTO` and `WebAssembly 2023`, with rollback. *Accept: loads in under 8 s, holds 30 fps on a mid-range phone.*

**Day 7 — Tuning only**

44. Full playtest ×3 per difficulty. Tune per §8.3 — HP scalar first.
45. Fix whatever the playtests surface. Nothing new gets built today.
46. itch.io page: description, screenshots, a GIF.

**Day 8 — Ship**

47. Final build, upload, test the live URL on three devices, publish.

---

## 15. Schedule

Day 0 is Saturday 8 August. Days 1 through 7 are Sunday 9 through Saturday 15. **Day 8, Sunday 16 August, is ship day** — Ticket 47 only, everything else done.

| Day | Focus | Must be true by end of day | If it isn't |
|:-:|---|---|---|
| 0 | Pipeline | Empty build runs on a real phone from itch.io | Drop to Unity 6.3 LTS today |
| 1 | Vertical slice | One turret kills one enemy; salvage increments | Nothing — this day cannot slip |
| 2 | Towers | Five turrets build, upgrade, sell | Cut Scan Relay to four turrets |
| 3 | Enemies | Ten-wave run completable; draw calls checked | Cut Roller, then Breaker; go to eight waves |
| 4 | Level | Real level, real camera, playable | Six hardpoints instead of eight |
| 5 | Feel | No placeholder UI | Bottom-sheet build menu only; cut Core damage states |
| 6 | Platform | 30 fps on a mid-range phone | Mobile becomes best-effort; ship desktop web |
| 7 | Tune | Winnable and losable on all three tiers | Cut Nightmare |
| 8 | **Ship** | Live on itch.io | — |

**Cut order.** Take from the top when a day slips: Prime Node; unique boss model; Nightmare; Roller; Core damage states; radial build menu; ten waves down to eight; Scan Relay; Veteran.

**Never cut.** A working Web build. Final-quality assets, no grey boxes. The counter system's readability — five turrets and six enemies is already the minimum that makes it work. The audio gate. Instant restart.

The likeliest way this slips is spending day one on the level instead of the loop. Refinery Delta is fun to build and is not on the critical path until day four. Build the ugly loop first.

---

## 16. Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|:-:|:-:|---|
| Turret and enemy hand-assembly overruns | **High** | **High** | Pre-cut scope (§1.2); one chassis on day 1 to learn the workflow; assembly first thing on day 2 |
| Draw calls over budget from multi-renderer mechs | **High** | **High** | Shadows off; mesh combine at Ticket 24; Frame Debugger on **day 3** |
| Creepy Cat kit blows the texture budget | **High** | High | Coarse deletion day 0; hard 1024 cap; **measured** at Ticket 38 |
| Unity 6.5 web toolchain regression | Medium | High | Day-0 gate at Ticket 5; drop to 6.3 LTS on any anomaly |
| Balance wrong on first playtest | **Certain** | **Medium** | Day 7 is tuning only; model in Appendix A; debug console at Ticket 12 |
| Humanoids pack broken on Unity 6 | **High** | Low | Pre-decided fallback (§4.5), 90-minute cap |
| Optimize Game Objects breaks modular rigs | Medium | Medium | Tested day 3 on one enemy; mesh combine solves both |
| Turret pivots wrong for pitch | Medium | Low | Empty pivot wrappers, ~2 h (§4.2) |
| Mecanim cost on single-threaded WASM | Medium | Medium | Cull Completely; 14-live cap; ≤2 renderers per enemy |
| No music available | Medium | Medium | Decided day 1 (Ticket 15), not day 6 |
| iOS heap growth failure past 256 MB | Medium | High | Set Initial Memory to measured steady state; never rely on growth |
| UI pack textures block compression | High | Low | Manifest flags from source dimensions; ≤12 sprites |

---

## 17. Open items to verify in-editor

Reconcile every prefab, mesh and clip path against `AssetManifest.md` and replace the `[VERIFY]` references. Confirm `Barrel_end` locators exist in the Slava Z. FBX hierarchies. Confirm whether the installed Cartoon FX is the free subset or the paid pack. Confirm whether Creepy Cat's `Sound` folder holds usable ambient beds. Confirm Sci-Fi Turret Constructor weapon pivot positions. Confirm which Creepy Cat branch is installed — native URP 6.3 or Built-in — and that the reported missing-children issue on `P_Ship_Interior_Module_01_B.prefab` affects nothing we use. Confirm whether Slava Z. parts are skinned to the shared skeleton or parented to bones, which decides Optimize Game Objects. Confirm current itch.io upload limits. Measure the real slew-efficiency factor on day two and correct Appendix A's 0.80.

---

## Appendix A — Balance model

The wave curve was validated numerically. Re-run this before changing any number in §7 or §8; it takes a second and it is much faster than playtesting a bad curve.

```python
PATH_LEN, AIR_LEN = 150.0, 95.0
# name: (hp, bounty, speed, armour, isAir)
E = {"Scuttler":(45,   8,  7.5, "Unarmoured", False),
     "Strider" :(110, 12,  5.0, "Plated",     False),
     "Lancer"  :(190, 18,  4.6, "Shielded",   False),
     "Wasp"    :(70,  14,  9.0, "Unarmoured", True),
     "Roller"  :(150, 20, 11.0, "Unarmoured", False),
     "Breaker" :(420, 35,  3.75,"Plated",     False),
     "Colossus":(2800,250, 3.0, "Shielded",   False)}

def traverse(n):
    if n == "Roller": return PATH_LEN*0.6/11.0 + PATH_LEN*0.4/4.6
    return (AIR_LEN if E[n][4] else PATH_LEN) / E[n][2]

# (unit, count, spawn gap, start offset)
W = [[("Scuttler",5,2.6,0)],
     [("Scuttler",8,2.2,0)],
     [("Strider",5,3.4,0),("Wasp",1,0,12)],
     [("Scuttler",8,1.9,0),("Strider",4,3.6,5)],
     [("Wasp",8,2.8,0)],
     [("Strider",8,3.2,0),("Wasp",4,4.0,10),("Scuttler",5,2.2,4)],
     [("Lancer",5,4.4,0),("Roller",3,6.0,5)],
     [("Lancer",5,4.6,0),("Scuttler",10,2.2,4),("Wasp",5,3.4,12)],
     [("Breaker",3,8.0,0),("Strider",8,3.0,5),("Wasp",5,3.4,10)],
     [("Colossus",1,0,4),("Scuttler",8,2.4,0),("Wasp",4,3.6,14)]]

START, DPS_PER, SPEND, SLEW, GROWTH = 300, 0.19, 0.85, 0.80, 0.18

def run(hp_tier=1.0, econ=1.0, label="NORMAL"):
    cum, total = START*econ, 0
    print(f"\n--- {label} ---")
    print(f"{'W':<3}{'units':>6}{'effHP':>8}{'income':>7}{'margin':>8}{'peak':>6}{'dur':>7}")
    for n, w in enumerate(W, 1):
        mult   = (1.0 + GROWTH*(n-1)) * hp_tier
        effHP  = sum(E[e][0]*c for e,c,_,_ in w) * mult
        income = int((sum(E[e][1]*c for e,c,_,_ in w) + 60 + 18*n) * econ)
        # duration DERIVED: first spawn -> last unit would reach the Core unkilled
        dur    = max(off + (c-1)*gap + traverse(e) for e,c,gap,off in w)
        expo   = 0.30 + 0.018*(n-1)          # coverage saturates as the map fills
        margin = cum * SPEND * DPS_PER * SLEW * expo * dur / effHP
        # concurrency WORST CASE: nothing dies. WaveManager caps live enemies at 14.
        ev   = [(off+i*gap, off+i*gap+traverse(e)) for e,c,gap,off in w for i in range(c)]
        peak = max(sum(1 for a,b in ev if a <= t <= b)
                   for t in [x*0.25 for x in range(int(dur*4)+1)])
        print(f"{n:<3}{sum(c for _,c,_,_ in w):6}{effHP:8.0f}{income:7}{margin:8.2f}{peak:6}{dur:7.1f}")
        total += dur; cum += income
    print(f"combat {total:.0f}s ({total/60:.1f} min)   earnable {cum:.0f}")

run()
run(1.25, 1.12, "VETERAN")
run(1.55, 1.22, "NIGHTMARE")

# Damage-type dominance check — no type should dominate the weighted mix.
TBL = {"Kinetic":  {"Unarmoured":1.00,"Plated":0.50,"Shielded":1.25},
       "Energy":   {"Unarmoured":1.00,"Plated":1.25,"Shielded":0.50},
       "Explosive":{"Unarmoured":1.30,"Plated":0.65,"Shielded":0.65}}
pool = {}
for n, w in enumerate(W, 1):
    for e, c, _, _ in w:
        pool[E[e][3]] = pool.get(E[e][3], 0) + E[e][0]*c*(1 + GROWTH*(n-1))
tot = sum(pool.values())
print("\nHP mix:", {k: f"{v/tot:.0%}" for k, v in pool.items()})
for dt, row in TBL.items():
    print(f"  {dt:<10} weighted {sum(row[a]*h for a,h in pool.items())/tot:.3f}"
          f"   worst cell {min(row.values())}")
```

A healthy Normal curve opens near 1.5, holds between 1.2 and 1.5 through the midgame, and closes at 1.0–1.1 on the boss. Veteran and Nightmare are expected to close below 1.0 — the model is conservative on three axes, so sub-1.0 means "requires above-model play," which is the point of those tiers. No damage type should sit more than about 0.15 from the others in weighted effectiveness.

Tune the wave HP scalar first — one number, reshapes everything. Tower numbers last; they ripple through every wave at once.
