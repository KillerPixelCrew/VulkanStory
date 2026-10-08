#version 330 core
#include vulkanstory-motion.ash
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 vertexPosition;
in vec3 blockLight;
in vec4 gnormal;
in vec4 camPos;
in float lod0Fade;
in float nb;

uniform float alphaTest;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform float dayLight;
uniform int haxyFade;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

// TAA motion vectors (Optimum P3). TAAMOTION and TAAMOTIONLOCATION are stamped
// by ShaderRegistry.registerDefaultShaderCodePrefixes; the location is the
// Primary colour attachment the motion texture occupies (2 without the SSAO
// G-buffer, 4 with it), which is the same index ClientPlatformWindows appends
// it at. With TAA off this preprocesses back to vanilla.
#if TAAMOTION > 0
in vec4 taaPrevClip;
uniform vec2 taaRenderSize;   // render-target size in pixels
uniform vec2 taaJitterPx;     // this frame's sub-pixel shear, in pixels
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;

// rg = previousPixel - currentUnjitteredPixel in render pixels, b = reactive,
// a = this fragment's window depth. The resolve accepts the vector only when
// a matches the depth buffer, so a zero alpha means "nobody wrote here" and
// sends the pixel to the camera-motion fallback - which is exactly what a
// previous position behind the previous camera deserves.
vec4 taaMotionVector(float reactive)
{
	if (taaPrevClip.w <= 1e-6) return vec4(0.0);

	return vec4(vulkanstoryMotionVector(taaPrevClip, taaRenderSize, taaJitterPx), reactive, gl_FragCoord.z);
}
#endif

#include vertexflagbits.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include colormap.fsh
#include underwatereffects.fsh

void main() 
{

	// --- Vanilla path (no tiling) ---
	vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;
	
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);
	
	float b = getBrightnessFromShadowMap();
	
	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz); 
	
	float glow = 0;
	float godrayLevel = 0;

	if (haxyFade > 0) {
	    if (rgba.a < 0.999) {
			vec4 skyColor = vec4(1);
			vec4 skyGlow = vec4(1);
			float sealevelOffsetFactor = 0.25;
		
			getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
			godrayLevel = skyGlow.g;
			outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1-dayLight, max(0.0, rgba.a)));
	    }
	}

	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);


#if NORMALVIEW == 0	
	float aTest = outColor.a + max(0.0, 1 - rgba.a) * min(1, outColor.a * 10) - lod0Fade;
	
	if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;
	
	if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif


#if SHINYEFFECT > 0
	if ((renderFlags & ReflectiveBitMask) != 0) {
		outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2 * fogAmount + 2*(1-b), 0, 1));
	}
	glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
#endif


#if SSAOLEVEL > 0
	outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
	outGNormal = gnormal;
#if OPTIMUMAO > 0
	// Optimum AO class channel (C.5) comes from the vertex, not the render pool.
#endif
#endif

#if NORMALVIEW > 0
	outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);	
#endif
	
	outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
#if TAAMOTION > 0
	outMotion = taaMotionVector(0.0);
#endif
}
