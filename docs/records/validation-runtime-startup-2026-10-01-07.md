# Mandatory final blit startup result — 2026-10-01

One bounded validation batch, run once: Release Game/backend build, fresh stage,
owned installation update and original executable launch. No tests, native or
shader rebuilds, archive regeneration, source fixes or second launch.

## Complete result

- Artifacts: `artifacts/validation/runtime-startup-20261001-151957/`.
- Build: passed, zero warnings/errors. Stage and deployment: passed.
- PID 40180 stayed alive at the 20-second observation with title `Vintage Story`
  and nonzero window handle 724354. CloseMainWindow returned true; the process
  exited within the subsequent 10-second wait, exit code 0.
- Bootstrap recorded runtime.active (SDL first window) and runtime.stopped
  (device drain before SDL teardown), then normal client Main/process return.
- No render.screen.failed or critical startup exception was recorded. The prior
  mandatory final-blit crash did not recur during this observation.
- Shader log reports 42 native, zero rewritten and zero failed programs. GUI
  Manager initialized; a local server began loading an existing save and its
  game/creative/survival/vulkanstory mods. World startup was not completed before
  the bounded close, so this is not world-rendering acceptance.
- Captured client-crash.log is historical: its source LastWriteTime is 15:10:59,
  before this batch. It contains earlier crashes and is not a new failure.

## Unresolved evidence

Streamline warns about missing VK_EXT_debug_utils, unsupported plugin hooks for
CmdBindPipeline/CmdBindDescriptorSets/BeginCommandBuffer, and a zero-sized optional
backbuffer extent reset by the SDK to 2560x1600. Feature initialization/signature
verification is not proof of DLSS-G execution. Investigate the Vulkan hook and
resource-extent paths next, preserving full provider functionality.

The stderr log also retains an old Optimum label from migrated Reflex code; this
is provenance-era logging, not evidence of a launcher dependency. Audio warnings
from the previous batch were not observed here; audio acceptance remains open.

No screenshot or presented-pixel capture was taken. Interactive menu/world,
upscaler/FG execution, settings/input, world shutdown, MFG proxy coexistence,
other vendors/platforms and current release archives remain open. The installed
candidate now includes the mandatory-pipeline source correction; the older
client archive remains stale. No source repair or validation rerun in this turn.
