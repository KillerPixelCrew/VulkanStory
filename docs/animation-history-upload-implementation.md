# Previous-animation upload ownership

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

The uniform-buffer adapter now observes original Animation object uploads and
generic payloads captured from the original handle provider. It invokes the
retained entity history using the original float-array identity, model/warp
scratch and current temporal view. Previous pose bytes use the existing owned
UBO update path before the current pose is uploaded.

The backend exposes the linked interface's named client-block binding. A
previous-animation buffer is created/reused only when AnimationPrev is actually
declared and motion history is enabled; uniform naming alone is insufficient.
It uses the original program's existing UBO dictionary and adapter buffer
metadata, without adding game fields. Disposed buffers are recreated. Shader
deletion and session shutdown release cached previous buffers while the device
is alive. Arbitrary pointer uploads are not decoded into object identities.

Compiled motion-mode publication, rigid/held-item identities, instance/particle
histories, liquid redraw and complete dense producer wiring remain open. No new
code has been compiled or run, and bone/motion/provider results remain unaccepted.
