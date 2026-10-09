# Retained controller source migration

Updated 2026-10-01. Source implementation only; no build, test, probe, package or
game run. Tests remain deferred until integration is complete.

The retained SDL gamepad mapper and its support files now live in the Game
project's `Input` namespace: profiles/persistence, button bindings/capture,
sneak-toggle state, cursor navigation, composer targets, glyphs, settings dialog,
keyboard content and both menu/in-game keyboard overlays. The immutable hint
snapshot implementation is also mod-owned.

Sources come from `porting/old-platform` and the retained controller hint contract
at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. Changes are namespace,
UI/log names and host boundaries. Mapping, profile validation/persistence, device
selection, gyro, damage/trigger haptics and keyboard/navigation algorithms remain.

`IControllerPlatformHost` replaces the old Vulkan platform subclass dependency.
It supplies the original platform for existing event handlers, actual SDL window/
cursor/focus state, mod-owned movement state and source-aware key/mouse injection.
Analog movement checks an explicit negotiated host capability instead of an
injected `ClientMain` field. No Optimum assembly or added game field is required.

The mapper has separate native-update and post-pump mapping entry points so the
process adapter can retain sole SDL queue ownership. Its original combined Poll
entry is retained for a caller that owns the queue. The session must choose one
path; the new post-pump entry does not drain the SDL queue again.

## Remaining integration

The concrete host/session attachment, controller phases in the process event loop,
GUI/IME keyboard target callbacks, movement negotiation/companion, hint consumer
hooks and native controller mapping database delivery remain unfinished. These
sources are not yet active through the incomplete startup profile. No controller
input, profile reload, gyro/haptics or keyboard overlay is accepted in the new host.
