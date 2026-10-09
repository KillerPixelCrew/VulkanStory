# Shader asset override policy

Updated 2026-10-01. Implementation only; no builds, tests, packages or game runs.
Tests remain deferred until integration is finished.

The session now supplies a live `ShaderOverridePolicy` to the graphics adapter
and native device. Original asset origins and selected asset patch flags replace
the old launcher-generated compatibility report dependency.

- External/patched stages take precedence over matching embedded replacements.
  The original loader handles them, and native bundle selection uses the rewriter.
- Custom-domain and in-memory registrations also mark their program overridden.
  Stage discovery covers later registrations and passes without embedded copies.
- Base include-name collisions disable native selection globally. Unrelated new
  mod include names do not. Unknown/failed discovery conservatively uses the rewriter.
- Required writer/shared-include overrides disable stock compiled motion/AO
  publication until a supported adapter supplies compatible temporal/G-buffer
  outputs. Diagnostics identify these fallbacks; settings alone are insufficient.

This preserves external replacements without claiming arbitrary shader temporal
compatibility. Direct shader-code mutation by another patch owner still needs an
explicit adapter. Override selection, native/rewriter pixels, reload and runtime
feature fallback remain unverified. Packaging, renderer settings/world UI and live
port acceptance remain open.
