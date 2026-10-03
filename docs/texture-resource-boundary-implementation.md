# Neutral texture/framebuffer resource boundary

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

The staged resource facade now uses VulkanStory-owned texture internal/pixel formats and framebuffer attachment tokens. These retain the exact original GL-shaped values and existing method bodies, byte counts, mip behavior and attachment indexing. `GameTextureDefinitions` converts official enums while preserving unknown tokens for the retained fallback behavior. The game-enum Vulkan format mapping moves to a neutral overload in compiled `GlEnums`; raw-format entry points remain available.

The resource facade no longer imports game API/config types. Its only old platform reference was an optional logger used by presentation diagnostics; `IRenderLogger` and `GameRenderLogger` now provide that boundary. The staged FG/XeSS-FG log calls target the neutral logger with messages/arguments and feature behavior preserved. The future game session must supply the adapter; bare/headless devices can retain no logger. No vendor SDK calls or synchronization algorithms changed.

This is source-level dependency removal, not full facade acceptance. The complete device/resource/provider partials remain excluded. The next bounded batch should run backend/game tests and build the profile tool to check compiled contracts/mapping/logger adapters. That cannot certify the excluded resource methods or staged presentation logs. Typed texture/framebuffer game patches, original-resource ownership, sampler/descriptor draws, transient/feedback behavior, the full facade, startup/menu/world and providers remain open.

The [first bounded validation](validation-p0-texture-boundary-2026-09-30-01.md) passed 51 backend and 28 game cases with a clean profile build. Contracts/adapters compile; dedicated forwarding/conversion execution and the excluded facade remain unverified.
