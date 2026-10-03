# Options fixed footer and header fit — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
The last actual Options capture showed the VulkanStory header truncated and
Save/Cancel initially clipped inside the scroll region. UI-01 remains open.

Shared RendererSettingsPanel now exposes AddFooter separately. Embedded Options
omits the inline footer, reserves 40 more layout units below the viewport, and
adds the same guarded Save/Cancel callbacks after EndClip under the background.
Scrolling therefore moves settings/messages only, while footer buttons retain
fixed hit/render coordinates. Standalone panel composition keeps its existing
inline footer by default. Pending-controller Save blocking and Cancel semantics
are preserved; callback guards still require the displayed editing composer.

Both the original Graphics landing entry and embedded VulkanStory header use
18-point button text within their existing 145-unit bounds. No shared vanilla
header bounds or provider/settings behavior changed.

Source inspection covered clip-scope balance, viewport/footer separation, shared
and standalone callers, footer mutation guards and minimum height geometry. No
build, tests, syntax probes, client runs, packages or deployment ran; no tests
added. Actual caption fit, pinned footer rendering/hit testing, scroll behavior,
Save/Cancel application/return and main-menu acceptance remain unverified.
Current build/stage predates this source. Prepared visible launchers/stage and
installed game/settings/save unchanged; visible approval remains pending.

| Source | SHA256 |
| --- | --- |
| src/Shared/RendererSettingsPanel.cs | `1E3B5DDC63E49B4FCE16203D95A3D606E34E9675B09F690384711D850F178B1F` |
| src/VulkanStory.Game/OptionsSettingsOwner.cs | `9B8FB5F4B43A96B2D17EDA8663BCC45EE9CBA7485DCEC6C0788692FA3B5DDAB8` |
