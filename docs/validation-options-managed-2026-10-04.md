# Current Game/Mod compilation — 2026-10-04

Workspace: `D:/Coding/VulkanStory-Rewrite`, DIRECT mode, validation turn.
No Git repository is present. One bounded batch ran each command once:

```powershell
dotnet build src/VulkanStory.Game/VulkanStory.Game.csproj -c Release --nologo
dotnet build src/VulkanStory.Mod/VulkanStory.Mod.csproj -c Release --nologo
```

Official references came from the existing `Directory.Build.local.props`:
`C:/Users/N1GHT/AppData/Roaming/Vintagestory`. Game built its Contracts, Input,
SDL and Vulkan renderer dependencies. Both commands exited 0. Game reported
zero errors and one CS8600 warning at `GameRenderSession.Scenarios.cs:503`
(`TryGetValue` into a nonnullable `ScenarioObservedField`). Mod reported zero
warnings/errors. No correction or second batch ran in this validation turn.

Complete logs and exits are in
[the batch directory](../artifacts/validation/options-managed-20261004-001125/):
`VulkanStory.Game.log`, `VulkanStory.Mod.log`, `results.json`.

The four UI source hashes match
[the implementation record](options-tab-integration-2026-10-04.md).
Scenario source SHA256:
`559151B9927628908280A5C3921FD21B99218C374D3086A4B0F0A01492AAD01E`.

| Release output | SHA256 |
| --- | --- |
| `src/VulkanStory.Game/bin/Release/net10.0/VulkanStory.Game.dll` | `2827D19F4AE3B98CA0C2FB2122A2F05806EBABFF6B1BD7183CD4C4F0BC018DD2` |
| `src/VulkanStory.Mod/bin/Release/net10.0/VulkanStory.Mod.dll` | `EEFBBCFCB1EDFF4AC0DC5A71201A5C9DDBAF640CC5D3E0777030ED821DB100C6` |
| `src/VulkanStory.Render.Vulkan/bin/Release/net10.0/VulkanStory.Render.Vulkan.dll` | `95AEDA49456DCE801F4D05A1BA6F1D60590DCE15FEA1DD0469DDAE79DE91B059` |

This proves managed compilation of the current source, including the Options
increment, scenario core, viewport and retirement changes. It does not prove
Harmony target matching, GUI navigation/scrolling/focus, settings application,
world lifecycle, provider execution, generated output, native ABI compatibility
or hardware behavior. No tests, probes, shader/native builds, packaging,
deployment or game run occurred. The October-1 ZIP, installed game, settings and
original save remain unchanged. UI-01 and every runtime/hardware gate stay open.
