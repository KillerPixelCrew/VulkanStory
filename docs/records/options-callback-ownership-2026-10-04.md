# Options callback ownership — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
UI-01 remains open. No builds, tests, probes, game runs, packages or deployment ran.

Cached original Graphics composers retained an unguarded VulkanStory entry, and
replaced panel composers retained callbacks into a shared editing draft. Those
callbacks could reopen/save/change settings after host navigation or recomposition.

Entry callbacks now use weak state/composer references and require the original
entry composer to be displayed. Panel Save/notify/close delegates require the live
editing generation and active host; they do not hold the host strongly. Clearing
Options invalidates the generation. Same-owner recomposition retains that editing
generation/draft but panel actions require the exact displayed composer. This
covers page/choice/debug changes, switches, sliders, Save/Cancel, Graphics return
and post-publication scrolling. Scroll initialization before publication still
works. Pending return blocks duplicate actions. The retained Mod dialog and menu
screen use matching composer/open-state guards, including title-bar close.

Source inspection covered all panel draft mutation callbacks, initial compose/
initialization, same-owner replacement, failed preparation, return/close and host
disposal. No tests were added. Compilation and actual Options interaction remain
unverified; this is an ownership repair, not GUI acceptance or controller entry.
Existing build/stage binaries predate it. Installed game/settings/save unchanged.

| File | SHA256 |
| --- | --- |
| src/Shared/RendererSettingsPanel.cs | `1D7F13106029B6586757075CE82A49394D50BC4F8813FC19B39AF6533A9AAC4D` |
| src/VulkanStory.Game/OptionsSettingsOwner.cs | `957F9E329167746D4CF778E26F5365189523E3011BB2A2250C13BFEEE06321D7` |
| src/VulkanStory.Game/RendererSettingsScreen.cs | `9022BE7B8A9DAE7B6B548B38012958F20773A735281CC22D49093EA5402338D8` |
| src/VulkanStory.Mod/RendererSettingsDialog.cs | `967982211157F874E82F0E4AB8C64C1334DEDDBF559F2448994F60AC3431639B` |
