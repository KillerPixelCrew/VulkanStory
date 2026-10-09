# Shader route order startup result — 2026-10-01

One Game build/stage/owned-update/launch batch, run once. No tests, source repairs,
native/shader rebuilds, repackages or second launch.

Build passed with zero warnings/errors; stage/update passed. PID 34080 activated
SDL/Vulkan and reached screen frame **51**. The preceding loading GUI OpenGL
binding error is absent. This is evidence the leaf-before-caller order resolves
that observed path; it does not independently prove the JIT's inlining mechanism.

The new failure is `The final scene blit did not draw`, originating in
GameGraphicsAdapter.BlitPrimaryToDefault through PresentScene. Startup loaded
game/creative/survival and the ordinary VulkanStory mod. Device drain/shutdown
markers were recorded. Crash handling exited 0, not a successful session exit.

Complete artifacts including captured first exception and client-crash.log:
`artifacts/validation/runtime-startup-20261001-151045/`.
The installed payload has this failure; candidate archives were not regenerated.

## Diagnosis and remaining uncertainty

The final blit throws if ShaderPrograms.Blit is not live/linked or DrawBlit returns
false. The native and original blit samplers both use `scene`, so a sampler-name
mismatch is not supported by the inspected source. Stdout reports blit linked
and no native-pipeline rejection warning, but does not record its readiness at the
failing frame or distinguish target-format/pass/fullscreen draw refusal. Capture
those exact conditions in the next source correction instead of suppressing the
required draw or deleting a blit branch.

Stdout also reports OpenAL IllegalCommand/source-ID failure when loading menu
music. This is a separate unresolved runtime issue to inspect after the immediate
blit failure. Presented pixels, interactive menu/world, upscaler/FG execution and
full shutdown acceptance remain open. No source fix or rerun occurred here.

## Final blit source correction — 2026-10-01

Source inspection identifies a concrete contract mismatch: native pipeline requests
queue asynchronous compilation, and BeginNativeDraw returns false while that
pipeline is pending. The final ordinary scene blit treated every false result as
fatal. This is a supported failure path; the previous trace does not prove it was
the particular refusal at frame 51.

The mandatory final native blit now uses the pipeline cache's blocking Get path
on a cache miss. Other draws retain asynchronous compilation. Completed cache
entries still use the normal fast path. The cache already discards a duplicate
background result when its key has been filled by a blocking lookup.

Shader readiness failures now report program ID/load/disposal state separately.
Native pass/draw refusals carry their reason to the final exception; stated
fallback refusals are preserved too. Final errors include source texture and
input/display dimensions. No required draw is suppressed.

Implementation only: no build, tests, deployment, package or game run. Installed
files still contain the frame-51 failure. A first-use pipeline compilation may
stall the mandatory presentation draw; startup/menu/world acceptance stays open.
