# Hidden Options shutdown checkpoint — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded Game/Mod build → fresh stage → hidden Options child ran once.
Both builds passed; Game retained one CS8600 warning, Mod was clean.
Artifacts: [options-shutdown-20261004-011713](../artifacts/validation/options-shutdown-20261004-011713/).

The historical verifier invocation first failed parameter binding (a string was
passed for TimedOut); the verifier body did not execute. Corrected argument
construction ran it once against the old retained crash run: exit 1, pass=false,
failures=[ClientCrash]. Both outputs are retained. No second runtime child or
unchanged runtime rerun was launched.

Fresh child PID 87568 used an isolated foggy village story snapshot, current
managed outputs, matching SR-retention FSR3 bridge and the previous Streamline
bridge. At world frame 30, the actual pause Options landing composer received
the VulkanStory entry click; receipt success=true identifies GuiDialogEscapeMenu,
gamesettings-graphicsingame → gamesettings-vulkanstory-1-1, entry (820,193).
Capture at frame 120 shows actual Image-page controls over the world; PNG inspected.
Hidden=true, focused=false, stagedModLoaded/worldReady=true. Child/batch exited 0,
verifier pass=true, no timeout and no client-crash.log. Main log records normal
world/server close and successful NGX shutdown, without cleanup exceptions.

Game DLL SHA256: `EA89ADBD958210CC2C93D03CDF506CA45A7E1ECB571E9132826BFD43C76B3ABE`.
The [UI cleanup ordering fix](options-shutdown-order-2026-10-04.md) now has scoped
normal-shutdown evidence. Failure injection and other lifecycle paths remain open.
Visible header truncation and initially clipped Save/Cancel footer remain defects;
page changes, scrolling, persistence, controller input and main-menu Options were
not exercised. This is not full UI-01 acceptance or FG evidence.

No source fix, tests, native build, deployment or visible launch occurred. Existing
prepared visible launchers/stage still await prior approval. Installed game,
settings and original save unchanged.
