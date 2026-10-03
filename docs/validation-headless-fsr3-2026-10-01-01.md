# FSR 3 SR/FG isolated world checkpoint — 2026-10-01

One bounded isolated capture batch, run once, reusing the compiled staged payload
from headless-xess-20261001-182957. No rebuilds, tests, installed deployment,
source repairs or second run. Artifacts:
`artifacts/validation/headless-fsr3-20261001-183319/`.

PID 30972 loaded the SQLite snapshot of foggy village story, captured all three
scheduled frames and attachment/AO files, then automatically closed. Result reports
success, hidden=true, focused=false. Device-drain marker and direct NGX shutdown
Success recorded. No user game/process/settings were modified.

FSR 3 quality first successful upscale: 1706x1018 -> 2560x1528. Final periodic sample:
233 successful SR frames, 231 prepared FG frames, 725 real presents and 954
SDK-reported presents (229 additional outputs). FPS sample is 24 real/49 SDK output.
These are counted outputs, not a requested-multiplier estimate. Source-frame PNG
shows world/HUD output; no generated-frame image/pacing or full parity acceptance.

Known Streamline hook-map warnings persist. New shutdown warning: repeated
slDLSSGSetOptions for frame 1, despite FSR being selected. Inspect unused-Streamline
shutdown configuration to avoid unnecessary Off calls; do not suppress the warning
or remove FSR/Reflex/PCL. No current render or teardown error was recorded.

No command scripts, quality/resize transitions, SSIM or other hardware/platform
checks. FSR 4 remains hardware-gated on this NVIDIA machine; implementation and
honest fallback must be retained without claiming AMD execution here. Full renderer,
input/settings/coexistence and release gates remain open. No source repair/rerun
in this validation turn.
