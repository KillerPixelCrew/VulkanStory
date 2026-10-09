# Isolated harness and scripted renderer settings — 2026-10-01

One bounded batch completed, with no rerun or source repair. Artifacts:
`artifacts/validation/headless-commands-20261001-192857/`.

Bootstrap, Game and Mod builds passed, followed by a fresh stage and one isolated
capture of a SQLite snapshot of **foggy village story**. No deployment to the
installed game, original settings edits, or interaction with the user's window.

The run started with FSR3 SR/FG and asynchronously compiled ordinary scene
pipelines. At world frame 90, the retained command runner dispatched:

```text
.vulkanstory set Upscaler xess
.vulkanstory set UpscalerQuality balanced
.vulkanstory set FrameGeneration xess
.vulkanstory status
```

All three changes survived sequential edits: the isolated saved JSON contains
`xess`, `balanced`, `xess`. The game log records world targets changing from
1706x1018 to 1280x764, with a successful XeSS balanced upscale to 2560x1528.
The final runtime record reports effective XeSS SR/FG, current camera/motion
inputs, 300 successful upscale frames and 297 prepared FG frames. Aggregate
provider counters across the transition are 832 real presents and 1122
SDK-reported presents; these are not a generated-image quality assertion.
The immediately following status command correctly describes the preceding
rendered FSR3 frame because the queued settings apply on the next frame.

`frames/headless-result.json` reports success, hidden=true, focused=false,
worldReady=true, and stagedModLoaded=true with the exact isolated DLL path.
All three scheduled PPM/PNG pairs were written at world frames 240, 270 and 300.
The last PNG was inspected: terrain, vegetation, sky, map and HUD are visible.
The process saved its isolated world, drained and exited normally; NGX shutdown
reports success. This establishes a usable harness, not full renderer parity.

Remaining warnings: existing Streamline SDK unsupported hooks for
CmdBindPipeline/CmdBindDescriptorSets/BeginCommandBuffer, shader-rewriter notices,
and one repeated slDLSSGSetOptions warning at the provider switch. No crash occurred.
That transition warning remains open for a later implementation turn.
Concurrent user-session performance and GPU interference are not measured;
the hidden renderer still shares GPU and memory resources.

No new tests were written. No second batch was started.
