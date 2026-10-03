# DLSS-G capability state and hidden-window evidence — 2026-10-01

Implementation only: no builds, tests, probes, packages, snapshots or launches.

The completed headless run exposes SDK presents/status/max count but not the
SDK's minimum resolution, VSync support hint or dynamic MFG support. A new native
GetFrameGenerationStateDetails export returns six explicitly sized uint values
from one SDK state query. The managed sequential struct and coordinator consume
that single query, so reading extra capabilities does not consume the SDK's
since-last-query present counter a second time.

The coordinator retains the last query/result for status output, uses the actual
SDK generated-frame limit, and waits (suspending FG) for a nonzero limit, adequate
output dimensions and supported VSync configuration. It no longer changes a zero
reported maximum into an invented supported count after a successful state query.
The first tagged frame keeps the existing conservative one-generated-frame request;
capabilities are queried after it, preserving the existing SDK initialization order.
Requested provider selection remains intact; settings can resolve a waiting state.
No dynamic mode is claimed or selected merely from the support hint.

Presentation/status output now carries those SDK capabilities and failure status.
Actual SDK presents remain separate from prepared inputs and requested multiplier.
This does not identify the cause of the hidden run's one-present-per-prepared-frame
behavior; visibility/occlusion/SDK behavior still require stronger evidence.

SDL exposes its actual visible flag. Periodic records include visible/focused state;
headless completion records both and fails if its window became visible or focused.
These checks support the user's non-interruption contract without desktop capture
or control of their game process. They are runtime harness behavior, not new tests.

References: Streamline 2.14.1 sl_dlss_g.h state/option fields and DLSS-G guide's
minimum-resolution/VSync conditions. Existing Vulkan/proxy/tag/queue ownership,
real-scene motion gating and private runtime paths are unchanged.

The new native export requires another matching bridge/backend/SDL staged build
before headless validation. The latest successful bundle remains the fourth
headless batch but lacks this new export. No installed files/settings changed.
Interpolation, SDK hook-map warnings, broad visual parity, other vendors/platforms,
settings/input/resize/coexistence and release delivery remain open.
