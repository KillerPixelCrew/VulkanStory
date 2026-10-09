# Settings and retained upscaler host

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs were performed for this increment. New test work is
deferred until renderer, SDL and provider integration is complete.

## Implemented in source

`RendererSettings` replaces the retained platform's Optimum configuration
dependency with renderer-owned settings and normalization. The JSON store uses
`ModConfig/vulkanstory.json` beneath the game data path. Settings cover render
scale, TAA, SR quality and LOD bias, FG selection/multiplier, latency, AO,
native shaders, Streamline and controller/touch enablement. Quality identifiers
retain `dlaa`, including the additional XeSS quality levels. The state object
implements the existing DLSS runtime-state contract and records session-only
provider disablement and the active scale/LOD plan.

`RuntimeUpscalers` migrates the retained registry for DLSS, XeSS, FSR 3.1 and
FSR 4. Preparation contributes SDK requirements before Vulkan device creation;
bring-up receives the real instance, physical-device and logical-device handles.
Planning, feature retirement, evaluation, failure fallback, first-success
reporting, late-overlay depth copying/clear fallback and shutdown retain the
existing backend contract. SDK implementations and resource layouts are retained.

`GameSessionServices.DeviceCreated` is a mandatory callback immediately after
successful device initialization and before graphics/input attachment. It gives
the concrete provider owner a place to call registry bring-up after its earlier
`ConfigureDevice` preparation. A callback failure enters session preparation
rollback. This defines the ordering; a production service factory is still
required to supply these callbacks.

## Still open

The later [session-provider increment](runtime-providers-implementation.md)
connects registry preparation/bring-up and framebuffer planning/failure callbacks
to GameRenderSession, adds owned FG preparation before present, and implements
settings-triggered resets/rebuilds/LOD callbacks. The chronological list below
records the original increment's open work; these source connections supersede
its registry/session attachment items. Actual temporal/post producers and the
complete service factory/profile remain open.

- Construct the settings/store/registry under the process runtime and connect
  the actual framebuffer planning and failure callbacks.
- Supply temporal camera/history data and invoke SR at the retained post stage.
- Connect settings changes to FG reset, temporal reset, deferred target rebuild,
  terrain shader reload and sampler LOD bias updates.
- Attach enabled FG and latency owners, real frame identities, resources and
  presentation ownership. Settings fields alone do not enable these features.
- Attach ordinary-mod settings/world lifecycle to this same early runtime.
- Complete scene/post routing and controller integration before registering
  the complete startup profile.

The runtime still reports `awaiting-complete-profile`. No upscaler or FG feature
has executed in the rewritten game host. The preceding verified backend and
interop records remain unchanged and do not validate these new source files.
