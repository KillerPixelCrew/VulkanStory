# Effective provider and temporal debug presentation

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The session publishes an immutable text snapshot after Present. It separates
requested SR from successful current-frame evaluation and requested FG from
prepared current-frame inputs. It also reports TAA resolve/motion availability,
compiled AO mode and the existing cumulative DLSS-G SDK present count when that
provider is selected. It does not infer generated output from the requested
multiplier. World leave clears output presentation.

The ordinary/framework bridge exposes this snapshot without renderer references
in the mod. The shared settings panel now has a Status page refreshed at most once
per second, used by both main-menu and in-world settings. A temporal debug selector
exposes the retained off/motion/reactive/validity/overlay shader modes.

The FPS overlay and broader vendor present counters remain unfinished. Prepared
inputs are explicitly distinct from confirmed interpolated/presented frames.
Visible status/debug output, vendor counters and switching remain unverified.
Native/runtime packaging and full live port acceptance remain open.
