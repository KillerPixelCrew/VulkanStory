#version 330 core

// Optimum (DLSS-FG design, step 3): the UI compose pass - the fullscreen triangle,
// generated from gl_VertexID exactly as blit.vsh does, so the pass binds no vertex
// data and RenderFullscreenTriangle can drive it on both backends.

out vec2 texCoord;

void main(void)
{
	float x = -1.0 + float((gl_VertexID & 1) << 2);
	float y = -1.0 + float((gl_VertexID & 2) << 1);
	gl_Position = vec4(x, y, 0, 1);
	texCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
}
