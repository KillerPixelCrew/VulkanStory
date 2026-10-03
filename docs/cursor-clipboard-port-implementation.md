# SDL cursor and OS-services adapter port

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages, or game runs.

`GameCursorController` retains the SDL cursor path from `VulkanClientPlatform.SdlInput.cs` at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`: scaled bitmap creation with the existing resampler, RGBA extraction, hotspot clamping, large cursor support, disposal of scaled copies only, named cursor selection, and default restoration. GUI scale remains a deferred original game-setting read. SkiaSharp is referenced from the official installation with copying disabled.

Three cursor methods join the real window-consumer prefix list, now 16 operations. Original current-cursor state is updated through a cached closed delegate to its protected setter. The official metadata verifier also checks this setter and the original OS-interface setter. No access modifier or field is changed in the game assembly.

`SdlXPlatformInterface` is an adapter migration of the retained wrapper. It forwards dialogs, codecs/AVI, disk/RAM/CPU services, and the legacy window property to the original OS implementation. Clipboard, display dimensions and focus use SDL only after transaction activation, with owner/session checks before native access. Dormant calls retain the original service. The sidecar attaches/restores this wrapper through the original protected property; the association is removed if setter attachment fails. Shutdown restores the original interface before destroying the SDL window and releases cursor/wrapper references. Startup must still replace the legacy `Window` assignment; the wrapper does not provide a fake GLFW window.

New source fixtures cover dormant OS forwarding, owner checks before native calls, and execution of the two real protected-setter delegates. The existing actual Harmony fixture will install/remove the expanded 16-operation group. Native bitmap/cursor creation, clipboard integration, wrapper attach/dispose transitions, and settings initialization are still unvalidated.

Next bounded validation: game tests, profile tool build, and one official metadata/signature pass. Further window consumers, startup/frame/shutdown producers, screenshots/video, the renderer facade, providers, and full live activation remain open.

The [first bounded validation](validation-g0-cursor-clipboard-2026-09-30-01.md) passed all 18 game tests, a clean profile build, and official bindings. Wrapper forwarding/owner checks and protected setter execution now have evidence. Native cursor/clipboard execution and full session attachment/teardown remain unverified.
