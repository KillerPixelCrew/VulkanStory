# Provider release build — 2026-10-04

DIRECT validation turn in `D:/Coding/VulkanStory-Rewrite` (no Git repository).
One bounded batch ran all four operations once; all exited 0:

| Operation | Result |
| --- | --- |
| Game Release build and dependencies | 0 errors; one CS8600 harness warning at GameRenderSession.Scenarios.cs:503 |
| Mod Release build | 0 warnings/errors |
| native/fsr3/build.ps1 | Built; eight warnings: seven function-pointer casts and one enum conditional |
| native/streamline/build.ps1 | Built; no compiler warnings emitted |

Official game references use the existing Directory.Build.local.props.
FSR3 headers: `D:/Coding/VulkanStory/_ref/fsr-vulkan`; Streamline release headers:
`D:/Coding/VulkanStory-Rewrite/sdk/streamline-2.14.1`; Vulkan headers:
`C:/VulkanSDK/1.4.357.0`. Native scripts used their existing g++ command/options.
The FSR3 directory supplies SDK headers only; no donor assemblies/binaries were copied.

Complete logs, operation exits, bridge hashes and objdump PE listings are in
[provider-release-20261004-001833](../../artifacts/validation/provider-release-20261004-001833).
The offline FSR3 export table contains both required new symbols:
VulkanStoryFsr3SwapchainPrepareDestroy and VulkanStoryFsr3SwapchainDestroyChainChecked.
Streamline's create/destroy exports remain present. This is PE symbol evidence,
not runtime loading, ABI execution or SDK behavior.

| Output | SHA256 |
| --- | --- |
| `src/VulkanStory.Game/bin/Release/net10.0/VulkanStory.Game.dll` | `70CDE94AA831593C1A3407A974267F87961E91FA45789F94ADFC4BFB022DC617` |
| `src/VulkanStory.Mod/bin/Release/net10.0/VulkanStory.Mod.dll` | `EEFBBCFCB1EDFF4AC0DC5A71201A5C9DDBAF640CC5D3E0777030ED821DB100C6` |
| `src/VulkanStory.Render.Vulkan/bin/Release/net10.0/VulkanStory.Render.Vulkan.dll` | `0597734AE94A3BDAAF8E95F42974A60E3C649DEE5D7215B865298B689AC9A51D` |
| `bridges/VulkanStoryFsr3.dll` in the batch | `58DE57E7165B20F069DF3FDC7B3634CE09CF9A13F00477E9B7C88737B48252CB` |
| `bridges/VulkanStoryStreamline.dll` in the batch | `EE2938C64EE2DD13C1819442DD666726C5306C54CEA40A9F082E0B7654347D75` |

The compiled increments are the [Options integration](options-tab-integration-2026-10-04.md),
[FSR3 release repair](fsr3-swapchain-release-2026-10-04.md) and
[session/device and Streamline cache repair](session-provider-release-2026-10-04.md).
No source correction or second validation batch ran. No tests, GPU/game runs,
shader builds, packaging or deployment occurred. Native exports were inspected
offline only. The October-1 ZIP, installed game/settings and original save remain
unchanged. Fresh bridges are standalone development outputs, not a delivered package.
SDK-03 failure-path execution, SDK-01/02 all-vendor SR/FG behavior and UI-01 runtime
navigation/visual acceptance remain open.
