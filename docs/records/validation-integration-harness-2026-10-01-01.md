# Accumulated integration hidden run — 2026-10-01

One bounded batch, once. Artifacts:
`artifacts/validation/integration-harness-20261001-201017/`.

Bootstrap, Game/full dependencies, Mod and Input.Companion builds all passed with
zero warnings/errors. A fresh stage used the preceding successful Streamline
bridge from integration-compile-20261001-200040 and the retained full native/shader
bundle. New glyph notices and both ordinary mods were staged. No ZIP or installed
deployment was produced. No tests, code changes or second validation batch.

The hidden harness loaded an isolated SQLite snapshot of foggy village story.
The new mandatory AVI ReadPixels call-site guard/patch was accepted at startup;
normal scene stage dispatch remained functional with the mod-pass host installed.
Server logs list vulkanstoryinput and its AnalogInputCompanion system. This proves
integrated-server mod loading, not controller packet negotiation or movement.

The command script at world frame 90 switched FSR3 SR/FG to XeSS balanced SR/FG.
The target resized from 1706x1018 to 1280x764, with successful XeSS output at
2560x1528. Final runtime reports current camera/motion, effective XeSS SR/FG,
300 successful SR frames, 297 prepared FG frames, 843 real presents and 1133
aggregate SDK-reported presents. These mixed-provider counters do not certify
generated-image quality or display pacing.

The result reports success, hidden=true, focused=false, worldReady=true,
stagedModLoaded=true and the exact isolated Mod DLL. All three PPM/PNG pairs at
world frames 240/270/300 were written. The last PNG was inspected: terrain,
vegetation, sky, map and HUD are visible. Normal world/server shutdown and NGX
release succeeded. No client/server error or exception was found in main logs.

The earlier repeated slDLSSGSetOptions switch warning is absent with the fresh
bridge. The three known unsupported Streamline SDK hook warnings remain; shader
rewriter notices remain. DLSS active toggle coverage is not inferred from this
FSR3-to-XeSS run.

Controller input is disabled in the harness, so font appearance, remaps and analog
negotiation were not exercised. No declared custom pass was registered and no AVI
was recorded; their compilation/startup anchors are supported, actual feature
execution remains open. Visible SDL icon/focus/IME behavior, full visual parity,
other hardware/platforms and current release package acceptance remain open.
The installed user game, settings and original world were not changed or controlled.
