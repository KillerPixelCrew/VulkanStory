# Optimum: working rules for agents

*Single source of truth for agent instructions. `CLAUDE.md` is a symlink to this file; both are in
`.git/info/exclude` and must never be committed.*

Optimum is a performance mod for Vintage Story: a patched client (OpenGL path in
`ClientPlatformWindows`) plus a Vulkan backend that substitutes the platform
(`VulkanClientPlatform : ClientPlatformWindows` in `Optimum.Render.Vulkan/Platform/`). Read this before touching anything. The
skills in `.claude/skills/` hold the step-by-step procedures; this file holds the rules.
Everything an agent needs is in THIS file: the rules, the procedures they point at, and the project
knowledge folded in below. A lesson learned during a session belongs here, in the repository - the
harness memory store on one machine is a cache, not the record.

## What this branch is

`feat/vulkan-taa` carries **the Vulkan backend and TAA only**: upstream PR #69 was split so the maintainer can land
those first. DLSS, XeSS, FSR, frame generation, the vendor latency backends (Reflex, anti-lag, XeLL) and NGX live on
`feat/dlss`, `feat/dlss-g` and `feat/latency` and are NOT work owed here. The plan file predates that split and still
describes them, which is why its items carry an explicit `[out]` mark.

**Frame structure is Vulkan foundation and IS in scope**: one frame identity per frame with markers around
simulation, render submit and present, and the world frame separated from UI composition (`SceneNoHud` plus a UI
target, HUD composed afterwards). They make pacing measurable and keep the HUD out of the scene image whether or not
an upscaler ever exists. What stays off the branch is the vendor layer that later sits on top of them.

## Where the truth lives (edit these, never the generated copies)

| What | Edit here | Generated from it | Ships as |
|---|---|---|---|
| Game client code | `build/VintagestoryLib/**` (decompiled + patched) | `patches/VintagestoryLib/*.patch` via `scripts/extract-patches.sh` | Cecil transplant into vanilla DLL; every changed/new method or member MUST be listed in `Optimum.Patcher/Program.cs` |
| Game API | `VintagestoryApi/**` (hand-maintained fork, git-ignored) | `sources/VintagestoryApi/**` via extract | `VintagestoryAPI-patched.dll`; new files also go in `optimum-api-contracts/optimum-api-contracts.csproj` (path `..\sources\VintagestoryApi\...`) and get a `<Compile Remove>` in both `VintagestoryApi/VintagestoryAPI.csproj` and `sources/VintagestoryApi/VintagestoryAPI.csproj` |
| Mods | `VSEssentials/`, `VSSurvivalMod/`, `VSCreativeMod/` (forks) | `patches/<mod>/*.patch` via extract | recompiled mod DLLs plus `Optimum.Patcher/mod-patcher.cs` manifests for the installed-runtime path |
| Shaders | `sources/shaders/*.vsh/.fsh` (override vanilla by file name) | shipped by `make deploy` and `scripts/package-*` | includes: `sources/shaderincludes/` (add to deploy and packagers when first used) |
| Vulkan backend | `Optimum.Render.Vulkan/**` | - | `Optimum.Render.Vulkan.dll` + `Silk.NET.*.dll` beside the client (`make deploy` copies them) |
| Vanilla reference | `_ref/**` and `.vanilla/**/assets` | read-only | - |

Never edit `patches/*.patch` or `sources/VintagestoryApi/**` by hand; extract overwrites them.
`.baseline/` is the decompiled vanilla; csproj overlays are folded into it by bootstrap, so a new
`<Compile Remove>` must also be added to `.baseline/VintagestoryApi/VintagestoryAPI.csproj` locally
or extract will keep emitting a stray csproj patch.

## Build, deploy, run, verify

```
dotnet build VintageStory.slnx -c Release          # everything
dotnet test Optimum.Render.Vulkan.Tests            # GPU tests, validation layers on (needs a GPU)
dotnet test Optimum.Tests -c Release               # source/patch coverage tests
bash scripts/extract-patches.sh && bash scripts/check-patches.sh   # after editing build/, forks, API
make deploy                                        # Cecil patch + copy into .vanilla/win-x64/vintagestory
scripts/dev/run-client.sh ["world name"]           # detached launch; RENDERER=vulkan|opengl env switches
scripts/dev/client-renderer.sh                     # which renderer ACTUALLY started (read this every time)
scripts/dev/screenshot.sh /tmp/x.png               # then look at the image with Read
scripts/dev/kill-client.sh                         # clean close; never pkill -f from a shell that mentions the process
scripts/dev/perf-capture.sh / pacing-gate.sh       # frame-time capture; gate: blocking uploads, stddev vs GL baseline, p99
scripts/dev/parity-capture.sh + ssim.py            # per-attachment dump on one backend; SSIM table between two dumps
scripts/dev/headless-capture.sh                    # real client and renderer, window never mapped, frames to disk; does not steal the desktop
                                                   # closes itself cleanly and prints "shutdown closed itself"; a crash count in its output means the run is suspect
                                                   # pacing numbers from it are meaningless: an unfocused window sits under the 30 FPS background cap
scripts/dev/worktree-bootstrap.sh                  # in a worktree: materialise build/ + forks offline (private .build copy)
scripts/dev/worktree-bootstrap.sh --in-place [--discard-build-edits]   # main checkout, after a merge changed patches/
```

Data dir: `~/.config/OptimumVintagestoryData` (`clientsettings.json`, `ModConfig/optimum.json` with
`"Renderer"`). Saves: `Saves/*.vcdbs`; pass the bare world name to `-o`, not the file name.
Settings that change what you see: `ssaa` (0.5 renders at half res on BOTH backends), `fxaa`,
`ssaoQuality`, `bloom`, `godRays`, `mipMapLevel`.

Backend diagnostics: `OPTIMUM_VULKAN_VALIDATION=1` (log: `$TMPDIR/optimum-vulkan-validation.log`, or set it to a path; add `OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best`), `OPTIMUM_RENDER_TRACE=<file>` (per-draw
trace: `program N 'name'`, `fullscreen program= tex0= target=`, `bind unit= texture=`,
`validation:` lines), `OPTIMUM_DUMP_TEXTURES=<ids> OPTIMUM_DUMP_DIR=<abs> OPTIMUM_DUMP_AFTER_SECONDS=60`
(PPM dumps of live textures; without the delay you dump the menu), `OPTIMUM_VULKAN_STATS=<file>`
(legacy line plus `key=value` lines: blocking uploads, waits per site, frame p50/p95/p99/stddev, scopes, barriers),
`OPTIMUM_FPS_LOG=<file>` (per-second `mean min max p99 stddev`), `OPTIMUM_PARITY_DUMP=<abs dir> OPTIMUM_PARITY_FRAME=<n>`
(every framebuffer attachment on both backends at in-world frame n, PPM/PFM, GL row order),
`OPTIMUM_VULKAN_POISON=1` (fresh images NaN/magenta/0xDEADBEEF, depth 0.5, buffers 0xDEADBEEF: undefined reads become loud).
Headless capture: `OPTIMUM_HEADLESS=1` (window created but never mapped or focused, both backends),
`OPTIMUM_HEADLESS_FRAMES=<dir>` plus `OPTIMUM_HEADLESS_FIRST_FRAME`/`_FRAME_COUNT`/`_FRAME_STRIDE` or
`_FRAME_LIST` (which in-world frames to write as PPM), `OPTIMUM_HEADLESS_COMMANDS=<file>` with
`OPTIMUM_HEADLESS_COMMAND_FRAME` (chat lines fed on that frame: `/time`, `/weather`, `.cam load`/`.cam play`),
`OPTIMUM_HEADLESS_FIXED_DT` (pins the simulated step). A display server is still required - headless here
means no visible window, not no display.
DLSS/NGX: the NVIDIA feature libraries, headers and programming guides live in `~/.local/share/optimum-ngx`
(never in the scratchpad - that is tmpfs and a reboot would make the NGX tests skip instead of fail).
Run the NGX tests with `OPTIMUM_NGX_FEATURE_PATH=~/.local/share/optimum-ngx/lib/Linux_x86_64/rel`.
**Implicit Vulkan layers poison validation and must be switched off deliberately.** On this machine MangoHud is
enabled globally (`~/.config/environment.d/mangohud.conf`) and `VK_LAYER_LS_frame_generation` (Lossless Scaling)
has no enable variable at all, so both hook every Vulkan process, the GPU test host included, and draw or present
on the swapchain in ways validation reports as the application's own hazards. Every GPU test run, validation run
and measurement exports:
`MANGOHUD=0 DISABLE_MANGOHUD=1 DISABLE_LSFG=1 DISABLE_VK_LAYER_VALVE_steam_overlay_1=1 DISABLE_VK_LAYER_VALVE_steam_fossilize_1=1 DISABLE_GAMESCOPE_WSI=1 DISABLE_VULKAN_RENDERDOC_CAPTURE_1_45=1 DISABLE_LAYER_MESA_ANTI_LAG=1`
and confirms with `VK_LOADER_DEBUG=layer vulkaninfo --summary` that none of them is inserted. `VK_LAYER_MESA_device_select`
stays (it only orders devices). A run without this is not evidence, and the fix for a finding is never a weaker
validation setting.
GPU tests run `sync,best` validation by default through `GpuTest` (override: `OPTIMUM_TEST_VALIDATION_FEATURES`, empty = off);
an unlisted `SYNC-` message fails `ValidationAssert.NoSyncHazards`.

## Rules that came from real failures

1. **A launch is not a verification.** The bootstrap falls back to OpenGL silently; MangoHud only
   shows on Vulkan. Grep the log for `[Optimum] Vulkan renderer` / `[Optimum] OpenGL renderer:`
   before saying anything about rendering. A PR was merged on an OpenGL run because this was skipped.
2. **Look at pixels, then diff the two paths.** For any "X looks wrong on Vulkan": capture a baseline
   (screenshot + trace + validation log) first, then read the OpenGL body (`ClientPlatformWindows` or the lib site) and the Vulkan override
   (`VulkanClientPlatform.*.cs`) of the same member side by side and list every state difference (sampler filter/wrap/mip/border/compare,
   blend enable vs per-attachment factors, draw-buffer masks, clears, viewports, formats). The bugs
   have all been parity gaps, never shader maths. Do not theorise from symptoms.
3. **Verify in the game, both backends, before claiming done.** Deploy, run, screenshot, compare with
   OpenGL live. Component tests passing is not evidence for the screen.
4. **Every fix gets a GPU readback test** in `Optimum.Render.Vulkan.Tests` (pattern:
   `VulkanDeviceIntegrationTests`, `AttachmentSemanticsTests`) and, for lib/patch changes, a
   source-coverage test in `Optimum.Tests` (pattern: `fsr-pipeline-coverage-tests.cs`).
5. **Process hygiene.** Launch through `scripts/dev/*.sh` (setsid wrappers). Never put `pkill -f` or
   `pgrep -f` in a command that also contains the process name in a heredoc or string: it matches the
   calling shell, so `pkill` kills it (exit 144) and `pgrep` reports the process as running when it is not -
   on 2026-09-16 that made me tell the owner the game was running long after they had closed it. Ask with
   `ps -eo pid,stat,args | grep -i <name> | grep -v grep`, or read the client log. Close the game with the kill script (window close
   first) to avoid shutdown-race crash reports. Close the game as soon as a check is done; never leave it running.
6. **Git.** Never `git stash`. Commit WIP on the branch with a `wip:` prefix instead. Branch from
   `main` (tracks `origin/main` = KillerPixelCrew/VulkanStory, migrated from NightHammer1000/VulkanStory on 2026-09-15; `upstream` = StratumServer/Optimum).
   Commit only when asked or when a phase is verified; say what was verified in the message.
7. **Batch reads.** Read whole methods and both paths in one command (`sed -n` ranges + `rg`), not
   ten single greps. Codex found in one pass what took an afternoon of small probes.
8. **Agents, models, effort.** The session model is Fable; it does only the hard parts, at low
   effort, high only for a hard bug. Everything else runs as a Workflow (ultracode): sonnet for
   map/search stages at **high or xhigh** (cheap, needs it to be trustworthy), opus for
   implementation and review at **medium, never higher**. Never launch an agent that inherits Fable.
   Parallelise: map stage first, then every independent implementation stage at once with
   `isolation: 'worktree'` (each commits on its own branch), then one integration stage that merges
   into the feature branch and runs the finish sequence, then review. Serial stages are only for
   genuinely dependent work. Worktree stages are created at `origin/main`, which is NOT an ancestor of the
   feature branch (`feat/vulkan-taa` diverged from it): their first commands are `git checkout -B <stage branch> <feature branch>`
   (never `merge --ff-only`, which fails) and `bash scripts/dev/worktree-bootstrap.sh`; integration merges into the feature branch, never main.
   Rules live in `.claude/skills/workflow-policy`. Codex (`.claude/skills/codex-handoff`, gpt-6-astra) has quota again since
   2026-09-12: hard rendering bugs can go to it (neutral brief, full machine access, low effort) or to Fable directly.

9. **Undefined behaviour differs between the APIs.** GL keeps an attachment the shader never writes;
   Vulkan writes garbage into it (pipelines now mask those off). A bug that only flickers between
   frames is invisible to screenshots and to per-frame probes: read the validation log with sync +
   best-practices enabled before instrumenting anything.

10. **Temporal and pacing claims need numbers, never screenshots.** Accepted evidence: the validation
   log with `sync,best`; a multi-frame GPU test (Present between frames, no readback in the loop);
   `pacing-gate.sh` numbers against the OpenGL baseline of the same scene; `ssim.py` per-attachment
   tables; a 60 fps `ffmpeg -f x11grab` capture with consecutive-frame diffs for flicker; poison mode
   for suspected undefined reads. The Vulkan-native rebuild plan and its phases:
   `/home/n1ght/.claude/plans/i-never-wanted-this-sequential-kernighan.md`, acceptance in `docs/vulkan-acceptance.md`.

11. **TAA on sub-pixel foliage: audit the resolve, not the backend.** The three-day Vulkan "distant
   trees jitter, TAA looks disabled" bug (fixed 2026-09-11) was `sources/shaders/taa-resolve.fsh` itself:
   a single-sample depth disocclusion test threw the history away on ~3.7% of distant leaf pixels every
   frame (a sub-pixel leaf hits the leaf in one jitter phase and the background in the next), and a
   fixed 10% blend let the moving clip box drag history. The fix, which must never be reverted: 3x3
   nearest-depth disocclusion with motion from the nearest-depth tap, and luminance anti-flicker weighting
   (0.3x..1.2x blendAlpha). Both backends had the flaw; every OpenGL-vs-Vulkan capture matched, which is
   why parity hunting never found it. When the user names a component (here: the TAA shader), audit that
   component against known practice (Karis 2014, Playdead 2016) and quantify it
   (`scripts/dev/taa-rejection.py` on a parity dump) before any backend comparison. Any change to the
   resolve, including the Phase 3 native rewrite, keeps both behaviours and their tests.

12. **Say only what you verified, and name the evidence.** A status in a plan, a handoff or a comment is a claim,
   not a fact; a passing test, a file:line, a log line or a measured number is a fact. On 2026-09-16 an audit found
   items marked done that were never done, and separately I asserted a model, a process state and a branch scope
   from documents instead of from checks - each one wrong. If it was not checked this session, say that it was not.

13. **Decide, don't ask.** Research the open question to a decision and act on it. Hand a choice back only when it
   is genuinely the owner's (money, scope, upstream, destructive acts) and you cannot resolve it from what they
   already said. Turning an instruction they just gave you back into a question is the failure mode.

14. **Reviews are expensive; verification is not.** Implement-only stages by default. The integration stage runs
   the full suites on the merged state, which is where defects actually show. Reserve a review stage for the
   genuinely high-risk change in a wave, not for every stage.

15. **No map stage by default - document the seams instead.** Map stages rediscovered the tree at five figures of
   tokens each and were thrown away with the run. Every render seam carries a doc comment at its declaration: what
   it draws, where the OpenGL body is, target and slots, the state that is not obvious and why, and the test that
   pins it (`docs/vulkan-native-render-systems.md` section 4). An implementation stage documents what it touches as
   part of the change and greps instead of mapping. Map only what the code cannot answer - measured behaviour,
   vendor documentation, a tree the repo does not contain. `scripts/dev/harvest-maps.py` recovers the map output of
   past runs from the workflow journals when one is needed again.

16. **Agents never launch the game, never `make deploy`, never push.** In-game verification, deployment and pushing
   are the session's own work, because they touch the owner's machine and their branches. A stage that needs the
   game verified says so in its return value.

17. **Identity and attribution.** Commit as `NightHammer1000 <nightstorm@kpc.bz>` (global config only). The work
   e-mail from the environment context must never appear in git config, commits, PRs, docs or output. No tooling or
   assistant attribution anywhere - not in code, comments, docs, tests or commit messages - and no co-author
   trailers.

18. **Never relax validation.** Validation findings are real. Never weaken a setting, suppress a message, add an
   allowlist entry or lower a report flag to go green: fix the cause. The one exception already in the tree is a
   documented vendor entry in `KnownSyncHazards`, and it names the driver and the reason.

## Testing notes
- **Tests are filed by subject, never by stage or review round.** Add to the existing coverage file for
  the thing under test (`upscaler-*`, `taa-*`, `latency-*`, `headless-*`); create a new file only when a
  genuinely new subject appears. A file named after the work that produced it (`pr3-review-round2-*`,
  `wave1-*`) is wrong by construction: nobody looks there when that subject breaks. Half the repo's diff
  against upstream is tests, and 44 of them are under 150 lines because each workflow stream wrote its own.
- **"Both backends" means OpenGL with the user's real `optimum.json` too.** OpenGL with `Upscaler: dlss` crashed on
  the loading screen from a8f09ae until 2026-09-13 and nobody saw it, because every check ran Vulkan. The headless
  harness makes the OpenGL run cost nothing: run it in the same pass as the Vulkan one.
- **Injected fields never run their initializers** (Cecil copies no constructor IL): an injected `= new T()` is null,
  an injected `= -1` is 0. `NoInjectedFieldAnywhereCarriesAnInitializer` enforces it for manifest fields. Use the CLR
  default as the starting state, or allocate lazily at the use site.
- **Do not touch a vanilla static class before vanilla does.** Its type initializer may not be inert:
  `ShaderRegistry`'s publishes uncompiled programs into `ShaderPrograms.*`, which is what crashed the OpenGL loading
  screen when the upscaler stand-down called into it during startup.
- `Optimum.Render.Vulkan.Tests` GPU tests must read back inside a frame; `BindFramebuffer`/`ClearColor`
  are no-ops between frames.
- Vulkan named UBOs are per-draw snapshots (fixed 2026-09-10); the uniform ring is now per frame slot
  (it used to divide one fixed 32 MiB by the slot count, so raising `FramesInFlight` silently cut it).
- **Poison mode is clean evidence as of 2026-09-12.** It used to report 5 sync hazards per run, written
  off as syncval noise across destroy/recreate; they were ours - the poison clear and the first upload of
  a fresh texture are both `TransferDst` writes in one batch and `BarrierBatcher` emits nothing when the
  usage does not change. A hazard under `OPTIMUM_VULKAN_POISON=1` is now a real finding, not a baseline.
- Shader pairs dropped in `sources/shaders/` are auto-translated by `ShaderTranslationTests`.
- Temporal bugs need multi-frame GPU tests (Present between frames, no readback in the loop); a
  single-frame readback passed while the R32F-history and masked-clear bugs were live (P2, 2026-09-10).
- Vulkan: `ClearColor` on an attachment masked out of `SetDrawBuffers` is a no-op; every framebuffer
  format must have an entry in `GlEnums.cs` or it silently degrades to RGBA8.
- TAA history rejection is a number too: `scripts/dev/taa-rejection.py <parity dump>` reports per-region
  rejection from the two history depth slots; distant leaves above ~1.5% per frame is the 2026-09-11 regression.
- "Does it still jitter" is answered with a number: still-camera screenshot pairs, wind stilled,
  luminance diff over the centre crop, both backends (vulkan-parity-debug skill, 2c).

## Project knowledge (folded in from the agent memory, 2026-09-16)

These were hard-won in earlier sessions and lived only on one machine until the owner pointed out that makes
them useless. They are instructions, not history: read them the same way as the numbered rules. When a new
lesson appears, it belongs HERE, in this file - the harness memory store is a local cache, not the record.

### Cleanup comes last

*Documentation and comment cleanup happens at the very end of the project; until then comments are moved but never trimmed.*

User, 2026-09-12: "We do Code documentation and comment cleanup at the very end."

**Why:** the comments in this repo are the record of what each defect cost, and many carry measured numbers - the 1.05 % distant-leaf TAA rejection, the 0.37 to 0.02 display-pixel jitter residual, why NGX's shutdown is gated, why the acquire wait stage may never be ALL_COMMANDS. While the renderer is still moving, trimming them deletes the reasoning that keeps the next agent from reintroducing the bug.

**How to apply:** never open a "tidy the comments" task, and never let a refactor quietly drop an xml-doc - a consolidation or a move carries every "why" forward verbatim. Stale comments that are actively wrong are still fixed on the spot, as part of the change that made them wrong. One cleanup pass at the end, when the renderer settles. Related: [[testing-suite-too-heavy]], [[speed-and-parallelism-over-testing]].

### Delegating to codex

*How the user wants Codex (gpt-6-astra) launched and steered on this project.*

The user delegates hard problems to the local `codex` CLI and has been specific about how:

- Model `gpt-6-astra`. **Launch at low reasoning effort** (`-c model_reasoning_effort="low"`)
  unless they ask otherwise — xhigh over-tests and over-scopes, and runs for hours.
- **It is on a weekly quota** and the user tracks it (one long xhigh session on the Vulkan
  backend cost roughly 30% of a week). Spend it on problems that are genuinely stuck, and
  keep briefs tight rather than launching speculatively.
- **Give it full machine access** (`--dangerously-bypass-approvals-and-sandbox`), not a
  sandbox. It can then drive the GPU, launch the game and take Wayland remote control to
  actually play and inspect the result. Sandboxing it blocked the only verification step
  that settles a rendering question, and the user objected to it directly.
- Brief it with **the symptom and the reproduction only** — attach the screenshot with
  `-i`, describe what is wrong, and let it investigate. Do not hand it my own conclusions
  or a "ruled out" list: the user called that poisoning its context, and the analysis I
  was most confident in turned out to be the part that was wrong.
- Prompt goes **on stdin** (`cat brief.md | codex exec ...`); `-i` takes multiple files and
  swallows a positional prompt argument.
- Steer a live session with `codex queue --thread <session-uuid> --message "..."`, taking
  the uuid from the `session id` line in its output. Copy any screenshot into a path it
  can read and name that path in the message.

- **Plan reviews are a good use of `high` effort.** On 2026-09-10 the user asked for a high-effort
  review of the TAA plan against the code and game source; it took ~15 minutes, cost far less than
  an xhigh implementation session, and found a pre-existing Vulkan bug (named UBOs shared across all
  draws in a frame) plus a dozen wrong assumptions. Brief it with the plan path and the source
  locations, ask for CONFIRMED/WRONG/UNVERIFIABLE with file:line, and tell it to write to a file
  in the scratchpad. Launch through a wrapper script with `setsid` so the tool timeout cannot kill
  it, and monitor for a sentinel line (see [[pkill-self-match]]).

**Why:** on the Vulkan backend it found three real bugs in one pass that I had missed over
a long session, and verified them by playing the game across several views and two worlds.

**How to apply:** when stuck on something the user is getting frustrated with, offer Codex
early rather than late, brief it neutrally, and give it the whole machine. See
[[verify-end-to-end-not-components]].

Steering (2026-09-10): `codex queue --thread <id> --message` only reaches a running session; messages queued after exit are lost, so continue with `codex exec resume <session-id>`. Monitor on `^CODEX_EXIT [0-9]+$` (Codex narrates the word and false-matched a looser pattern). Relay the user's observations verbatim and promptly; each one ("gets worse with distance") narrowed the search. Codex does not push; verify its claims in-game, then push.

Quota: exhausted on 2026-09-10; **reset and available again from 2026-09-12** (user). Codex handoffs are back on the table for genuinely stuck rendering bugs and for plan reviews at `high`; it still costs a weekly quota, so keep briefs tight and do not launch speculatively.

### Frame generation needs pacing

*DLSS-FG (and any frame generation) ships only together with a present-thread pacer and correct Reflex out-of-band presentation; never an unpaced intermediate step.*

User, 2026-09-13: "DLSSFG without pacing (Reflex) is useless and unplayable."

**Why:** I had split frame generation into a synchronous step 5 (both presents issued from the render thread) and a later step 6 (present thread + pacer), and launched step 5 on its own with a user-facing setting. Unpaced generated frames judder and add latency, so the intermediate step is not a usable feature - it is a regression the player can switch on. NVIDIA's DLSS-FG guide section 7 says the feature does no timing or presentation itself; the app must present the generated frame on evaluate completion and the real frame (OutputReal) at an equal interval, asynchronously from the render thread, with Reflex keeping the held-back real frame's latency in check.

**How to apply:** treat the FG evaluate, the present thread, the pacer and Reflex out-of-band presentation (own queue, vkQueueNotifyOutOfBandNV, out-of-band markers) as one deliverable. Do not expose any FG setting to players before the pacer lands; internal test switches are fine. The same holds for XeFG and FSR frame interpolation later. Related: [[own-vendor-orchestrator-decision]], [[low-latency-layer-reference]], [[user-graphics-expertise]].

### Git remotes

*"In the Optimum checkout, origin is KillerPixelCrew/VulkanStory (the org repo, migrated from NightHammer1000/VulkanStory on 2026-09-15) and upstream is StratumServer/Optimum; main tracks origin/main."*

Remote layout (set 2026-09-10 at the user's request):
- `origin` = https://github.com/KillerPixelCrew/VulkanStory.git ("ours"; the repository moved into the KillerPixelCrew organisation on 2026-09-15, it used to be NightHammer1000/VulkanStory). `main` tracks `origin/main`. PRs for the Vulkan work go here; `gh` default repo is set to it.
- `upstream` = https://github.com/StratumServer/Optimum.git. Does not have the Vulkan backend yet.

**How to apply:** push branches and open PRs against `origin`. Only touch `upstream` when the user asks to sync with or contribute to StratumServer. Related: [[optimum-upscaling-roadmap]].

### Look before you work

*"Before designing or launching any implementation wave, read the vendor docs in full and the reference implementations on disk (~/Projekte/ReScaleFrame/references) and check best practice online; the user has had to say this three times."*

User, 2026-09-13: "This is the third time i have to tell you to actually look before you work." (The previous two, same day: "DLSSFG without pacing (Reflex) is useless and unplayable" and "Have you checked that frameplacment against best practice online and in the Framegen Documentation?")

**Why:** I designed DLSS-G frame pacing from one chapter of NVIDIA's guide plus my own reasoning and launched a 7-agent workflow on it. The user's own reference checkouts in `~/Projekte/ReScaleFrame/references/` (Streamline, FidelityFX-SDK, xess, OptiScaler) and their ReScaleFrame design docs already contradicted it: AMD paces both presents from the previous present with a 10-frame moving average, CPU-waits for GPU completion before presenting, keeps one frame in flight, caps render slightly below half the output rate; Streamline measures pacing by display change, not present call; only generated frames are dropped. Two workflows were stopped as a result.

**How to apply:** for any feature with vendor SDKs or prior art: (1) read the vendor guides in full, not the chapter that matches the question; (2) read the reference implementations on disk - check `~/Projekte/ReScaleFrame/references/` and the user's ReScaleFrame docs first, they are the user's own research; (3) search online for best practice; (4) write the design with a source for every decision and mark what is reasoning; (5) show the user the sourced design before launching an implementation wave. A map of *our* code is not research into *how it should be done*. Related: [[research-before-repeating-loops]], [[audit-the-component-the-user-names]], [[frame-generation-needs-pacing]], [[user-graphics-expertise]].

### Low latency layer reference

*"Korthos low_latency_layer (MIT, github.com/Korthos-Software/low_latency_layer) implements VK_NV_low_latency2 and VK_AMD_anti_lag on any GPU; its algorithm is the model for Optimum's vendor-neutral latency tier."*

User pointed at https://github.com/Korthos-Software/low_latency_layer on 2026-09-11 (clone in the session scratchpad vendor-research/low_latency_layer, commit 3138b14).

What it does: an implicit Vulkan layer (`LOW_LATENCY_LAYER=1`, `LOW_LATENCY_LAYER_REFLEX=1` to expose VK_NV_low_latency2 instead of VK_AMD_anti_lag) that paces without driver support. At the sleep point (vkLatencySleepNV signal semaphore, or vkAntiLagUpdateAMD INPUT stage) it waits until every graphics-queue submission of the previous frame has finished on the GPU (timestamp queries at top/bottom of pipe; for low_latency2 submissions are grouped by present ID), then applies the frame cap (minimumIntervalUs or maxFPS, measured release to release), then releases the app to sample input. A jitter/drain controller exists only for games with a decoupled simulation queue (Marvel Rivals). Benchmarks with a Reflex Analyzer on an RX 7900 XTX: matches or beats Windows Anti-Lag 2; the Mesa anti-lag layer measured as a no-op.

How to apply (user, 2026-09-11: "Requiring a layer might be a bad idea but replicating what it does here might work out"): never depend on the layer; Optimum's vendor-neutral latency tier is this algorithm done natively. The renderer owns the Frame timeline semaphore, so "previous frame's GPU work finished" is a timeline wait on the previous frame's present-submit value placed before input sampling, with no timestamp queries and no layer. It covers the Arc 140V (no XeLL on Vulkan) and AMD. Vintage Story's client simulation and render share one thread, so the decoupled-queue controller is not needed. Detect the layer (instance layer `VK_LAYER_KORTHOS_low_latency`) and log it, since it would pace on top of Optimum. Related: [[own-vendor-orchestrator-decision]], [[optimum-upscaling-roadmap]].

### Ngx needs a native shim

*"NVIDIA NGX aborts when called from a .NET P/Invoke stub (it resolves the caller module by return address), so every NGX call needs a small native shim .so/.dll; DLSS SR and DLSS-G both report available on native Linux."*

Spike on 2026-09-12 (branch feat/dlss, commit bad1122; RTX 4070 Laptop, driver 615.71.09, X11, DLSS SDK 310.9.1, no Proton):

- DLSS Super Resolution and DLSS Frame Generation both report `Available = 1` with `NeedsUpdatedDriver = 0` on native Linux Vulkan (min driver 470 and 520). Optimal settings at 2560x1490: Quality 1707x993, Performance 1280x745, dynamic range 50-100 %.
- **`libnvidia-ngx.so.1` resolves its caller's module from the return address.** A .NET P/Invoke stub lives in anonymous JIT memory, so NGX builds a string from a null path and aborts the process (`std::logic_error`, `basic_string::_M_construct null not valid`). Proved with `scripts/dev/ngx-probe.c`: identical calls succeed from C and abort through a trampoline in an anonymous mmap page. NGX checks only the immediate caller, so there is no managed workaround.
- The per-feature extension queries return `FAIL_NotImplemented` on Linux; use the SDK wrapper's fixed lists: instance `VK_KHR_get_physical_device_properties2`, device `VK_NVX_binary_import`, `VK_NVX_image_view_handle`, `VK_KHR_buffer_device_address`, `VK_KHR_push_descriptor`.
- Interop traps: the exported `Init_ProjectID` is not the header prototype (no `vkGet*ProcAddr` arguments, SDKVersion before FeatureCommonInfo); `PathListInfo.Path` is `wchar_t**` (UTF-32) on Linux; the driver exports no C accessors for `NVSDK_NGX_Parameter`, so parameters go through the C++ vtable in declaration order with no virtual destructor. NGX writes no log on Linux.

Shim built 2026-09-12 (commit c5272ad, `native/optimum-ngx/`): C99, dlopen's libnvidia-ngx.so.1 lazily, flat C ABI, exported version checked by the managed side, source committed and built by `make native` plus an MSBuild target that degrades to "DLSS unavailable" when no compiler exists. **The shim must never tail-call NGX**: `return ngx_entry(args);` compiles to `jmp` at -O2, the wrapper's frame is gone and NGX reads the managed caller's return address again, so it aborts exactly as before. Fixed with a volatile local plus `-fno-optimize-sibling-calls`; the first build had this bug and it looked identical to the original failure.

DLSS SR ran end to end on 2026-09-12 (commit c1fb719): 1280x745 to 2560x1490, Success on create and every evaluate, pattern preserved, eight accumulating frames with no validation output. Two more NGX rules found there: **NGX needs the `bufferDeviceAddress` feature enabled**, not just `VK_KHR_buffer_device_address` (without it every evaluate trips VUID-vkGetBufferDeviceAddress-bufferDeviceAddress-03324, and NGX's own extension queries never mention it); and, believed at the time, "NGX allows exactly one lifetime per process" - **that was wrong** (see below).

**The real shutdown crash, found 2026-09-12 (commit 19f9645):** `NVSDK_NGX_VULKAN_Shutdown1` is declared with one parameter in `nvsdk_ngx_vk.h` and implemented with **two** in driver 615.71.09 - the second is an out-parameter (`int*` remaining reference count) the driver writes through with no null check (libnvidia-ngx.so.1 0xa64b0 -> 0xa1750, store at 0xa1898; the deprecated one-arg `Shutdown` passes `lea 0xc(%rsp)` there). Called through the header prototype from a .NET process the register holds 0x2000, so NGX segfaults on the **first** shutdown - both earlier core dumps were first shutdowns, and the "second Shutdown1 segfaults / one lifetime per process" conclusion was a misattribution of the same undefined store. Fix: the shim calls it as `(void*, int*)` with a local int. A/B on the same test binary: old shim crashed the host 3/3, new shim 5/5 clean. Features are still released and the frame timeline drained before shutdown, through a single process-wide owner (`NgxLifetime`), because `ReleaseFeature` after `Shutdown1` remains untested. NGX's own `vkCmdClearColorImage` trips a sync hazard against its own barrier on the first evaluate; both sides are NGX's images, so it is pinned as a vendor entry in KnownSyncHazards.

**How to apply:** every NGX call goes through a small native shim (`libOptimumNgx.so` / `OptimumNgx.dll`) that forwards the entry points and the parameter vtable; never plan a design around direct P/Invoke, and never let an NGX failure path run unguarded, since the failure mode is a process abort rather than an error code. Related: [[own-vendor-orchestrator-decision]], [[optimum-upscaling-roadmap]], [[vulkan-native-rebuild-decision]].

### Nvidia driver update needs reboot

*GLXBadFBConfig on every OpenGL launch plus Vulkan silently picking the Intel iGPU means the NVIDIA userspace driver was updated without a reboot; check nvidia-smi and the log's GPU line before any capture.*

On 2026-09-11 a pacman update at 14:38 moved nvidia-utils 610.57.04 to 615.71.09 while the loaded kernel module stayed 610. Symptoms: every OpenGL launch through `prime-run` crashed at window creation ("GLX: Failed to create context: GLXBadFBConfig"), `prime-run glxinfo -B` failed with "X Error ... BadValue", `nvidia-smi` printed "Failed to initialize NVML: Driver/library version mismatch", and Vulkan still started but on "Intel(R) UHD Graphics (ADL-S GT1)", which is invalid for measurements. A reboot fixed all of it.

**Why:** it looked like a Phase 0 regression and cost a capture round; the user watched the clients crash.

**How to apply:** before any in-game capture run `nvidia-smi` (must print the GPU and driver, not a mismatch) and, after each launch, require `Graphics Card Renderer: NVIDIA` in the client log (on Vulkan that line is the selected Vulkan device name). If the mismatch shows, tell the user a reboot is needed instead of launching. Related: [[confirm-renderer-from-log]], [[vulkan-native-rebuild-decision]].

### Optimum upscaling roadmap

*"Optimum rendering roadmap: TAA (done, P0-P6 on feat/taa 2026-09-11) -> XeSS/DLSS/FSR upscalers -> frame generation, maybe path tracing + ray reconstruction; target hardware includes an Arc 140V handheld."*

Order agreed with the user: in-house TAA first (feat/taa, PR #2 on origin), then vendor upscalers (XeSS 2 / DLSS / FSR 3.1) as separate consumers of the frozen temporal contract, then frame generation, possibly path tracing with ray reconstruction later. Target hardware includes an Intel Arc 140V handheld, so performance must be measured there, not only on the RTX 4070 laptop.

Status 2026-09-11: TAA plan P0-P6 all landed on feat/taa (c60a4cc); P2 and P4 accepted in game by the user; the contract is frozen in docs/temporal-frame-contract.md v1 with stability tests (Optimum.Tests/temporal-contract-tests.cs). Open: the user's 18-row acceptance matrix (docs/taa-acceptance.md) and the default-on decision (TAA default off until then); Arc 140V frame times (the laptop's compositor caps at 165 Hz, see TAA-PLAN P5 note); shader patch system ([[shader-patch-system-todo]]); then the vendor upscaler/FG plan.

**How to apply:** new temporal consumers adapt to the contract document, never to the resolve; bump the contract version through its change procedure. Related: [[taa-p2-vulkan-parity-lessons]], [[vulkan-validation-log-and-flicker]], [[git-remotes]].

Update 2026-09-11: TAA on Vulkan is now stable (resolve fix, see [[vulkan-taa-jitter-root-cause]]). The user wants DLSS next, as soon as the Vulkan-native backend reaches Milestone 1; DLSS needs only Phase 2's native device and graph handles, so it can precede native shaders, perf and the mod API. XeSS for the Arc 140V follows through the same upscaler seam. Related: [[vulkan-native-rebuild-decision]].

### Own vendor orchestrator decision

*"2026-09-11 user decision - Optimum builds its own multi-vendor orchestrator (upscaler, latency, frame generation); Streamline rejected as the multi-vendor layer (NVIDIA-signed plugins only) and as the NVIDIA backend (Reflex via VK_NV_low_latency2, DLSS/DLSS-G via NGX directly)."*

Decision: Optimum owns a thin vendor orchestrator with three slots and one backend per vendor: upscaler (DLSS, XeSS, FSR), latency (Reflex via VK_NV_low_latency2, Intel XeLL, AMD VK_AMD_anti_lag / AntiLag 2) and frame generation (DLSS-G, XeFG, FSR frame interpolation). No Streamline at all, on either OS (recommended 2026-09-11 after the direct-vs-Streamline research): Reflex = VK_NV_low_latency2 called directly; DLSS SR and DLSS-G = NGX Vulkan helpers from the DLSS SDK (NGX_VK_CREATE_DLSSG / NGX_VK_EVALUATE_DLSSG, Linux libnvidia-ngx-dlssg.so), with Optimum owning DLSS-G pacing (DLSS-FG guide section 7: present the generated frame when evaluate completes, retained real frame at equal spacing, async from the render thread).
Evidence: NVIDIA's own Linux driver guide says native Linux Reflex works "not via the Reflex SDK but directly via the Vulkan extension VK_NV_low_latency2"; the spec says VK_NV_low_latency is legacy for the Reflex SDK's NvLowLatencyVk.dll (the 615.71.09 note is only about that DLL under Proton). On this machine driver 615.71.09 advertises VK_NV_low_latency2 revision 2, so explicit VkLatencySubmissionPresentIdNV attribution (revision 3+) is not honoured: check the revision at runtime. Before 615 the extension did not cut latency on Wayland and VK_KHR_display swapchains. Mesa ships VK_LAYER_MESA_anti_lag (VK_AMD_anti_lag revision 1 on the Intel iGPU). Slot coupling (user, 2026-09-12): a vendor latency backend only when the active upscaler's vendor matches the GPU vendor, otherwise Optimum's own pacing. DLSS/DLSS-G on NVIDIA = Reflex (VK_NV_low_latency2); FSR on AMD = VK_AMD_anti_lag; XeSS(+XeFG) on Intel = XeLL, but only on the Windows D3D12 bridge, Native on Vulkan/Linux; every cross-vendor pair (FSR on NVIDIA or Intel, XeSS on AMD or NVIDIA) = Native. With no upscaler active the device-based auto order applies. Wire it into LatencyBackendSelector (device-only today) when the upscaler slot lands on the DLSS branch.

Measured 2026-09-12 on the RTX 4070 (three 60 s runs, fixed scene, vsync off): input-to-present 7.67 ms with latency off, 1.84 ms with Optimum's own completion pacing and 1.85 ms with Reflex; mean frame time 7.70 / 9.43 / 7.66 ms, so Reflex is free and own pacing costs 18 % of the frame rate. VK_NV_low_latency2 works on the Linux driver at revision 2 and fills its driver/OS-queue/GPU intervals. User decision: **latency reduction ships on by default, Native pacing included** (auto order NV, AMD, Native, None; OPTIMUM_VULKAN_LATENCY forces one).

Intel (user, 2026-09-11): XeFG is a D3D12 proxy swapchain only, so no Linux; on Windows Optimum adds a D3D12 bridge present path (Vulkan images and a timeline semaphore shared with a D3D12 device, DXGI flip swapchain wrapped by XeFG), and XeLL rides on it (XeFG requires XeLL, one shared frame counter, no other latency tech; XeLL needs DXGI Present). Latency backend follows the present path; in XeLL mode Optimum adds no waits of its own.
Licence settled by the user (2026-09-11): the NVIDIA feature libraries are redistributables shipped as binaries, never as source; OptiScaler (GPL-3.0) does the same (loads the driver's NGX core at runtime, ships no NVIDIA DLLs, vendors only headers). Binding: driver 615 `libnvidia-ngx.so.1` (nvidia-utils) exports `NVSDK_NGX_VULKAN_*` itself, so C# P/Invokes the driver library directly, no native shim linking `libnvsdk_ngx.a`; the driver also ships `/usr/lib/nvidia/wine/nvngx_dlssg.dll`, so the Linux driver supports DLSS-G. OptiScaler clone (vendor-research/optiscaler) is the design reference for the orchestrator: `low_latency/` (XeLL, LatencyFlex, VK_AMD_anti_lag, AntiLag 2, Reflex input) and `framegen/IFGFeature` (its frame generation is D3D12-only, so no Vulkan pacer to copy).

**Why:** Streamline advertises cross-IHV but ships only NVIDIA features (plus D3D12 DirectSR); NVIDIA said in NVIDIA-RTX/Streamline issue #12 (2024-03) "implement the plugins yourself"; production `sl::security::loadLibrary` requires `verifyEmbeddedSignature`, which demands a secondary NVIDIA signature (include/sl_security.h isSignedByNVIDIA), so custom Intel/AMD plugins cannot load; Streamline is Windows-only while Optimum also targets Linux.

**How to apply:** design the orchestrator after the vendor research synthesis (workflow on 2026-09-11, local SDK clones under the session scratchpad vendor-research/); it starts on the DLSS branch after Milestone 1 merges to main. Latency seams (frame IDs, markers, sleep point before input, swapchain creation extension point, present IDs) can land in the Phase 2 follow-up. Related: [[optimum-upscaling-roadmap]], [[vulkan-native-rebuild-decision]], [[user-graphics-expertise]].

### Physically correct rendering direction

*Owner decision 2026-09-15 - rendering targets physically correct results, not vanilla's look: AO radiometric (no floor/contrast hack); the long-term path is generated PBR materials and finally ray/path tracing.*

User, 2026-09-15, asked whether the new AO should reproduce vanilla SSAO's look (0.5/0.7 floor, 1.4x contrast) or the radiometric value: "physically correct. as we go for better graphics later on with Generated PBR like some minecraft shaders do and ray/pathtracing in the end."

**Why:** later stages (generated PBR materials as some Minecraft shader packs do, then ray/path tracing) need physically based inputs; art-direction hacks in AO or lighting would have to be undone and would make RT/denoiser comparisons meaningless.

**How to apply:** when a choice is "match vanilla's look" versus "physically correct", pick physically correct (e.g. AO power per the research, no floor or contrast boost; multi-bounce and albedo-dependent terms become real once PBR albedo exists). Keep OpenGL "OFF is vanilla" unchanged. Design data paths (G-buffer channels, material classes) so a PBR material pass can feed them later. Related: [[xegtao-default-with-taa]], [[research-combines-sources]], roadmap items HDR and ray tracing in docs/vulkan-native-plan.md.

### Research before repeating loops

*"User feedback 2026-09-11: on a hard rendering bug, research online and form a real model before more launch/measure loops; repeating in-game hoops without new information reads as no effort and cost the project."*

On 2026-09-11 the Vulkan TAA distance shimmer came back (distant trees jitter between frames). I ran a chain of launch / screenshot-pair / DLL-swap loops, none of which could see one-frame alternation, and never searched for how other TAA implementations handle sub-pixel foliage shimmer or what the Vulkan symptoms of a broken history look like. The user pulled the project ("not skilled enough, no effort to understand, never researched online") and handed it to Codex.

**Why:** the user judges effort by whether new information enters the loop. Re-running the same in-game checks with a measurement already documented as blind to the bug class is visible as churn. Yesterday's fix came from reading the validation log, which was new information; today nothing new was read.

**How to apply:** for a Vulkan-only or TAA-quality bug, before any second launch: (1) web-search the symptom (TAA shimmer on thin/distant geometry, history rejection, jitter phase alternation, swapchain/frame-pacing causes) and the relevant Vulkan spec/best-practice pages, (2) write down the competing mechanisms and the one observation that separates them, (3) only then launch, and only for that observation (e.g. a TaaDebugView validity view over the shimmering region, or an OpenGL eyes-on control). Never offer luma-diff pairs as evidence for frame-to-frame flicker. Related: [[vulkan-validation-log-and-flicker]], [[verify-end-to-end-not-components]], [[taa-p2-vulkan-parity-lessons]].

### Research combines sources

*2026-09-15 feedback - sources the owner names are for deep research that combines their best parts, not a menu to pick one from and integrate; such research goes to a Fable agent at high reasoning.*

User, 2026-09-15, after naming MXAO, Alchemy AO, low-sample GTAO + spatial denoise, openmw-ssao and a Unity GTAO port while I was picking an AO algorithm: "I have not given you those sources to simply integrate. You should research them all and Combine the best parts of all of them. Including XeGTAO. Give this research task to a fable agent at high reasoning."

**Why:** I answered each named source with a verdict (use / reference only / not adopted) and kept steering toward one implementation, instead of studying every source in depth for the parts worth combining.

**How to apply:** when the owner lists sources or alternatives for a design, launch a deep research task (Fable, high effort - an explicit exception to the no-Fable-agents rule) that reads the actual papers and code of every source, compares them against this renderer's constraints and writes a combined design with per-component provenance and licence notes; hold implementation until it is back. Licence limits still decide what may be taken as code versus as an idea. Related: [[look-before-you-work]], [[decide-dont-ask]], [[xegtao-default-with-taa]].

### Scratchpad is tmpfs

*"The session scratchpad is on a 16 GB tmpfs shared with the system; filling it broke the user's system upgrade, so keep dumps small and put anything needed twice in ~/.local/share."*

2026-09-12, user: "your scratchdir has tmpfs filled. made my system upgrade fail". The scratchpad had grown to 12 GB of a 16 GB `/tmp` tmpfs - parity dumps (`p0`, `p1`), TAA traces (`taa-trace`, `tt`), blame and binary copies, plus vendor SDK clones (DLSS with its 1.3 GB `lib`, FidelityFX, OptiScaler, Streamline, the SCS fork).

**Why:** `/tmp` is RAM on this machine and shared with everything else the user runs; a full tmpfs fails package transactions, not just my own commands.

**How to apply:** delete a capture directory as soon as its numbers are recorded in `docs/vulkan-acceptance.md` or the plan - the conclusions are the deliverable, the frames are not. Shallow-clone vendor SDKs, read them, then remove them; the synthesis stays. Anything a test or a later session needs (the NVIDIA NGX libraries, headers and guides) goes to `~/.local/share/optimum-ngx`, never the scratchpad: on tmpfs it vanishes at reboot and the NGX tests then *skip* rather than fail, which hides the breakage. Check `df -h /tmp` before writing GB-scale dumps, and prefer per-attachment dumps at one frame over frame sequences. Related: [[testing-suite-too-heavy]], [[ngx-needs-a-native-shim]].

### Shader patch system todo

*"Future task: build a shader patch system for Optimum; shaders are whole-file overrides today and game updates shadow them silently."*

Raised by the user on 2026-09-10 while P3 of the TAA plan was adding more shader overrides ("might be a nightmare to upkeep with future Updates"). Whole-file overrides in `sources/shaders/` predate TAA (upstream v0.1.0). Agreed: note it and build it later, not during TAA. Design sketch is in TAA-PLAN.md "Follow-up: shader patch system" and CLAUDE.md "Known debt": patches against `.vanilla/archives/vs_client_*.tar.gz`, produced by extract-patches, verified by check-patches, overrides kept additive.

**How to apply:** when the user asks about upkeep, game updates or "shader patches", this is the task; keep new shader edits additive meanwhile. Related: [[taa-p2-vulkan-parity-lessons]], [[optimum-upscaling-roadmap]].

### Speed and parallelism over testing

*"2026-09-11 user direction during the Vulkan-native rebuild - \"enough testing, speed this up, more parallelism in the workflow\"; fewer in-game verification rounds, wider parallel stages."*

After the Phase 1 exit (several in-game capture rounds plus an A/B/A pacing investigation) the user said: "enough testing. Speed this up a bit. More paralellism in the workflow as well".

Capture sessions stay short: 3 minutes is plenty for a session measurement ("That 10 Minute run was excessive", 2026-09-11); never schedule a 10-minute run again.

**Why:** the rebuild spent hours in serial chains (one stage per worktree after another) and in repeated in-game measurement rounds; the user wants throughput.

**How to apply:** design each phase's workflow as wide parallel waves with explicit file ownership and interface contracts in the prompts (no map stage when the touch points are already known), one merge agent per wave, one review at the end. Keep in-game runs to the phase's single exit capture; do not add investigation launches unless a result blocks the next phase. Unit and source tests inside stages stay mandatory. Related: [[vulkan-native-rebuild-decision]], [[no-subagents]], [[research-before-repeating-loops]].

### Taa p2 vulkan parity lessons

*"Why Vulkan TAA jittered for three Codex passes: missing GL_R32F mapping and a masked-out motion clear; single-frame tests hid both. TAA P2 accepted 2026-09-10."*

TAA P2 (in-house resolve) was accepted by the user on 2026-09-10 ("TAA is CHEFSKISS now") at commit 9c32acb on feat/taa. The Vulkan-only "no AA, just jitter" that took three Codex passes came from two parity gaps, not the resolve maths:
1. `GlEnums.cs` had no GL_R32F entry, so the history depth target degraded to RGBA8; 8-bit previous depth made rejection fire randomly, worse with distance (b4d58a2).
2. `ClearColor` on Vulkan is a no-op for an attachment masked out of `SetDrawBuffers`; the motion attachment kept stale vectors (8e4a970). Clear = enable, clear, restore mask.
Both slipped past single-frame GPU tests; Codex's regression test spans frames in flight with Present between them. Acceptance is numeric: still camera, wind stilled (`/weather setw still`), luminance diff of screenshot pairs; parity was Vulkan 1.84 vs OpenGL 1.87.

**How to apply:** for any Vulkan "looks wrong" report, check the format table and clear-vs-mask first (now in the vulkan-parity-debug skill, sections 2 and 2c), and write multi-frame tests for temporal state. P3+ of TAA-PLAN.md continue via workflows (sonnet map, opus stages). Related: [[verify-end-to-end-not-components]], [[delegating-to-codex]], [[optimum-upscaling-roadmap]].

### Testing suite too heavy

*"2026-09-11 - the Vulkan acceptance matrix is too heavy; cut in-game capture to one short run, drop per-attachment SSIM matrices, cap sessions at ~3 minutes."*

User, 2026-09-11, during the Milestone 1 exit capture: "That 10 Minute run was excessive... 3 minutes would have been more than enough" and "The whole testing Suite is Exessive and wastes so much time."

**Why:** the heavy rows measure world noise, not the backend. Two OpenGL launches of one save differed at SSIM 0.86 on the primary colour, so the per-attachment parity matrix cannot separate a real gap from weather, chunk streaming and entity movement; the fixed scene helps pacing but not parity. The long session added nothing the first minute had not shown.

**How to apply:** keep the cheap numeric evidence that actually catches regressions (pacing gate on a 60 s run, the Vulkan stats counters, `taa-rejection.py` on one dump per backend, the GPU suite's `sync,best` validation) and drop the rest: no 10-minute sessions, no multi-launch SSIM matrices, no repeated interleaves unless a number disagrees. One short Vulkan launch for the user to judge closes a milestone. Always set MANGOHUD=0 for validation runs: MangoHud's overlay render pass trips sync validation on the swapchain image and produced 10 phantom errors. Related: [[speed-and-parallelism-over-testing]], [[verify-end-to-end-not-components]], [[run-for-user-no-input]].

### User graphics expertise

*"The user is a graphics programmer who authored the XeSS PR for Skyrim Community Shaders; skip upscaler and TAA primers, talk at implementation level."*

The user authored the XeSS integration PR for Skyrim Community Shaders and judges TAA/upscaler behaviour live by eye with precision (distance-dependent instability, frame-to-frame flicker, "TAA has a distinctive blur"). Their observations have been right every time this project doubted them.

**How to apply:** no primers on jitter, motion vectors or reactive masks; when their live observation contradicts a measurement, the measurement is the suspect. Related: [[vulkan-validation-log-and-flicker]], [[run-for-user-no-input]].

### Vulkan native rebuild decision

*"2026-09-11 decision to rebuild the Vulkan backend as a proper renderer via platform substitution (VulkanClientPlatform : ClientPlatformWindows); plan file path, branches, Milestone 1 definition."*

On 2026-09-11 the user rejected the OpenGL-under-Vulkan emulation design ("I never wanted this as an OpenGL under Vulkan emulator") and approved a plan to rebuild it as a proper Vulkan backend. Plan file: /home/n1ght/.claude/plans/i-never-wanted-this-sequential-kernighan.md. Branch: feat/vulkan-native from origin/main 94e2cc0 (feat/taa merged 2026-09-11). The sky-direction fix lives on fix/taa-sky-direction as its own PR; GPU tests prove it, the user has not accepted it by eye.

Decisions: the client drives a frame graph (the lib only announces frame and stage boundaries); Vulkan-aware mods only (raw GL or Harmony-on-platform mods are routed to OpenGL by the launcher scan); Vulkan-native GLSL for the 48 vanilla programs compiled offline, rewriter kept for mod shaders; Milestone 1 = stable frame delivery with TAA (blocking uploads 0, pacing gate against the OpenGL baseline, sync+best validation clean), judged in game only after the numbers; integration = unseal ClientPlatformWindows through the patcher and ship VulkanClientPlatform : ClientPlatformWindows inside Optimum.Render.Vulkan.dll, deleting the IOptimumGraphicsDevice seam.

Status 2026-09-11 evening: Phase 0 merged (cdd7412) and exit-verified on the RTX 4070 (numbers in docs/vulkan-acceptance.md "Phase 0 exit results"). Vulkan fails the pacing gate (stddev 4.96 ms vs GL 0.55, blocking uploads ~50/s, a FlushFrame per frame from occlusion queries); the user saw no distance jitter on the two Phase 0 exit runs, but it was back on Vulkan in every later run (Phase 1 build included) and never appears on OpenGL: Vulkan-only, intermittent between sessions, still unexplained. Next: Phase 1A and 1B in parallel.

Status 2026-09-11 night: **Milestone 1 accepted by the user** at 6568556 after a 10-minute Vulkan session. Phases 0, 1A, 1B and 2 are done and merged into main locally (not pushed): blocking uploads 0, passes == scopes (22.3 per frame), 0 pass splits or mask restarts, validation clean, SSAO alpha gap closed, TAA distant-leaf rejection 1.05 % on both backends. Carried to Phase 4: Vulkan costs ~25 % more frame time than OpenGL on the fixed scene (7.59 ms vs 6.08, stddev 0.37 vs 0.12) and is GPU-bound (5.43 ms of the frame in the frame-pacing wait), so the pacing gate still fails its stddev rule. Also open: TransientAllocator is not wired into the frame graph, ClearDepth ignores the depth write mask, BuildMipMaps LOD-bias parity. Next: the latency seams (plan section "Latency seams", branch feat/latency) and DLSS.

Branching rule (user, 2026-09-11): at Milestone 1, merge feat/vulkan-native back into main (after merging fix/taa-antiflicker-disocclusion into it), then start a new branch from main for the next work (DLSS). Confirm the merge mechanics (PR on origin, as with feat/taa PR #2) with the user at that point.

**Why:** the GL-shaped seam forced GL semantics per call (scope inference, ALL_COMMANDS barriers, synchronous uploads, coupled present) and the user judged the backend brittle at the foundation.

**How to apply:** work phase by phase from the plan file (0 foundations, 1A platform substitution, 1B sync foundation, 2 frame graph = Milestone 1, 3 native shaders, 4 performance, 5 mod API, 6 upscaler seams); in-game runs only at phase exits, both backends, renderer line confirmed; evidence is numbers and logs, never screenshot pairs. Related: [[research-before-repeating-loops]], [[vulkan-validation-log-and-flicker]], [[taa-p2-vulkan-parity-lessons]], [[no-subagents]].

### Vulkan validation log and flicker

*"Vulkan validation messages go to a file, not the client log (OPTIMUM_VULKAN_VALIDATION=1 -> $TMP/optimum-vulkan-validation.log; FEATURES=sync,best); frame-to-frame flicker cannot be seen in screenshots. P4 accepted 2026-09-11."*

2026-09-11: the Vulkan-only "everything jitters, no AA, worse at the horizon" after P3/P4 survived every single-frame probe (motion, validity, history, uniforms all identical to GL) because the defect alternated between frames: fullscreen passes left the SSAO normal/position attachments write-enabled without storing to them, Vulkan wrote undefined values, SSAO outlines flickered. Found within minutes once the validation log was actually read (it had been going to a file named "1" or nowhere) with sync + best-practices validation. Fix 95bf71d: mask unwritten fragment outputs in the pipeline, present-path wait stage AllCommands, per-image semaphores, layout-accurate barrier accesses. User: "that fixed the instability issue fully".

**How to apply:** for any Vulkan-only artefact, first run with `OPTIMUM_VULKAN_VALIDATION=/abs/log OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best` and read `[error]` lines; screenshots and per-frame diag shaders cannot see one-frame alternation. The user judges live; when they say it flickers between frames, believe it and look for API-level undefined behaviour, not resolve maths. Related: [[taa-p2-vulkan-parity-lessons]], [[run-for-user-no-input]].

### Waiting on long running processes

*"Wait for a real signal (log line, exit sentinel, Monitor), never blind-sleep; for the game the in-world line is '[Client Chat] Welcome' plus 8 s."*

Blind sleeps repeatedly captured the loading screen or typed into a game that was not accepting input yet. The reliable markers: `[Client Chat] Welcome` for "player is in the world" (savegame-loaded and AssetsFinalize come ~20 s earlier), `^CODEX_EXIT [0-9]+$` for the Codex wrapper, workflow task notifications for agents. Kill leftovers through the wrapper scripts before a new launch.

**How to apply:** poll the log for the marker with a bounded loop, then a short fixed margin; never `sleep 60` and hope. Related: [[pkill-self-match]], [[run-for-user-no-input]].

### Xegtao default with taa

*Owner decision 2026-09-15 - XeGTAO is the default ambient occlusion on Vulkan whenever TAA is active; vanilla SSAO otherwise and always on OpenGL.*

User, 2026-09-15: "XeGTAO should become default when TAA is active."

The AO setting on Vulkan is Auto by default: XeGTAO while TAA (the temporal consumer) is active, vanilla SSAO when it is off; an explicit choice overrides Auto. OpenGL keeps vanilla SSAO ("OFF is vanilla").

**Why:** the user, same day: "the games SSAO is worst case for Temporal Rendering" (screen-locked Bayer dither re-rolled every jittered frame). XeGTAO's noise is designed to converge through a temporal accumulator; without TAA vanilla SSAO's fixed dither is the better fallback, and the whole-frame jitter work already moved AO into the scene before the resolve.

**How to apply:** any XeGTAO stage, setting default, coverage test or acceptance note follows this rule; handoff item 8 in docs/vulkan-branch-progress.md should state it. Related: [[vulkan-taa-jitter-root-cause]], [[frame-generation-needs-pacing]].

## Known debt
- Shaders are whole-file overrides, not patches. A shader patch system (`patches/shaders/*.patch`
  against the vanilla archive, extract + check) is planned; until then keep overrides additive and
  diff against `.vanilla/archives/vs_client_*.tar.gz` after every game update (see TAA-PLAN.md follow-up).
