# P0 corrected full-device native validation — 2026-09-30

Status: **clean tool build and four full-facade clear/readback/present frames
passed.** One bounded batch ran once. No implementation source changed, no
command was rerun, and no CPU suite, game launch, package or deployment ran.

Both commands exited 0 within their 60-second limits:

```text
dotnet build tools/VulkanStory.Preflight/VulkanStory.Preflight.csproj -c Release
dotnet tools/VulkanStory.Preflight/bin/Release/net10.0/VulkanStory.Preflight.dll --sdl-device
```

The [summary](../artifacts/validation/p0-device-native-20260930-02/summary.json),
[build stdout](../artifacts/validation/p0-device-native-20260930-02/build.stdout.log),
[build stderr](../artifacts/validation/p0-device-native-20260930-02/build.stderr.log),
[device stdout](../artifacts/validation/p0-device-native-20260930-02/device.stdout.log)
and [device stderr](../artifacts/validation/p0-device-native-20260930-02/device.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Build | Zero warnings/errors. |
| Owner initialization | Real retained device facade initialized its managers, default target and SDL Vulkan presentation path. |
| Native default clear/readback | Four successive centre RGBA assertions passed: red `(255,0,0,255)`, green `(0,255,0,255)`, blue `(0,0,255,255)`, yellow `(255,255,0,255)`, tolerance 2. |
| Frame submission | Each decoded readback used the retained partial-submit path and each frame reached device `Present` with an empty `GetError`. |
| Lifetime | Normal device disposal completed before SDL window disposal; process exit 0, no stderr/timeout. |

The incorrect framebuffer argument from [the first batch](validation-p0-device-native-2026-09-30-01.md)
is resolved by using the native default sentinel. CPU tests were not repeated;
the preceding recorded 75-case result remains their latest evidence.

This checks the exercised full-device owner and clear/readback/presentation
lifetime. It does not certify scanout pixels, facade shader/mesh/sampler draws,
AO/temporal/world work, resize/focus/device loss, standard game startup/menus,
or vendor features. Streamline/native shader selection were explicitly disabled.
An inherited `[Optimum]` diagnostic label remains in stdout; it is source naming
cleanup, not evidence of an external runtime dependency. Full port and release
milestones remain open.
