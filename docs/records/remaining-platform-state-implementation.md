# Remaining legacy platform state

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

`RemainingPlatformConsumerPatches` supplies the required
`graphics-platform-remaining` group with eight original public methods:

- `BindTexture2d`, cube bind and cube unbind use retained unit-zero stated
  texture binding. The 2D identity is also tracked separately for the original
  argument-less mip-generation operation.
- `GlGenerateTex2DMipmaps` invokes the retained device's mip generation for the
  remembered 2D texture, matching the old platform implementation.
- Stencil mask/function/operation/clear preserve the retained implementation's
  no-effect behavior: the original client framebuffer formats have no stencil
  attachment. Stencil enable/disable state and line smoothing were already routed
  by `StateConsumerPatches`; no new stencil algorithm or format is introduced.

Each method is guarded by its exact original signature and raw GL call count.
Inactive routing runs the original body; active routing requires the original
platform's owned renderer adapter. The group installs/removes with its own Harmony
owner and participates in the existing transaction rollback.

`StartupProfileComposition.Create1227` now constructs this actual group instead
of requiring an external placeholder. The profile factory can assemble all named
groups in source, but it is not yet registered or activated.

Provenance: retained `VulkanClientPlatform.State.cs` at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`, with exact public method signatures
from the official 1.22.7 platform snapshot. General third-party stencil targets
remain outside the original renderer's retained capability.

## Remaining work

Complete source coverage reconciliation of private GL helpers/constructors and
remaining window consumers, early version-profile registration, motion coverage,
analog/hint consumers and native/runtime packaging remain unfinished. A named
group or source factory does not prove a GL-free startup or correct rendering.
No Harmony install, texture/mip result or live game frame is accepted here.
