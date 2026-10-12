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

layout(push_constant, scalar) uniform OptimumDraw {
    OPTIMUM_SAMPLER_SLOT(sampler2D, depthTex);
    OPTIMUM_SAMPLER_SLOT(sampler2D, motionTex);
};
layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
    vec2 renderSize;
    vec2 jitterPx;
    mat4 invViewProjJittered;
    mat4 prevViewProj;
    vec3 cameraDelta;
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
layout(location = 0) out vec4 outMotion;
bool finiteFloat(float value) { return !isnan(value) && !isinf(value); }
bool finiteVector(vec4 value) { return !any(isnan(value)) && !any(isinf(value)); }
void main() {
    ivec2 pixel = ivec2(clamp(texCoord * renderSize, vec2(0.0), renderSize - 1.0));
    vec2 centre = vec2(pixel) + 0.5;
    float depth = texelFetch(optimumTextures2D[depthTex], pixel, 0).r;
    vec4 writer = texelFetch(optimumTextures2D[motionTex], pixel, 0);
    float reactive = finiteFloat(writer.b) ? clamp(writer.b, 0.0, 1.0) : 1.0;
    outMotion = vec4(0.0, 0.0, 1.0, 0.0);
    if (!finiteFloat(depth) || depth < 0.0 || depth > 1.0) return;
    bool written = finiteVector(writer) && writer.a > 0.0 &&
        abs(writer.a - depth) <= max(2e-4, 8e-4 * depth);
    if (written) { outMotion = vec4(writer.rg, reactive, writer.a); return; }
    vec2 ndc = centre / renderSize * 2.0 - 1.0;
    vec4 worldH = invViewProjJittered * vec4(ndc, depth * 2.0 - 1.0, 1.0);
    if (!finiteVector(worldH)) return;
    vec4 previousClip;
    if (depth >= 0.999999) {
        vec4 nearH = invViewProjJittered * vec4(ndc, -1.0, 1.0);
        vec3 direction = worldH.xyz * nearH.w - nearH.xyz * worldH.w;
        if ((worldH.w < 0.0) != (nearH.w < 0.0)) direction = -direction;
        previousClip = prevViewProj * vec4(direction, 0.0);
    } else {
        if (abs(worldH.w) <= 1e-6) return;
        previousClip = prevViewProj * vec4(worldH.xyz / worldH.w + cameraDelta, 1.0);
    }
    if (!finiteVector(previousClip) || previousClip.w <= 1e-6) return;
    vec2 previousPixel = (previousClip.xy / previousClip.w * 0.5 + 0.5) * renderSize;
    vec2 velocity = previousPixel - (centre - jitterPx);
    if (!finiteVector(vec4(velocity, 0.0, 0.0)) || any(greaterThan(abs(velocity), vec2(65504.0)))) return;
    outMotion = vec4(velocity, reactive, 0.0);
}
#endif
