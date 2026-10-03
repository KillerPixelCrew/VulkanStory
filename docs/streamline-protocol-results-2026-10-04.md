# Streamline protocol result checks — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
SDK-04 partially addressed; SDK-02 actual interpolation gain remains unresolved.

Current DLSS-G state has one consuming call per active application frame, through
RuntimeFrameGeneration.GenerateDlss → GetFrameGenerationStateDetails. No second
production consumer was found. The local 2.14.1 DLSS-G guide section 13 describes
two reported presents as an application frame plus one generated frame. Thus the
preceding near-one count does not demonstrate the requested 2× interpolation.
It is not justified to inflate telemetry by adding rendered frames to that count.

## Applied source repair

VulkanDevice runtime Reflex SetOptions results were discarded, ReflexSleep failure
could be ignored outside optional traces, and appliedLatencyMode was cached before
SetOptions succeeded. The new RequireStreamlineProtocol check propagates a concrete
SDK result/operation/frame error. SetVendorLatencyFrameCap checks Reflex options.
SleepVendorLatency checks token creation, rejects an empty successful token,
checks mode options before advancing the applied-mode cache, and checks sleep
before incrementing its success counter. SetFrameGenerationPresentation checks
the Reflex-Off result before continuing a XeSS pacing handoff. Missing/unbound
Streamline paths retain their existing readiness/null guards; these checks target
calls to already-bound features. A bound SDK protocol failure stops current work
instead of silently assuming success. No new retry loop was added.

The existing single pre-input sleep point, token identity, marker placement,
mode mapping, frame-cap units, native ABI, tags and rendering math are unchanged.
The change is not a proved explanation or fix for missing DLSS interpolation.

## Skill/static/runtime report

Used the Streamline Reflex/PCL skill's integration and validation checklists,
local sl_reflex.h/sl_core_api.h/sl_dlss_g.h and DLSS-G/Reflex guide contracts.
Engine profile remains the owned SDL pre-input loop, Game simulation/render hooks,
VulkanDevice recording/submission and Streamline swapchain proxy. SDK calls and
managed state publication were inspected. Static source repair applied; overall
integration PASS is not established.

Build command/result: NOT RUN in this implementation turn. Off/On/Boost run
commands: NOT RUN; no new mode-matrix inputs were produced. Verification tool,
App Called Sleep, marker timestamp/order/token runtime evidence, ReflexState
reports, toggles, VSync/limits/resize/fullscreen/threaded renderer, unsupported
GPU/missing-runtime and performance regression: NOT RUN for this source.
No tests, probes, packages, native/GPU/game run or deployment ran; no tests added.
The prior runtime used the existing staged release DLLs, not this increment.

Remaining source work includes discarded XeLL and PCL marker error paths and
direct-device provider release ordering. Remaining DLSS runtime work includes
actual added-frame proof and impact of current fresh-log unsupported hooks:
sl.common:Vulkan:CmdBindPipeline, CmdBindDescriptorSets and BeginCommandBuffer.
Those warnings were observed, not repaired or classified harmless.
Installed game/settings/save and existing build/stage binaries were unchanged.

## Current source SHA256

| File | SHA256 |
| --- | --- |
| src/VulkanStory.Render.Vulkan/VulkanDevice.cs | `975A99D2BCF2BE7FD9E6565A7EC96A3BEBAA0D9342ABE63BD66CC100AD6645FE` |
| src/VulkanStory.Render.Vulkan/VulkanDevice.FrameGeneration.cs | `9BEAF710A12B60FDCA652104AE508A83205395F06905CC168B92BEA6E800FFFA` |
