# Session-owned providers and temporal frame state

Date: 2026-09-30. Implementation turn: no builds, tests, probes, packages or
game runs. Test work remains deferred. All changes below are unvalidated source.

## Actual session connections

`GameRenderSession` now creates and owns the retained SR registry. It applies
native-shader/Streamline/latency settings, contributes provider requirements
before device initialization and brings providers up afterward. The framebuffer
host uses that registry's planning/failure callbacks and the session's FG reset.
TAA failure updates renderer settings state. Provider target rebuilds and terrain
shader reloads retain the guard that waits for initial terrain shader loading.
Settings application resets FG, retires SR features and requests temporal reset,
target rebuild and terrain LOD updates.

The post chain has a session `RenderUpscaler` entry that evaluates the current
frame before bloom/final/UI. Its actual post call site still needs migration.
The session calls the concrete FG host after post/UI completion and immediately
before the retained device's real Present. Teardown releases FG and SR owners
while the device and SDL window are alive.

## Frame generation

`RuntimeFrameGeneration` ports the retained DLSS-G, XeSS-FG and FSR 3 FG host.
It keeps upright scene/UI/depth/motion formats, the GL-to-Vulkan camera depth
remap, previous/current camera transforms, SDK frame preparation, provider
switching, resize recovery, wait/failure handling and deferred GPU retirement.
It also tracks render-resolution changes when rebuilding motion inputs.

| Input | Source and convention |
| --- | --- |
| Depth/motion | Primary framebuffer and its published motion attachment; same current rendered frame. |
| HUD-less color/UI | Published scene capture and separate premultiplied UI targets, matching display/output dimensions. |
| Camera | Session temporal snapshot, retained column-major matrices, Vulkan clip-depth remap for Streamline. |
| Jitter | Actual applied render-pixel jitter; upright Y conversion remains in the existing vendor path. |
| Identity | Snapshot must match the device latency/rendered frame ID; XeSS receives that same ID. |
| Lifetime | Existing backend/proxy consumers, timeline retirement and reset-before-image-disposal. |

Incomplete menu/loading/pause/world inputs suspend generation rather than reuse
stale tags. Requested provider/multiplier, prepared inputs, effective DLSS count
and SDK actual-present count are separate. DLSS counts clamp to SDK limits;
older runtimes retain their one-generated-frame fallback. FSR 3 retains 2×.
The Streamline bridge already caches unchanged options. Preparing inputs is not
reported as proof that a generated frame was presented.

## Input identity and latency

The session owns pre-input frame identity, vendor sleep and SimulationStart.
IME/controller preparation follows; Anti-Lag's input-start marker immediately
precedes the SDL pump. The existing pump's input-sample callback marks the same
frame; SDL input-age recording uses the retained recorder. Existing device draw,
submit and present markers retain the backend's frame token and queue ownership.
The platform callback factories must supply controller/text work without adding
a second frame identity, sleep or latency marker sequence.

## Retained temporal state

`TemporalFrameState` is a direct migration of the retained temporal frame and
math source into the game project. The injected API singleton and static motion
hook holders are omitted. World/hand projection histories, camera/player deltas,
warp snapshots, Halton jitter and reset rules remain in the state object.
`AdvanceForFrame` associates one advance with a new rendered-frame ID.
`Snapshot` exports the current provider/camera data without a game-assembly
extension, retaining applied jitter after the draw jitter window closes.

The state still needs its original ClientMain/camera/projection producers and
motion writer hooks. Copying the state does not establish motion-vector coverage.

## Engine profile and acceptance limits

Backend: retained Vulkan facade and queue/frame graph. DLSS-G uses the existing
Streamline Vulkan proxy/tag/present bridge; FSR 3 uses its existing proxy callback;
XeSS uses the existing Vulkan/DX12 presenter and actual SDL Win32-handle lookup.
No SDK versions, application identifiers, runtime mode or native bridge ABIs were
changed. Runtime delivery, accepted NVIDIA identifier, production-mode loading,
real scene tags, actual swapchain behavior and enabled SDK execution are unproved
in this host. The configuration exposes Off/DLSS/FSR 3/XeSS FG and 2×–6× requests;
the new host has no completed settings UI yet.

| Gate | Result |
| --- | --- |
| Source migration and session call sites | Implemented; inspected as source only. |
| Build/static validation | NOT RUN. |
| Optional-runtime fallback, setup and tags in a live game | NOT RUN. |
| Actual FG/SR/latency, visual correctness and pacing | NOT RUN. |
| Overall integration acceptance | Open; not an accepted MVP or release candidate. |

Next: install temporal producers and motion hooks, migrate dedicated scene/post
and UI/capture routes, finish concrete controller/settings/game service factories,
and register the complete startup profile. No active profile has been registered.

The subsequent [temporal-producer increment](temporal-producers-implementation.md)
installs those camera/projection producer routes in source and replaces the
snapshot/reset service callbacks with direct session ownership. Motion writers
and the scene/post/UI routes remain open; no new acceptance evidence exists.
