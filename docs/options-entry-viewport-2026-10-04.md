# Options entry viewport placement — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
UI-01 remains open. Original Graphics Options uses fixed 950×740 content; centered
composition can put its VulkanStory entry above or left of the usable viewport
on a smaller window/high GUI scale. This affects reaching the responsive custom
panel even when that panel itself would fit.

OptionsSettingsOwner now registers a weak OnComposed handler on its original
Graphics landing composer. After the original host placement runs, it adjusts
only this composer's root offsets to keep the entry visible, preferring to expose
the header top and then keeping the entry within the bottom edge. Main-menu
sidebar width is reserved; when it leaves less room than the entry requires, the
owner temporarily hides it. Custom panel sizing then uses the actual shown-
sidebar state. Clear/navigation/host close restores the prior ShowMainMenu value.
Repeated composition resets owned vertical correction before recalculating it;
stale/inactive callbacks cannot reposition another displayed editing host.

Shared game tab/header bounds are not mutated. The saved official 1.22.7 GUI
source shows composer texture rendering at root renderX/renderY and child render/
hit coordinates inheriting root offsets. Source inspection covered callback order,
root/child coordinate rules, weak/generation guards and sidebar restoration.
No build, syntax probe, tests, game run, package or deployment ran. No tests added.
Prepared visible scripts/stage were not changed and still await prior approval.

This is entry reachability source work, not complete responsive vanilla Options
or GUI acceptance. Other vanilla controls may still exceed a small viewport, and
the custom panel retains its minimum usable dimensions. Visible fit, clipping,
click/input coordinates, repeated resize/GUI-scale changes, both host contexts
and close/failure restoration remain unverified. Installed game/settings/save
unchanged. Current build/stage predates this source.

Source: src/VulkanStory.Game/OptionsSettingsOwner.cs.
SHA256: `C8AC5A71C9CFD249B978AD7650E0E19B876907F719C346FE659429164577505A`.
