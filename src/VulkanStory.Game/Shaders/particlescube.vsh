#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Optimum override of the vanilla particlescube.vsh (TAA P4).
//
// Cube particles are drawn on Primary, inside the Opaque stage, with blending
// on and depth writes on - so unlike the quad particles (which go through the
// OIT path and are covered by the merge's revealage reactive) they can and must
// write Primary's motion attachment themselves. The temporal contract in docs/vulkan.md
// requires reactive coverage and replace blending for motion.
//
// Current-particle shading follows the retained game path. The previous-frame
// calculation preprocesses away when TAAMOTION is 0.
//
// Previous position/scale/direction come from rendered spawn-identity history.
// A new, revived, absent or reset particle supplies invalid history; the writer
// retains reactive coverage and lets temporal resolve use the current pixel.

layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec4 normalv;				// Per vertex
layout (location = 2) in vec2 uv;						// Per vertex
layout (location = 3) in int renderFlags; 			// Per instance

layout (location = 4) in vec3 particlePosition; 	// Per instance (=per particle)
#if defined(VEC3SCALE)
layout (location = 5) in vec3 scale;					// Per instance
#else
layout (location = 5) in float scale;					// Per instance
#endif
layout (location = 6) in vec4 particleDir; 			// Per instance
layout (location = 7) in vec4 rgbaLightIn; 		// Per instance
layout (location = 8) in vec4 rgbaBlockIn; 		// Per instance
layout (location = 9) in vec3 previousParticlePosition;
layout (location = 10) in vec3 previousParticleScale;
layout (location = 11) in vec4 previousParticleDirection;
layout (location = 12) in float particleHistoryValid;

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;	
uniform float fogMinIn;
uniform float fogDensityIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec4 color;
out vec4 rgbaFog;
out vec3 normal;
out float fogAmount;
out vec4 worldPos;

// TAA motion vectors (Optimum P4). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED world projection
uniform mat4 prevModelViewMatrix;    // previous frame's CameraMatrixOrigin
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
out vec4 taaPrevClip;
#endif
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

#define M_PI 3.1415926535897932384626433832795

mat4 rotation3d(vec3 axis, float angle) {
  axis = normalize(axis);
  float s = sin(angle);
  float c = cos(angle);
  float oc = 1.0 - c;

  return mat4(
    oc * axis.x * axis.x + c,           oc * axis.x * axis.y - axis.z * s,  oc * axis.z * axis.x + axis.y * s,  0.0,
    oc * axis.x * axis.y + axis.z * s,  oc * axis.y * axis.y + c,           oc * axis.y * axis.z - axis.x * s,  0.0,
    oc * axis.z * axis.x - axis.y * s,  oc * axis.y * axis.z + axis.x * s,  oc * axis.z * axis.z + c,           0.0,
    0.0,                                0.0,                                0.0,                                1.0
  );
}

float atan2(in float y, in float x)
{
    bool s = (abs(x) > abs(y));
    return mix(M_PI/2.0 - atan(x,y), atan(y,x), s);
}


#if TAAMOTION > 0
// The position half of vanilla's main() below, as a function of the warp state
// and the particle's position, so the same code can be evaluated for this frame
// and for the previous one. Vanilla's own lines are left exactly as they are;
// this is the twin beside them, and the coverage test compares the two.
vec4 taaParticleWorldPos(WarpState st, vec3 taaParticlePosition)
{
	vec4 taaWorldPos;
#if defined(VEC3SCALE)
	mat4 rotMat = rotation3d(vec3(0,1,0), atan2(previousParticleDirection.z, previousParticleDirection.x) + previousParticleDirection.w);
	taaWorldPos = rotMat * (vec4(vertexPosition,1.0) * vec4(previousParticleScale,1.0)) + vec4(taaParticlePosition, 1.0);
	taaWorldPos.w=1;
#else
	taaWorldPos = vec4(vertexPosition * previousParticleScale + taaParticlePosition, 1.0);
#endif

	taaWorldPos = applyVertexWarpingState(st, renderFlags, taaWorldPos);
	taaWorldPos = applyGlobalWarpingState(st, taaWorldPos);
	return taaWorldPos;
}
#endif


void main()
{
#if defined(VEC3SCALE)
	mat4 rotMat = rotation3d(vec3(0,1,0), atan2(particleDir.z, particleDir.x) + particleDir.w);
	worldPos = rotMat * (vec4(vertexPosition,1.0) * vec4(scale,1.0)) + vec4(particlePosition, 1.0);
	worldPos.w=1;		
#else
	worldPos = vec4(vertexPosition * scale + particlePosition, 1.0);
#endif

	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);
	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;
	
	int flags = min(255, 2 * (renderFlags & 0xff)); // increase the glow on cube particles
	color = applyLight(rgbaAmbientIn, rgbaLightIn, flags, cameraPos) * rgbaBlockIn;
	
	fogAmount = getFogLevel(vec4(particlePosition, 0), fogMinIn, fogDensityIn);
	rgbaFog = rgbaFogIn;
	normal = normalv.xyz;
	
	calcShadowMapCoords(modelViewMatrix, worldPos);
	
#if SSAOLEVEL > 0
	
	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal.xyz, 0.25);
#endif

#if TAAMOTION > 0
	// Previous rendered position is already relative to the previous camera.
	// Re-evaluate its warp with previous counters and project with the previous
	// unjittered projection and CameraMatrixOrigin.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = taaParticleWorldPos(taaPrev, previousParticlePosition);
		taaPrevClip = particleHistoryValid > 0.5 ? prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos) : vec4(0.0);
	}
#endif
}
