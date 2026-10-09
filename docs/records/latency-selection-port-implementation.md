# Neutral latency selection and render-stage boundary

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

The staged device now receives an immutable `RendererLatencySelection` instead of reading `OptimumConfig.LowLatencyMode` and `EffectiveFrameGeneration`. Its Off/On/Boost numeric mapping and active DLSS/FSR 3/XeSS FG dependency remain the existing policy; the inherited default stays On. The selection reference is replaced atomically. The future game/mod host must update the effective provider state and selection before the next frame's pacing/input boundary. Native availability gates, SDK options/sleep/marker calls and limiter ownership are unchanged.

`ILatencyStageListener` moves from the old platform namespace into VulkanStory contracts. The device's callback remains `NoteRenderStageStarted`. Unused game API/config imports are removed from the main and binding partials. The complete partials still do not compile in the active project; these source changes do not activate a renderer or latency provider.

The `integrate-reflex-pcl` skill's integration/validation checklists were consulted, alongside the retained latency code and local Streamline 2.14.1 guide/headers under `D:/Coding/VulkanStory/_ref/streamline`. The SDK mode tags remain Off 0, Low Latency 1 and Boost 2. Seven source contract fixtures preserve selection and active-FG dependency combinations; they have not run.

## Reflex/PCL evidence report

| Item | Current result |
| --- | --- |
| Summary | FAIL for complete Reflex/PCL integration acceptance: not yet proved. No failed runtime trial is claimed. Source dependency adaptation is implemented. |
| Files/functions changed | Contracts selection/listener; `VulkanDevice.LatencySelection`, `DesiredLatencyMode`, listener declaration; unused binding imports; selection fixtures. |
| Build command/result | Planned next validation: `dotnet test tests/VulkanStory.Contracts.Tests/VulkanStory.Contracts.Tests.csproj -c Release`. Not run this turn; full device remains excluded. |
| Off/On/Boost runtime commands | No new-host executable mode command exists yet. Mod settings/startup integration must be connected before these live cases can run. |
| Runtime dependencies | Existing optional Streamline bridge/path remains. `sl.interposer.dll`, `sl.common.dll`, `sl.reflex.dll`, `sl.pcl.dll` package presence/loading not inventoried or tested in this turn. |
| Static check | Source has no live Optimum config read in the edited main/binding boundary; numeric policy matches the retained implementation and SDK tags. No complete integration audit. |
| Runtime / verification tools / App Called Sleep | Not run or verified. |
| Marker/frame-token and order evidence | Retained calls unchanged; no new-host runtime evidence. Required input/simulation/render/present order remains an acceptance gate. |
| ReflexState / toggle evidence | No new-host availability/latency report or runtime toggle trial. |
| VSync, limiter, resize/fullscreen, threaded renderer | No new-host matrix or effective present-state evidence. |
| Unsupported GPU / runtime missing | No new-host trial. Existing fallback code is retained, not accepted by this edit. |
| Performance regression | Not measured. |
| Remaining fixes | Connect game/mod settings and effective FG state, compile the full facade/provider owner, bind stage/input/present callbacks, then run the documented mode/runtime matrix. |

The skill's build/runtime checks are deferred by the repository's implementation-versus-validation turn rule. The next bounded validation should run contracts/backend/game regressions and build the tools once; that alone cannot close live Reflex/PCL acceptance. Full device, game graphics, providers and release remain open.

Subsequent [bounded validation](validation-p0-latency-selection-2026-09-30-01.md) passed 10 contracts, 54 backend and 28 game cases and a clean tool build. Selection/FG policy fixtures have evidence; excluded device wiring and all live Reflex/PCL acceptance items above remain unverified.
