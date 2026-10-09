# Handheld shadow prefix lifecycle — 2026-10-01

Implementation turn: source inspection/editing only; no builds, tests, probes,
packages or game launch.

Reviewed provider runtime controls and resize/resource ownership against the
retained implementation. Frame-generation multiplier is already refreshed during
Generate, provider resources retire through the device, and the framebuffer host
reads current SDL pixel dimensions. Those paths needed no speculative alteration.

Found the handheld shadow compiler prefix only added HANDHELDSHADOWS=1; disabling
the live option did not remove an adapter-inserted define from a reused Shader
stage. Full-size rebuilt targets could therefore pair with the old reduced-sample
specialization. The existing ApplyRendererSettings path queues target rebuild and
ShaderRegistry/world shader reload; the compiler must describe the new selection.

CompileShader now tracks stages whose handheld define it inserted using weak
ownership. On disabled recompilation it removes that inserted define. Pre-existing
shader-supplied defines are retained. Define recognition handles line whitespace;
no shadow sampling GLSL, specialization ID or framebuffer allocation changed.
Weak tracking does not retain old shader stages after replacement.

Source changes are unbuilt/unexecuted. The fresh client-refresh-20261001-221545 ZIP
is now older than this increment; its clean build/package evidence remains valid
for its own payload. Runtime shadow-toggle visual parity, sky artifact and full
renderer/provider/SDL acceptance remain open. No tests were written.