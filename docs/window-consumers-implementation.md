# Core Harmony window consumers

Date: 2026-09-30. Implementation turn: no builds, tests, probes, packages, or game runs.

`WindowConsumerPatches` adds an actual Harmony group for 13 original platform operations: focus/display-size queries, mouse capture get/set, window border get/set, title, size, focus, state get/set, window attributes, and direct mouse mode. The target list pins declared instance signatures and return types against the official game references. The supported assembly's file hash is already required by the bootstrap/profile. The static profile tool now checks these targets as well; this increment has not yet compiled or run.

The implementation reuses the retained SDL window-state/border/attribute operations from `VulkanClientPlatform.SdlInput.cs` at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. Prefixes pass through while the transaction gate is false. After activation they require the associated original-platform sidecar and owner thread; a missing sidecar raises an error instead of entering a GLFW body. Relative/raw input stays under SDL ownership. No original window field receives an SDL handle.

The group binds every method before mutation, records installation attempts, and removes its own Harmony owner on transaction rollback. Its process gate is registered before the first patch, while the transaction remains dormant. `WindowConsumerRoutingTests` will install these real patches against a constructor-bypassed official platform, prove original display-size behavior while dormant, prove rejection of active routing without an adapter, remove the patches, and check restored behavior. This fixture creates no native window and is unvalidated.

This is the core subset of the window-consumer group, not complete mandatory window coverage. Cursor bitmap/state handling, clipboard/XPlatform integration, screenshots/video dimensions, exit handling, frame mouse sampling, and remaining direct window consumers still require routes. Startup/window producers and the graphics API are not yet replaced. `CreateCoreGroup` is not called by the live bootstrap and must not be treated as sufficient for full transaction activation. Border initialization from settings and the session's complete resize/resource handling also remain to be connected.

Next validation: new game tests, profile-tool build, and one official metadata/profile check in a bounded batch. Native SDL execution, Harmony ordering with other mods, full rollback/session lifetime, and visible game startup remain open.

The [first bounded validation](validation-g0-window-consumers-2026-09-30-01.md) passed 15 tests, a clean profile build, and official signatures. The actual Harmony fixture proved installation/removal and dormant/missing-adapter behavior through one getter; successful SDL replacements remain unexercised.
