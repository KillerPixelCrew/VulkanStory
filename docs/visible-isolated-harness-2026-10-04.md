# Explicit visible isolated harness — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
The strict hidden DLSS run reported no interpolation gain. Visible-window
behavior and actual Options interaction remain missing evidence; this increment
prepares that distinct checkpoint without launching a client.

The harness now accepts -Visible. Its process-local environment selects a visible
SDL window while retaining all isolated world/settings/mod paths and diagnostic
capture machinery. Routine/default mode explicitly selects hidden operation,
including overriding an inherited visible environment flag. Production startup
outside the harness is unchanged. The verifier requires hidden=false only for an
explicit visible run; default mode still requires hidden=true/focused=false.
Visible runs may be focused for real input. All typed fields, capture/scenario
receipts, counts, process result and staged-mod/world invariants remain required.
Scenario visibility assertions are not waived and must match their intended mode.

-KeepOpen is accepted only with -Visible. It disables capture-completion exit for
manual Options inspection, retaining the existing timeout and exact owned-child
process handle. The child can be closed normally after inspection; deadline
termination still fails validation. No existing user game is controlled.
The world/settings remain in the new run directory, not the installed data path.

Source inspection covered parameter flow, isolated environment, SDL hidden flags,
verification visibility/focus predicates and existing completion/deadline behavior.
No builds, syntax probes, tests, packages, client runs or deployment occurred.
Current binaries/stages predate these flags; a matching build/stage is required.
No numerical, tag-lifecycle, manual-hook preference or presentation-count change
was made. The local SDK documents our Vulkan proc-address route; removing the
manual flag or claiming those unsupported hook warnings harmless is not justified.

## Next validation boundary

Compile and stage this source before launch. A focused DLSS gain scenario can
then use -Visible; manual pause Options inspection can additionally use -KeepOpen
with a bounded timeout. This requires explicit user instruction under the existing
Roadmap rule: "Visible launches/deployment require the user's instruction."
Neither these flags nor an automatic goal continuation grants that instruction.
Options main-menu coverage, controller input/focus and full visible FG quality/
pacing remain open. Installed game/settings/save were unchanged.

| File | SHA256 |
| --- | --- |
| src/VulkanStory.Game/HeadlessHarnessOptions.cs | `E72E905524132B633B2C6D0C6C9C6B85FE22133C332ADBB2D15D9C21E88CA8DD` |
| src/VulkanStory.Game/GameRenderSession.cs | `2BB85E600336E77616C94A029CDB3CF01F266E2A314E9B81E351CBAB45BB9375` |
| scripts/dev/headless-capture.ps1 | `5E9DB5BA74E995DA12C4B096E040E2EB6BECF5A1DECDE161A8261A3811C1CBF3` |
| scripts/dev/verify-headless-result.ps1 | `8691BAFBEDC7A1AF57DDB0DC4AE693B35362AECBAA3949C50DCCB9B9E9C64D2E` |
