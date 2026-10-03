# Lifecycle correction startup batch — 2026-10-01

One bounded build/stage/deploy/launch batch. No tests, source fixes or reruns.

Game Release build passed with zero errors/warnings. Staging and owned update
passed. PID 37308 recorded runtime.installed, then **runtime.active** at the first
SDL-owned window, then runtime.stopped with device drain before SDL teardown.
The preceding dormant setup/cleanup guard failure is no longer reported.

The first menu frame failed with the asset-stage exception supplied by the user:
`Mods must not get assets before AssetsLoaded stage`.
The game handled this as a crash and exited 0; exit 0 is not menu acceptance.
No successfully presented menu/world/provider frame has been established.

Complete logs, trace, crash copy, backup and stage:
`artifacts/validation/runtime-startup-20261001-143350/`.
The installed payload contains this build; downloadable archives were not updated.

## Diagnosis from existing source

OwnedShaderSource in GameGraphicsAdapter.UiSeparation uses AssetManager.TryGet
for embedded UI/native-pass shader replacements. Original AssetManager.TryGet
explicitly throws before AssetsLoaded; TryGet_BaseAssets is its underlying lookup
used by the game's early/base asset path. ShaderOverridePolicy already uses the
latter at the registered source boundary. Change the owned-shader lookup to that
base path, keeping embedded fallback and selected overrides, without changing
the normal mod asset-stage guard.

UI finalizer returns the original error after aborting its scope, consistent with
the reported ScreenManager.Render_Patch1 stack. No second launch or source repair
occurred in this validation turn. Next is the concrete early owned-shader access
correction; menu/world/SDK feature operation remain open.

## Following early asset source correction — 2026-10-01

Owned shader lookup and include-override inspection now use TryGet_BaseAssets,
the original early/base lookup. It consults the same selected asset dictionary
that TryGet uses after its mod-stage guard, retaining loaded override selection
and embedded owned-shader fallback. Override discovery now reads asset metadata
without eagerly loading every shader/include. The game's normal mod API guard
is unchanged. No rendering pass or required input was removed.

Implementation only: no build, tests, package, deployment or game run. These fixes
remain uncompiled; installed build still has the recorded first-frame failure.
