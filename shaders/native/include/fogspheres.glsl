// Native port of the game include fogspheres.ash (docs/vulkan.md).
// optimum-port-of: fogspheres.ash
// optimum-port: verbatim
// optimum-program-uniform: float fogSpheres[3 * 8]
// optimum-program-uniform: int fogSphereQuantity
//
// Loose uniforms: the members this file owns read the FrameGlobals block (frame.glsl), frame
// textures come from bindings.glsl, and every optimum-program-uniform above is declared by the
// including program (push block, record, or the frame block when it includes that name's owner).

#ifndef OPTIMUM_INCLUDE_FOGSPHERES_GLSL
#define OPTIMUM_INCLUDE_FOGSPHERES_GLSL

// const int MaxSpheres = 3;
/// Each sphere has 8 floats:
/// 3 floats x/y/z offset to the player
/// 1 float radius
/// 1 float density
/// 3 floats rgb color


// Length of the camera-to-fragment segment inside one sphere. The center is
// camera-relative, so this also handles a camera already inside the sphere.
float sphereRayLength(vec3 center, float radius, vec3 worldPos) {
	float depth = length(worldPos);
	if (depth <= 1e-6 || radius <= 0.0) return 0.0;
	vec3 direction = worldPos / depth;
	float projected = dot(center, direction);
	float halfChordSquared = radius * radius - dot(center, center) + projected * projected;
	if (halfChordSquared < 0.0) return 0.0;
	float halfChord = sqrt(halfChordSquared);
	return max(0.0, min(depth, projected + halfChord) - max(0.0, projected - halfChord));
}

float getSpheresFogAmount(vec3 worldPos) {
	if (fogSphereQuantity == 0) return 0.0;

	float fogamount = 0;

	for (int i = 0; i < fogSphereQuantity; i++) {
		vec3 L = vec3(fogSpheres[i * 8], fogSpheres[i * 8 +1], fogSpheres[i * 8 + 2]);
		float radius = fogSpheres[i * 8 + 3];
		float density = fogSpheres[i * 8 + 4];

		fogamount += sphereRayLength(L, radius, worldPos) * density;
	}

	return fogamount;
}


vec4 applySpheresFog(vec4 color, float standardFogAmount, vec3 worldPos) {
	if (fogSphereQuantity == 0) return color;

	for (int i = 0; i < fogSphereQuantity; i++) {
		vec3 L = vec3(fogSpheres[i * 8], fogSpheres[i * 8 +1], fogSpheres[i * 8 + 2]);
		float radius = fogSpheres[i * 8 + 3];
		float density = fogSpheres[i * 8 + 4];
		vec3 fogrgb = vec3(fogSpheres[i * 8 + 5], fogSpheres[i * 8 + 6], fogSpheres[i * 8 + 7]);

		float fogamount = sphereRayLength(L, radius, worldPos) * density;

		color.rgb = mix(color.rgb, fogrgb, clamp(fogamount - (standardFogAmount - fogamount), 0, 1));
	}


	return color;
}

#endif
