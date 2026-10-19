// Native port of the game include vertexwarp.vsh (docs/vulkan.md).
// optimum-port-of: vertexwarp.vsh
// optimum-port: transformed
// optimum-frame-owner: vertexwarp.vsh
// optimum-program-uniform: float prevTimeCounter = 0
// optimum-program-uniform: float prevWindWaveCounter = 0
// optimum-program-uniform: float prevWindWaveCounterHighFreq = 0
// optimum-program-uniform: float prevWaterWaveCounter = 0
// optimum-program-uniform: float prevWindSpeed = 0
// optimum-program-uniform: vec3 prevPlayerpos = vec3(0.0, 0.0, 0.0)
// optimum-program-uniform: float prevGlobalWarpIntensity = 0
// optimum-program-uniform: float prevGlitchWaviness = 0
// optimum-program-uniform: float prevWindWaveIntensity = 1
// optimum-program-uniform: float prevWaterWaveIntensity = 1
// optimum-program-uniform: int prevPerceptionEffectId = 1
// optimum-program-uniform: float prevPerceptionEffectIntensity = 1
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_VERTEXWARP_GLSL
#define OPTIMUM_INCLUDE_VERTEXWARP_GLSL

#define OPTIMUM_FRAME_OWNER_VERTEXWARP_VSH
#include "frame.glsl"
#include "specialization.glsl"
#include "vertexflagbits.glsl"

// Optimum override of the vanilla vertexwarp.vsh (TAA P3).
//
// Motion-vector writers have to evaluate the vertex warp twice: once with this
// frame's animation state and once with the previous frame's, through the very
// same code. So every warp function here takes an explicit WarpState carrying
// every uniform it reads, and the vanilla entry points became one-line wrappers
// that pass currentWarpState(). The maths inside is unchanged, line for line,
// which is what keeps every other shader that includes this file - liquids,
// particles, clouds, decals, wireframe, the shadow map - byte-for-byte
// identical to vanilla for current values.
//
// The prev* uniforms default to zero and are set only by a pass that actually
// writes motion (ChunkRenderer sets them from OptimumTemporal.Frame). A shader
// that never calls previousWarpState() drops them at compile time.
//
// The counters wrap (DefaultShaderUniforms.Update takes them modulo 6000), so
// the previous values are snapshotted values, never "current minus dt".




// Previous frame's values of exactly the same set (TAA P3).



#include "noise3d.glsl"



// Every uniform the warp functions read, so one call site can evaluate them for
// this frame and the previous frame without any hidden global state.
//
// Native port: the members carry a warp prefix because frame.glsl defines the
// uniform names (timeCounter, playerpos, ...) as macros for optimumFrame members,
// and a macro would rewrite a member of the same name. Nothing outside this file
// reads the members; the functions and their signatures are unchanged.
#include "warpstate.glsl"

WarpState currentWarpState() {
	return WarpState(
		timeCounter, windWaveCounter, windWaveCounterHighFreq, waterWaveCounter,
		windSpeed, playerpos, globalWarpIntensity, glitchWaviness,
		windWaveIntensity, waterWaveIntensity, perceptionEffectId, perceptionEffectIntensity);
}

WarpState previousWarpState() {
	return WarpState(
		prevTimeCounter, prevWindWaveCounter, prevWindWaveCounterHighFreq, prevWaterWaveCounter,
		prevWindSpeed, prevPlayerpos, prevGlobalWarpIntensity, prevGlitchWaviness,
		prevWindWaveIntensity, prevWaterWaveIntensity, prevPerceptionEffectId, prevPerceptionEffectIntensity);
}


// ---- vanilla entry points, unchanged behaviour ----------------------------
// Evaluated with this frame's uniforms, so every existing caller sees exactly
// what vanilla produced.

vec3 applyPerceptionWarping(vec3 worldPos) {
	return applyPerceptionWarpingState(currentWarpState(), worldPos);
}

vec4 applyLiquidWarping(bool windAffected, vec4 worldPos, float div) {
	return applyLiquidWarpingState(currentWarpState(), windAffected, worldPos, div);
}

vec4 applyVertexWarping(int renderFlags, vec4 worldPos) {
	return applyVertexWarpingState(currentWarpState(), renderFlags, worldPos);
}

vec4 applyGlobalWarping(vec4 worldPos) {
	return applyGlobalWarpingState(currentWarpState(), worldPos);
}

#endif
