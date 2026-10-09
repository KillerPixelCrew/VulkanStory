# Renderer settings dialog

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The ordinary API-only mod now opens `RendererSettingsDialog` with `.vulkanstory`
or `.vulkanstory settings`. Four pages expose image/upscaler quality, generation/
latency, AO/effects and input/device choices. Renderer enablement, native shader
and Streamline options are marked as requiring restart. Quality choices account
for XeSS's additional presets; switching away resets unsupported quality choices.

The dialog holds a JSON draft, preserving settings not exposed on these pages.
Page controls update only the draft. Save uses the existing validated/persistent
bridge and queues live changes at the owner boundary; Cancel does not apply them.
Dialog close/disposal stays with the ordinary game GUI. No second renderer or
native assembly reference is added to the ordinary mod.

Controller enablement now selects mapping versus release after the single event
pump. Touch enablement gates gesture dispatch/ticking and releases active touch
state when disabled. Device/window restart semantics remain unchanged.

The dialog is available through the in-world client command; a later shared-panel
increment also adds active-profile main-menu access. Debug/FPS presentation controls
and effective provider-state display remain open. Visible layout, interaction,
save/cancel, switching and input
release behavior remain unverified. Runtime packaging and live port acceptance
are still unfinished.
