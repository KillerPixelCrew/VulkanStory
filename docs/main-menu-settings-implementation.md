# Main-menu renderer settings

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The active profile adds a VulkanStory button in the original main-menu sidebar's
gap between Credits and Quit. A checked original/incoming composer call receives
the button before normal GUI composition; existing menu items and layout remain.

The button opens `RendererSettingsScreen`, preserving and rendering its parent
screen. Back/Cancel return to that parent. Page changes compose at a render
boundary, and resize is forwarded to the parent before rebuilding the panel.
The required `graphics-menu-settings` group owns and removes the menu hook.

Settings controls now live in one source file linked into Game and the ordinary
API-only mod. Both the main-menu screen and in-world dialog use its draft pages,
Save/Cancel and runtime bridge. The ordinary mod gains no renderer/game/native
reference or second runtime. Save errors stay visible in the panel instead of
depending on a main-menu chat window.

This menu hook belongs to the active renderer profile; a disabled/missing early
runtime still needs its existing file or ordinary-mod re-enable path. Visible
button placement, layout, controller navigation, resize and save behavior remain
unverified. Remaining debug/FPS/effective-state presentation and native/runtime
packaging are open, and full port acceptance remains unproven.
