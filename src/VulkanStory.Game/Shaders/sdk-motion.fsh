#version 330 core
// Complete SDK motion input, independent of selected mod geometry/lighting.
uniform sampler2D depthTex;
uniform sampler2D motionTex;
uniform vec2 renderSize;
uniform vec2 jitterPx;
uniform mat4 invViewProjJittered;
uniform mat4 prevViewProj;
uniform vec3 cameraDelta;
in vec2 texCoord;
out vec4 outMotion;

bool finiteFloat(float value) { return !isnan(value) && !isinf(value); }
bool finiteVector(vec4 value) { return !any(isnan(value)) && !any(isinf(value)); }

void main() {
    ivec2 pixel = ivec2(clamp(texCoord * renderSize, vec2(0.0), renderSize - 1.0));
    vec2 centre = vec2(pixel) + 0.5;
    float depth = texelFetch(depthTex, pixel, 0).r;
    vec4 writer = texelFetch(motionTex, pixel, 0);
    float reactive = finiteFloat(writer.b) ? clamp(writer.b, 0.0, 1.0) : 1.0;
    // Finite invalid reprojections have no recoverable velocity; retain their
    // reactive rejection without certifying an object history through alpha.
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
        // A direction cancels both camera translations, including rebases.
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
    // RGBA16F cannot represent a larger displacement. Such projections cannot
    // address any useful history; reactive stays fully rejected, alpha zero.
    if (!finiteVector(vec4(velocity, 0.0, 0.0)) || any(greaterThan(abs(velocity), vec2(65504.0)))) return;
    outMotion = vec4(velocity, reactive, 0.0);
}
