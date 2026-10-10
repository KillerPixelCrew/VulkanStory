#version 450
#extension GL_EXT_scalar_block_layout : require
#extension GL_GOOGLE_include_directive : require
#include "bindings.glsl"
#include "frame.glsl"
#include "specialization.glsl"

layout(push_constant, scalar) uniform OptimumDraw
{
    OPTIMUM_SAMPLER_SLOT(sampler2D, godrayParts);
};

#if defined(OPTIMUM_VERTEX)
layout(location = 0) out vec2 texCoord;
void main()
{
    float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0.5, 1.0);
    texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
}
#elif defined(OPTIMUM_FRAGMENT)
layout(location = 0) in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    outColor = vec4(texture(optimumTextures2D[godrayParts], texCoord).rgb, 0.0);
}
#else
#error Select OPTIMUM_VERTEX or OPTIMUM_FRAGMENT
#endif
