layout(std430, set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer VulkanDeformation
{ vec4 values[]; } vsGeometry;
vec4 vsDeformedPosition() { return vsGeometry.values[gl_VertexIndex * 4]; }
vec4 vsDeformedNormal(vec3 normal) {
    int index = gl_VertexIndex * 4;
    return vsGeometry.values[index + 1] * normal.x + vsGeometry.values[index + 2] * normal.y +
        vsGeometry.values[index + 3] * normal.z;
}
