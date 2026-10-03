# Isolated world Options navigation — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
Visible approval remains pending; prepared launchers/stage are unchanged.

Added the harness-only .vulkanstory diagnostic-options command. Its framework
delegate is installed only with HeadlessHarnessOptions.Enabled and removed with
the other runtime bridges. Ordinary clients cannot invoke its runtime operation.
The command queues work on the existing pre-input control boundary and preserves
the captured world identity. No controller/touch profile is enabled or fabricated.

The session locates the actual original world pause dialog from LoadedGuis,
opens it, invokes its original OpenSettingsMenu, obtains the actual VulkanStory
toggle and dispatches MouseDown/MouseUp at its measured center through the original
landing GuiComposer. It checks entry center visibility, changed composer identity,
composition completion and the owned VulkanStory dialog name. It never directly
constructs the settings panel or forces a callback. The receipt records original
host/context, entry coordinates, displayed composer and window facts in
frames/options-diagnostic.json. A navigation failure marks the harness run failed.

The command fixture is scripts/dev/scenarios/options-world.commands.txt. A later
bounded build/stage/hidden run can pass it via -Commands and capture after its
CommandFrame. Acceptance must inspect the receipt and actual PNG; normal capture
success alone does not prove Options navigation. This covers the loaded-world
Image page, not main-menu context, other pages, persistence, controller/human input
or generated-frame quality. Existing physical input/GUI acceptance remains open.

Source inspection used existing official pause-composite fields/methods and the
same MouseEvent signature already used by GameInputBridge. No build, tests,
syntax probes, game run, package or deployment ran. No regression tests added.
Current build/stage predates this source. Installed game/settings/save unchanged.

| File | SHA256 |
| --- | --- |
| src/VulkanStory.Game/RuntimeControlBridge.cs | `596794549944EC8970157312C72B98DC13965219D4DFF0F8AFB24C6A96FF0AC9` |
| src/VulkanStory.Mod/VulkanStoryModSystem.cs | `CF89E8A1DF0BF42E506201E0CB197D2F289B6D3E69321E3336A33D511CFD9C86` |
| src/VulkanStory.Game/GameRenderSession.OptionsDiagnostic.cs | `2E7AEB8F4AB153E5F4BB607C89D041AD1E7C0C623E643B6F37589992B77CDCA8` |

The subsequent build found the missing Vintagestory.API.Common import for
EnumMouseButton. A later implementation turn added that import, matching the
existing GameInputBridge. Corrected OptionsDiagnostic source SHA256:
`6AB1EC39F45ADEF135915C99196889B054AFF9985D599E5D15139F47C3B3701B`.
No build/run accompanied the correction; earlier failed evidence remains valid
for its original source. Corrected compilation and capture are still pending.
