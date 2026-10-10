#version 330 core
uniform sampler2D godrayParts;
in vec2 texCoord;
layout(location = 0) out vec4 outColor;
void main()
{
    // Additive RGB blending preserves Primary's alpha; grading still happens after SR.
    outColor = vec4(texture(godrayParts, texCoord).rgb, 0.0);
}
