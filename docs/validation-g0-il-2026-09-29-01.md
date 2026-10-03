# G0 official startup IL inventory — first batch, 2026-09-29

Status: **focused scanner tests, tool build, and exact-profile IL inventory passed.** This was one bounded validation batch. No source was changed and no command was rerun; the game was not launched.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Bootstrap.Tests`, Release build of `tools/VulkanStory.GameProfile`, then one timed static inspection of the existing official game installation after checking every hash in `profiles/vs-1.22.7-win-x64.json`. The [summary](../artifacts/validation/g0-il-20260929-01/summary.json), [test log](../artifacts/validation/g0-il-20260929-01/test.log), [TRX](../artifacts/validation/g0-il-20260929-01/bootstrap.trx), [build log](../artifacts/validation/g0-il-20260929-01/build.log), [inventory stdout](../artifacts/validation/g0-il-20260929-01/inventory.stdout.log), [inventory stderr](../artifacts/validation/g0-il-20260929-01/inventory.stderr.log), and complete [startup IL JSON](../artifacts/validation/g0-il-20260929-01/startup-il.json) are retained.

| Gate | Recorded result |
| --- | --- |
| Bootstrap tests | Built in Release; TRX records 10/10 executed and passed, zero failed or skipped. |
| Game-profile tool build | Succeeded with 0 warnings and 0 errors. |
| Official 1.22.7 profile check and IL inventory | Exited 0 without timeout or stderr. All listed file hashes and all seven exact startup signatures matched. |

| Target | IL bytes | Member operands | Window-related matches* |
| --- | ---: | ---: | ---: |
| `ClientProgram.Main` | 51 | 8 | 0 |
| `ClientProgram.Start` | 1363 | 178 | 55 |
| `ClientPlatformWindows` constructor | 311 | 51 | 27 |
| `ClientProgram.AttemptToOpenWindow` | 322 | 23 | 5 |
| `GameWindowNative` constructor | 224 | 20 | 7 |
| `ScreenManager.Start` | 382 | 67 | 37 |
| `ClientPlatformWindows.Start` | 426 | 71 | 61 |

*The tool's summary counts member names containing `Window`, `GLFW`, or `ScreenManager`; these are discovery counts, not a certified list of mandatory patch sites. The JSON preserves every member operand and IL offset for exact review. In particular, `ClientProgram.Start` has direct calls to `AttemptToOpenWindow`, `GameWindow.Run`, centering, size reads, GLFW callback/iconification, and native-window disposal. `ClientPlatformWindows.Start` reads its `window` field 15 times and registers OpenTK window event handlers. A constructor-only substitution would leave these routes in place.

This is static binding evidence for the checked game version. It does not install Harmony routing, create SDL, render game frames, prove normal-shortcut activation with the revised resolver, or establish compatibility with another game version. The next implementation should turn reviewed operands into checked patch anchors and a transactional startup/window adapter, accounting for the direct field reads and cleanup paths before enabling Vulkan routing.
