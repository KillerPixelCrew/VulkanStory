# Super-resolution provider compile group

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

The retained DLSS host/shared provider adapter, FSR 3.1 backend, XeSS native/backend and NGX/FidelityFX texture adapters join backend compilation. `IUpscalerDevice` replaces their concrete full-device dependency with the actual operations they already call: texture ownership, feature creation/evaluation, retirement and teardown drain. The staged device implements explicit forwarding to its retained DLSS/FSR/XeSS methods. Provider evaluation, plans, SDK requirements, formats, temporal inputs and resource algorithms are unchanged.

FSR 4 remains staged because its Vulkan/DX12 shared-image owner is not extracted yet. Its common bring-up signature follows the new interface, while its existing concrete interop owner remains the real device. That boundary is still pending; no FSR 4 feature is removed from the objective. The full renderer facade/forwarders likewise remain excluded, so successful provider compilation will not establish their execution.

The `integrate-dlss-sr` checklist and local Streamline 2.14.1 DLSS documentation were consulted alongside the retained NGX seam and temporal contracts. This port continues the existing NGX SR route; it does not migrate SR to another SDK or change motion/jitter/resolution/composition conventions. A source fixture checks that unrequested DLSS preparation returns before native loading and leaves selection untouched. It has not run.

## SR evidence report

- Files changed: provider host interface, shared provider contract and DLSS/FSR 3/XeSS provider types, staged device forwarders/base declaration, compile list, FSR 4 staged signature and disabled-selection fixture.
- Planned next build/test: Release backend tests and `dotnet build tools/VulkanStory.Preflight/VulkanStory.Preflight.csproj -c Release`, once in a bounded validation turn. No build this turn.
- DLSS disabled/enabled game commands: no new-host runtime command yet; actual game/provider activation must be connected first.
- Printed internal/output/present extents, MSE/bilinear comparisons, regional pixels and motion-vector cases: no live SR run or capture in this turn.
- Fallback: existing requested/active distinction and native failure paths retained; the new disabled-selection case is unvalidated.
- Input binding/frame delta/reset/MV producer and UI order: source evaluation remains unchanged; new-host real-scene acceptance is still missing.
- Runtime DLL/package availability, feature initialization/evaluation, switching/resize/shutdown and cross-hardware coverage: open.

The skill's runtime build/disabled-enabled checks are deferred under the repository's implementation/validation rule. Complete device/provider owner compilation, scene/temporal producers, all vendor SR/FG/latency paths, startup/window/menu/world and release acceptance remain open.

The [first bounded validation](validation-p0-sr-provider-2026-09-30-01.md) passed 68 backend cases and a clean tool build. The compiled provider group and disabled-DLSS early-out have evidence; real device forwarding, FSR 4 interop, enabled SDK evaluation and all live SR quality/integration gates remain open.
