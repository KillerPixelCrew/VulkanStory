# Streamline camera/release correction result — 2026-10-01

One bounded native/Bootstrap/Game build -> bundle/stage -> isolated headless
capture batch, run once. No tests, installed deployment, repairs or reruns.
Artifacts: `artifacts/validation/headless-20261001-155956/`.

Streamline bridge built against SDK 2.14.1/Vulkan SDK 1.4.357.0. Other four bridges
were reused unchanged from their recorded build. Bootstrap and Game/backend/SDL
builds passed with zero warnings/errors. Fresh native bundle and stage passed.

PID 36504 loaded the isolated SQLite snapshot of foggy village story, captured
all 3 scheduled frames (180/210/240), attachment/AO outputs and automatically
exited. Headless result reports success, 3/3. Bootstrap records device drain
before SDL teardown. No user game process, installed files or settings touched.

## Corrected runtime evidence

The prior cameraPinholeOffset warning is absent. The prior Streamline NGX release
0xbad00004 error is absent. Direct SR NVSDK_NGX_VULKAN_Shutdown1 reports Success.
No cleanup exception or new render failure is recorded. This supports the explicit
centered-camera constant and release-before-shared-NGX-shutdown corrections in
this bounded session, not all resize/toggle or device-loss lifecycles.

Final periodic sample: 233 successful DLSS SR frames, 231 FG-prepared frames,
230 SDK-reported presents, current camera/motion. Hidden-run presents remain
approximately one per prepared frame; generated output above real presents is
not proven. Three supplied SDK hook-map warnings remain visible/unresolved.

No SSIM comparison, commands or physical scanout were checked. Next implementation
should pursue remaining provider/capture/compatibility scope using the working
headless path, preserving full features. Other providers/platforms, input/settings,
resize/coexistence and current release archives remain open. This batch's matching
native bundle is the new source for staged headless runs; do not fall back to the
older bundle missing the FreeFrameGenerationResources export. Installed candidate
still predates the harness/lifetime corrections. No source repair or rerun here.
