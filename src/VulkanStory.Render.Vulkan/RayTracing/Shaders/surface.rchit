layout(location = 0) rayPayloadInEXT Surface result;
hitAttributeEXT vec2 barycentric;
void main() {
    Geometry mesh; Vertex a, b, c;
    triangle(uint(gl_InstanceCustomIndexEXT), uint(gl_PrimitiveID), mesh, a, b, c);
    vec3 weights = vec3(1.0 - barycentric.x - barycentric.y, barycentric);
    vec3 faceNormal = safeNormal(cross(b.position - a.position, c.position - a.position), vec3(0, 1, 0));
    vec3 normal = safeNormal(vertexNormal(a) * weights.x + vertexNormal(b) * weights.y + vertexNormal(c) * weights.z, faceNormal);
    normal = safeNormal(transpose(mat3(gl_WorldToObjectEXT)) * normal, faceNormal);
    vec4 colour = textureColour(mesh, a, b, c, weights, gl_HitTEXT);
    result.position = gl_WorldRayOriginEXT + gl_WorldRayDirectionEXT * gl_HitTEXT;
    result.previousPosition = gl_ObjectToWorldEXT * vec4(a.previousPosition * weights.x + b.previousPosition * weights.y + c.previousPosition * weights.z, 1.0);
    result.distance = gl_HitTEXT;
    result.normal = faceforward(normal, gl_WorldRayDirectionEXT, normal);
    result.albedo = max(colour.rgb, vec3(0.0));
    result.opacity = clamp(colour.a, 0.0, 1.0);
    // TODO(RT): supply actual normal, roughness, metallic, emission and transmission/IOR material maps instead of these defaults.
    result.roughness = (mesh.kind == 2u || mesh.kind == 3u) ? 0.025 : ((a.flags & (1u << 11)) != 0u ? 0.18 : 0.8);
    result.metallic = 0.0;
    float emission = float(a.flags & 255u) * weights.x + float(b.flags & 255u) * weights.y + float(c.flags & 255u) * weights.z;
    result.emission = result.albedo * (emission / 255.0);
    result.glow = emission / 255.0;
    result.kind = mesh.kind | (gl_HitKindEXT == gl_HitKindFrontFacingTriangleEXT ? 0u : 0x80000000u);
    result.identity = uvec2(uint(mesh.identity), uint(mesh.identity >> 32));
}
