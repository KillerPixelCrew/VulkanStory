# SDL window startup completion — 2026-10-01

Implementation turn; no builds, tests, packages, deployment or game launches.
The previous goal turn made source progress on Streamline provider transitions.

## Donor comparison and missing integration

Compared the donor VulkanClientPlatform.SdlInput window startup and window
operations with GameRenderSession, GamePlatformAdapter and WindowConsumerPatches.
Title, logical size, display size, border/state, mouse grab, focus, cursor and
native IME routes already exist. The donor intentionally avoids GLFW raw-mouse
configuration under SDL; the rewritten DirectMouse prefix preserves that behavior.

The donor's visible startup calls SetSdlWindowIcon immediately after creating
the SDL window. The new host had the pixel upload operation but no game caller.
This missing call is now migrated directly into GameRenderSession.Window.cs and
invoked before window state and Vulkan device initialization.

GamePaths.AssetsPath/gameicon.png lookup, Skia decoding, RGBA channel ordering
and optional warning-on-failure behavior are retained. Hidden captures skip
the operation. Game asset lookup stays in the integration package, while SDL
continues accepting only pixels. File provenance is recorded separately.

## Status

This startup addition is unbuilt and visible-window behavior remains unverified.
No normal user session was touched. Existing SDL input/IME/controller/resize
runtime acceptance and full renderer/provider parity remain open.
