// Resident entity ABI (scalar layout; all offsets and strides are bytes).
// Pass name: entityanimated.instanced; native compiler discovers this root .glsl.
// Set 0 frame globals/textures and set 1 bindless textures retain bindings.glsl.
// Set 2 binding 0: readonly EntityRows SSBO, rows[gl_InstanceIndex].
// firstInstance is the row id, including for a one-instance indirect command.
// Row stride 612: OptimumProgram [0,568), OptimumDraw [568,604),
// uint poseOffset @604, uint previousPoseOffset @608.
// OptimumDraw retains all 36 original bytes: entityTex @568 (set 1 binding 0),
// addRenderFlags @572, extraGlow @576, taaHistoryValid @580, taaReactive @584,
// entityId @588, glitchFlicker @592, vsComputeDeformation @596 and
// vsComputeWorldDeformation @600. The last two fields are ignored: resident
// skinning and all current/previous warps execute in the vertex shader.
// Set 2 binding 1: readonly EntityPoses SSBO, a tightly packed mat4 values[].
// Both pose offsets count floats from that SSBO's start, are multiples of 16,
// and select values[poseOffset / 16 + jointId] for current/previous skinning.
// Retain both poses until frame retirement; previousPoseOffset is read only
// with TAAMOTION=1, USEOIT=0 and a nonzero row taaHistoryValid.
// No program UBO, push constants or deformation/world-warp storage inputs.
// Flat location 15 carries the row id to the fragment stage; its texture slot
// is indexed with nonuniformEXT because instances can use different materials.
