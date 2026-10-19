#version 330 core
uniform sampler2D particleMotion;
uniform sampler2D particleDepth;
uniform sampler2D sceneMotion;
layout(location = TAAMOTIONLOCATION) out vec4 outMotion;
void main() {
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec4 motion = texelFetch(particleMotion, pixel, 0);
    float depth = texelFetch(particleDepth, pixel, 0).r;
    if (motion.b <= 0.0 || motion.a <= 0.0 || depth >= 1.0 || abs(motion.a - depth) > max(2e-4, 8e-4 * depth)) discard;
    // Retain OIT/material reactivity, including unknown transparent layers.
    outMotion = vec4(motion.rg, texelFetch(sceneMotion, pixel, 0).b, motion.a);
    gl_FragDepth = depth;
}
