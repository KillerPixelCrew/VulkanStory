# Optimum roadmap

**Goal:** a smooth, playable Vintage Story on handheld PCs, not only maximum FPS on strong
GPUs. The reference device is the **MSI Claw 8 AI+** (Intel Core Ultra 7 258V: 4 P-cores
and 4 LP E-cores; Arc 140V iGPU with XMX; unified LPDDR5X; 1920×1200 120 Hz VRR;
Windows 11), and development moves onto it. Work is judged by frame-time consistency
(1% lows), CPU and GPU efficiency under a shared power budget, streaming without hitches,
battery life, and controller-first play. Results from the RTX 4070 Laptop development system
(an Optimus laptop whose Intel iGPU scans out) find costs; they are not the target.

Players get it as a **double-click installer and launcher from GitHub Releases**: no SDKs,
build tools, scripts, or PowerShell execution-policy changes on their machine.

The project is standalone. It renders with Vulkan only and supports Windows and Linux.
macOS is dropped. DX12 remains as an optional Windows interop subsystem for XeSS FG and
FSR 4.

**Done** means integrated into the Vulkan renderer, exposed in the product configuration
where appropriate, and validated with the available automated and in-game checks. A
vendor or GPU missing from the development machine is recorded as a validation gap; it does
not move an otherwise complete integration back into planned work.

Impact ratings below are estimates from reading the code unless a measurement is cited. The
Claw baseline in milestone 1 decides the final order inside each later milestone.

## Done

| Area | Delivered | Evidence |
| --- | --- | --- |
| **Vulkan-native renderer** | Native Vulkan rendering and shaders, frame graph and synchronization, async uploads, bindless resources, native post-processing, TAA, GTAO, separate HUD-less scene/UI resources, and Vulkan presentation | [`vulkan.md`](vulkan.md) |
| **Upscalers** | NVIDIA DLSS SR, Intel XeSS SR (native Vulkan), AMD FSR 3.1 SR, and AMD FSR 4 through the Vulkan/DX12 interop path; presets, fallback, and live switching share one renderer contract | [`upscaler.md`](upscaler.md) |
| **Frame generation** | NVIDIA DLSS-G, Intel XeSS FG, and AMD FSR 3 FG consume the common depth, motion, HUD-less scene, and premultiplied UI resources. The UI exposes 2x–6x; DLSS-G and XeSS-FG clamp to the SDK/GPU maximum, FSR 3 FG stays at 2x | [`frame-generation.md`](frame-generation.md) |
| **Low latency** | NVIDIA Reflex, Intel XeLL, and AMD Anti-Lag use the engine frame identity, frame cap, sleep, and simulation/render/present markers. SDL input keying and the PCL ping path are implemented; cross-vendor hardware and verification-tool trials remain (milestone 3) | [`frame-generation.md`](frame-generation.md) |
| **Optimization pass** | GPU timestamps (`stats.gpu`) found the major costs. Persistent chunk meshes moved to mapped VRAM where supported, the GTAO prefilter was parallelized, the foliage alpha test moved ahead of lighting, redundant pipeline binds were removed, and stray OpenGL calls in the cloud renderers were fixed | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |
| **XeSS-FG pacing** | The next frame's Vulkan work waits on the GPU for XeSS-FG's DX12 work instead of time-slicing with it, and the proxy `Present` runs on a present thread; the bimodal 2/10 ms presents are gone | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |
| **Platform scope** | macOS removed from packaging, installer, bootstrap, CI, and docs | — |

On the RTX 4070 Laptop, the optimization pass cut the native 1080p GPU frame from
**10.30 ms to 4.49 ms**. Focused-window 1080p Quality runs reached 2x output with all three
frame-generation providers: 334 FPS for FSR 3 FG, 258 FPS for DLSS-G, and 286 FPS for
XeSS-FG (229 before its pacing fix). These numbers describe that system and scene; they
are not guarantees.

## Now: milestone 0 — player installer and launcher

**Done — client-discovery foundation (2026-09-26):** `Optimum.Bootstrap.Core/Install/GameInstallProbe.cs`
provides read-only client discovery at common Windows/Linux locations and explicitly
supplied directories. It reads `GameVersion.ShortGameVersion` from API assembly metadata
without loading game code, preserves prerelease suffixes, rejects incomplete client
layouts, and reports unreadable versions as unknown (never as compatible). Six dedicated
discovery/metadata tests pass. Discovery is wired into the release-bundle GUI; exact pack
compatibility is checked by hashes. Windows registry uninstall entries now supply custom
install locations, with client-layout validation and deduplication; remote pack delivery
remains open.

**Distribution decision (2026-09-26): binary deltas.** Build the patched assemblies on
our machine with the existing Cecil pipeline; distribute VCDIFF patches against exact
official input hashes, plus separately reviewed Optimum binaries. Players supply their
installed originals. Apply into a separate cache, verify output hashes, and leave vanilla
untouched. Prebuilt transplant packs and a wholesale runtime-IL rewrite are not the plan.

**Local proof of concept:** [`scripts/binary-delta-poc.py`](../scripts/binary-delta-poc.py)
uses xdelta 3.2.0 to build and apply DLL and shader-asset packs, with input/patch/output
SHA-256 checks and staging before publishing a local result directory. Eight synthetic
integration tests pass (`OPTIMUM_XDELTA=<executable> python scripts/test-binary-delta-poc.py`). It is a
developer experiment; Python and the xdelta CLI are not the planned player interface.

**C# application path:** `BinaryDeltaPack` in Bootstrap.Core now consumes
`optimum-vcdiff-1` manifests with exact game/Optimum versions, RID, and input/delta/output
sizes and hashes. A shader delta may name a different official source path for a new
Optimum shader; the allowed asset paths and source paths are restricted and checked. It verifies all inputs before decoding, decodes verified snapshots into
staging, checks every result, and publishes only a complete new output directory. It rejects
unsupported targets, traversal paths, symbolic-link components and existing outputs;
cancellation or verification failure discards staging. The caller must authenticate the
release manifest; hashes alone do not authenticate its publisher.

The developer CLI exposes this without invoking a build:

```text
optimum apply-delta --game-dir <absolute-original-dir> --pack <absolute-pack-dir>
  --output <absolute-new-cache-dir> --decoder <absolute-xdelta-executable>
  --game-version 1.22.7 --optimum-version 0.3.17 --rid linux-x64
```

The generator's `build` command also requires `--game-version`, `--optimum-version` and
`--rid`. `--decoder` is now optional: a self-contained CLI publish with a bundled decoder
successfully reconstructed the real four-DLL pack without a `dotnet` invocation or a
decoder-path argument. `scripts/bundle-delta-decoder.py` stages a separately authenticated
xdelta 3.2.0 executable, its upstream license and SHA-256 descriptor. Publish the CLI,
installer or launcher with `-p:DeltaDecoderDirectory=<absolute-bundle-directory>` to include
it. The decoder remains a bundled subprocess; in-process decoding is still open.
`release-inputs.json` now pins the exact Windows/Linux client archives, xdelta 3.2.0
binary/source archives and extracted executables by SHA-256. `acquire-delta-decoder.py`
checks those hashes, takes the license from the pinned source archive and stages the
bundle for CI. Its offline contract tests and local acquisition for both RIDs pass;
the hosted CI acquisition has not run yet.

**Separate runtime installation and activation:** `DeltaRuntimeInstaller`, exposed as
`optimum install-delta` (the `apply-delta` flags plus `--payload <absolute-payload-dir>`),
copies the local original client and a trusted published Optimum payload into staging,
adds the verified patched DLLs, writes `.optimum/delta-runtime.json`, and publishes a new
runtime directory only after verification. No hard links or writes to original game files.
Vanilla `.optimum` state, logs, `datapath.cfg` and stale target PDBs are excluded; payloads
cannot override the four delta targets or their symbols. The payload must supply all
Optimum runtime dependencies; this command does not build or acquire them.

The launcher recognizes the receipt before entering its legacy donor/cache route. It
checks original and output hashes, then loads the prepared runtime directly, bypassing
`DeployPatchedMods` and vanilla restoration. An upstream original-DLL change requires a
matching new runtime; it fails closed with a logged explanation for now, without automatic
vanilla fallback.

**GUI release-bundle route:** when `delta-release.json` is present beside the installer,
the application opens `DeltaInstallWindow` directly, without constructing the legacy
prerequisite/source-acquisition services. It discovers a local client, offers source and
destination folder selection, requires acknowledgment, supports cancellation and retries,
and installs to a new directory. Install success requires a complete hashed payload
inventory and the staged launcher's `--validate-only` startup/JIT check. Validation uses
an isolated data folder inside staging. Payload hashes are checked again after copying,
before its launcher executes; failed validation prevents activation. The startup check
does not substitute for in-game rendering validation.

`scripts/stage-delta-release.py --installer <published-installer> --pack <delta-pack>
--payload <published-runtime-payload> --uninstaller <single-file-optimum-cli>
--output <new-release-directory>` assembles this
local layout and records payload hashes. It refuses existing destinations and known game
assembly/donor filenames in the payload. This is not a redistribution audit of every
dependency or asset. Source artifacts must be trusted; release signatures and downloads
remain open. Without the descriptor, developer builds retain the legacy wizard. The delta
GUI offers optional application-menu and desktop shortcuts. Presets, automatic updates,
Linux runtime validation and controller navigation remain open.

Earlier DLL-only measurements use the official Linux 1.22.7 archive,
patched anew with the rebuilt Cecil tool and existing local donor builds on Windows:

| DLL | Patched bytes | Delta bytes | Decode ms |
| --- | ---: | ---: | ---: |
| VintagestoryLib | 3,171,328 | 831,757 | 47.06 |
| VintagestoryAPI | 2,146,304 | 497,949 | 32.16 |
| VSEssentials | 1,213,952 | 358,892 | 30.97 |
| VSSurvivalMod | 3,101,184 | 894,499 | 53.20 |

All reconstructed DLLs match the fresh Cecil outputs exactly, both in the Python generator
and through the C# CLI. Total delta size is 2,583,097 bytes (2.46 MiB), down from 5.62 MiB
in the initial mixed-cache experiment. Decode timings include subprocess startup but not
SHA-256 verification. These are single-run feasibility measurements, not release sizing
or performance guarantees. Validation: 181 Core tests, 28 CLI tests, 93 installer tests,
60 launcher tests, and eight Python/xdelta integration tests pass. Game rendering and
PDBs remain unvalidated. Local inputs, tools, patches and reconstructed DLLs remain
under ignored `.tools/`.

**Clean Windows local integration (2026-09-26):** the official 1.22.7 Windows installer was
extracted locally. Fresh Cecil outputs were diffed against its exact DLLs and all 42
Optimum shader overrides (22 replace existing official shaders; 20 new shader outputs name
an explicit official shader as their delta source). The resulting 46-file VCDIFF pack is
2,637,264 bytes. The self-contained CLI with bundled xdelta applied it to a separate copy
of that clean client and ran the staged launcher `--validate-only` before activating the
runtime. The launcher JIT-validated 15,558 Lib, 8,550 API, 4,152 Essentials and 13,729
Survival methods; all four assemblies passed. Vanilla inputs stayed in place. This is a
startup/JIT check, not an in-game rendering or handheld test.

**Versioned download path (2026-09-27):** `package-delta-download.py` produces a
single archive of a staged pack, runtime payload, decoder, descriptor and removal helper.
The installer has an opt-in `--download-release` route: it reads the selected client's
version, looks for the exact game/Optimum/RID asset on the project's GitHub release,
requires the release API's SHA-256 digest, verifies cached or downloaded bytes, and
extracts only bounded, allowed paths before invoking the existing staged installer.
Five acquisition tests cover exact selection, damaged bytes/cache, missing digest and
unsafe archive paths; three archive-contract tests and all 103 installer tests pass. Both
platform archives were packaged from local proof bundles. A Windows end-to-end proof
used a controlled HTTP response
for the 1.22.7 archive, reconstructed and startup-validated the separate runtime,
damaged and repaired that runtime from the cached verified archive, retained the previous
copy, and confirmed the original DLL was unchanged. This does not establish a live published
release or a default player update flow.

**Installer startup route (2026-09-27):** a published installer containing the pinned
decoder bundle opens the release download UI by default. A local release descriptor
still takes priority; `--developer-build` keeps the source-build wizard available.
`release-installer.yml` now acquires the pinned decoder for each RID, includes it in
the self-contained publish, and rejects a release version that differs from `VERSION`.
The Windows publish contains the verified decoder descriptor, executable and license;
all 105 installer tests pass. The hosted release workflow has not run with this change.

**Pinned release build inputs (2026-09-27):** the platform bootstrap jobs now verify the
exact 1.22.7 client archive and acquire the pinned xdelta decoder. After each platform
package build, `build-delta-pack.py` runs the Cecil patcher against its four original
DLLs and staged donors, builds all 46 deltas, reconstructs every output, and makes a
verified pack available as a CI artifact. Both RIDs completed this pack/reconstruction
path locally from the official extracted clients. The shipped C# CLI also reconstructed
all 46 files from each fresh pack. The Linux local check ran on a
Windows host with the pinned Windows decoder executable; the Linux CI job is wired to
use the pinned Linux executable and repeat the C# consumer check. Hosted job completion and release publication remain
to be verified.

A self-contained installer publish, the 46-file pack and the local proof payload were
staged into a release-shaped bundle. `tools/delta-release-smoke` invoked the same
`DeltaReleaseService` that the GUI uses: it verified the payload inventory, built the
separate runtime, ran the staged launcher check and confirmed the official Lib DLL hash
was unchanged. Additional release-service smoke installs selected an existing custom data
folder and created a new one after staged validation; both recorded `datapath.cfg` in the
activated runtime while keeping the original client unchanged. Interactive Windows setup
UI and game rendering are still unverified.
The release service now writes the existing uninstall manifest into the staged runtime.
A clean-client smoke install followed by `optimum uninstall` removed only that separate
runtime; the official DLL hash and custom data folder were unchanged. Optional shortcut
paths are recorded in the install manifest, and the existing uninstaller removes them;
local Windows `.lnk` and Linux `.desktop`/icon cleanup are covered.

**Delta removal UI (2026-09-27):** the installer now offers a removal window from its
delta setup screen and through `--uninstall-runtime`. It requires an explicit target and
confirmation, checks the delta receipt and install manifest, and rejects overlapping
original/data directories, moved manifests and linked runtime paths. A fresh local Windows
release-service install was removed through the same service: the separate runtime was
gone, the official Lib DLL hash was unchanged, and external data remained. Headless UI and
service tests pass. Interactive player testing remains open.

**Durable Windows uninstall entry (2026-09-27):** Windows release staging now requires a
self-contained single-file CLI and records its SHA-256 in the release descriptor. The GUI
verifies it, stores a content-addressed copy under LocalAppData outside the runtime, and
registers a path-specific Windows Settings uninstall entry. The entry invokes the guarded
`uninstall-delta` command, which refuses ordinary folders. A fresh clean-client local proof
install recorded the registry entry; invoking its standalone executable removed the runtime
and registry entry while preserving the official Lib DLL hash and external data. The
untrimmed publish is about 38 MB. Interactive Windows Settings testing remains open; the
local proof bundle is not a player release.

**Linux uninstall entry (2026-09-27):** Linux release staging also requires a standalone
single-file CLI. The installer verifies it, stores a content-addressed executable outside
the runtime under XDG data, and records a per-install application-menu `.desktop` entry.
That entry opens a terminal and requires the exact `REMOVE` response before the guarded
`uninstall-delta` command runs. Synthetic install/removal tests confirm the menu entry is
removed with the runtime while the external tool remains. A fresh `linux-x64` local-proof
archive contains the ELF uninstaller with a matching descriptor SHA-256 and mode `0755`;
the installer, launcher and decoder modes were checked too. Actual Linux execution and
interactive desktop-menu behavior remain unverified on this Windows host.

`scripts/build-delta-runtime-payload.py` assembled the local Windows payload from a
self-contained launcher publish, compiled Optimum assemblies and SPIR-V. It labels the
result `LOCAL-PROOF-ONLY.txt`; `scripts/stage-delta-release.py` refuses that marker unless
explicitly given `--local-proof`. The native SPIR-V and optional vendor binaries still
need a redistribution audit before player publication.

**Linux local packaging and reconstruction (2026-09-26):** the official Linux 1.22.7
archive supplied the four original DLLs and shader sources. A fresh 46-file `linux-x64`
pack (four DLLs and 42 shader overrides, 2,637,504 delta bytes) reconstructed every output
through the C# CLI and passed size/SHA-256 verification. The Linux xdelta 3.2.0 download
matched its published SHA-256; a self-contained Linux launcher and installer published
with that bundled decoder. `build-delta-runtime-payload.py --rid linux-x64` assembled a
local proof payload with the Linux shaderc library, and `stage-delta-release.py
--linux-archive <path.tar.gz> --local-proof` produced a release-shaped archive. Its
installer, launcher and both decoder executables have mode `0755` in the archive,
including when staged on Windows. The archive is not an AppImage or a player release.
No Linux runtime execution, startup/JIT, in-game rendering, or interactive setup has been
validated yet; this development host has no Linux execution environment.

**Vulkan startup preflight (2026-09-26):** for an explicit `Renderer: vulkan` choice,
the launcher asks the renderer to create a headless Vulkan context before loading game
code. This reuses its actual Vulkan 1.3 device floor, including required features and
limits. A failure is logged and shown in a Windows dialog or a Linux `zenity`/`kdialog`
dialog when available. `--check-vulkan` runs the same check without a game client.
The self-contained Windows payload passed against the development GPU; a payload missing
the renderer DLL returned a repair message and nonzero exit. Headless validation and the
current OpenGL default skip this preflight. A window surface, swapchain and in-game
rendering remain unverified by it.

### Milestone 0 completion checklist

Checked items below are completed implementation slices with the validation recorded
above. **Milestone 0 remains in progress** until complete platform releases and in-game
validation are finished.

1. **Binary-delta release packs — core implementation done.**
   - [x] Choose binary deltas; retain decompilation, donor compilation and Cecil patching
     on the build machine.
   - [x] Generate VCDIFF packs from pristine originals to fresh Cecil outputs; verify
     byte-for-byte reconstruction of the four DLLs and all 42 shader overrides.
   - [x] Identify packs by game/Optimum version, RID and exact input SHA-256 hashes.
   - [x] Verify input/delta/output sizes and hashes, stage verified copies, reject
     mismatches and publish only complete results.
   - [x] Integrate xdelta through a C# adapter and bundle the executable, license and
     checksum descriptor in self-contained publishes; smoke-test the bundled CLI.
   - [ ] Finalize the symbol policy and review compiled native shader programs and other
     changed game-derived files for release packaging.
   - [x] Validate a clean Windows client copy through reconstruction and staged startup/JIT.
   - [x] Rebuild the full Linux pack from official 1.22.7 inputs and verify all 46 outputs.
   - [ ] Validate Windows in-game rendering and a complete Linux player install.
   - [ ] Replace the bundled decoder subprocess if the in-process-only requirement is retained.

2. **Installer without prerequisites — local release-bundle route done.**
   - [x] Discover common Windows/Linux client locations and inspect versions without
     executing game code; allow manual folder selection.
   - [x] Open the delta GUI directly when a local release descriptor is present, bypassing
     prerequisite scanning, source acquisition and `ScriptBuildDriver`.
   - [x] Provide acknowledgment, source/destination selection, cancellation and retry.
   - [x] Verify the local payload inventory, reconstruct patched copies, and require a
     staged startup/JIT check before activating the install.
   - [x] Install into a separate new runtime directory without changing vanilla;
     discard staging on cancellation or failed validation.
   - [x] Assemble a Linux local-proof bundle and archive with executable modes preserved.
   - [x] Discover custom Windows client paths from registry uninstall entries.
   - [x] Offer an existing or new custom data folder and record it in the separate runtime.
   - [x] Record a delta install manifest and verify CLI removal preserves vanilla and
     external user data.
   - [x] Provide a delta removal window and verify removal of a fresh local Windows copy
     preserves vanilla and external data.
   - [x] Offer menu and desktop shortcuts and record them for uninstall cleanup.
   - [x] Add desktop/handheld installer presets in separate new or empty data folders;
     the desktop seed selects Vulkan/TAA, and the handheld seed selects Vulkan/XeSS
     quality with a 0.75 render-scale fallback, a 60 FPS cap, greedy meshing and
     lighter effects.
     Particle, LOD, mip, cloud and view-distance starting values are seeded; device
     measurements and tuning remain in Milestone 1.
   - [x] Add a durable standalone Windows uninstall command and path-specific Settings
     registration for the delta route; verify a local proof install/removal round trip.
   - [x] Add a Linux application-menu uninstall entry backed by an external standalone
     command with explicit confirmation; verify archive mode and descriptor hash.
   - [ ] Validate Linux execution and interactive OS removal flows on a supported host.
   - [x] Package versioned download archives and add an opt-in installer route that
     selects an exact game/Optimum/RID release asset, verifies its published SHA-256,
     and uses the existing staged installation and repair paths; validate a local
     remote-flow proof for both operations.
   - [x] Make decoder-bundled installer publishes open the release download route by
     default, while preserving local staged releases and an explicit developer wizard.
   - [ ] Publish and validate matching release assets over the live feed, make the
     complete download install work on both platforms, and handle game-version updates.
   - [ ] Validate the complete Windows setup and Linux AppImage player experience,
     including touch/controller navigation. Developer builds without a release descriptor
     still use the legacy build wizard.

3. **Launcher as the front door — delta activation done.**
   - [x] Recognize prepared delta runtimes and check original/output hashes before loading
     game code; reject changed originals and corrupted outputs.
   - [x] Load the separate runtime directly, bypassing donors, `DeployPatchedMods` and
     vanilla restoration. Full in-game validation remains tracked above.
   - [ ] Fetch matching packs on game updates and otherwise offer vanilla with a clear message.
   - [x] Add a Vulkan 1.3 preflight for explicit Vulkan selection with a readable error.
   - [x] Offer to launch the recorded original game when delta verification fails;
     provide `--launch-original` for direct recovery without loading Optimum game code.
   - [x] Offer to start the original game after removing a separate Optimum copy.
   - [x] Repair an installed delta copy from the same local release with staged validation,
     rollback on activation failure, and a retained previous-copy backup; verify a
     damaged Windows local-proof runtime is rebuilt while the original stays unchanged.
   - [ ] Add launcher self-update through Velopack.
   - [ ] Complete automated repair/update acquisition and in-launcher removal flow.

4. **Trust — open.**
   - [ ] Code-sign installer and launcher, publish release checksums and authenticate downloads.
   - [ ] Confirm redistribution terms for every bundled dependency and SDK, including
     Streamline/DLSS, XeSS/XeLL, FidelityFX and SDL3.

5. **Release pipeline — local staging done; production CI open.**
   - [x] Stage a local release from published installer, delta pack and runtime payload;
     record payload hashes and reject known game-assembly/donor filenames and overwrites.
   - [x] Stage the launcher managed dependencies and root-level OpenAL/SDL3 native DLLs
     in the Windows source package; a freshly packaged opt-in SDL client passed package
     validation and reached the headless main menu.
   - [x] Add opt-in decoder bundling to installer, CLI and launcher publish outputs.
   - [x] Assemble a local Windows proof payload with the renderer and pass staged JIT validation.
   - [ ] Build and validate reviewable, redistributable Windows and Linux player payloads,
     including native bridges and in-game rendering.
   - [x] Pin client and decoder release inputs by SHA-256; verify both platform client
     archives and acquire both decoder bundles locally with offline tamper tests.
   - [x] Build and reconstruct all 46 outputs from fresh Cecil patching locally for
     both versioned pack IDs, including a C# consumer round trip; wire those checks
     and artifact upload into platform CI.
   - [ ] Run both hosted platform jobs successfully and verify their pack artifacts
     before using them in a player release.
   - [ ] Publish compatible signed setup/AppImage releases without game originals or donors,
     with compatibility notes and dependency notices.

6. **Retire script entry points for players — open.**
   - [ ] Make `install-windows.ps1`/`.cmd` and `install-linux.sh` developer-only or remove them.
   - [ ] Lead the README with the verified player installer once release delivery is ready.

## Milestone 1 — Claw baseline, handheld preset, and power

1. **Claw baseline:** capture native, XeSS SR, and XeSS SR + FG at 1920×1200 and at lower
   render scales, with GPU timestamps, PresentMon, and power/clock telemetry, in surface,
   forest, cave, village, and exploration scenes. Set targets, e.g. a stable 60 FPS and a
   40–60 FPS base for frame generation at the handheld's power limits. Revalidate XeSS FG
   there: on Intel the driver paces presentation (not the cross-vendor pacer measured so
   far) and XeLL is native.
2. **Greedy meshing on:** it is built but off by default (`OptimumConfig.GreedyMeshEnabled`).
   Enable it with a light tolerance of 1–2 (0 merges almost nothing under smooth lighting)
   and a far band. It cuts vertices in the main and shadow passes, and vertex fetch was the
   measured chunk bottleneck.
   - [x] Enable it in the handheld preset with light tolerance 1 and a 128-block far band.
   - [ ] Compare `GreedyMeshTextureGrad=false` on Arc and validate visual quality,
     1% lows and power on the Claw.
3. **Handheld shadow tier:** shadow maps are at least 4096² D32 at every quality
   (`VulkanClientPlatform.FrameBuffers.cs`), both cascades are redrawn every frame, and each
   lit pixel takes 18 comparison taps. Add a 2048/1024 tier with D16, and sample only the
   cascade that covers the pixel with 4 gathered taps.
   - [x] Add an opt-in 2048² near / 1024² far tier to the handheld preset, with D16 when
     the Vulkan device supports the required depth attachment and sampling features.
     Sample one cascade with four gathered comparisons per lit pixel. The native shader
     package and D16 GPU attachment/gather test pass.
   - [ ] Check cascade transitions, shadow quality, GPU time, memory use and power on the Claw.
4. **Frame pacing without busy-waiting:** the legacy `PreciseFramePacing` path ends every
   capped frame in a `Thread.Yield`/`SpinWait` tail (`ClientPlatformWindows.cs`) and keeps a
   process-wide `timeBeginPeriod(1)`. Replace the tail with a high-resolution waitable
   timer. Add power-aware caps (40/45/60, VRR), and prefer FIFO or a cap over uncapped
   MAILBOX on the handheld.
   - [x] Add an opt-in Windows high-resolution waitable-timer path for capped frames.
     The handheld preset enables it with a 60 FPS cap and FIFO VSync; the legacy
     pacing path remains available when the timer is unsupported. The advanced
     graphics slider can select 40 or 45 FPS for device trials.
   - [ ] Measure frame-time distribution, wakeups and power on the Claw, including
     40/45/60 FPS and VRR behavior; revalidate frame generation pacing.
5. **Idle thread wakeups:** 8 client threads poll with `Thread.Sleep(5)`
   (`ClientThread.cs`), and about 6 singleplayer server threads poll every 2 ms
   (`ServerThread.SleepMs`). That is thousands of wakeups per second even when idle. Use
   event-driven waits, or at least longer idle sleeps.
   - [x] Enable deadline-based sleeps in the handheld preset, capped at 25 ms. Client
     systems with 0/1/5 ms intervals retain short waits; slower systems wait toward their
     next tick instead of polling at a fixed 2–5 ms rate.
   - [ ] Measure actual wakeups, power and responsiveness on the Claw.
6. **No forced blocking memory cleanup on battery:** with `OptimizeRamMode==2` the client
   forces a blocking full GC with large-object-heap compaction every 602 s (30 s when
   unfocused, `SystemCompressChunks.cs`), a guaranteed periodic hitch.
   - [x] Set `AvoidForcedGc` in the handheld preset and skip that timer-triggered
     collection in the patched client.
   - [ ] Measure in-game memory use and hitch behavior.
7. **Handheld preset:** XeSS SR on by default (render scale 0.67–0.77 otherwise), async
   particle caps 8–16k instead of 80k, `particleLevel` below 100, lower LOD bias, texture
   mip cap 4–5 instead of 3, flat clouds, shorter god-ray sampling, and a view distance
   tuned on the Claw.
   - [x] Seed 16k async particle caps, particle level 60, LOD biases 0.25/0.55,
     mip level 4, flat clouds and 160-block view distance in the separate data folder.
     XeSS quality, 0.75 render-scale fallback and god-ray sample cap are already seeded.
   - [ ] Measure and tune these values on the Claw, including frame-time and power impact.
8. **Unified-memory checks:** confirm the discrete-GPU VRAM placement does no harm on
   shared memory (all heaps are device-local there), and track memory footprint and
   battery life per preset.
   - [x] Include Vulkan heap flags beside per-heap allocated/budget bytes and, when
     available, driver-reported heap usage in `stats.memory`; allocator tests verify
     the reported flags against the selected physical device.
   - [ ] Capture those counters and battery/power telemetry for each preset on the Claw;
     compare mapped-mesh placement, frame times and memory pressure.

## Next (in order, after milestones 0 and 1)

### 2. Retire the OpenGL renderer

Cleanup that also narrows the SDL3 work to a Vulkan-only window.

- Make Vulkan the only renderer: `OptimumConfig.Renderer` still defaults to `"opengl"`.
  Remove the renderer choice and its migration paths.
- Replace the silent fall back to OpenGL with a clear startup error that names the missing
  Vulkan 1.3 feature, extension, or driver.
- Delete the OpenGL route: legacy post passes, GL state shims kept only for parity, and
  the GL-vs-native differential tests. Keep the GLSL rewriter; it still translates game and
  mod shaders to Vulkan.
- Drop OpenGL-only Cecil transplants and patches once nothing calls them, and give mods a
  documented Vulkan path instead of raw GL.
- Keep the DX12 interop. XeSS FG exists only as a D3D12 swap-chain proxy
  (`xefg_swapchain_d3d12.h`) with D3D12-only XeLL, and FSR 4 runs through the same
  Vulkan↔DX12 shared-image and shared-fence interop (`IDx12SharedRuntime`). It stays
  optional, falls back cleanly, and never stops Vulkan from starting. XeSS SR stays native
  Vulkan (`xess_vk.h`).

### 3. SDL3 platform layer and controller play

**Current Windows desktop SDL3 milestone (controller work on hold):**

- [x] Build and stage a Windows package with SDL3 as the default Vulkan window.
  The isolated packaged client reached the login screen, and the launcher
  JIT-validated all four patched assemblies.
- [x] Resolve Streamline and optional native graphics runtimes from the executable
  directory when the renderer assembly lives in the launcher's patch cache.
  Packaging now rejects a stale Streamline bridge missing required PCL/Reflex exports.
  In the packaged SDL client, 8,495 On, 153 Off and 264 Boost menu frames each had
  one successful Reflex sleep and six ordered, successful PCL phase markers on one
  frame token. The SDL Windows ping hook delivered successful live PCL ping markers;
  `OPTIMUM_STREAMLINE=0` kept the Vulkan window running through 646 fallback frames.
- [x] Run this package in a visible Windows world. The SDL Vulkan client stayed
  responsive through native resize, maximize, minimize/restore and F11 fullscreen
  in both directions. A focused Windows-injected F12 key event saved a nonempty gameplay
  screenshot with terrain and HUD. With DLSS-G enabled on the focused window,
  the SDK reported 239 actual presents for 120 rendered frames and resumed 2×
  output after resize, fullscreen and minimize/restore.
- [ ] Resolve the Windows SDL presentation acceptance warning before declaring
  presentation complete. The latest finished Release build and packaged launcher
  startup/JIT check passed, but both resize/present cases in the full suite still
  report `SYNC-HAZARD-PRESENT-AFTER-WRITE` (batch 5 evidence in
  `local-builds/desktop-20260927-validation-batch5/evidence/`). Widening the
  render-frame wait did not help and was reverted; changing the synchronization-2
  present release to no destination stage also did not clear the warning. A focused
  batch 6 run confirmed that RTSS and every other implicit Vulkan layer were disabled,
  yet both cases still failed. The reported prior command is `vkCmdEndRenderPass`;
  its origin is unresolved. Submit B now uses `vkQueueSubmit2` with an explicit
  `ALL_COMMANDS` signal scope for the binary present semaphore so its synchronization-2
  layout release is included. That source change has not been validated. Earlier
  isolated Streamline, SDL PCL ping and FSR3 probes passed.
- [ ] Validate live clipboard and file drop, cursor behavior, and physical
  keyboard/mouse controls across QWERTY and QWERTZ layouts. Test a high-DPI
  display move when a second display is available. A focused visible SDL window
  on German QWERTZ delivered a physical key event followed by a single `é`
  text commit from the acute-accent dead key and E; broader hotkeys and mouse
  behavior still need a live pass. The IME target now follows the game's dialog
  keyboard dispatch order, and queued preedit/commits require the same focused
  field that started SDL text input; the full batch 5 test run passed the SDL
  routing tests, but this field-switch behavior needs a live IME pass. At 150%
  Windows display scale, a focused SDL window reported 640×240 window and pixel
  dimensions and delivered a warped
  mouse-motion event at the requested 200,100 client position.
- [ ] Validate non-Latin IME preedit/candidates and committed text in login, chat,
  signs and a modded field with the native Windows candidate UI. A Windows
  direct Unicode input probe reached the in-game chat field without exercising
  IME composition; its Japanese characters rendered as missing-glyph boxes under
  the current English font. Loading a Japanese keyboard layout in a focused SDL
  probe still produced direct Latin commits from synthetic romaji keys, so it did
  not verify preedit or candidate placement.
- [ ] Validate PCL/Reflex in visible gameplay with NVIDIA's verification HUD and
  an actual latency report. The focused-window DLSS-G SDK present count and
  live PCL marker traces pass; displayed generated-frame quality still needs
  direct monitor or capture-tool inspection.

Required for handheld play: the baseline game had no controller support. SDL3
replaces OpenTK/GLFW windowing and all input, keyboard and mouse included. SDL only
delivers keyboard and mouse events for windows it owns, so it takes over the window and the
event loop. Controller play is modelled on Minecraft Java's controller mods (Controlify,
Controllable). OpenTK remains only for OpenAL audio.

- **Scope today:** window and input use sits in `ClientPlatformWindows` (83 window
  references, 48 of them `ClientSize`), `GameWindowNative`, and `ClientProgram`'s
  `GameWindow.Run` loop. Every key reaches the game through one translation,
  `KeyConverter.NewKeysToGlKeys`, and the game, API, and mods only see `GlKeys`,
  `KeyEvent`, and `MouseEvent`. A single SDL scancode table at that seam keeps game and mod
  code unchanged.
- **Window and loop:** an SDL3 window and our own frame loop replace `GameWindow.Run`.
  Vulkan gets its surface from SDL. DLSS-G, FSR 3, and the XeSS-FG DXGI proxy get the HWND
  from SDL's window properties. While XeSS FG is active the proxy is the only swap chain on
  that window, so SDL must not claim one, and resize and fullscreen changes must reach the
  proxy. Display modes, DPI, window state, icon, cursors, clipboard, and file drop move to
  SDL.
  - [x] Add an SDL-owned Vulkan window/surface host and let the renderer request instance
    extensions, surface creation, and the Win32 handle from either SDL or the existing GLFW
    window. Hidden-window probes exercise native SDL3 surface creation and renderer
    swapchain initialization through that host.
  - [x] Let the Vulkan client platform initialize against the SDL host through its normal
    graphics setup and teardown; a hidden-window probe confirms the platform owns an SDL
    swapchain and records the window ID for input routing. It now sizes the swapchain from
    SDL's drawable pixel size rather than the requested logical window size.
  - [x] Add SDL host operations for logical/pixel size, minimum size, title, visibility,
    fullscreen, relative mouse, text input area, and clipboard text. Hidden-window tests
    exercise size changes, text-input activation, and relative mode; live window-state,
    clipboard, and high-DPI handheld behavior still need validation.
  - [x] Route platform focus, mouse grab, and controller menu-cursor bounds/position through
    the SDL host when it owns the window. A hidden SDL platform probe covers those values
    and cursor movement.
  - [x] Install the existing game icon on the SDL-owned window; a native hidden-window
    probe covers icon and color-cursor installation.
  - [x] Convert SDL logical mouse coordinates and controller cursor warps to the drawable
    pixel space used by the game UI. Controller target snapping and same-frame clicks now
    share that space; a virtual mouse-position seam keeps the game's controller/physical
    button arbitration. Synthetic high-DPI coordinate tests pass, while actual handheld
    scaling and sensitivity still need device validation.
  - [x] Coalesce pixel-size events from one SDL input pump into a single Vulkan resize
    before simulation. Save windowed dimensions in SDL logical units for the next launch,
    and request GUI recomposition when the window changes display or display scale.
    Dock/undock and live-resize behavior still need validation on a handheld.
  - [x] Expose the existing client frame body to an SDL-owned loop, skipping GLFW cursor
    recentering and window reads there. The SDL loop uses the game's close-cancellation
    callback; a hidden-window probe runs three manual frames and exits the loop from a
    native SDL close event after one more frame.
  - [x] Add an opt-in `OPTIMUM_SDL_WINDOW=1` client startup path: SDL owns the Vulkan
    surface and frame loop while a hidden `GameWindowNative` keeps older game APIs alive.
    A packaged Windows headless run reached the main menu, loaded the complete shader
    set, and remained running. The loading screen uses its minimal GUI shader until the
    full GUI shader is compiled.
  - [x] Add the initial experimental `OPTIMUM_SDL_NO_GLFW=1` mode within the SDL path. It skipped
    `GameWindowNative` creation, routes startup framebuffer sizing and custom game cursors
    through SDL, and reached shader initialization and the login stage in a packaged
    Windows headless run without a GLFW window. A visible packaged run reached the main
    screen and exited through its Quit action. Hidden-window cursor creation has a
    focused test. The separate flag has since been retired; a later no-GLFW headless
    run entered a local world and captured rendered gameplay frames. Handheld
    visual and input behavior still needs validation.
  - [x] Make the no-GLFW route the default whenever SDL selects Vulkan.
    `OPTIMUM_SDL_COMPAT_GLFW=1` explicitly retains the hidden compatibility window.
  - [x] Route window border and state operations through SDL in the experimental path:
    resizable, fixed, borderless, minimized, maximized, fullscreen, restore, and the
    fullscreen minimize-on-focus-loss setting. Hidden-window tests cover border state;
    visible resize, fullscreen and minimize/restore passed on Windows. Handheld
    display behavior still needs validation.
  - [x] Decode SDL file-drop events, copy UTF-8 paths while SDL owns the event memory,
    and send drops on the active window to the game's existing file handler. A focused
    routing test covers non-ASCII filenames and window-ID filtering. App-level drops
    without a window ID now route only while the SDL window has focus; live drag-and-drop
    remains unverified.
  - [x] Route the platform interface's clipboard and focus requests through the SDL
    window, and report its SDL display size. The no-GLFW platform constructor skips its
    GLFW monitor query; the exact-version patcher transplanted that constructor and a
    fresh packaged headless run reached login without a crash. A hidden-window test
    verifies adapter installation and teardown. Live clipboard and focus behavior still
    need validation.
  - [x] Avoid constructing OpenTK `NativeWindowSettings` on the no-GLFW SDL path;
    its constructor initialized GLFW even when no GLFW window was created. Create it
    only on the GLFW route or an OpenGL fallback. A packaged Vulkan/SDL headless run
    reached login with `SDL3.dll` loaded and no `glfw3.dll` module in the process.
  - [x] Size screenshot capture and optional screenshot scaling through the platform's
    client-size seam, so the SDL-owned window does not require a GLFW `GameWindow`.
    Headless world-frame capture uses the same seam. A focused Windows F12 event
    saved an intact visible SDL gameplay screenshot with terrain and HUD.
  - [x] Use the SDL client-size seam when loading and unloading shared framebuffers.
    This removed a null GLFW-window dereference in the terrain render path. An isolated
    Windows SDL/Vulkan run without a GLFW window loaded a local world, captured two
    nonempty 1280×850 gameplay frames, and exited cleanly. A subsequent visible
    SDL/no-GLFW desktop run entered the same world and exited cleanly through the
    window-close path. The headless PPM writer stores rows bottom-up by design;
    visible handheld presentation and controls still need validation.
  - [x] Route the game's focus-window request through SDL restore/raise when the SDL
    host owns the window, including mod-install links received while minimized.
  - [x] Select the SDL-owned window and frame loop by default for Vulkan, without a
    hidden GLFW window. `OPTIMUM_SDL_WINDOW=0` keeps the GLFW Vulkan path available.
    With the SDL flag unset, an isolated Windows client loaded a local world,
    captured a nonempty gameplay frame and exited cleanly; the exact-version
    patcher applied all 251 required methods.
  - [x] Coalesce logical resize, maximize/restore, display/scale, safe-area and
    fullscreen events with pixel-size changes before resizing Vulkan and
    recomposing the GUI. Persist windowed dimensions in logical units even
    when the drawable pixel size stays the same. Display-level orientation,
    mode, content-scale and usable-bounds events reach the same path for the
    active display; add/remove events also trigger a layout refresh. Hidden
    SDL resize and display-event probes pass.
  - [x] Exercise a native SDL resize → fullscreen → restore sequence through
    Vulkan presentation. After each next-frame swapchain acquire, the drawable
    size and swapchain extent match; the client still exits on SDL close.
  - [ ] Validate high-DPI display moves, cursor behavior and handheld dock/undock
    with the default SDL route. Visible world play, native resize, fullscreen
    and minimize/restore passed in the current Windows package.
- **Keyboard, mouse, text:** relative mouse mode on raw input replaces the per-frame
  cursor recentring in `UpdateMousePosition`. SDL text input and IME cover chat, signs, and
  text fields, including composition for non-Latin languages.
  - [x] Add a physical SDL-scancode-to-`GlKeys` map and one SDL event-queue reader that
    distinguishes key, UTF-8 text, mouse, window, and gamepad events. A native queue
    probe verifies window events and gamepad hot-plug reach their separate routes.
  - [x] Add SDL physical-key and UTF-8 text injection at the client input seam, retaining
    controller/keyboard hold arbitration. The donor builds and Cecil injects both entry
    points; key, modifier, text, and both release orders have focused tests.
  - [x] Add SDL mouse motion, button, wheel, and focus-loss injection at the same seam.
    SDL button order, flipped wheel direction, window-ID filtering, and controller/mouse
    hold arbitration have focused tests. The frame input hook conditionally connects the
    shared SDL queue to these dispatchers for an assigned SDL window ID; a native SDL
    queue probe reaches the game's physical event handlers. Cecil injects all six
    physical-input entry points.
  - [x] Start SDL Unicode text input only while an editable GUI field in the active
    screen or open in-game dialog has focus. Stop it on focus loss or teardown, and place
    the native input-method suggestion area at the focused field and its rendered caret,
    accounting for horizontal text scroll and high-DPI conversion. Hidden-window tests
    verify SDL text-input activation, and a coordinate test covers cursor placement;
    German dead-key text committed as `é` through a focused visible Windows SDL
    window. Non-Latin composition and handheld keyboards still need validation.
  - [x] Use SDL's native OS IME UI for preedit and candidate display, decode
    `SDL_TEXT_EDITING` separately from committed `SDL_TEXT_INPUT`, and clear an
    unfinished composition on Escape, when focus moves to another field, the
    window loses focus, or graphics shut down. Focused decode and hidden-window
    native-composition probes passed before the current target-routing change;
    the new guard awaits validation.
  - [ ] Validate non-Latin IME composition and candidate placement in chat,
    signs, login and modded fields on desktop and handheld keyboards.
  - [x] Decode SDL finger events and filter its synthetic touch-mouse events. A
    single-finger gesture path maps taps to one left click, drags to a held left
    button after movement slop, and stationary long-press to a right click;
    cancellation and a second finger release the hold without a stray click.
    Touch, physical mouse and controller button holds remain independent. Keep
    pumping SDL window events if the optional gamepad subsystem is unavailable.
    Focused gesture, event-layout, arbitration and native queue tests pass.
  - [ ] Validate touchscreen tap, drag, long-press, text-field focus and
    physical/controller switching on handheld displays, including high DPI.
- **Latency:** the SDL-owned path now orders the vendor sleep, `SDL_PumpEvents`, input
  sampling, event dispatch, and simulation. Event timestamps feed input-age diagnostics;
  the Windows message hook remains for PCL reporting.
  - [x] Stamp `SimulationStart` after vendor sleep and before SDL input collection,
    then `InputSample` after `SDL_PumpEvents` and the explicit gamepad state refresh,
    before SDL dispatches events or reads controller state. XeLL requires simulation start as its first marker
    after sleep. A native event-queue probe verifies the pump callback precedes
    dispatch; the old GLFW path retains its physical-input routing.
  - [x] Measure SDL keyboard, text, mouse, and touch event age at dispatch on SDL's nanosecond
    clock, and report bounded mean/p99/max samples in `stats.input_age`. Invalid or
    future timestamps are ignored; focused tests cover bounded samples and formatting.
  - [x] Load PCL marker/state/options independently of Reflex and DLSS-G, obtain
    `statsWindowMessage`, and handle its SDL Windows message with
    `ePCLatencyPing` on the current frame token. The hook is removed with the
    SDL window. A posted-message probe verifies callback delivery; an isolated
    hidden SDL/Streamline run with the signed runtime delivered one ping marker
    on a live frame token. The managed renderer and native bridge build, and
    Windows packaging can rebuild the bridge from `-StreamlineSdkRoot`.
  - [x] Bind Streamline Reflex independently of DLSS-G, query `ReflexState`, and
    call Reflex sleep once per frame even in Off mode and when XeLL owns the
    active low-latency mode. Start AMD Anti-Lag's keyed INPUT stage directly
    before SDL polling, pair it once with the same frame's PRESENT stage, and
    clear skipped frames. On the XeSS-FG present thread, keep that frame's
    Streamline token for PCL markers around the actual DXGI Present. The XeSS-FG
    handoff aligns its present ID with XeLL's current sleep and marker frame key. Menu frames
    now also emit the full simulation/render/present marker sequence. A signed
    Streamline SDL world run captured gameplay in Off, On and Boost; the On trace
    had 2,922 presented frames with one successful sleep and ordered successful
    PCL markers each. AMD frame-key tests pass. Intel XeLL and AMD Anti-Lag
    physical-device timing still need validation.
    Streamline SDK 2.14.1 deprecates PCL `eInputSample`; XeLL and the native
    Vulkan latency path still receive the SDL input stamp.
  - [x] Keep Streamline PCL available on non-NVIDIA adapters without loading
    NVIDIA-only Reflex or DLSS-G plugins, whose Vulkan extension requests
    prevented device creation on the Intel GPU. The signed hidden-window probe
    now passes three frames on Intel UHD Graphics 770 with 18 successful PCL
    phase markers; the NVIDIA probe passes with PCL and three successful Reflex
    sleeps. Explicit Vulkan device pins select PCL-only by default, with
    `OPTIMUM_STREAMLINE_REFLEX=1` and `OPTIMUM_STREAMLINE_DLSS_G=1` available
    for a deliberately pinned NVIDIA adapter. XeLL on an Arc device and AMD
    Anti-Lag on an AMD device remain to be timed in gameplay.
  - [ ] Verify PCL ping markers, frame-token timing, Reflex modes and runtime
    fallback in visible gameplay with the Streamline verification tools.
- **Controllers:** SDL3's gamepad, sensor, and haptic subsystems through a C# binding such
  as `ppy.SDL3-CS`. Hot-plug, the community mapping database, the Claw's built-in
  controller, Xbox, DualShock 4/DualSense, Switch Pro, and Steam Deck controllers,
  coexisting with Steam Input. Controller actions map onto the hotkey system, so vanilla
  and mod hotkeys stay bindable. Analog movement and look with deadzones and response
  curves, optional gyro aiming, radial menus, and sneak/sprint toggles.
  - [x] First handheld input slice: initialize SDL3 gamepads alongside the current window,
    hot-plug the first controller, release held inputs on disconnect/focus loss, route
    movement and face buttons through the existing hotkey handlers, use the right stick
    for look or a menu cursor, and map triggers and shoulders to mouse actions and hotbar
    scrolling. Bundle the SDL3 native library with Windows/Linux source packages and
    delta payloads; package and installer checks require it. A virtual SDL gamepad
    probe covers connection, button/axis updates, and disconnection.
  - [x] Bundle a pinned SDL_GameControllerDB mapping set in Windows, Linux and
    delta packages. Load it before the first gamepad scan, then load an optional
    `ModConfig/gamecontrollerdb.txt` so device-specific fixes override the bundle.
    SDL3 accepted 576 Windows mappings; the virtual-controller probe and a
    default-SDL local-world run with the staged database passed.
  - [x] Write an editable per-device SDL3 profile on first connection with separate
    movement/look deadzones, stick response, look/cursor sensitivity, trigger threshold,
    inversion, and button bindings. Controller serials are hashed in profile keys.
  - [x] Hand control between connected pads when another pad presses a button or
    moves an axis past a deliberate-activity threshold; ignore idle stick noise
    and activity while the game is unfocused. Release held inputs, load the new
    device profile and refresh glyphs when SDL remaps a device. Unsaved profile
    edits stay associated with their original device through a failed write.
    Two native SDL virtual pads exercise switching and profile recovery.
  - [x] Add opt-in SDL3 gyro aiming with profile sensitivity/deadzone/inversion and an
    optional secondary-trigger hold; switch the sensor off when aim is inactive.
    The virtual gamepad probe covers sensor enablement and sampled rates.
  - [x] Add controller-specific toggle sneak, clearing the latch on menu/focus/device
    transitions, and expose the game's existing toggle-sprint setting in the controller panel.
  - [x] Carry a bounded left-stick speed factor through client prediction and a
    player-entity packet to the bundled server. The server accepts it
    only for that client's own player and never raises speed above the player's
    configured base; physical keyboard movement stays at full speed. The patched
    client/server methods and focused factor-codec tests pass.
  - [x] Preserve continuous on-foot stick direction on compatible servers by
    sending signed X/Y axes with the speed byte and applying them after the shared
    client/server movement-vector calculation. The original key flags still drive
    actions and the fallback path; stale axes expire, and keyboard movement clears
    the analog override. Negotiated analog play starts walking just beyond the
    configured stick deadzone; digital fallback keeps its separate press/release
    thresholds. The API and game DLL patchers succeed and a focused vector
    test covers the analog angle and digital fallback.
  - [x] Negotiate analog support with a versioned player-entity probe and ack.
    The client sends continuous input only after a matching server response;
    older servers stay on digital movement. New world sessions clear the capability.
    The exact-version game DLL patcher transplants both packet handlers.
  - [ ] Validate analog movement and fallback against physical handhelds and
    compatible/unpatched multiplayer servers; tune gyro and device switching.
- **Menus without a mouse:** a virtual cursor that snaps to slots and widgets, D-pad focus
  movement, inventory and crafting slot actions (pick up, split, move stack), and an
  on-screen keyboard.
  - [x] D-pad arrows navigate a focused inventory grid; elsewhere they snap the virtual
    cursor to the next active widget or slot, with a discrete-step fallback. South activates
    a focused slot or clicks under the cursor.
  - [x] Route focused-slot clicks through the normal mouse path (South/left trigger for
    left click, right trigger for right click), with stick-click Shift/Ctrl modifiers.
  - [x] Add an in-game controller keyboard for a focused editable field. LS+RS toggles
    it; the existing D-pad/virtual cursor selects letters, digits, Shift, Space,
    Backspace and Enter. Text goes through the field's normal key handlers, and
    closing or changing worlds unregisters the temporary dialog. The renderer builds.
  - [x] Reuse that key grid on main-menu and login text fields through a dedicated
    overlay screen. It preserves the underlying menu screen, routes controller
    cursor input only to the keyboard, keeps the original field as SDL's IME target,
    and forwards Enter to the underlying screen before restoring it on close.
    The renderer builds; menu typing still needs runtime validation.
  - [ ] Verify pickup/split/move-stack, crafting and keyboard text behavior in-game
    and on the login/main-menu screens; check built-in and modded GUIs on a handheld
    display.
- **Feedback and settings:** controller-specific button glyphs in hints and keybinding
  screens, rumble and haptic trigger effects, per-controller profiles, a remapping and
  sensitivity UI, and seamless switching between controller and keyboard/mouse.
  - [x] Add optional per-controller whole-pad and trigger-rumble pulses on controller
    interactions, with strength and duration settings. A virtual SDL3 gamepad exercises
    both callbacks; physical feel and device support remain to be checked.
  - [x] Add a distinct damage pulse from the local player's existing `onHurt`
    event. The listener follows player/world changes, queues the pulse onto the SDL
    polling thread, and obeys the active controller's rumble setting and strength.
    A focused test covers listener replacement and stale-pulse clearing; physical
    intensity and event timing remain to be checked.
  - [x] Add an in-game controller panel (Back/View) for deadzones, look/cursor and gyro
    sensitivity, inversion, and gyro/rumble toggles; slider arrows work from the D-pad.
    Changes apply to the active profile and persist to `ModConfig/optimum-controllers.json`.
  - [x] Add Actions/More pages for the ten controller button actions. Capture waits for the
    opener to be released, suppresses normal actions, and supports Start+Back cancellation;
    occupied buttons swap actions, and new bindings apply immediately to the active profile.
    Face-button labels follow SDL's reported layout, and More can swap trigger roles.
  - [x] Add an Axes page that assigns any of SDL's six standard axes to movement,
    look, and left/right mouse triggers, with positive/negative direction per role.
    Per-device selections apply immediately and persist; profile validation and
    negative-half trigger behavior have focused tests. Physical-device trials remain.
  - [x] Keep physical and SDL3 controller key/mouse holds independent at the shared
    platform event seam, including focus loss and multiple controller actions mapped
    to one key. Both release orders are covered by input arbitration tests.
  - [x] Publish device-layout controller glyphs from the active profile, including
    remapped face buttons and trigger axes. World-interaction hotkey hints draw
    circular face glyphs or compact button capsules, and the controller binding
    panel shows the same shapes beside each action. The SDL prompts switch back to
    keyboard/mouse on physical input, without treating controller cursor warps as
    physical motion; disconnect clears the snapshot. Focused layout, source-switch
    and snapshot tests and the exact-version patcher pass; visible glyph rendering
    remains to be checked on a handheld display.
  - [x] Show the active controller's compact badge beside matching actions in the
    game's keyboard and mouse binding lists. The keyboard mapping remains the
    editable value, so conflict detection and rebinding keep their existing behavior.
    The exact-version game patcher transplants both list builders, and a focused
    test covers badge display and keyboard fallback.
  - [x] Refresh binding badges when the input source or active profile changes while
    the controls page is open. A weak change listener updates only a composed list
    and leaves an active key capture alone; focused listener tests and the
    exact-version constructor/list-builder transplants pass.
  - [ ] Extend glyphs to remaining game/mod hints,
    add additional game-action haptics, then validate
    axis mappings and physical-device switching across the handheld/controller matrix.
- **Compatibility and validation:** mods that reach into OpenTK windowing or GLFW directly
  need a list and shims where practical. Key-by-key parity for layouts (QWERTY, AZERTY,
  QWERTZ, dead keys), alt-tab, minimize/restore, multi-monitor, and a controller device
  matrix. SDL3 is zlib-licensed.
  - [ ] Run the default SDL Vulkan client on Linux under X11 and Wayland, including
    surface creation, resize/fullscreen, clipboard, IME and gamepad hot-plug.
  - [ ] Audit built-in and mod window APIs for direct OpenTK/GLFW access, add
    practical SDL shims, and exercise the layout, alt-tab and controller matrix.

### 3a. Positional audio and VR (after the handheld SDL3 path)

The client already supplies world positions to OpenAL sources, updates a listener from the
player camera, and offers an HRTF option. Keep those behaviors while the window/input stack
moves to SDL; spatial-audio work should improve direction, distance, occlusion, and device
behavior rather than replace the existing audio mixer by default.

- [ ] Validate positional cues on handheld speakers and headphones: front/back and elevation,
  near/far attenuation, moving emitters, stereo versus mono source behavior, caves and
  reverb, and the existing HRTF toggle. Include output-device changes, unplug/reconnect,
  latency, CPU use, and battery draw in the device matrix.
- [ ] Add configurable spatial-audio quality and accessibility controls where those trials
  show a need: HRTF profile/fallback, headphone versus speaker behavior, and readable cues
  when a sound's position is gameplay-relevant. Keep music and interface sounds unspatialized.
- [ ] Add an OpenXR capability and runtime path for VR, with per-eye Vulkan rendering,
  predicted head/hand poses, input actions, stereo UI and interaction, comfort options,
  and frame pacing measured against the headset's refresh target. Keep the desktop and
  handheld paths working when no headset or runtime is present.
- [ ] In VR, drive the OpenAL listener from the tracked head pose at the same predicted
  display time as the rendered eyes; verify HRTF direction and world-locked audio while
  turning, leaning, teleporting, and recentering. Check audio/visual latency and avoid
  applying camera-only head movement twice.

### 4. Culling rework

Visibility is the classic voxel-engine bottleneck. After the optimization pass, chunk
passes were still about 2.9 ms of a 4.5 ms native 1080p GPU frame, shadows included. On
Minecraft Java, Sodium plus culling add-ons are credited with large FPS gains; the goal is
the equivalent here, proven by measurement.

- **Today:** VS's ray-based `ChunkCuller` plus Optimum's Sodium-style BFS visibility walk
  (face connectivity through `ClientChunk.IsTraversable`), on by default, at whole
  32³-chunk granularity. CPU frustum tests per mesh-pool part (`MeshDataPoolManager`)
  before the multi-draw. Hidden faces between solid neighbours are already removed at
  meshing time (`SideOpaque`). Back faces are only culled by the rasterizer in the opaque,
  topsoil, and decorative passes, after every vertex was fetched and shaded, and all four
  shadow passes draw with face culling off.
- **Measure first:** per-pass counters for loaded, frustum-visible, BFS-visible, and drawn
  chunks and triangles, plus a sampled estimate of how much drawn geometry is occluded, in
  surface, forest, cave, and city scenes. Rank the items below by that data.
- **16³ culling sections:** a VS chunk is 32³ blocks, 8× the volume of Minecraft/Sodium's
  16³ section, so every frustum, BFS, and occlusion decision is coarse. Keep 32³ chunks for
  storage and meshing, but split each chunk's index ranges and connectivity graph into
  eight 16³ sections at tessellation time and cull per section. Multi-draw indirect keeps
  the extra ranges cheap. Check whether the ray culler still earns its cost next to BFS.
- **Per-facing geometry:** tessellate quads into six facing groups (plus unaligned) and draw
  only the groups that can face the camera (Sodium's block-face culling), removing back
  faces before the vertex shader. The same test against the light direction applies to
  shadows.
- **GPU-driven culling:** frustum and occlusion tests in a compute pass that writes the
  existing indirect draws (reprojected depth-pyramid occlusion), with separate tests for
  shadow cascades.
- **Entities and block entities:** entities are culled by dimension, distance with
  hysteresis, a frustum sphere, and their chunk's visibility (`SystemRenderEntities`), so
  anything inside a visible 32³ chunk is animated and drawn even when hidden. Add
  per-entity occlusion (asynchronous ray-casts against block opacity, like Minecraft's
  Entity Culling, or the depth pyramid) with hysteresis. Culled entities skip animation,
  draws, and name tags; shadows get their own light-view test. Put block-entity renderers
  behind the same gate, and add distance/size culling for small decorative geometry.
- **Guardrails:** no popping or see-through holes (the MC-70850 class of bug that BFS
  connectivity guards against), culling state consistent for frame generation and TAA
  history, and every step proven with GPU timestamps and the counters above.

### 5. CPU efficiency and streaming

On the development system the main thread already spends about as long per frame as the GPU
(simulation ~1.6 ms plus render recording/submit ~2.8 ms against a 4.5 ms GPU frame). On a
handheld, CPU time also takes power from the GPU.

- **Entity animation LOD:** block animators have distance LOD (`AnimBlockLod`), but
  entities recompute every joint every frame (`AnimatorBase.OnFrame` →
  `ClientAnimator.calculateMatrices`), and the head controller runs even for culled
  entities. Reuse the block LOD tiers (full rate up close, every 2nd–3rd frame at mid range,
  ~10 Hz far) while keeping attachment points, collision boxes, and animation sounds
  correct. High impact in villages and herds.
- **Particles:** the main particle pools simulate and collide on the main thread
  (`ParticlePoolQuads.OnNewFrame`), and the async pools still extrapolate every particle on
  the CPU each frame. Move main-pool simulation to the async thread, extrapolate in the
  vertex shader, and gate spawns by a shorter distance.
- **Render-thread overhead:** several arrays and descriptor keys are allocated per draw
  (`VulkanDevice.Binding.cs`), sets that name a ring offset are rebuilt every frame, and
  passes copy their read lists with `ToArray()`. Use reused scratch buffers and struct keys,
  cache descriptor state, and let GPU culling generate draws.
- **Chunk upload budget:** the main-thread upload drain is limited by vertex count, not time
  (`ChunkTesselatorManager.OnBeforeFrame`). Give it a time budget to remove exploration
  spikes.
- **Singleplayer loopback:** chunk and map-chunk packets have no direct handler, so they go
  through protobuf serialize → clone → parse plus a ZSTD round trip even in-process. Hand the
  objects to the network-processing thread directly, then share uncompressed chunk data.
- **Hybrid cores:** no thread has an affinity or QoS hint except the main thread
  (`AboveNormal`). Keep the render, tessellation, culling, and XeSS present threads on
  P-cores, and move chunk compression, pipeline prewarm, sound loading, and cache
  persistence to E-cores (EcoQoS). On 8 logical CPUs the worldgen policy adds no extra
  workers, and the singleplayer server runs `BelowNormal`; tune both on the Claw.
- **Far entities:** distance-gate the per-entity interpolation renderers and client
  `Entity.OnGameTick` for far, unrendered entities. Enable the existing
  `DistanceSendFrequency` and far-entity tick stride, and consider strided server AI beyond
  ~48 blocks.
- **Streaming (C2ME-style):** worldgen is already multithreaded (from Stratum), chunk
  deserialization is parallel, and frustum culling uses SIMD. Measure a fly/teleport
  benchmark (chunks generated, lit, meshed, and uploaded per second, plus hitch counts) on
  the Claw, then parallelize or vectorize what it points to: noise with `Vector256`,
  lighting propagation, and tessellation.
- **Runtime:** consider ReadyToRun (checked against the Cecil transplant flow) for startup
  JIT on E-cores, and `System.GC.ConserveMemory`/`RetainVM`. Stay on workstation concurrent
  GC.
- **Small fixes:** the temporal-stability hotbar tooltip now calls `SetNewText` only
  when its displayed percentage or hover element changes, avoiding a Cairo tooltip
  recompose on every shown frame. The donor builds, and Cecil injects both guard fields
  and the patched `renderGear` method. Other repeated rich-text and hover-text updates
  still need an audit and in-game measurement.
- **Rule:** faster math only in measured hot loops; engine overhead is fixed by doing less
  work, not by faster arithmetic.

### 6. GPU efficiency

- **Smaller vertices:** the default path uses 64 bytes of face data per quad (fp32
  positions and edge vectors) plus 4 bytes of light per vertex, with 32-bit indices.
  Quantize to about 32 bytes (chunk-local 16-bit positions, small-integer edges, packed
  flags) and use 16-bit or shader-generated indices. About 40% less vertex bandwidth,
  aimed directly at the measured bottleneck.
- **Shadow caching:** update the far cascade every 2nd–4th frame, or when the sun or camera
  moves meaningfully, with texel-snapped projection.
- **Half-resolution passes:** volumetric clouds march up to 200 steps per pixel at full
  resolution; bloom's bright-pass runs at full resolution but only feeds half-resolution
  blurs; GTAO has no half-resolution option, and upscaling bumps it from Medium to High.
  Add half/quarter-resolution variants with depth-aware upsampling, and slim the G-buffer
  (position target, normal encoding).
- **Unified-memory uploads:** static meshes still go staging → GPU copy
  (`MeshManager.cs`), a pointless extra pass through the same RAM on an iGPU. Write them
  directly into device-local host-visible memory, and use smaller staging and image blocks
  there (about 64 MB+ saved).
- **Dynamic resolution:** none exists. Drive render scale from GPU timestamps to hold a
  target frame time, within upscaler and TAA history rules.
- **Compressed textures:** block, entity, and item atlases are uncompressed RGBA8 (about
  64 MB per 4096² atlas). BC7 cuts texture traffic about 4×; tile bounds and runtime atlas
  rebuilds make it the last step here.

## Later

- **Far-terrain LOD:** merged/downsampled geometry for distant chunks. Very high impact
  for long view distances on a handheld, but the largest effort.
- **Mod-facing renderer API:** stabilize native pass, resource, motion-writer, and
  capability contracts so mods participate without OpenGL assumptions.
- **HDR output:** HDR scene range and tone mapper, display-referred UI, HDR10 or scRGB
  presentation, and an FG-compatible format path.
- **Auto-PBR materials:** normal, roughness/metalness, and emissive companion atlas data
  generated from VS material classes, with authored resource-pack data taking precedence.
- **Ray tracing and denoising:** acceleration-structure maintenance for the editable chunk
  world, then ray-query AO, shadows, and reflections with cross-vendor denoising and DLSS
  Ray Reconstruction where available.
- **Documentation cleanup:** remove stale implementation notes, keep the reasons behind
  non-obvious synchronization and SDK decisions, and make renderer entry points
  approachable once the systems stop moving.

## Validation and polish (ongoing)

- Run FSR 4 on supported AMD hardware; its fallback and code path are covered, but no
  current development device can execute it.
- Resolve or conclusively classify the two real-window resize synchronization-validation
  reports from the performance pass.
- Extend visible-window and cross-vendor coverage for resize, minimize/restore, history
  reset, motion-vector scale/sign, UI recomposition, and multi-frame limits.
- Register `HudDebugScreen.cs.patch` and `ShaderProgram.cs.patch` in
  `patches/cecil-owned.list` (or the ownership test's allow-list); the Cecil ownership test
  fails until they are.
- Keep measuring before taking barrier, descriptor, or interop shortcuts; the profile lists
  candidates, not pre-approved optimizations.

## Product rules

- Handheld playability is the bar: 1% lows, power, and battery count alongside average FPS,
  and claims are measured on the Claw.
- Frame generation always consumes a genuinely HUD-less scene and a separate UI resource;
  generated UI is not acceptable.
- Vendor features remain optional at runtime. Missing SDK binaries or unsupported hardware
  fall back cleanly without preventing Vulkan from starting.
- Requested settings and effective SDK state are distinct. The renderer reports clamping,
  fallback, and generated-present counts rather than pretending a requested multiplier ran.
- Correctness and pacing are judged on visible output and GPU/SDK evidence, not merely by
  successful feature creation or a passing headless capture.
