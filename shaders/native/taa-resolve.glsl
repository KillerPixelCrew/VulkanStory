#version 450
#if defined(OPTIMUM_VERTEX) || defined(OPTIMUM_FRAGMENT)
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif
// Optimum-derived camera/depth guide; eight-sample transported TAA window.
// Raw cached scene/glow samples live on the fixed output-centre grid.
// Raster depth remains separate guide metadata. No accumulated output is cached.

layout(push_constant, scalar) uniform OptimumDraw {
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample0);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample1);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample2);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample3);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample4);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample5);
    OPTIMUM_SAMPLER_SLOT(sampler2D, sample6);
    OPTIMUM_SAMPLER_SLOT(sampler2D, oldSample6);
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, motionTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyDepth);
    OPTIMUM_SAMPLER_SLOT(sampler2D, historyCount);
    OPTIMUM_SAMPLER_SLOT(sampler2D, newCount);
};

layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
    vec2 renderSize;
    vec2 jitterPx;
    vec2 prevJitterPx;
    mat4 invViewProjJittered;
    mat4 prevViewProj;
    mat4 viewMatrix;
    vec3 cameraDelta;
    int resetHistory;
    int stage;
};

#if defined(OPTIMUM_VERTEX)
layout(location = 0) out vec2 texCoord;
void main() {
    float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0.5, 1.0);
    texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
}
#elif defined(OPTIMUM_FRAGMENT)
layout(location = 0) in vec2 texCoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outDepth;

bool finiteFloat(float value) { return !isnan(value) && !isinf(value); }
bool finiteVector(vec4 value) { return !any(isnan(value)) && !any(isinf(value)); }

vec4 catmullWeights(float f) {
	return vec4(f * (-0.5 + f * (1.0 - 0.5 * f)),
	            1.0 + f * f * (-2.5 + 1.5 * f),
	            f * (0.5 + f * (2.0 - 1.5 * f)),
	            f * f * (-0.5 + 0.5 * f));
}

// Metadata describes which individual ages exist at each previous-grid texel.
// Malformed metadata is unavailable, never a source of a fabricated high count.
int availableCount(sampler2D counts, ivec2 pixel) {
	float value = texelFetch(counts, pixel, 0).r;
	if (!finiteFloat(value) || value < 0.0 || value > 7.0 || floor(value) != value) return 0;
	return int(value);
}

vec4 bilinearWeights(vec2 f) {
	return vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y),
	            (1.0 - f.x) * f.y, f.x * f.y);
}

ivec2 bilinearPixel(ivec2 base, int corner) {
	return clamp(base + ivec2(corner % 2, corner / 2),
	             ivec2(0), ivec2(renderSize) - ivec2(1));
}

// Missing data changes support, not the lifetime of every neighboring sample.
// Rescale surviving positive weights before summing, so even a tiny available
// corner is a well-conditioned convex reconstruction without an epsilon cutoff.
bool samplePartialBilinear(sampler2D tex, sampler2D counts, vec2 uv,
                          int age, out vec4 value) {
	vec2 position = uv * renderSize - 0.5;
	ivec2 base = ivec2(floor(position));
	vec4 weights = bilinearWeights(fract(position));
	float maximumWeight = 0.0;
	for (int i = 0; i < 4; i++) {
		if (weights[i] > 0.0 && availableCount(counts, bilinearPixel(base, i)) > age)
			maximumWeight = max(maximumWeight, weights[i]);
		else
			weights[i] = 0.0;
	}
	value = vec4(0.0);
	if (maximumWeight <= 0.0) return false;
	float mass = 0.0;
	for (int i = 0; i < 4; i++) {
		if (weights[i] <= 0.0) continue;
		float weight = weights[i] / maximumWeight;
		vec4 tap = texelFetch(tex, bilinearPixel(base, i), 0);
		if (!finiteVector(tap)) return false;
		value += tap * weight;
		mass += weight;
	}
	if (mass <= 0.0) return false;
	value /= mass;
	return finiteVector(value);
}

// Signed Catmull reconstruction is retained only when its entire nonzero
// footprint exists for this age. Never normalize an incomplete signed kernel.
bool sampleSceneHistory(sampler2D tex, sampler2D counts, vec2 uv,
                        int age, out vec4 value) {
	vec2 position = uv * renderSize - 0.5;
	ivec2 base = ivec2(floor(position));
	vec2 f = fract(position);
	vec4 wx = catmullWeights(f.x), wy = catmullWeights(f.y);
	ivec2 maximum = ivec2(renderSize) - ivec2(1);
	bool complete = true;
	for (int y = 0; y < 4; y++)
	for (int x = 0; x < 4; x++) {
		if (wx[x] * wy[y] == 0.0) continue;
		ivec2 pixel = clamp(base + ivec2(x - 1, y - 1), ivec2(0), maximum);
		if (availableCount(counts, pixel) <= age) complete = false;
	}
	if (!complete) return samplePartialBilinear(tex, counts, uv, age, value);
	value = vec4(0.0);
	for (int y = 0; y < 4; y++)
	for (int x = 0; x < 4; x++) {
		float weight = wx[x] * wy[y];
		if (weight == 0.0) continue;
		ivec2 pixel = clamp(base + ivec2(x - 1, y - 1), ivec2(0), maximum);
		vec4 tap = texelFetch(tex, pixel, 0);
		if (!finiteVector(tap)) return false;
		value += tap * weight;
	}
	value = max(value, vec4(0.0));
	return finiteVector(value);
}

// Glow uses explicit positive support too, so unavailable age values are never
// read by an opaque hardware bilinear fetch.
bool sampleGlowHistory(sampler2D tex, sampler2D counts, vec2 uv,
                       int age, out vec4 value) {
	return samplePartialBilinear(tex, counts, uv, age, value);
}

// Nested per-texel counts make this maximum the longest contiguous prefix with
// strictly positive bilinear support. Outer Catmull lobes cannot erase it.
int historyPrefix(sampler2D counts, vec2 uv) {
	vec2 position = uv * renderSize - 0.5;
	ivec2 base = ivec2(floor(position));
	vec4 weights = bilinearWeights(fract(position));
	int count = 0;
	for (int i = 0; i < 4; i++) {
		if (weights[i] <= 0.0) continue;
		count = max(count, availableCount(counts, bilinearPixel(base, i)));
	}
	return count;
}

struct HistoryGuide {
	ivec2 pixel;
	vec2 historyUv;
	float linearDepth;
	float reactive;
	int nOld;
	bool sparseDepthMismatch;
};

// Keep the existing nearest-depth dilation and written/camera motion contract.
// Resolved colors use fixed output centres; depth metadata uses its raster grid.
HistoryGuide buildGuide(sampler2D currentDepth, sampler2D currentMotion,
                        sampler2D oldDepth, sampler2D oldCount) {
	HistoryGuide guide;
	guide.pixel = ivec2(clamp(texCoord * renderSize, vec2(0.0), renderSize - 1.0));
	guide.historyUv = (vec2(guide.pixel) + 0.5) / renderSize;
	guide.linearDepth = 0.0;
	guide.reactive = 0.0;
	guide.nOld = 0;
	guide.sparseDepthMismatch = false;
	vec2 invSize = 1.0 / renderSize;
	vec2 centre = vec2(guide.pixel) + 0.5;
	float centreDepth = texelFetch(currentDepth, guide.pixel, 0).r;
	if (!finiteFloat(centreDepth)) return guide;
	float closestDepth = centreDepth, farthestDepth = 0.0;
	ivec2 closestPixel = guide.pixel;
	float neighbourhoodDepth[9];
	for (int y = -1; y <= 1; y++)
	for (int x = -1; x <= 1; x++) {
		ivec2 p = clamp(guide.pixel + ivec2(x, y), ivec2(0), ivec2(renderSize) - 1);
		float depth = texelFetch(currentDepth, p, 0).r;
		if (!finiteFloat(depth)) return guide;
		neighbourhoodDepth[(y + 1) * 3 + x + 1] = depth;
		if (depth < closestDepth) { closestDepth = depth; closestPixel = p; }
		farthestDepth = max(farthestDepth, depth);
	}
	vec2 ndc = centre * invSize * 2.0 - 1.0;
	vec4 worldH = invViewProjJittered * vec4(ndc, centreDepth * 2.0 - 1.0, 1.0);
	vec3 world = worldH.xyz / max(abs(worldH.w), 1e-6) * sign(worldH.w);
	guide.linearDepth = -(viewMatrix * vec4(world, 1.0)).z;
	if (!finiteFloat(guide.linearDepth)) { guide.linearDepth = 0.0; return guide; }
	vec2 closestCentre = vec2(closestPixel) + 0.5;
	vec2 closestNdc = closestCentre * invSize * 2.0 - 1.0;
	vec4 closestH = invViewProjJittered * vec4(closestNdc, closestDepth * 2.0 - 1.0, 1.0);
	vec3 closestWorld = closestH.xyz / max(abs(closestH.w), 1e-6) * sign(closestH.w);
	float closestLinearDepth = -(viewMatrix * vec4(closestWorld, 1.0)).z;
	vec4 motion = texelFetch(currentMotion, closestPixel, 0);
	if (!finiteVector(motion) || !finiteFloat(closestLinearDepth)) return guide;
	guide.reactive = clamp(motion.b, 0.0, 1.0);
	vec2 currentUnjittered = closestCentre - jitterPx;
	vec2 mv;
	bool written = motion.a > 0.0 &&
		abs(motion.a - closestDepth) <= max(2e-4, 8e-4 * closestDepth);
	if (written) {
		mv = motion.rg;
	} else {
		bool sky = closestDepth >= 0.999999;
		vec4 nearH = invViewProjJittered * vec4(closestNdc, -1.0, 1.0);
		vec3 skyDirection = closestH.xyz * nearH.w - nearH.xyz * closestH.w;
		if ((closestH.w < 0.0) != (nearH.w < 0.0)) skyDirection = -skyDirection;
		vec4 prevClip = sky ? prevViewProj * vec4(skyDirection, 0.0)
		                   : prevViewProj * vec4(closestWorld + cameraDelta, 1.0);
		if (!finiteVector(prevClip) || prevClip.w <= 1e-6) return guide;
		vec2 prevPixel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * renderSize;
		mv = prevPixel - currentUnjittered;
	}
	guide.historyUv = (centre + mv) * invSize;
	vec2 depthUv = (currentUnjittered + mv + prevJitterPx) * invSize;
	if (!finiteVector(vec4(guide.historyUv, depthUv)) || resetHistory != 0 ||
	    any(lessThan(guide.historyUv, vec2(0.0))) ||
	    any(greaterThan(guide.historyUv, vec2(1.0)))) return guide;
	int count = historyPrefix(oldCount, guide.historyUv);
	if (count == 0) return guide; // No reads of fresh or unavailable old samples.
	float historyNearest = texture(oldDepth, depthUv).r;
	if (!finiteFloat(historyNearest)) return guide;
	for (int y = -1; y <= 1; y++)
	for (int x = -1; x <= 1; x++) {
		float depth = texture(oldDepth, depthUv + vec2(x, y) * invSize).r;
		if (finiteFloat(depth)) historyNearest = min(historyNearest, depth);
	}
	int nearDepthTaps = 0;
	for (int i = 0; i < 9; i++)
		if (abs(neighbourhoodDepth[i] - closestDepth) <= 2e-4) nearDepthTaps++;
	bool distantDepthEdge = closestLinearDepth > 20.0 &&
		farthestDepth - closestDepth > 2e-4 && nearDepthTaps <= 2;
	bool depthMismatch = abs(historyNearest - closestLinearDepth) >
		0.5 + 0.08 * closestLinearDepth;
	if (depthMismatch && !distantDepthEdge) return guide;
	guide.sparseDepthMismatch = depthMismatch;
	guide.nOld = count;
	return guide;
}

bool windowMean(HistoryGuide guide, out vec4 current, out vec4 mean) {
	// The first seven terms are already transported into this completed new bank.
	current = texelFetch(optimumTextures2D[sample0], guide.pixel, 0);
	mean = current;
	if (!finiteVector(current)) { current = vec4(0.0); mean = current; return false; }
	int count = guide.nOld;
	float published = texelFetch(optimumTextures2D[newCount], guide.pixel, 0).r;
	int expected = min(count + 1, 7);
	// A cache-stage nonfinite reset must govern both color and glow resolve.
	if (!finiteFloat(published) || published < float(expected) ||
	    published > 7.0 || floor(published) != published) count = 0;
	vec4 value;
	if (count > 0) {
		value = texelFetch(optimumTextures2D[sample1], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count > 1) {
		value = texelFetch(optimumTextures2D[sample2], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count > 2) {
		value = texelFetch(optimumTextures2D[sample3], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count > 3) {
		value = texelFetch(optimumTextures2D[sample4], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count > 4) {
		value = texelFetch(optimumTextures2D[sample5], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count > 5) {
		value = texelFetch(optimumTextures2D[sample6], guide.pixel, 0);
		if (!finiteVector(value)) return false;
		mean += value;
	}
	if (count == 7) {
		bool valid = stage == 0
			? sampleSceneHistory(optimumTextures2D[oldSample6], optimumTextures2D[historyCount], guide.historyUv, 6, value)
			: sampleGlowHistory(optimumTextures2D[oldSample6], optimumTextures2D[historyCount], guide.historyUv, 6, value);
		if (!valid) return false;
		mean += value;
	}
	mean /= float(min(count + 1, 8));
	return finiteVector(mean);
}

void main() {
	HistoryGuide guide = buildGuide(optimumTextures2D[depthTex], optimumTextures2D[motionTex], optimumTextures2D[historyDepth], optimumTextures2D[historyCount]);
	vec4 current, mean;
	if (!windowMean(guide, current, mean)) mean = current;
	// Reactivity controls displayed response; it never alters stored sample ages.
	vec4 displayed = mix(mean, current, guide.reactive);
	if (stage == 0) {
		outColor = displayed;
		outDepth = vec4(guide.linearDepth);
	} else {
		// Preserve the existing sparse-depth exception's current-glow response.
		outGlow = guide.sparseDepthMismatch ? current : displayed;
	}
}

#endif
