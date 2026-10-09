# Controller enable/disable transition — 2026-10-01

Implementation turn only: no build, tests, probes, package or game launch.

Source review of SDL resize/fullscreen/focus handling found those adapter paths
retain the donor flow. The new session controller-enabled gate, however, skipped
PollAfterEventPump while continuing PrepareForEventPump and repeatedly calling
OnFocusLost. An already-open remapping dialog or controller keyboard remained
owned but stopped receiving its controller polling. Debounced profile writes also
stopped until re-enable or shutdown.

The session now detects controller-enabled transitions before input pumping.
Disabling closes owned controller UI, cancels binding capture, releases controller
keys/buttons/axes/gyro/rumble and residual bridge references, flushes profiles and
clears damage-haptic listener/pending rumble. Controller hints become inactive.
Disabled frames retry pending profile writes without gamepad input preparation,
SDL_UpdateGamepads or controller activity selection. Window event pumping and
vendor input-stage marking retain their order.

Re-enabling forces a device rescan (including hot-plug while disabled); the first
poll has reset elapsed time. The controller subsystem/device can remain owned for
reuse and is released by normal session disposal. No rendering algorithms or SDL
native API bindings changed. Motion-output certification from the preceding turn
and this lifecycle increment remain unbuilt; interactive/controller acceptance
is still open. No tests were written or original saves/settings changed.