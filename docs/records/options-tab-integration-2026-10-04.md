# Options-tab source increment — 2026-10-04

Workspace: `D:/Coding/VulkanStory-Rewrite`, DIRECT mode, implementation turn.
This directory has no Git repository; the source hashes below identify the
increment. No donor/runtime binaries were copied. UI-01 remains open.

## Applied behavior

The original `GuiCompositeSettings.ComposerHeader` now adds a VulkanStory entry
on Graphics, the Options landing tab in both `GuiScreenSettings` and the pause
menu. The earlier separate main-menu entry is replaced by this integration.
Selecting it loads the shared Image, Generation, Effects, Device and Status
pages into the existing host/composite, without leaving the world or opening a
second dialog. Save and Cancel return to the original Graphics tab; the existing
read/apply callbacks and restart labels remain in use. Other original tabs keep
their original layouts; return through Graphics to enter VulkanStory again.

The VulkanStory sidecar owns the draft, current composer, scroll offset and
refresh state. Recomposition retains the panel/draft and clamps/restores scroll.
The content viewport scrolls the full page, including messages and Save/Cancel.
Embedded labels use individually owned dynamic text textures and measured
heights, so they follow scrolling instead of remaining baked into the parent
texture. Narrow content stacks controls below labels and wraps the page buttons.
The custom panel accounts for window pixels, GUI scale and main-menu sidebar
width. Below its minimum size it preserves the prior composer and logs a concrete
resize/scale reason; an initial failure resets the entry toggle.

Preparation uses a fresh cache name and fresh bounds for every attempt. It does
not mutate shared vanilla header bounds. The host receives only a fully composed
replacement; old custom-cache disposal follows publication. Failed preparation
disposes only that attempt, preserves the prior composer/draft/scroll and avoids
per-frame retries at unchanged geometry. Host close/disposal, vanilla-tab return
and host composer replacement release custom cached UI. The standalone Mod
settings dialog now closes/disposes on world departure as well as Mod disposal.
Owned composer entries are detached from the host before cache disposal, avoiding
another disposal through the host's shutdown path.

## Source inspection and limits

Inspected the existing official 1.22.7 decompiled Options/host/composer/bounds
sources and saved text/scrollbar API contracts under
`.codex/plans/1738e94bc7e243258362ee74fff5f9b4/P01-prep/UI` and `P01/D1`.
These are static reference inputs, not new assembly compatibility evidence.
Inspection covered patch signatures, the two original hosts, clip-scope balance,
dynamic text registration, scrollbar units/restoration, publication versus failed
preparation, weak owner registry and matching close/disposal routes.

No build, test, probe, package, native/GPU/game run or deployment occurred. No
tests were added. The existing candidate ZIP, installed game, settings and save
were not changed. Compilation, patch acceptance and visible GUI/input/scroll
behavior still need a separate bounded validation turn.

This is a useful first UI-01 increment, not full acceptance. Remaining work
includes controller remapping/gyro/haptics entry with confirmed save/open/return
ownership; broader vanilla navigation at narrow windows/high GUI scale; visible
reachability and text fit; save-error, resize, close, leave/rejoin and persistence
acceptance. No receipt/controller/harness proposal from the stopped dispatch
was applied. Existing harness/viewport and retirement source were preserved.

## Applied source identities (SHA256)

| File | SHA256 |
| --- | --- |
| `src/VulkanStory.Game/MenuSettingsConsumerPatches.cs` | `5C7BA28ED8074BE943A9121EA07F3FF9339621013631838BD8E3A357AFA51BC8` |
| `src/VulkanStory.Game/OptionsSettingsOwner.cs` | `FAB5E540B070F84044F18BA3EE042CE2DEF70C6A9E04D916638F7DAF4799E524` |
| `src/Shared/RendererSettingsPanel.cs` | `9637030213B1D679BCD1B2F9FCC8B51D5A44B1B122B8666E5A7DA3475C8E3DDD` |
| `src/VulkanStory.Mod/VulkanStoryModSystem.cs` | `68B4C77CA699A8A2867960DE16AFB7BC897D6DA3D72497227B782B0BA96D26F8` |
