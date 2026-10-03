# Unused Streamline cleanup ownership correction — 2026-10-01

Implementation only: no build, tests, probes, packages, snapshots or launches.

The FSR 3 run captured a repeated DLSS-G options warning at shutdown. Device
release unconditionally sent Off, even when another provider had owned the whole
session. The bridge also used fgConfigured both as its options-cache validity and
as the condition for resource release; Create/DestroySwapchain invalidates that
cache, which is not reliable evidence that previously enabled FG owns no resources.

The bridge now tracks fgResourcesMayExist separately. Successful On records that
resources may need release. Swapchain cache invalidation preserves that lifetime
state; successful free or full SDK shutdown clears it. A new
DisableFrameGenerationForRelease export skips unused or already-Off DLSS-G and
uses the cached metadata only when a real On->Off transition is needed.

Device shutdown calls that ownership-aware disable, drains the proxy's GPU/present
work, and frees any potentially owned FG resources before direct NGX shutdown.
Release no longer requires a live Vulkan swapchain: XeSS can own presentation while
old DLSS-G resources still need cleanup. Initialization/readiness guards remain.
No warning filtering, SDK replacement or provider selection changes.

New native export requires matching Streamline bridge/backend build and fresh
staging before another isolated run. Existing bundled bridge lacks the export.
Repeated-warning removal and provider-switch/resize cleanup remain unverified.
Known SDK hook warnings, DLSS hidden interpolation, other feature/platform and
release acceptance remain open. Installed game/settings untouched.
