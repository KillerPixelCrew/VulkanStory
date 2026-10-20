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
#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require

// Native port of shadowmapentityanimated.vsh/.fsh (vanilla asset, docs/vulkan.md).
// Resident inputs replace only the original uniform/push and Animation loads.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"

struct OptimumProgram
{
    mat4 projectionMatrix;
};

struct OptimumDraw
{
    uint entityTex;
    mat4 modelViewMatrix;
    int addRenderFlags;
    int vsComputeDeformation;
};

struct EntityRow
{
    OptimumProgram program;
    OptimumDraw draw;
    uint poseOffset;
    uint previousPoseOffset;
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA, scalar) readonly buffer EntityRows
{
    EntityRow rows[];
} vsEntityRows;

#if defined(OPTIMUM_VERTEX)
layout(location = 15) flat out uint vsEntityRow;
#define OPTIMUM_ENTITY_ROW_INDEX uint(gl_InstanceIndex)
#elif defined(OPTIMUM_FRAGMENT)
layout(location = 15) flat in uint vsEntityRow;
#define OPTIMUM_ENTITY_ROW_INDEX vsEntityRow
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif

#define projectionMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.projectionMatrix
#define entityTex vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.entityTex
#define modelViewMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.modelViewMatrix

#if defined(OPTIMUM_VERTEX)
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_ANIMATION) readonly buffer EntityPoses
{
    mat4 values[];
} vsEntityPoses;

layout(location = 0) out vec2 uv;

void main(void)
{
	vsEntityRow = uint(gl_InstanceIndex);
	vec4 cameraPos = modelViewMatrix *
		vsEntityPoses.values[vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].poseOffset / 16u + uint(jointId)] *
		vec4(vertexPositionIn, 1.0);
	uv = uvIn;
	gl_Position = projectionMatrix * cameraPos;

	// GL clip depth [-w, w] to Vulkan's [0, w], as in the stock native shader.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main () {
	outColor = vec4(texture(optimumTextures2D[nonuniformEXT(entityTex)], uv));
	if (outColor.a < 0.01) discard;
}
#endif
