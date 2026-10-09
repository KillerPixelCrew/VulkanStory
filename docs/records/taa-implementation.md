# Retained TAA resolve and sharpen

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Test work remains deferred until integration is complete.

`GameGraphicsAdapter.Taa` migrates retained history selection/parity, jittered
view-projection construction, previous-view projection, reset decision, resolve
inputs/uniforms, sharpen guards and post-pass state restoration. Existing history
slots 19/20 and sharpen slot 21 are used. The resolve writes color, glow and
linear depth from seven current/history inputs with the retained .1 blend alpha
and 1.25 variance gamma. Shader algorithms are unchanged.

Native passes use cached placements and explicit target/read declarations. The
fallback uses the same owned shader program through existing backend uniform,
sampler and stated fullscreen routes. Resolved textures/history/parity are
published only when a draw succeeds. Missing current motion/camera coverage
keeps history invalid instead of presenting an unwritten output.

Sharpen reads the final-composited scene at the later blit boundary. It is skipped
when no TAA resolve ran, sharpness is zero, or FSR will supply sharpening. Failed
draws return the input scene. Both native and fallback routes restore blend,
depth test and primary binding.

The original taa-resolve and taa-sharpen vertex/fragment files are copied
unchanged into the game project's embedded shaders. The existing owned UI shader
loader now serves these programs too, preferring asset replacements. Shader
reload retires owned TAA programs, clears placements/history and requests the
session's ShaderReload temporal reset. Final host setup must still configure
the existing native shader mod-override scanner.

`GameRenderSession.RenderTemporalPostTail` selects SR or TAA after AO, then
passes the actual selected scene/glow to bloom, god rays and luma. Its original
game call-site replacement still needs the AO integration. Sharpen's final-blit
caller is also unfinished.

## Remaining acceptance

Dense motion writers, AO/GTAO/SSAO, native final composition/blit/FSR/debug,
complete original post routing, remaining controller/settings service factories
and startup-profile registration remain open. New code has not been compiled or
run. No TAA image/history, rewritten world or enabled provider result is accepted.
