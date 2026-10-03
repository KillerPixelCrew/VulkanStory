# Motion transition diagnostics — 2026-10-04

DIRECT implementation turn in `D:/Coding/VulkanStory-Rewrite`; no Git repository.
The [failed off → FSR3 run](validation-provider-world-2026-10-04.md) reports zero
SR/FG work and invalid motion but does not identify the first failed gate.
Source inspection confirms ApplyPendingProviderTargets already reloads registered
and world shaders. Adding another reload is not justified by present evidence.

Applied source now retains the reason motion certification failed: scene stage,
motion-write window, projection/frame identity, disabled compiled mode, rejected
scene draw or liquid/sky tail failure. Shader diagnostics expose certified versus
allocated motion locations. Incomplete shader-load logs list requested modes and
the missing/mismatched writer names. Existing runtime JSON samples include
motionReadiness and compiledMotionStatus. No motion-valid predicate, writer
requirement, rendering mathematics, resource layout or scenario assertion changed.
These fields describe the existing decisions; they do not bypass certification.

Source inspection covered the Begin/scene-stage/tail/reset/detach paths, shader
load certification and JSON sampling. No build, tests, probes, packages, GPU/game
run or deployment occurred; no tests were added. This diagnoses the observability
gap, not the black-frame cause. Both source compilation and actual gate evidence
remain pending. The existing binary/stage does not contain these diagnostics.
Any next runtime work must target this missing gate evidence rather than repeat
the unchanged all-provider sweep. REN-03 and SDK-01/02 remain open.

| Source | SHA256 |
| --- | --- |
| GameTemporalOwner.cs | `68CFECF4921C8ACB259EB6B4B370EA38648A23810D864ACF490DEB49D14F141D` |
| GameGraphicsAdapter.ShaderModes.cs | `E2F02791C094C28096A7623DD871CFC6BF200FC1E457666AD5A71272AACB2800` |
| TemporalConsumerPatches.cs | `3BE16C7662BDB82D36C92B550863F8AE694E599891475BE500503A06C2CB46AE` |
| GameRenderSession.Diagnostics.cs | `0C64411DDD4ABBB439FAAF14CD611D9B7D90D89B01F40018BC132FE39E3DB915` |

All four source paths are under src/VulkanStory.Game. Installed files, settings
and original save remain unchanged.
