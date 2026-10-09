# Early original-game profile registration

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages,
deployment or game runs. Tests remain deferred until integration is finished.

`RuntimeBootstrap.Install` now prepares the assembled original 1.22.7 routing
profile after installing the pinned startup observer. `VersionProfile1227` loads
the original Essentials and Survival DLLs from the official installation's
`Mods` directory and rejects an existing assembly loaded from another location.
Typed leaf guards and the startup transaction remain mandatory before routing
can become active.

Preparation happens before original `Main`, so the current `ClientProgram.Start`
invocation receives its startup/window/frame/shutdown transpilers. The platform
constructor postfix can associate the unchanged platform before its first window.
No SDL or Vulkan initialization occurs during this profile preparation.

## Deferred selected data path

`GameStartupPlan` carries a session-service factory. The first window request
resolves it once using the game's actual `GamePaths.DataPath`, after original
argument/path handling. This preserves custom data paths without duplicating
the game's command-line parser or reading settings from the default path too early.

If normalized settings disable VulkanStory, prepared routing is removed and the
original window body continues. If enabled, the existing transaction creates the
real session and commits routing only after its owners are ready. Existing
pre-commit preparation failure cleanup and original-startup fallback remain.

## Window/source reconciliation

Inspection reconciled direct original platform window accesses with the owned
window, startup, frame, framebuffer/post, query/capture and cursor routes. Original
native focus callbacks are not subscribed by the replaced platform Start body;
SDL dispatch uses the existing original focus/event bindings. Framebuffer/debug
and scissor properties with raw GL bodies are already routed by their typed groups.
This is source path evidence, not runtime proof of complete graphics coverage.

## Remaining acceptance and implementation

The newly composed/registered source has not compiled or installed Harmony in a
running game. The installed older B0 payload is unchanged. No SDL first window,
menu/world frame, enabled provider or disable/fallback case is accepted here.

Per-frame motion coverage, analog/hint consumers, shader override compatibility,
native/provider runtime packaging and ordinary mod settings/world attachment
remain unfinished. Complete port acceptance remains open.
