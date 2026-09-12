#version 330 core

// Optimum (DLSS-FG design, step 3): the UI compose pass - the UI target over the
// display image, under premultiplied-alpha blending (ONE, ONE_MINUS_SRC_ALPHA):
//
//     dst = ui.rgb + dst.rgb * (1 - ui.a)
//
// The UI target already holds premultiplied colour: the GUI draws into it with the
// RGB factors it always had (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) over transparent black,
// which accumulates exactly the over-operator's premultiplied result, while the alpha
// channel accumulates coverage under the separate (ONE, ONE_MINUS_SRC_ALPHA) the
// scoped blend state supplies while this target is bound. So this pass passes the
// texel through untouched and lets the blend hardware do the operator.
//
// Why not ShaderPrograms.Blit: blit.fsh ends with "outColor.a = 1", which is right
// for a blit onto an opaque backbuffer and fatal here - a forced alpha of 1 makes
// every UI texel fully cover the scene and the world disappears behind a black frame.

uniform sampler2D uiTex;

in vec2 texCoord;

out vec4 outColor;

void main(void)
{
	outColor = texture(uiTex, texCoord);
}
