# P0 full retained device compile validation — 2026-09-30

Status: **full device compilation and 75 backend tests passed; three nullable
warnings remain.** One bounded batch ran once. No implementation source changed,
no command was rerun, and no native preflight/SDK, game, package or deployment ran.

Release backend tests and the preflight-tool build exited 0 within their
60-second limits. The [summary](../artifacts/validation/p0-full-device-20260930-01/summary.json),
[test stdout](../artifacts/validation/p0-full-device-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/p0-full-device-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/p0-full-device-20260930-01/backend.trx),
[build stdout](../artifacts/validation/p0-full-device-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/p0-full-device-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Complete compile group | All retained device partials, provider forwarders, GTAO/timestamp/FG/XeLL support and texture tag adapter compiled. |
| Backend cases | 75/75 executed and passed; zero failures/skips. Includes five retained timestamp label/gating cases. |
| First compilation | Three nullable warnings, no errors. |
| Tool build | Exit 0, zero reported warnings/errors; incremental compilation does not resolve the first-step warnings. |

## Source diagnosis for the next implementation turn

- `VulkanDevice.cs:671`, CS8601: `VulkanContext.TryCreate` supplies a nullable
  failure output directly to `InitializeWindow`'s nonnullable failure parameter.
  Capture the nullable result locally and normalize it at the facade boundary,
  preserving the public contract and failure message.
- `VulkanDevice.Programs.cs:134`, CS8604: source dumping passes the nullable
  `program.PassName` despite the already-computed normalized `passName` local.
  Pass that local to the dump helper.
- `VulkanDevice.XessFg.cs:117`, CS8602: restoration assigns an `out Swapchain?`
  after a successful factory result and then dereferences the nullable field.
  Make the factory's successful nonnull ownership explicit without changing
  recreation, provider selection or presentation behavior.

These repairs were not made or revalidated in this turn. No facade instance was
initialized or exercised by this batch. Embedded GTAO resource inclusion compiled
but shader contents/loading/compute execution were not exercised. SDK runtime
loading, shared resource/fence execution, actual device frames, scene/UI input
producers, game graphics/startup and world routing remain open.

DLSS-G evidence: build/static is PARTIAL (compile pass with warnings); runtime
fallback, Streamline setup, functional activation, real-scene tags, visual and
performance/pacing are NOT RUN. No new-host FG command, state/multiplier report
or present-active evidence exists. Full renderer/SR/FG/latency/SDL/game/release
acceptance stays open. See [implementation and profile](full-device-port-implementation.md).
