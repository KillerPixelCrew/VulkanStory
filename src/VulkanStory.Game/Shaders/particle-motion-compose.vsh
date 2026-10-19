#version 330 core
void main() {
    vec2 position = vec2(-1.0 + float((gl_VertexID & 1) << 2), -1.0 + float((gl_VertexID & 2) << 1));
    gl_Position = vec4(position, 0.0, 1.0);
}
