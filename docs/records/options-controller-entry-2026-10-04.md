# Options controller entry — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
UI-01 remains open. No builds, tests, probes, game runs, packages or deployment ran.

The shared Device page now offers **Save and open controller settings**. It submits
the complete draft through a framework-only deferred bridge; Controllers must be
enabled in that draft and a world must be loaded. Immediate persistence/validation
errors keep the draft/editor. While accepted work is pending, duplicate entry,
ordinary Save and draft mutation are blocked; Cancel can cancel opening. The UI
states explicitly that already-saved settings remain applied/persisted.

The bridge saves then queues application. Provider application failures report
their error and still propagate, preserving the existing failed-drain boundary.
Opening is queued at the following input boundary so controller enablement and
device polling can run first. Both stages verify the original world/session;
opening also verifies the live editor and effective controller enablement. Success
requires the actual opened dialog associated with that world. A missing device or
open failure is reported to the editor. The old chat-open bridge is preserved.

Embedded in-game Options attaches a weak child-close return handler, closes the
pause Options dialog, then explicitly focuses the controller child. If parent
close fails, it cancels that handoff and reports the failure. Child close reopens
the original parent at Graphics only while the same active world still owns that
controller dialog. SdlGamepadInput clears ownership before world/disposal closure,
so those closures cannot reopen an old world's Options. Standalone editor hosts
close only on actual-open acknowledgement. All deferred editor references are
weak; same-owner recomposition preserves the pending editing lifetime. Main-menu
controller entry reports the loaded-world requirement rather than claiming a
controller panel opened without an active game.

Source inspection covered full-draft persistence/application, deferred queue-count
boundary, first controller poll, failure propagation, cancellation, pending action
guards, actual dialog receipts, parent input capture/focus, child-close return and
world/disposal ownership. The original GuiDialog Focus/TryOpen/TryClose/OnClosed
contracts came from the saved official 1.22.7 API profile. No tests were added.
Compilation, controller hardware/profile discovery, visual layout, focus/input,
save errors and actual leave/rejoin/return behavior remain unverified. Existing
build/stage binaries predate this increment. Installed game/settings/save unchanged.

## Source SHA256

| File | SHA256 |
| --- | --- |
| src/Shared/RendererSettingsPanel.cs | `58DE267704CC50A200A79C3424F90E4A6B7F6755194D01CCEDF277B534373140` |
| src/VulkanStory.Game/OptionsSettingsOwner.cs | `28A12992E599E09C7F692C8EECD27061B1C02CAF81BD302EEEF5520912AC7307` |
| src/VulkanStory.Game/RuntimeControlBridge.cs | `972317F357E3EFC25DDF94E665E19B5C2A7DFEFCAE9E401C3FE951E635EFF1DE` |
| src/VulkanStory.Game/GameRenderSession.Controllers.cs | `6A5ED34D87FD99D179E5DF1575ACE91F4F99D6982A203AD0F03D2323D1F98971` |
| src/VulkanStory.Game/Input/SdlGamepadInput.cs | `BBFFDB93EA439B66124E831BC88A7B37F1121E6910688E009D813AFE886A7F21` |
| src/VulkanStory.Game/RendererSettingsScreen.cs | `5FA2494E4C01CA2BC96DC63A2F3FEAD9E54907F9A62E7E8EB5393764CEB5F4AD` |
| src/VulkanStory.Mod/RendererSettingsDialog.cs | `696B75C8AF07DA16C8DC08A397E8F222494AFAFB19CB6BEFC6BFE46B3B45E7FB` |
