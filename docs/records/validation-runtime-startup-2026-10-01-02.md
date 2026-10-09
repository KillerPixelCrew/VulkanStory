# Corrected profile deployment/startup — 2026-10-01

One bounded batch after the mechanical-name source correction. No tests, source
fixes, reruns or native/shader rebuilds occurred.

| Step | Result |
| --- | --- |
| Bootstrap and Game Release builds | Passed, both zero errors/warnings |
| Full staging and owned update | Passed; backed-up current payload replaced |
| Full profile preparation | Passed; runtime.installed recorded before Main/window request |
| SDL/Vulkan session creation | Failed: dormant routing guard used during initialization |
| Process | PID 35204 exited 0 through the game's crash handling; exit 0 is not a successful startup |

Complete logs, trace, stage, backup, result JSON and copied client-crash.log:
`artifacts/validation/runtime-startup-20261001-141611/`.
No archive was regenerated; the earlier downloadable ZIP predates this correction.

## Diagnosis

The full original profile now validates and installs. Commit deliberately calls
the session factory while routing is in PreparingSession, enabling game routing
only after creation succeeds. ConfigureShaderOverrides calls RequireDevice,
which rejects dormant routing, so initialization fails before commit. Cleanup's
ReleasePreviousAnimations also calls the same active-routing accessor and raises
the second exception in the aggregate. The user supplied the matching crash log.

Correct setup/resource teardown to use an owner-thread-checked device association
without requiring active game routing. Keep draw/patch dispatch gated by active
routing. Review all preparation/cleanup calls so the failure path releases every
created resource even if one cleanup action fails; do not enable routing early.

Captured stderr shows signed NVIDIA plugins loading and PCL/Reflex/DLSS-G setup,
including Reflex availability. It also records Streamline warnings about disabled
debug-utils and unsupported Vulkan CmdBindPipeline/CmdBindDescriptorSets/
BeginCommandBuffer hooks. These are not proof of FG execution or correctness and
remain follow-up runtime items. No menu/world/rendered provider frame is accepted.

The sampled process/window values were taken after exit; they do not prove no
SDL window was briefly created. The installed payload still has this failure.
No fix or second launch was performed in this validation turn. Next is a concrete
implementation correction of lifecycle device access and failure cleanup.

## Following lifecycle source correction — 2026-10-01

Added a private owner-thread/device association accessor for lifecycle work.
Shader-policy setup uses it before commit; RequireDevice still rejects dormant
game draw/resource dispatch. Previous-animation bulk teardown deletes owned
native UBO handles and marks the original buffer disposed through its protected
setter, without invoking an inactive GL-routed Dispose. Liquid motion has a
separate native teardown path; normal reload remains active-routing guarded.

Session cleanup now attempts controller/IME, FG, AO, OIT, previous-animation,
liquid and SR teardown independently and collects failures. GPU disposal is
attempted while SDL is alive; observers/graphics detach only after it succeeds.
Failed initialization and normal session disposal share this owned cleanup.
Input/window detachment can finish after non-GPU cleanup errors, but a failed
GPU drain retains the window. Input detachment also attempts all cleanup steps
before reporting collected errors. Controller shutdown no longer skips IME
stop/movement reset when controller disposal throws.

Implementation only: no builds, tests, repackages, deployments or game runs.
These changes remain uncompiled; the installed payload still contains the
reported failure. Full startup, rendered frames and teardown remain unverified.
