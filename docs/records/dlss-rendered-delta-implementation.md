# NGX rendered-frame delta input — 2026-10-01

Implementation only; no builds, tests, probes, captures or game launches.
The preceding offline analysis localized the pattern to raw SR output before
final composition/FG. The DLSS-SR skill and local NGX Vulkan SDK helper/header
were used for contract review; the host uses direct NGX for SR, not Streamline SR.

Reviewed the retained pixel-space MV scale, positive-height offscreen viewport,
projection jitter mapping and reset path. No evidence supported changing jitter
signs/scales. Existing scale (1,1), unjittered motion convention and exposure inputs
remain. The game temporal snapshot already carries rendered DeltaTimeMs, but
NgxDlssEvaluation.FromTemporalFrame omitted it and Evaluate did not set the SDK
FrameTimeDeltaInMsec parameter. The local nvsdk_ngx_helpers_vk.h includes and writes
that optional input, with a comment describing its temporal filtering role.

Added the neutral delta field/mapping and exact SDK parameter name. Each evaluate
writes a finite positive rendered-frame delta, or 0 for an unused/invalid input,
preventing a long-lived parameter block from retaining an old value. The duration
belongs to the rendered frame, not generated-present timing. Existing diagnostic
samples now record the same renderedDeltaTimeMs. No native ABI/export change and
no shader/math/algorithm rewrite.

This closes an input-wiring omission carried through the donor migration; it is
not a proved explanation or fix for the sky pattern. Source is unbuilt/unrun.
Motion/depth artifact inspection and visual acceptance remain open, along with
the complete renderer/provider/SDL goal. New tests remain deferred.
