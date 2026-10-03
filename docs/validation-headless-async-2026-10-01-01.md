# Asynchronous composition headless checkpoint — 2026-10-01

One bounded Bootstrap/Game build -> fresh stage -> isolated user-world capture
with -AsyncPipelines, run once. No tests, native rebuild, installed deployment,
source repairs or rerun. Artifacts:
`artifacts/validation/headless-async-20261001-185302/`.

Builds passed, zero warnings/errors. Reused native bundle from cleanup-184218.
Staging passed. PID 44840 loaded the isolated foggy village story snapshot,
captured all three scheduled frames/attachment/AO outputs and automatically
closed. Result: success, hidden=true, focused=false. Device drain recorded.
No required-composition refusal or new render/cleanup error. Known SDK hooks remain.

The launcher set the explicit asynchronous pipeline override; source applies it
before device creation, overriding the capture directory's normal blocking default.
This run does not prove the driver cache was cold or that every first-use compile
queued on the worker; no cold-driver-cache or broad pipeline acceptance claimed.
Required-pass policy is supported by compiled source and this scoped runtime.

Final sample: 234 successful FSR SR frames, 232 FG-prepared frames, 717 real/947
SDK presents (230 additional outputs). The corrected diagnostic gate produced a
current composed-world image; inspected PNG shows world geometry/vegetation/sky
and HUD. Full visual parity, streamed-terrain completeness and generated-frame
quality still require stronger synchronized evidence.

Installed game/user settings unchanged. All required-pass/stated-fallback source
changes now compile. No transition scripts, input, other-provider/platform, proxy
coexistence or release archives checked. Remaining full-feature/release gates stay
open. No source repair/rerun in this validation turn.
