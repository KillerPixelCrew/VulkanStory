#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in int colormapData;
 #endif

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float cameraUnderwater;

uniform float shadowIntensity = 1;
uniform vec3 lightPosition;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

out vec4 rgba;
out vec2 uv;
out vec4 rgbaFog;
out float fogAmount;
out vec3 normal;
out vec3 vertexPosition;
out vec4 worldPos;
out vec4 camPos;
out float lod0Fade;
out float nb;

 #if SSAOLEVEL > 0
out vec4 gnormal;
 #endif


flat out int renderFlags;

// TAA motion vectors (Optimum P3). TAAMOTION is stamped by
// ShaderRegistry.registerDefaultShaderCodePrefixes and is 1 only while TAA is
// on, so with TAA off this shader preprocesses back to vanilla.
#if TAAMOTION > 0
uniform mat4 prevProjectionMatrix;   // previous frame's UNJITTERED world projection
uniform mat4 prevModelViewMatrix;    // previous frame's CameraMatrixOrigin
uniform vec3 cameraPosDelta;         // cameraPos(this frame) - cameraPos(previous frame)
out vec4 taaPrevClip;
#endif

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexID / 4];
	int vIndex = gl_VertexID & 0x03;
	renderFlags = vdata.flags[vIndex];
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif


	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	bool isLeaves = ((renderFlags & WindModeBitMask) > 0); 
	
	worldPos = applyVertexWarping(renderFlags, truePos);
	worldPos = applyGlobalWarping(worldPos);
	
	camPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * camPos;
	
	calcShadowMapCoords(modelViewMatrix, worldPos);
 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);

 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = uvIn;

 #endif

	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);
	
	// Distance fade out
	rgba.a = clamp(17.0 - 20.0 * length(worldPos.xz) / viewDistance + max(0.0, worldPos.y * 0.02), -1.0, 1.0);
	
	rgbaFog = rgbaFogIn;
	
	normal = unpackNormal(renderFlags);
	
#if SSAOLEVEL > 0
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	gnormal.w = isLeaves ? 1 : 0;
#if OPTIMUMAO > 0
	// Bit 15 of colour-map metadata is Optimum's explicit thin-block class
	// for non-wind chunk geometry. Wind-mode geometry is already thin; the
	// same bit retains its vanilla season-offset meaning there.
#if USESSBO > 0
	if (!isLeaves && (vdata.colormapData & 0x8000) != 0) gnormal.w = 1;
#else
	if (!isLeaves && (colormapData & 0x8000) != 0) gnormal.w = 1;
#endif
#endif
#endif


	// To fix Z-Fighting on blocks over certain other blocks
	if (gl_Position.z > -1) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);
	}
	

#if TAAMOTION > 0
	// The same vertex, one frame ago, through the same code path: the chunk's
	// camera-relative position moved by exactly the camera's own motion
	// (accuracy rule 4), the warp is re-evaluated with the previous frame's
	// counters, and the previous unjittered projection replaces this frame's
	// jittered one. The warp noise consumes an absolute-ish position, which is
	// prevRel + prevPlayerpos - that is what previousWarpState() carries.
	{
		WarpState taaPrev = previousWarpState();
		vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);
		taaPrevPos = applyVertexWarpingState(taaPrev, renderFlags, taaPrevPos);
		taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);
		taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);

		// The z-fighting w-offset shifts where the fragment lands on screen, so
		// leaving it off the previous position would report that shift as motion.
		if (taaPrevClip.z > -1) {
			int taaPrevZOffset = (renderFlags & ZOffsetBitMask) >> 8;
			taaPrevClip.w += taaPrevZOffset * 0.00025 / ((taaPrevClip.z + 3) * 0.05);
		}
	}
#endif


	if ((renderFlags & Lod0BitMask) != 0) {
		float b = clamp(10 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 2.5, 0.0, 1.0);
		lod0Fade = 1 - b;
	}
	else    lod0Fade = 0.0;
	

#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0;
#else
	float intensity = 0.45;
#endif
	nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);
}
