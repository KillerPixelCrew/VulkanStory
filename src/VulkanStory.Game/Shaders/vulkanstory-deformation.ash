uniform int vsComputeDeformation = 0;
layout(std430) readonly buffer VulkanDeformation { vec4 values[]; } vsGeometry;
vec4 vsDeformedPosition() { return vsGeometry.values[gl_VertexID * 4]; }
vec4 vsDeformedNormal(vec3 normal) {
    int index = gl_VertexID * 4;
    return vsGeometry.values[index + 1] * normal.x + vsGeometry.values[index + 2] * normal.y +
        vsGeometry.values[index + 3] * normal.z;
}
