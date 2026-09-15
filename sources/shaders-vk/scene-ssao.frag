#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
// Native port of scene-ssao.fsh (docs/vulkan-native-shaders.md). The SSAOLEVEL > 1 preprocessor branch is a
// specialization-constant branch with the same expression; it gates no declaration, so no variant axes.
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"
#include "scene-ssao.interface.glsl"

layout(location = 0) in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    float ao = texture(optimumTextures2D[ssaoScene], texCoord).r;
    if (OPTIMUM_SSAOLEVEL > 1) {
        ao = min(ao, texture(optimumTextures2D[ssaoScene], texCoord - vec2(0.0, invRenderHeight)).r);
    }
    // EnumBlendMode.Multiply: dstRGB * (1 - srcAlpha). RGB is not read.
    outColor = vec4(0.0, 0.0, 0.0, 1.0 - clamp(ao, 0.0, 1.0));
}
