# Optimum: working rules for agents

*How to work on this repository, for any agent on any branch. `CLAUDE.md` is a symlink to this file.
Branch scope, status and decisions are in `docs/vulkan-branch-progress.md`, not here.*

Optimum is a performance mod for Vintage Story: a patched client (OpenGL path in
`ClientPlatformWindows`) plus a Vulkan backend that substitutes the platform
(`VulkanClientPlatform : ClientPlatformWindows` in `Optimum.Render.Vulkan/Platform/`). Read this before touching anything. The
skills in `.claude/skills/` (tracked) hold the step-by-step procedures; this file holds the rules and the
working knowledge. Work happens in the session, one step at a time, visibly (rule 8). A lesson learned during a session belongs here, in the repository; the harness memory
store on one machine is a cache, not the record.

## Where the truth lives (edit these, never the generated copies)

| What | Edit here | Generated from it | Ships as |
|---|---|---|---|
| Game client code | `build/VintagestoryLib/**` (decompiled + patched) | `patches/VintagestoryLib/*.patch` via `scripts/extract-patches.sh` | Cecil transplant into vanilla DLL; every changed/new method or member MUST be listed in `Optimum.Patcher/Program.cs` |
| Game API | `VintagestoryApi/**` (hand-maintained fork, git-ignored) | `sources/VintagestoryApi/**` via extract | `VintagestoryAPI-patched.dll`; new files also go in `optimum-api-contracts/optimum-api-contracts.csproj` (path `..\sources\VintagestoryApi\...`) and get a `<Compile Remove>` in both `VintagestoryApi/VintagestoryAPI.csproj` and `sources/VintagestoryApi/VintagestoryAPI.csproj` |
| Mods | `VSEssentials/`, `VSSurvivalMod/`, `VSCreativeMod/` (forks) | `patches/<mod>/*.patch` via extract | recompiled mod DLLs plus `Optimum.Patcher/mod-patcher.cs` manifests for the installed-runtime path |
| Shaders | `sources/shaders/*.vsh/.fsh` (override vanilla by file name) | shipped by `make deploy` and `scripts/package-*` | includes: `sources/shaderincludes/` (add to deploy and packagers when first used) |
| Native Vulkan shaders | `sources/shaders-vk/*.vert/.frag/.interface.glsl` + `include/` (contract: `docs/vulkan-native-shaders.md`) | `shaders.manifest.json` + SPIR-V by `tools/shader-compiler` (MSBuild target) | `shaders-vk/` beside the client; runtime falls back per program to the rewriter |
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
make patch-il                                      # Cecil patch only, no deploy: run after every lib/patcher change; "N/N required methods patched" or it fails
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
`OPTIMUM_VULKAN_POISON=1` (fresh images NaN/magenta/0xDEADBEEF, depth 0.5, buffers 0xDEADBEEF: undefined reads become loud),
`OPTIMUM_VK_NATIVE_SHADERS=force|0` (force: link every program from the native manifest even without a mod scan; 0: rewriter for all;
the log line `[Optimum] shaders: N native, M rewritten, K failed` says what happened), `OPTIMUM_VK_SHADER_SOURCE=<dir>` (compile the
native tree at runtime for the dev loop), `OPTIMUM_VULKAN_SYNC_PIPELINES=1` (blocking pipeline creation instead of the background worker).
Headless capture: `OPTIMUM_HEADLESS=1` (window created but never mapped or focused, both backends),
`OPTIMUM_HEADLESS_FRAMES=<dir>` plus `OPTIMUM_HEADLESS_FIRST_FRAME`/`_FRAME_COUNT`/`_FRAME_STRIDE` or
`_FRAME_LIST` (which in-world frames to write as PPM), `OPTIMUM_HEADLESS_COMMANDS=<file>` with
`OPTIMUM_HEADLESS_COMMAND_FRAME` (chat lines fed on that frame: `/time`, `/weather`, `.cam load`/`.cam play`),
`OPTIMUM_HEADLESS_FIXED_DT` (pins the simulated step). A display server is still required - headless here
means no visible window, not no display.
DLSS/NGX (branches `feat/dlss*` only; not present on the Vulkan-only branches): the NVIDIA feature libraries live in
`~/.local/share/optimum-ngx`, never in the scratchpad (tmpfs; after a reboot the NGX tests would skip instead of fail);
`OPTIMUM_NGX_FEATURE_PATH=~/.local/share/optimum-ngx/lib/Linux_x86_64/rel` runs them.
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
   on 2026-09-16 that produced a "client is running" report hours after the owner had closed it. Ask with
   `ps -eo pid,stat,args | grep -i <name> | grep -v grep`, or read the client log. Close the game with the kill script (window close
   first) to avoid shutdown-race crash reports. Close the game as soon as a check is done; never leave it running.
6. **Git.** Never `git stash`. Commit WIP on the branch with a `wip:` prefix instead. Branch from
   `main` (tracks `origin/main` = KillerPixelCrew/VulkanStory, migrated from NightHammer1000/VulkanStory on 2026-09-15; `upstream` = StratumServer/Optimum).
   Commit only when asked or when a phase is verified; say what was verified in the message.
7. **Batch reads.** Read whole methods and both paths in one command (`sed -n` ranges + `rg`), not
   ten single greps. Codex found in one pass what took an afternoon of small probes.
8. **Work directly in the session, sequentially. No workflows, no background agents.** (Owner, 2026-09-16:
   "The Workflow approach does not work for me. I cant see whats happening.") The session reads, edits, builds,
   tests and reports each step itself, in order, so the owner can watch every change land. A subagent is allowed
   only for a read-only search the session would otherwise do by hand, returns text, and never edits. Codex
   (`.claude/skills/codex-handoff`) remains available for a genuinely stuck rendering bug on the owner's say-so.

19. **Build and tests cannot see two crash classes; `make patch-il` and the API-drift check can.** Both compile
   against the fork, so a transplant tuple with the wrong parameter count or a lib call to a member that exists only
   in the API fork passes them and ships as a crash (2026-09-16: 1271 + 1107 tests green, then
   RenderTextureIntoFrameBuffer listed with 9 params against vanilla's 10, and `MeshDataPool.get_ModelRef` crashing
   both backends). After any lib or fork change run `make patch-il` and `diff -r .baseline/VintagestoryApi
   VintagestoryApi`, and grep the lib for each added public member. A new member on a vanilla API type never ships
   unless api-patcher.cs injects it: use a scope seam (BeginChunkPass/EndChunkPass pattern) or a contracts type.

9. **Undefined behaviour differs between the APIs.** GL keeps an attachment the shader never writes;
   Vulkan writes garbage into it (pipelines now mask those off). A bug that only flickers between
   frames is invisible to screenshots and to per-frame probes: read the validation log with sync +
   best-practices enabled before instrumenting anything.

10. **Temporal and pacing claims need numbers, never screenshots.** Accepted evidence: the validation
   log with `sync,best`; a multi-frame GPU test (Present between frames, no readback in the loop);
   `pacing-gate.sh` numbers against the OpenGL baseline of the same scene; `ssim.py` per-attachment
   tables; a 60 fps `ffmpeg -f x11grab` capture with consecutive-frame diffs for flicker; poison mode
   for suspected undefined reads. Where the numbers are recorded: `docs/vulkan-acceptance.md`; branch status and the plan pointer:
   `docs/vulkan-branch-progress.md`.

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
   items marked done that were never done, and separately a model, a process state and a branch scope were asserted
   from documents instead of from checks - each one wrong. If it was not checked this session, say that it was not.

13. **Decide, don't ask.** Research the open question to a decision and act on it. Hand a choice back only when it
   is genuinely the owner's (money, scope, upstream, destructive acts) and you cannot resolve it from what they
   already said. Turning an instruction they just gave you back into a question is the failure mode.

14. **Verification, not review rounds.** After each change: build, both suites, `make patch-il`, and for anything
   that touches the screen the headless both-backends capture. No separate review passes.

15. **Grep, don't map - and document the seams.** Every render seam carries a doc comment at its declaration: what
   it draws, where the OpenGL body is, target and slots, the state that is not obvious and why, and the test that
   pins it (`docs/vulkan-native-render-systems.md` section 4). Document what you touch as part of the change.

16. **Only the session launches the game, deploys or pushes**, and only after the checks in rule 14.

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
  an injected `= -1` is 0. `CecilInjectedFieldInitializerTests` enforces it for every injected field, with no allowance list. Use the CLR
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

Working practice learned the hard way, branch-agnostic. Decisions, roadmap and branch scope are NOT here:
they live in `docs/vulkan-branch-progress.md` and the plan. A new working lesson belongs here; a new decision
belongs there.

### Cleanup comes last

*Documentation and comment cleanup happens at the very end of the project; until then comments are moved but never trimmed.*

User, 2026-09-12: "We do Code documentation and comment cleanup at the very end."

**Why:** the comments in this repo are the record of what each defect cost, and many carry measured numbers - the 1.05 % distant-leaf TAA rejection, the 0.37 to 0.02 display-pixel jitter residual, why NGX's shutdown is gated, why the acquire wait stage may never be ALL_COMMANDS. While the renderer is still moving, trimming them deletes the reasoning that keeps the next agent from reintroducing the bug.

**How to apply:** never open a "tidy the comments" task, and never let a refactor quietly drop an xml-doc - a consolidation or a move carries every "why" forward verbatim. Stale comments that are actively wrong are still fixed on the spot, as part of the change that made them wrong. One cleanup pass at the end, when the renderer settles. Related: `testing-suite-too-heavy`, `speed-and-parallelism-over-testing`.

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
  it, and monitor for a sentinel line (see `pkill-self-match`).

**Why:** on the Vulkan backend it found three real bugs in one pass that I had missed over
a long session, and verified them by playing the game across several views and two worlds.

**How to apply:** when stuck on something the user is getting frustrated with, offer Codex
early rather than late, brief it neutrally, and give it the whole machine. See
`verify-end-to-end-not-components`.

Steering (2026-09-10): `codex queue --thread <id> --message` only reaches a running session; messages queued after exit are lost, so continue with `codex exec resume <session-id>`. Monitor on `^CODEX_EXIT [0-9]+$` (Codex narrates the word and false-matched a looser pattern). Relay the user's observations verbatim and promptly; each one ("gets worse with distance") narrowed the search. Codex does not push; verify its claims in-game, then push.

It is on a weekly quota the owner tracks: keep briefs tight and do not launch speculatively.

### Git remotes

*"In the Optimum checkout, origin is KillerPixelCrew/VulkanStory (the org repo, migrated from NightHammer1000/VulkanStory on 2026-09-15) and upstream is StratumServer/Optimum; main tracks origin/main."*

Remote layout (set 2026-09-10 at the user's request):
- `origin` = https://github.com/KillerPixelCrew/VulkanStory.git ("ours"; the repository moved into the KillerPixelCrew organisation on 2026-09-15, it used to be NightHammer1000/VulkanStory). `main` tracks `origin/main`. PRs for the Vulkan work go here; `gh` default repo is set to it.
- `upstream` = https://github.com/StratumServer/Optimum.git. Does not have the Vulkan backend yet.

**How to apply:** push branches and open PRs against `origin`. Only touch `upstream` when the user asks to sync with or contribute to StratumServer. Related: `optimum-upscaling-roadmap`.

### Look before you work

*"Before designing or launching any implementation wave, read the vendor docs in full and the reference implementations on disk (~/Projekte/ReScaleFrame/references) and check best practice online; the user has had to say this three times."*

User, 2026-09-13: "This is the third time i have to tell you to actually look before you work." (The previous two, same day: "DLSSFG without pacing (Reflex) is useless and unplayable" and "Have you checked that frameplacment against best practice online and in the Framegen Documentation?")

**Why:** I designed DLSS-G frame pacing from one chapter of NVIDIA's guide plus my own reasoning and launched a 7-agent workflow on it. The user's own reference checkouts in `~/Projekte/ReScaleFrame/references/` (Streamline, FidelityFX-SDK, xess, OptiScaler) and their ReScaleFrame design docs already contradicted it: AMD paces both presents from the previous present with a 10-frame moving average, CPU-waits for GPU completion before presenting, keeps one frame in flight, caps render slightly below half the output rate; Streamline measures pacing by display change, not present call; only generated frames are dropped. Two workflows were stopped as a result.

**How to apply:** for any feature with vendor SDKs or prior art: (1) read the vendor guides in full, not the chapter that matches the question; (2) read the reference implementations on disk - check `~/Projekte/ReScaleFrame/references/` and the user's ReScaleFrame docs first, they are the user's own research; (3) search online for best practice; (4) write the design with a source for every decision and mark what is reasoning; (5) show the user the sourced design before launching an implementation wave. A map of *our* code is not research into *how it should be done*. Related: `research-before-repeating-loops`, `audit-the-component-the-user-names`, `frame-generation-needs-pacing`, `user-graphics-expertise`.

### Nvidia driver update needs reboot

*GLXBadFBConfig on every OpenGL launch plus Vulkan silently picking the Intel iGPU means the NVIDIA userspace driver was updated without a reboot; check nvidia-smi and the log's GPU line before any capture.*

On 2026-09-11 a pacman update at 14:38 moved nvidia-utils 610.57.04 to 615.71.09 while the loaded kernel module stayed 610. Symptoms: every OpenGL launch through `prime-run` crashed at window creation ("GLX: Failed to create context: GLXBadFBConfig"), `prime-run glxinfo -B` failed with "X Error ... BadValue", `nvidia-smi` printed "Failed to initialize NVML: Driver/library version mismatch", and Vulkan still started but on "Intel(R) UHD Graphics (ADL-S GT1)", which is invalid for measurements. A reboot fixed all of it.

**Why:** it looked like a Phase 0 regression and cost a capture round; the user watched the clients crash.

**How to apply:** before any in-game capture run `nvidia-smi` (must print the GPU and driver, not a mismatch) and, after each launch, require `Graphics Card Renderer: NVIDIA` in the client log (on Vulkan that line is the selected Vulkan device name). If the mismatch shows, tell the user a reboot is needed instead of launching. Related: `confirm-renderer-from-log`, `vulkan-native-rebuild-decision`.

### Research before repeating loops

*"User feedback 2026-09-11: on a hard rendering bug, research online and form a real model before more launch/measure loops; repeating in-game hoops without new information reads as no effort and cost the project."*

On 2026-09-11 the Vulkan TAA distance shimmer came back (distant trees jitter between frames). I ran a chain of launch / screenshot-pair / DLL-swap loops, none of which could see one-frame alternation, and never searched for how other TAA implementations handle sub-pixel foliage shimmer or what the Vulkan symptoms of a broken history look like. The user pulled the project ("not skilled enough, no effort to understand, never researched online") and handed it to Codex.

**Why:** the user judges effort by whether new information enters the loop. Re-running the same in-game checks with a measurement already documented as blind to the bug class is visible as churn. Yesterday's fix came from reading the validation log, which was new information; today nothing new was read.

**How to apply:** for a Vulkan-only or TAA-quality bug, before any second launch: (1) web-search the symptom (TAA shimmer on thin/distant geometry, history rejection, jitter phase alternation, swapchain/frame-pacing causes) and the relevant Vulkan spec/best-practice pages, (2) write down the competing mechanisms and the one observation that separates them, (3) only then launch, and only for that observation (e.g. a TaaDebugView validity view over the shimmering region, or an OpenGL eyes-on control). Never offer luma-diff pairs as evidence for frame-to-frame flicker. Related: `vulkan-validation-log-and-flicker`, `verify-end-to-end-not-components`, `taa-p2-vulkan-parity-lessons`.

### Research combines sources

*2026-09-15 feedback - sources the owner names are for deep research that combines their best parts, not a menu to pick one from and integrate; such research goes to a Fable agent at high reasoning.*

User, 2026-09-15, after naming MXAO, Alchemy AO, low-sample GTAO + spatial denoise, openmw-ssao and a Unity GTAO port while I was picking an AO algorithm: "I have not given you those sources to simply integrate. You should research them all and Combine the best parts of all of them. Including XeGTAO. Give this research task to a fable agent at high reasoning."

**Why:** I answered each named source with a verdict (use / reference only / not adopted) and kept steering toward one implementation, instead of studying every source in depth for the parts worth combining.

**How to apply:** when the owner lists sources or alternatives for a design, launch a deep research task (Fable, high effort - an explicit exception to the no-Fable-agents rule) that reads the actual papers and code of every source, compares them against this renderer's constraints and writes a combined design with per-component provenance and licence notes; hold implementation until it is back. Licence limits still decide what may be taken as code versus as an idea. Related: `look-before-you-work`, `decide-dont-ask`, `xegtao-default-with-taa`.

### Scratchpad is tmpfs

*"The session scratchpad is on a 16 GB tmpfs shared with the system; filling it broke the user's system upgrade, so keep dumps small and put anything needed twice in ~/.local/share."*

2026-09-12, user: "your scratchdir has tmpfs filled. made my system upgrade fail". The scratchpad had grown to 12 GB of a 16 GB `/tmp` tmpfs - parity dumps (`p0`, `p1`), TAA traces (`taa-trace`, `tt`), blame and binary copies, plus vendor SDK clones (DLSS with its 1.3 GB `lib`, FidelityFX, OptiScaler, Streamline, the SCS fork).

**Why:** `/tmp` is RAM on this machine and shared with everything else the user runs; a full tmpfs fails package transactions, not just my own commands.

**How to apply:** delete a capture directory as soon as its numbers are recorded in `docs/vulkan-acceptance.md` or the plan - the conclusions are the deliverable, the frames are not. Shallow-clone vendor SDKs, read them, then remove them; the synthesis stays. Anything a test or a later session needs (the NVIDIA NGX libraries, headers and guides) goes to `~/.local/share/optimum-ngx`, never the scratchpad: on tmpfs it vanishes at reboot and the NGX tests then *skip* rather than fail, which hides the breakage. Check `df -h /tmp` before writing GB-scale dumps, and prefer per-attachment dumps at one frame over frame sequences. Related: `testing-suite-too-heavy`, `ngx-needs-a-native-shim`.

### Sequential, visible work (supersedes "speed and parallelism", 2026-09-16)

Earlier direction favoured wide parallel workflow waves. The owner reversed it on 2026-09-16 after a day of
merges landing work they could not watch: "The Workflow approach does not work for me. I cant see whats
happening." One change at a time in the session, verified before the next. What survives from the earlier
direction: capture sessions stay short (3 minutes is plenty; never a 10-minute run) and in-game runs are for the
exit of a piece of work, not for investigation loops.

### Taa p2 vulkan parity lessons

*"Why Vulkan TAA jittered for three Codex passes: missing GL_R32F mapping and a masked-out motion clear; single-frame tests hid both. TAA P2 accepted 2026-09-10."*

TAA P2 (in-house resolve) was accepted by the user on 2026-09-10 ("TAA is CHEFSKISS now") at commit 9c32acb on feat/taa. The Vulkan-only "no AA, just jitter" that took three Codex passes came from two parity gaps, not the resolve maths:
1. `GlEnums.cs` had no GL_R32F entry, so the history depth target degraded to RGBA8; 8-bit previous depth made rejection fire randomly, worse with distance (b4d58a2).
2. `ClearColor` on Vulkan is a no-op for an attachment masked out of `SetDrawBuffers`; the motion attachment kept stale vectors (8e4a970). Clear = enable, clear, restore mask.
Both slipped past single-frame GPU tests; Codex's regression test spans frames in flight with Present between them. Acceptance is numeric: still camera, wind stilled (`/weather setw still`), luminance diff of screenshot pairs; parity was Vulkan 1.84 vs OpenGL 1.87.

**How to apply:** for any Vulkan "looks wrong" report, check the format table and clear-vs-mask first (now in the vulkan-parity-debug skill, sections 2 and 2c), and write multi-frame tests for temporal state. Related: `verify-end-to-end-not-components`, `delegating-to-codex`, `optimum-upscaling-roadmap`.

### Testing suite too heavy

*"2026-09-11 - the Vulkan acceptance matrix is too heavy; cut in-game capture to one short run, drop per-attachment SSIM matrices, cap sessions at ~3 minutes."*

User, 2026-09-11, during the Milestone 1 exit capture: "That 10 Minute run was excessive... 3 minutes would have been more than enough" and "The whole testing Suite is Exessive and wastes so much time."

**Why:** the heavy rows measure world noise, not the backend. Two OpenGL launches of one save differed at SSIM 0.86 on the primary colour, so the per-attachment parity matrix cannot separate a real gap from weather, chunk streaming and entity movement; the fixed scene helps pacing but not parity. The long session added nothing the first minute had not shown.

**How to apply:** keep the cheap numeric evidence that actually catches regressions (pacing gate on a 60 s run, the Vulkan stats counters, `taa-rejection.py` on one dump per backend, the GPU suite's `sync,best` validation) and drop the rest: no 10-minute sessions, no multi-launch SSIM matrices, no repeated interleaves unless a number disagrees. One short Vulkan launch for the user to judge closes a milestone. Always set MANGOHUD=0 for validation runs: MangoHud's overlay render pass trips sync validation on the swapchain image and produced 10 phantom errors. Related: `speed-and-parallelism-over-testing`, `verify-end-to-end-not-components`, `run-for-user-no-input`.

### User graphics expertise

*"The user is a graphics programmer who authored the XeSS PR for Skyrim Community Shaders; skip upscaler and TAA primers, talk at implementation level."*

The user authored the XeSS integration PR for Skyrim Community Shaders and judges TAA/upscaler behaviour live by eye with precision (distance-dependent instability, frame-to-frame flicker, "TAA has a distinctive blur"). Their observations have been right every time this project doubted them.

**How to apply:** no primers on jitter, motion vectors or reactive masks; when their live observation contradicts a measurement, the measurement is the suspect. Related: `vulkan-validation-log-and-flicker`, `run-for-user-no-input`.

### Vulkan validation log and flicker

*"Vulkan validation messages go to a file, not the client log (OPTIMUM_VULKAN_VALIDATION=1 -> $TMP/optimum-vulkan-validation.log; FEATURES=sync,best); frame-to-frame flicker cannot be seen in screenshots. P4 accepted 2026-09-11."*

2026-09-11: the Vulkan-only "everything jitters, no AA, worse at the horizon" after P3/P4 survived every single-frame probe (motion, validity, history, uniforms all identical to GL) because the defect alternated between frames: fullscreen passes left the SSAO normal/position attachments write-enabled without storing to them, Vulkan wrote undefined values, SSAO outlines flickered. Found within minutes once the validation log was actually read (it had been going to a file named "1" or nowhere) with sync + best-practices validation. Fix 95bf71d: mask unwritten fragment outputs in the pipeline, present-path wait stage AllCommands, per-image semaphores, layout-accurate barrier accesses. User: "that fixed the instability issue fully".

**How to apply:** for any Vulkan-only artefact, first run with `OPTIMUM_VULKAN_VALIDATION=/abs/log OPTIMUM_VULKAN_VALIDATION_FEATURES=sync,best` and read `[error]` lines; screenshots and per-frame diag shaders cannot see one-frame alternation. The user judges live; when they say it flickers between frames, believe it and look for API-level undefined behaviour, not resolve maths. Related: `taa-p2-vulkan-parity-lessons`, `run-for-user-no-input`.

### Waiting on long running processes

*"Wait for a real signal (log line, exit sentinel, Monitor), never blind-sleep; for the game the in-world line is '[Client Chat] Welcome' plus 8 s."*

Blind sleeps repeatedly captured the loading screen or typed into a game that was not accepting input yet. The reliable markers: `[Client Chat] Welcome` for "player is in the world" (savegame-loaded and AssetsFinalize come ~20 s earlier), `^CODEX_EXIT [0-9]+$` for the Codex wrapper, workflow task notifications for agents. Kill leftovers through the wrapper scripts before a new launch.

**How to apply:** poll the log for the marker with a bounded loop, then a short fixed margin; never `sleep 60` and hope. Related: `pkill-self-match`, `run-for-user-no-input`.

