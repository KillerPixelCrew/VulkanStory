# Retained instance motion history

Updated 2026-10-01. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`InstanceMotionHistory` directly migrates the retained 40-float layout: light,
current transform, previous transform and validity/reactive metadata. Histories
are keyed by buffer and device object, with once-per-frame rotation and original
view/reset checks, so reordered slots do not borrow another device's transform.
The temporal owner supplies session-owned frame and compiled-motion state.

Existing instanced draw dispatch now supplies the retained previous-camera and
shared motion uniforms while the temporal window and compiled history are enabled.
The instanced draw now opens and closes its primary motion attachment scope only
when the compiled program declares the retained previous-camera uniform and motion
history is enabled.

`InstanceMotionConsumerPatches` adds a guarded Survival assembly subset for the
original mechanical producers: generic gears, angled cage/peg gears, transmissions,
clutches, creative rotors and pulverizers. Constructors and allocation helpers
expand their original light/transform buffers from 20 to 40 floats per instance
before mesh upload. Existing object identities are preserved for renderer fields,
MeshData and cloned cage layouts. Checked multiply constants update buffer counts
and writer offsets together; inactive routing retains the original 20-float path.

Each original device-specific transform method supplies a scoped device identity.
The original quaternion, rotation, translation and returned transform calculations
remain intact. After each original buffer write, the retained history appends the
same device's previous transform and validity/reactive metadata. Finalizers restore
the prior device identity even if transform generation throws. The subset checks
allocation/stride anchors in original and incoming IL before mutation.

Provenance: original producer sources are in the retained official Survival snapshot
under `Systems/MechanicalPower/Renderer`; migration behavior follows the matching
retained patches at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`.

Compiled mode publication, complete scene/profile registration and remaining host
integration still prevent dense validity and startup registration. No new code
has been compiled or run, and no instance/motion/provider result is accepted.
