# Motion window and sky/cloud reactive pass

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Motion` adds the retained primary-only motion-write window,
including temporal/jitter guards, motion-only selection, replacement blending
and normal world mask restoration. Other targets and late unjittered stages
cannot open this window.

The retained sky/cloud pass uses current jittered inverse view-projection and
previous unjittered view-projection, transparent revealage and cloud reactive
coverage 1. It writes only the published motion attachment, tests far depth with
LessOrEqual and writes no depth. The original shader sources are copied unchanged
and owned by the adapter, with their motion-location defines and reload handling.

The original scene-loop postfix invokes sky motion only when the transparent
pass ran, after AfterOIT renderers and before post processing. Normal blend,
depth-write/function and cull state are restored after the native pass. Failed
prerequisites do not assert a successful draw or dense motion coverage.

Per-model/bone/warp/particle histories and liquid velocity redraw, compiled
motion shader modes and remaining host/scene integration are still open. Dense
motion validity remains false until the complete producer chain is connected.
No new code or guard has been compiled or run, and no motion/provider result is
accepted from this increment.
