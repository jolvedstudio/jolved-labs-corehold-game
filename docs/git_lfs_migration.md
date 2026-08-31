# Git LFS migration — runbook

**Why this exists:** pushes from this branch fail partway through with what
GitHub Desktop reports as a lost connection. It is not the network. The repo
carries 1,103 MB of binary art in the git pack, and a push is a single long
HTTPS POST that hits a server-side timeout before it finishes. LFS uploads each
object separately and resumably, which is what makes the push complete.

**Why we are not just deleting the art:** it is load-bearing. 49 committed
assets reference 525 vendor GUIDs, transitively pulling in 885 MB of the
1,094 MB under `Assets/_COREHOLD/Vendored/`:

- every `Enemy_*.asset` → vendor death and weapon SFX
- every `Tower_*.asset` → vendor muzzle-flash prefabs and SFX
- every animator controller → vendor FBX animation clips
- `VFXDirectorConfig.asset` → 15 vendor VFX prefabs
- `Blueprint_SandyDesert_s9488.unity` → 99 vendor references
- materials → vendor textures; `Weather_Dust.asset` → vendor ambience

Dropping them would leave a clone that compiles and runs but has T-posing
enemies, silent weapons and a blueprint scene missing 99 objects. Only 208 MB
is unreferenced and safe to delete outright.

---

## Before you start

| | |
|---|---|
| **Rewrites history?** | Yes — but only `claude/campaign-manager-a0`. `main` has never contained a Vendored file, so it is untouched. |
| **Who is affected** | Anyone holding that branch must re-clone or hard-reset after the force-push. |
| **PR #73** | Survives. GitHub updates a PR when its head branch is force-pushed. |
| **Cost** | 885 MB+ exceeds GitHub's free 1 GB LFS storage **and** its 1 GB/month bandwidth. You need a data pack before this is usable by more than one person. Check current pricing — it has been $5/month per 50 GB storage + 50 GB bandwidth. |

**Close Unity and GitHub Desktop.** The migration rewrites every file in the
working tree, and on Windows an open handle from Unity's asset importer will
make it fail partway through — which is the worst moment for it to stop.

**Back up first.** History rewriting is not reversible from inside the repo.
Run this from the repo's PARENT directory — it creates a folder where you
stand, and a 1 GB backup nested inside the repo is a problem of its own:

```bash
cd ..
git clone --mirror git@github.com:jolvedstudio/jolved-labs-corehold-game.git corehold-backup.git
cd jolved-labs-corehold-game
```

---

## The migration

Run in **Git Bash** from the repo root — the `\` line continuations below are
Bash syntax and break in PowerShell and CMD, where each command has to be one
line. Not GitHub Desktop either: it has no LFS migration UI and hides the errors
you need to see.

Confirm where you are before starting:

```bash
git rev-parse --show-toplevel && git branch --show-current
```

Step 3 takes a while and looks hung — it is rewriting 139 commits and repacking
about 1.1 GB. Let it run.

```bash
# 1. Install the LFS filters into your git config (once per machine)
git lfs install

# 2. Get the .gitattributes that defines what LFS tracks
git checkout claude/campaign-manager-a0
git pull origin claude/campaign-manager-a0

# 3. Rewrite THIS BRANCH's history, moving matching blobs into LFS.
#    --include-ref is what keeps main out of it.
git lfs migrate import \
  --include-ref=refs/heads/claude/campaign-manager-a0 \
  --include="*.tga,*.png,*.jpg,*.jpeg,*.tif,*.tiff,*.psd,*.exr,*.hdr,*.wav,*.mp3,*.ogg,*.aif,*.aiff,*.fbx,*.obj,*.blend,*.mov,*.mp4,*.unitypackage"

# 4. Check it did what you think
git lfs ls-files | wc -l        # expect ~400+ files
git lfs status

# 5. Reclaim the local disk the old blobs still occupy
git reflog expire --expire=now --all
git gc --prune=now

# 6. Push. This uploads LFS objects first, then a small git pack.
git push --force-with-lease origin claude/campaign-manager-a0
```

Step 6 is the one that used to fail. It should now move a ~70 MB pack plus
per-object LFS uploads that resume rather than dying as a unit.

## If the push still stalls

```bash
git config lfs.concurrenttransfers 3    # default 8; lower is steadier on flaky links
git config http.postBuffer 524288000
git config lfs.activitytimeout 60
```

`git lfs push --all origin claude/campaign-manager-a0` moves only the LFS
objects, so you can get the bulk uploaded first and then push the branch.

## Everyone else, afterwards

```bash
git lfs install                 # MUST run before cloning, or you get pointer files
git fetch origin
git reset --hard origin/claude/campaign-manager-a0
```

A clone made **without** `git lfs install` produces ~130-byte text files where
the textures belong, and Unity opens a project full of missing references. If
someone reports that symptom, this is the cause.

---

## Worth doing next

**The art is oversized independent of where it is stored.** The top offenders:

| file | size |
|---|---|
| `Creepy_Cat/.../Dirt_B_Norm.png` | 88 MB |
| `Soundbits_freeSFX_2025/.../ca1_ambience_city_rain_distantthunder.wav` | 56 MB |
| `MC_Spiders_Camo_Albedo_Teal.png` | 23 MB |
| `MC_Humanoids_Camo_Albedo_Teal.png` | 22 MB |

An 88 MB PNG is an 8K uncompressed normal map, and a 56 MB WAV is uncompressed
stereo ambience — neither ships at that size in a WebGL build, because Unity
recompresses on import. Downscaling the textures to 2K and converting the
ambience to OGG would cut several hundred megabytes from both the repo and the
LFS bill without changing a single GUID, so nothing breaks.

**208 MB is referenced by nothing.** 165 vendor files are not reachable from any
committed asset, even transitively. They can be deleted outright — a separate,
safe change from this one.

**The `.gitignore` no longer describes what the project does.** Its "Vendored
assets (excluded from version control)" block targets `/Assets/Vendor/`, but the
kits actually live at `/Assets/_COREHOLD/Vendored/Vendor/` and are committed on
purpose so builds work from a clone. The rule matches nothing and the comment
argues for a policy the repo does not follow.
