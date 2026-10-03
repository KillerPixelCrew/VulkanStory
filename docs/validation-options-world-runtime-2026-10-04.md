# World Options rendered; shutdown failed — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded corrected Game/Mod build → fresh stage → hidden world child ran.
Both builds passed; Game retained one CS8600 warning, Mod was clean. Current
SR-retention FSR3 bridge and previous matching Streamline bridge were staged.
No source fix, second batch, tests, visible launch or installed deployment ran.

Artifacts: [options-world-20261004-010804](../artifacts/validation/options-world-20261004-010804/).
Owned child PID 85516; isolated foggy village story snapshot. Command at world
frame 30; one capture at frame 120. Receipt options-diagnostic.json records the
original GuiDialogEscapeMenu, gamesettings-graphicsingame landing, measured click
(820,193) and displayed gamesettings-vulkanstory-1-1. Hidden=true, focused=false.
The PNG was inspected: actual Image-page controls and page/header buttons render
over the world. Header text truncation and footer/scroll reachability need further
work; no save, cancel, page change or controller interaction was executed.

## Overall FAIL despite raw verifier pass

The raw capture verifier reported pass=true and process exit was 0. However,
client-crash.log records a shutdown crash: Startup patch removal failed while
OptionsSettingsOwner.ClearAll → Clear → GuiComposerManager.Dispose released a
LoadedTexture via GLDeleteTexture, which fell through to uninitialized OpenGL.
The complete stack and main log are retained. This is not successful lifecycle
acceptance and the raw pass must not override the crash evidence.

Source diagnosis: ProcessRuntime.Shutdown disposes/nulls the session before
disposing the routing transaction. Routing disposal disables routing before
subgroup rollback invokes Options ClearAll. The remaining custom GUI texture
cleanup therefore runs too late for Vulkan routing/device ownership. Fix owned
Options cleanup ordering in an implementation turn, and make the verifier reject
a fresh child crash log even when the game handles its crash with process exit 0.
No unchanged rerun was performed.

Game DLL SHA256: `3AC9D986A4EA350D5DE3A504DF1C67A9320BE77CF8B3D71A466CA6EE82143D24`.
Mod DLL SHA256: `B2E2220A182C74B9FC44638EAFA057E16C056A279A9DD09238505E93642764CC`.
UI-01 has scoped original navigation/render evidence only and stays open. Main-menu,
physical input, persistence/return, controller and shutdown acceptance remain.
Prepared visible launchers/stage and original installed game/settings/save unchanged.
