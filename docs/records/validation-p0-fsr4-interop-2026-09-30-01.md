# P0 FSR 4 interop compile validation — 2026-09-30

Status: **69 backend tests and clean tool build passed.** One bounded batch ran
once. No implementation source changed, no command was rerun, and no native
preflight, bridge build, SDK evaluation, game launch, package or deployment occurred.

Both commands exited 0 within their 60-second limits:

```text
dotnet test tests/VulkanStory.Render.Vulkan.Tests/VulkanStory.Render.Vulkan.Tests.csproj -c Release --logger trx;LogFileName=backend.trx --results-directory <batch-directory>
dotnet build tools/VulkanStory.Preflight/VulkanStory.Preflight.csproj -c Release
```

The [summary](../../artifacts/validation/p0-fsr4-interop-20260930-01/summary.json),
[test stdout](../../artifacts/validation/p0-fsr4-interop-20260930-01/test.stdout.log),
[test stderr](../../artifacts/validation/p0-fsr4-interop-20260930-01/test.stderr.log),
[TRX](../../artifacts/validation/p0-fsr4-interop-20260930-01/backend.trx),
[build stdout](../../artifacts/validation/p0-fsr4-interop-20260930-01/build.stdout.log)
and [build stderr](../../artifacts/validation/p0-fsr4-interop-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Backend cases | 69/69 passed; zero failures/skips. |
| Managed ABI | FSR 4 dispatch size 96 and all field offsets passed, including ready/done fence values at 80/88, on x64. |
| Compile group | FSR 4 backend/runtime/shared-frame owner, shared Vulkan image, DX12 runtime interface and shared timeline fence compile through the renderer host interface. |
| Tool build | Zero warnings/errors; stdout/stderr retained. |

The native bridge's static assertion and exported functions were not compiled
or loaded by this batch. No shared resource/fence was created, no provider
initialized/evaluated, and no real SR image was captured. The complete device,
forwarders and `VulkanDevice.Fsr4.cs` remain excluded. This does not establish
native ABI/export agreement, supported AMD execution, Vulkan/DX12 synchronization,
runtime fallback, resizing/switching/shutdown, scene temporal inputs or game
presentation. Full renderer/SR/FG/latency/SDL/game/release acceptance stays open.
See [the implementation/provenance record](fsr4-interop-port-implementation.md).
