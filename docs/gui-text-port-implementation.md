# GUI-aware SDL text input port

Date: 2026-09-30. Implementation turn: no builds, tests, probes, packages, or game runs.

`SdlGuiTextInput` adapts the existing focused-field search and native IME coordination from `VulkanClientPlatform.SdlInput.cs` at migration baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. It follows the current menu composer, keyboard-receiving game dialogs, and nested focused containers. Controller keyboard screen/dialog targets enter through explicit resolver callbacks, retaining that extension point while their concrete overlays remain to be ported.

The original arithmetic for logical IME rectangles and scrolled caret positioning is retained. The coordinator clears native composition when switching fields, updates the candidate area only when changed, starts/stops SDL text input, and accepts commits only when the active field still matches the current focused field. Private current-screen, running-game, dialog-list, caret, and horizontal-offset fields use cached typed Harmony accessors instead of repeated reflection. The official static profile verifier now checks these five additional field contracts; that new check has not yet run.

`GamePlatformCallbacks.WithTextInput` attaches the concrete coordinator's callbacks. A target revision clears the sidecar's composition string after field transitions, preserving the original Escape behavior and avoiding stale preedit state. Preedit remains native; only committed text reaches game handlers. All coordination stays on the SDL owner thread. No bootstrap caller, GUI object construction, or live patch activation is added here.

Next validation must compile the coordinator and verify the new original-field metadata, retaining input regressions. Native IME transitions, stale queued commits, controller overlay targets, and actual menu/world focus require subsequent integration fixtures/live checks. Renderer startup, graphics route, provider attachment, and full G0/G3 acceptance remain open.

The [first bounded validation](validation-g0-gui-text-2026-09-30-01.md) passed a clean build, 14 existing input regressions, and the five new GUI field metadata checks. Coordinator behavior, actual GUI accessor execution, and native IME remain unverified.
