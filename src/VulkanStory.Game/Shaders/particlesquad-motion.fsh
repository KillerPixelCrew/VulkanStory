#version 330 core
#include vulkanstory-motion.ash
in vec4 color;
in vec2 uv;
in vec4 quadPreviousClip;
flat in float quadHistoryValid;
uniform vec2 taaRenderSize;
uniform vec2 taaJitterPx;
layout(location = 0) out vec4 outMotion;
void main() {
    vec2 edge = vec2(max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
        max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9)));
    float alpha = clamp(color.a, 0.0, 1.0) * (1.0 - length(edge) * 10.0);
    if (alpha <= 1.0 / 255.0) discard;
    vec2 velocity = vec2(0.0);
    bool valid = alpha >= 0.9 && quadHistoryValid > 0.5 && quadPreviousClip.w > 1e-6;
    if (valid) {
        velocity = vulkanstoryMotionVector(quadPreviousClip, taaRenderSize, taaJitterPx);
        valid = !any(isnan(velocity)) && !any(isinf(velocity)) && !any(greaterThan(abs(velocity), vec2(65504.0)));
    }
    if (!valid) velocity = vec2(0.0);
    // Untracked/soft foreground quads occlude deeper tracked quad guides.
    outMotion = vec4(velocity, valid ? alpha : -alpha, gl_FragCoord.z);
}
