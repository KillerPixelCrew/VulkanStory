# Controller world-owned damage input — 2026-10-01

Implementation turn only: source inspection and editing, no build/test/probe,
package, game launch or installed changes.

WorldLeaving previously closed its world-specific dialogs and released actions,
but left the player damage listener attached until another controller poll or
shutdown. The controller now records the world owning that listener. Unload
explicitly unbinds a listener belonging to the departing world, including when
EntityPlayer has already been cleared. Normal damage polling attaches the current
world; suspension and disposal use the same unbind path to clear owner/pending rumble.

A delayed old-world release now checks the original active GUI's world identity
after closing old associations. If a newer world is already loading before its
first temporal scene capture, it retains its binding capture and controller actions.
This complements the temporal owner's existing newer-client guard.

Motion histories remain session-owned weak tables and use frame/reset/view validity;
no speculative history/shader algorithm change was made. Provider retirement and
camera detachment source were inspected, not newly exercised. Current ZIP predates
this increment. Compile, world rejoin/haptic/input execution and full renderer/
provider/SDL acceptance remain open. No tests were written.