# Retained terrain pool scope

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Chunks` migrates scoped terrain multi-draw preparation,
sampler reads/custom sampling, vertex layout and deferred first-draw pass
opening. Target/motion slots and the original caller's blend/depth/cull state
are captured at each pool call. Bound-depth sampling is declared explicitly.
The pool's original origin uniforms and range selection still execute. Before
the native pass opens, missing prerequisites use stated fallback; after it
opens, that native scope owns subsequent draws. Scope teardown ends the pass.

`ChunkConsumerPatches` wraps original pool-manager Render calls in the depth,
shadow, opaque, OIT and after-OIT methods. It guards 1/4/5/3/1 typed pool anchors
respectively in original and incoming IL. The opaque method's nine raw sampler
clear calls are routed to the adapter as well. Scope wrappers close in finally,
including exceptions. No injected platform method or pool member is needed.

This is a graphics-scene-chunks subset. Liquid velocity redraw, other dense motion
producers, clouds, entities/hands, compiled shader-mode publication and remaining
host factories still prevent complete scene/startup registration. New code and
guards have not been compiled or run. No terrain/world/provider result is accepted.
