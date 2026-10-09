# Provider lifetime compile checkpoint — 2026-10-01

One bounded Game/FSR3/Streamline compile batch completed once. Artifacts:
`artifacts/validation/provider-lifetime-compile-20261001-210219/`.

| Target | Result |
| --- | --- |
| Game/full managed dependencies | Passed, zero errors, one CS9113 warning |
| FSR3 bridge with retained Vulkan FidelityFX SDK | Passed, eight existing native warnings |
| Streamline bridge with SDK 2.14.1 | Passed, no compiler diagnostics |

Fresh bridges are under the batch's native directory. These include the
disable/drain/destroy ownership corrections; older bundles/stages/ZIPs do not.
FSR3 warnings are the retained seven dynamic function-pointer casts and one
enum/non-enum conditional. No warnings were suppressed.

Game CS9113 reports the now-unused RuntimeFrameGeneration warning callback.
Its last callers became explicit disable/drain exceptions in the preceding
implementation. Source inspection confirms only the constructor declaration
still references it; remove that unused parameter/call-site delegate in a later
implementation turn. No repair or rerun occurred here.

No tests, staging, native export/loading probes, deployment or game run. This
proves compilation only; successful SDK execution and injected failure/drain
paths are not verified. Full renderer/provider/SDL acceptance stays open.

## Subsequent source correction

Removed the unused warning callback and its session constructor argument.
Frame-generation failures now retain the original reason for Unavailable/status
queries, keeping the same once-per-provider logging and selection retry behavior.
Reset clears the preceding DLSS-G state/query result alongside its counters, so
world/quality changes cannot publish a prior query as current evidence.
Implementation only; no builds, tests, probes or runs after these corrections.
