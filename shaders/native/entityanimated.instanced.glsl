// Resident entity ABI (scalar layout; all offsets and strides are bytes).
// Pass name: entityanimated.instanced; native compiler discovers this root .glsl.
// Set 0 frame globals/textures and set 1 bindless textures retain bindings.glsl.
// Set 2 binding 0: readonly EntityRows SSBO, rows[gl_InstanceIndex].
// firstInstance is the row id, including for a one-instance indirect command.
// Row stride 612: OptimumProgram [0,568), OptimumDraw [568,604),
// uint poseOffset @604, uint previousPoseOffset @608.
// OptimumDraw retains all 36 original bytes: entityTex @568 (set 1 binding 0),
// addRenderFlags @572, extraGlow @576, taaHistoryValid @580, taaReactive @584,
// entityId @588, glitchFlicker @592, entityReserved0 @596 and
// entityReserved1 @600. The last two fields retain padding: resident
// skinning and all current/previous warps execute in the vertex shader.
// Set 2 binding 1: readonly EntityPoses SSBO, a tightly packed mat4 values[].
// Both pose offsets count floats from that SSBO's start, are multiples of 16,
// and select values[poseOffset / 16 + jointId] for current/previous skinning.
// Retain both poses until frame retirement; previousPoseOffset is read only
// with TAAMOTION=1, USEOIT=0 and a nonzero row taaHistoryValid.
// No program UBO, push constants or deformation/world-warp storage inputs.
// Flat location 15 carries the row id to the fragment stage; its texture slot
// is indexed with nonuniformEXT because instances can use different materials.
#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require

// Native port of entityanimated.vsh/.fsh (the Optimum override, docs/vulkan.md).
// The program/push structs retain the original member order and scalar layout.
// Axes and specialization are unchanged. OIT does not read a previous pose.
#if defined(OPTIMUM_FRAGMENT)
#define OPTIMUM_FRAME_OWNER_FOGANDLIGHT_VSH
#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#endif
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "varyings.glsl"

struct OptimumProgram
{
    vec3 rgbaAmbientIn;
    vec4 rgbaLightIn;
    vec4 rgbaFogIn;
    float fogMinIn;
    float fogDensityIn;
    vec4 renderColor;
    float frostAlpha;
    mat4 projectionMatrix;
    mat4 viewMatrix;
    mat4 modelMatrix;
    int skipRenderJointId;
    int skipRenderJointId2;
    mat4 prevProjectionMatrix;
    mat4 prevViewMatrix;
    mat4 prevModelMatrix;
    vec3 cameraPosDelta;

    float prevTimeCounter;
    float prevWindWaveCounter;
    float prevWindWaveCounterHighFreq;
    float prevWaterWaveCounter;
    float prevWindSpeed;
    vec3 prevPlayerpos;
    float prevGlobalWarpIntensity;
    float prevGlitchWaviness;
    float prevWindWaveIntensity;
    float prevWaterWaveIntensity;
    int prevPerceptionEffectId;
    float prevPerceptionEffectIntensity;

    vec2 taaRenderSize;
    vec2 taaJitterPx;
    float alphaTest;
    float glitchEffectStrength;
    float depthOffset;

    vec2 frameSize;
};

struct OptimumDraw
{
    uint entityTex;
    int addRenderFlags;
    int extraGlow;
    int taaHistoryValid;
    float taaReactive;
    int entityId;
    int glitchFlicker;
    int entityReserved0;
    int entityReserved1;
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

// Keep every expression below and in the shared includes on its original name.
#define rgbaAmbientIn vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.rgbaAmbientIn
#define rgbaLightIn vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.rgbaLightIn
#define rgbaFogIn vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.rgbaFogIn
#define fogMinIn vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.fogMinIn
#define fogDensityIn vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.fogDensityIn
#define renderColor vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.renderColor
#define projectionMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.projectionMatrix
#define viewMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.viewMatrix
#define modelMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.modelMatrix
#define skipRenderJointId vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.skipRenderJointId
#define skipRenderJointId2 vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.skipRenderJointId2
#define prevProjectionMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevProjectionMatrix
#define prevViewMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevViewMatrix
#define prevModelMatrix vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevModelMatrix
#define cameraPosDelta vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.cameraPosDelta
#define prevTimeCounter vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevTimeCounter
#define prevWindWaveCounter vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWindWaveCounter
#define prevWindWaveCounterHighFreq vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWindWaveCounterHighFreq
#define prevWaterWaveCounter vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWaterWaveCounter
#define prevWindSpeed vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWindSpeed
#define prevPlayerpos vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevPlayerpos
#define prevGlobalWarpIntensity vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevGlobalWarpIntensity
#define prevGlitchWaviness vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevGlitchWaviness
#define prevWindWaveIntensity vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWindWaveIntensity
#define prevWaterWaveIntensity vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevWaterWaveIntensity
#define prevPerceptionEffectId vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevPerceptionEffectId
#define prevPerceptionEffectIntensity vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.prevPerceptionEffectIntensity
#define taaRenderSize vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.taaRenderSize
#define taaJitterPx vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.taaJitterPx
#define alphaTest vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.alphaTest
#define glitchEffectStrength vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.glitchEffectStrength
#define depthOffset vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.depthOffset
#define frameSize vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.frameSize
#define entityTex vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.entityTex
#define addRenderFlags vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.addRenderFlags
#define extraGlow vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.extraGlow
#define taaHistoryValid vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.taaHistoryValid
#define taaReactive vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.taaReactive
#define entityId vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.entityId
#define glitchFlicker vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].draw.glitchFlicker

#if defined(OPTIMUM_VERTEX)
// The fragment include uses frostAlpha as a function parameter, so this alias
// belongs only to the vertex stage that reads the per-entity uniform.
#define frostAlpha vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].program.frostAlpha
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

#if TAAMOTION == 1
layout(location = 14) out vec4 taaPrevClip;
#endif

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 rgbaFog;
layout(location = 3) out float fogAmount;
layout(location = 4) out vec3 vertexPosition;
layout(location = 5) out vec4 worldPos;
layout(location = 6) out float damageEffect;
layout(location = 7) out vec4 camPos;
layout(location = 8) out float fragFrostAlpha;
layout(location = 9) flat out int renderFlags;
layout(location = 10) out vec4 glPos;
layout(location = 11) out vec3 normal;
#if GBUFFER == 1
layout(location = 12) out vec4 fragPosition;
layout(location = 13) out vec4 gnormal;
#endif

#include "vertexflagbits.glsl"
#include "shadowcoords.glsl"
#include "fogandlight.vert.glsl"
#include "vertexwarp.glsl"

void main(void)
{
	vsEntityRow = uint(gl_InstanceIndex);
	damageEffect = damageEffectIn;
	mat4 animModelMat = modelMatrix *
		vsEntityPoses.values[vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].poseOffset / 16u + uint(jointId)];
	worldPos = animModelMat * vec4(vertexPositionIn, 1.0);

	renderFlags = flags | addRenderFlags;

	if ((renderFlags & WindModeFruitMask) > 0) {
		fragFrostAlpha = frostAlpha / 4;
		renderFlags &= ~WindModeFruitMask;
	} else fragFrostAlpha = frostAlpha;

	if ((renderFlags & WindModeWaterMask) > 0) {
		worldPos = applyLiquidWarping(true, worldPos, 5);
	} else {
		worldPos = applyVertexWarping(renderFlags, worldPos);
	}
	worldPos = applyGlobalWarping(worldPos);

#if TAAMOTION == 1
#if USEOIT == 0
	// Reconstruct before the local renderFlags below shadows the flat output,
	// preserving the current/previous warp flags and fruit-mask clearing.
	{
		vec4 taaPrevWorld;
		if (taaHistoryValid != 0) {
			mat4 taaPrevAnimMat = prevModelMatrix *
				vsEntityPoses.values[vsEntityRows.rows[OPTIMUM_ENTITY_ROW_INDEX].previousPoseOffset / 16u + uint(jointId)];
			taaPrevWorld = taaPrevAnimMat * vec4(vertexPositionIn, 1.0);
			WarpState taaPrev = previousWarpState();
			if ((renderFlags & WindModeWaterMask) > 0) {
				taaPrevWorld = applyLiquidWarpingState(taaPrev, true, taaPrevWorld, 5);
			} else {
				taaPrevWorld = applyVertexWarpingState(taaPrev, renderFlags, taaPrevWorld);
			}
			taaPrevWorld = applyGlobalWarpingState(taaPrev, taaPrevWorld);
		} else {
			// Same camera-only fallback as the original native shader.
			taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);
		}
		taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);
	}
#else
	// Entityanimated_Oit never reads the previous-position varying.
	taaPrevClip = vec4(0.0);
#endif
#endif

	vertexPosition = vertexPositionIn.xyz * 1.5;

	vec4 cameraPos = camPos = viewMatrix * worldPos;

	uv = uvIn;
	int renderFlags = extraGlow + flags;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;

	// Distance fade out
	color.a *= clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	gl_Position = projectionMatrix * cameraPos;
	calcShadowMapCoords(viewMatrix, worldPos);

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);

	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal.x, normal.y, normal.z, 0)).xyz;

	#if GBUFFER == 1
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif

	// GL clip depth [-w, w] to Vulkan's [0, w], as in the stock native shader.
	gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
}

#elif defined(OPTIMUM_FRAGMENT)
layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec4 rgbaFog;
layout(location = 3) in float fogAmount;
layout(location = OPTIMUM_LOCATION_GLOW_LEVEL) in float glowLevel;
layout(location = 4) in vec3 vertexPosition;
layout(location = 9) flat in int renderFlags;
layout(location = 11) in vec3 normal;
layout(location = 5) in vec4 worldPos;
layout(location = OPTIMUM_LOCATION_BLOCK_LIGHT) in vec3 blockLight;
layout(location = 7) in vec4 camPos;
layout(location = 6) in float damageEffect;
layout(location = 8) in float fragFrostAlpha;

#include "oit.glsl"

#if USEOIT == 0
	layout(location = 0) out vec4 outColor;
	layout(location = 1) out vec4 outGlow;
	#if GBUFFER == 1
	layout(location = 12) in vec4 fragPosition;
	layout(location = 13) in vec4 gnormal;
	layout(location = 2) out vec4 outGNormal;
	layout(location = 3) out vec4 outGPosition;
	#endif
#endif

#if TAAMOTION == 1
layout(location = 14) in vec4 taaPrevClip;
#if USEOIT == 0
#if GBUFFER == 1
layout(location = 4) out vec4 outMotion;
#else
layout(location = 2) out vec4 outMotion;
#endif
#include "motion.glsl"
#endif
#endif

#include "vertexflagbits.glsl"
#include "fogandlight.frag.glsl"
#include "noise3d.glsl"
#include "noise2d.glsl"
#include "underwatereffects.glsl"

void main() {
	float b = 1;

	if (damageEffect > 0) {
		float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
		if (f < damageEffect - 1.3) discard;
		b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
	}

	vec4 texColor = texture(optimumTextures2D[nonuniformEXT(entityTex)], uv);

	// Every path overwrites the initial value, as in the original native shader.
	float intensity = 0.0;
	if (OPTIMUM_SHADOWQUALITY > 0) {
	intensity = 0.34 + (1 - shadowIntensity)/8.0;
	} else {
	intensity = 0.45;
	}

	// Integer seed arithmetic retains the original NVIDIA workaround.
	int eidfloor = (entityId / 100) * 100;
	float seed = (entityId - eidfloor) / 5.0;

	texColor = applyFrostEffect(fragFrostAlpha, texColor, normal, vertexPosition + vec3(seed));
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition + vec3(seed), 0);

	texColor *= color;
	texColor.rgb *= b;

#if USEOIT == 1
	vec4 outColor;
#endif

	float murkiness=getUnderwaterMurkiness();
	if (murkiness > 0) {
		outColor = applyFogAndShadowWithNormal(texColor, 0, normal, 1, intensity, worldPos.xyz);
		outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
	} else {
		outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
	}

	if (glitchFlicker >0 && glitchEffectStrength > 0) {
		float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
		outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);

		float b = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
		outColor.a *= mix(1, clamp(b * 10 + 2, 0, 1), glitchEffectStrength);
	}

	if (OPTIMUM_NORMALVIEW == 0) {
	if (outColor.a < alphaTest) discard;
	}

	float glow = 0;
	if (OPTIMUM_SHINYEFFECT > 0) {
	outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
	}

#if USEOIT == 0 && GBUFFER == 1
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, 0);
#endif

	if (OPTIMUM_NORMALVIEW > 0) {
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
	}

#if USEOIT == 1
	OIT(outColor, glowLevel+glow);
#else
	outGlow = vec4(glowLevel + glow, 0, 0, color.a);
#endif

#if ALLOWDEPTHOFFSET == 1
	gl_FragDepth = gl_FragCoord.z + depthOffset;

	#if USEOIT == 0 && GBUFFER == 1
		outGPosition.w=1;
	if (OPTIMUM_OPTIMUMAO > 0) {
		outGNormal.w = -1.0;
	}
	#endif
#endif

#if TAAMOTION == 1 && USEOIT == 0
	#if ALLOWDEPTHOFFSET == 1
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));
	#else
		outMotion = optimumWriteMotion(taaPrevClip, taaRenderSize, taaJitterPx, taaReactive, gl_FragCoord.z);
	#endif
#endif
}
#endif
