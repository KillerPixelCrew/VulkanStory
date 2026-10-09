# Early asset correction startup batch — 2026-10-01

One bounded Game build/stage/update/launch batch, run once. No tests, source fixes,
native/shader rebuilds, archive regeneration or second launch occurred.

Game compiled with zero errors/warnings; stage and owned deployment passed.
PID 27496 recorded runtime.installed, runtime.active and runtime.stopped. The
previous AssetsLoaded-stage failure is no longer reported. Native logs identify
the Vulkan 1.4.351 device as RTX 4070 Laptop GPU and report DLSS-SR availability;
this does not establish successful upscaling.

First menu frame instead throws:
`You need to initialize the OpenGL binding first by calling LoadBindings() or creating a compatible OpenGL window`.
The user supplied the matching crash report. Game crash handling exited 0; no
successfully presented menu/world/provider frame is accepted.

Complete stdout/stderr, trace, build/deployment logs, backup, stage, result JSON
and copied client-crash.log:
`artifacts/validation/runtime-startup-20261001-143958/`.

## Source diagnosis and remaining uncertainty

The original ScreenManager.Render body has two direct GL calls (depth clear and
depth range); the existing UI transpiler guards and replaces both. Its remaining
calls include platform state/post methods and nested screen/GUI render methods.
The reported stack is flattened at Render_Patch1 and does not identify which
nested/fallback operation reached OpenGL. No exact escaped call is yet proven.

Next implementation work is to preserve the first exception's detail and record
the menu call boundary needed to identify and route that operation. Do not create
a GL window, install dummy GL bindings or suppress the frame failure as a fix.
Continue the complete Vulkan routing scope. No instrumentation/source repair or
rerun was performed in this validation turn. Installed payload still crashes on
the first menu frame; full rendering/SDK acceptance remains open.

## Following bounded menu diagnostic source — 2026-10-01

The UI transpiler now records the last original call boundary for the first four
active screen frames, including instruction index and method identity. Markers
consume only their own string, preserve branch/EH metadata, and leave IL prefixes
adjacent to the original call. Existing depth replacements and all render calls
remain. UI composition postfix has its own boundary label.

The finalizer traces the first exception before cleanup/rethrow and records any
cleanup exception separately, returning the original error. It never suppresses
the failed frame or initializes OpenGL. Per-call tracking writes no log on success;
failure emits one diagnostic record. Tracking is bounded to four frames.

Implementation only: no builds/tests/deployment/game runs. The escaped operation
is still unproven until the updated diagnostic payload runs; installed build has
the preceding flattened failure. Remove temporary call tracking after routing the
identified operation. Full renderer/provider acceptance stays open.
