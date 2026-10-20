// Resident shadow entity ABI (scalar layout; all offsets and strides are bytes).
// Pass name: shadowmapentityanimated.instanced; compiler discovers this root .glsl.
// Set 0 frame globals/textures and set 1 bindless textures retain bindings.glsl.
// Set 2 binding 0: readonly EntityRows SSBO, rows[gl_InstanceIndex].
// firstInstance is the row id, including for a one-instance indirect command.
// Row stride 148: OptimumProgram [0,64), OptimumDraw [64,140),
// uint poseOffset @140, uint previousPoseOffset @144 (unused in this pass).
// OptimumProgram is projectionMatrix @0. OptimumDraw retains its 76 bytes:
// entityTex @64 (set 1 binding 0), modelViewMatrix @68, addRenderFlags @132,
// vsComputeDeformation @136. The compute flag is ignored: resident skinning
// executes in the vertex shader, retaining stock shadow arithmetic.
// Set 2 binding 1: readonly EntityPoses SSBO, a tightly packed mat4 values[].
// poseOffset counts floats from that SSBO's start, is a multiple of 16, and
// selects values[poseOffset / 16 + jointId]. Retain it until frame retirement.
// No program UBO, push constants or deformation/world-warp storage inputs.
// Flat location 15 carries the row id to the fragment stage; its texture slot
// is indexed with nonuniformEXT because instances can use different materials.
