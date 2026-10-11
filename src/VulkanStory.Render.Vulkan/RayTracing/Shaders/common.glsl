#version 460
#extension GL_EXT_ray_tracing : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_buffer_reference2 : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require
#extension GL_EXT_nonuniform_qualifier : require

const float PI = 3.141592653589793;
struct Vertex {
    vec3 position; uint flags;
    vec2 uv; uint color; uint material;
    vec2 secondaryUv; uint auxiliary; uint packedNormal;
    vec3 previousPosition; uint jointIndex;
};
struct Geometry {
    uint64_t vertices; uint64_t indices; uint64_t identity;
    uint textureSlot; uint kind; uint lod; uint textureFlags;
};
layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Vertices { Vertex data[]; };
layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Indices { uint data[]; };
layout(set = 0, binding = 0) uniform accelerationStructureEXT scene;
layout(set = 0, binding = 1, std140) uniform Frame {
    mat4 inverseProjection;
    mat4 cameraToWorld;
    mat4 previousWorldToClip;
    mat4 currentWorldToClip;
    vec4 originDeltaFar;
    vec4 sizeJitter;
    vec4 sunDirectionIntensity;
    vec4 sunColour;
    vec4 skyUpper;
    vec4 skyHorizon;
    uvec4 options; // frame identity, samples, bounces, history reset
} frame;
layout(set = 0, binding = 2, scalar) readonly buffer SceneGeometry { Geometry geometry[]; };
layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(set = 0, binding = 3, rgba32f) uniform image2D noisyColour;
layout(set = 0, binding = 4, rgba16f) uniform image2D normalRoughness;
layout(set = 0, binding = 5, rgba16f) uniform image2D diffuseAlbedo;
layout(set = 0, binding = 6, rgba16f) uniform image2D specularAlbedo;
layout(set = 0, binding = 7, r32f) uniform image2D depth;
layout(set = 0, binding = 8, rgba16f) uniform image2D motion;
layout(set = 0, binding = 9, r32f) uniform image2D specularDistance;
layout(set = 0, binding = 10, rgba32f) uniform image2D noisyDiffuse;
layout(set = 0, binding = 11, rgba32f) uniform image2D noisySpecular;
layout(set = 0, binding = 12, rgba32f) uniform image2D stableRadiance;
layout(set = 0, binding = 13, r32f) uniform image2D glow;
layout(set = 0, binding = 14, r32f) uniform image2D viewDepth;

struct Surface {
    vec3 position; float distance;
    vec3 normal; float roughness;
    vec3 albedo; float metallic;
    vec3 emission; float opacity;
    uint kind; uvec2 identity; float glow;
    vec3 previousPosition;
};
uint hash(uint value) {
    value ^= value >> 16; value *= 0x7feb352du;
    value ^= value >> 15; value *= 0x846ca68bu;
    return value ^ (value >> 16);
}
float random(inout uint state) { state = hash(state + 0x9e3779b9u); return float(state >> 8) * (1.0 / 16777216.0); }
vec3 safeNormal(vec3 value, vec3 fallback) { return dot(value, value) > 1e-12 ? normalize(value) : fallback; }
vec3 flagNormal(uint flags) {
    vec3 magnitude = vec3((flags >> 14) & 7u, (flags >> 18) & 7u, (flags >> 22) & 7u);
    vec3 signValue = vec3(1.0) - 2.0 * vec3((flags >> 13) & 1u, (flags >> 17) & 1u, (flags >> 21) & 1u);
    return magnitude * signValue;
}
vec3 packedNormal(uint bits) {
    return vec3(int(bits << 22) >> 22, int(bits << 12) >> 22, int(bits << 2) >> 22) / 511.0;
}
vec3 vertexNormal(Vertex vertex) { return vertex.packedNormal != 0u ? packedNormal(vertex.packedNormal) : flagNormal(vertex.flags); }
void triangle(uint instance, uint primitive, out Geometry mesh, out Vertex a, out Vertex b, out Vertex c) {
    mesh = geometry[instance];
    Indices indices = Indices(mesh.indices);
    Vertices vertices = Vertices(mesh.vertices);
    a = vertices.data[indices.data[primitive * 3u]];
    b = vertices.data[indices.data[primitive * 3u + 1u]];
    c = vertices.data[indices.data[primitive * 3u + 2u]];
}
vec4 textureColour(Geometry mesh, Vertex a, Vertex b, Vertex c, vec3 weights, float distance) {
    vec2 uv = a.uv * weights.x + b.uv * weights.y + c.uv * weights.z;
    vec2 dimensions = vec2(textureSize(textures[nonuniformEXT(mesh.textureSlot)], 0));
    vec2 ab = b.uv - a.uv, ac = c.uv - a.uv;
    float uvArea = abs(ab.x * ac.y - ab.y * ac.x);
    float area = length(cross(b.position - a.position, c.position - a.position));
    float density = sqrt(uvArea / max(area, 1e-10));
    float footprint = distance * 2.0 * abs(frame.inverseProjection[1][1]) / max(frame.sizeJitter.y, 1.0);
    float lod = max(0.0, log2(max(footprint * density * max(dimensions.x, dimensions.y), 1.0)));
    vec4 colour = textureLod(textures[nonuniformEXT(mesh.textureSlot)], uv, lod);
    if (mesh.kind == 4u) {
        vec2 inset = vec2(0.5) / max(dimensions, vec2(1.0));
        vec2 ua = a.secondaryUv - vec2(a.auxiliary & 1u, (a.auxiliary >> 1) & 1u) * inset;
        vec2 ub = b.secondaryUv - vec2(b.auxiliary & 1u, (b.auxiliary >> 1) & 1u) * inset;
        vec2 uc = c.secondaryUv - vec2(c.auxiliary & 1u, (c.auxiliary >> 1) & 1u) * inset;
        vec4 overlay = textureLod(textures[nonuniformEXT(mesh.textureSlot)], ua * weights.x + ub * weights.y + uc * weights.z, lod);
        colour.rgb = mix(colour.rgb, overlay.rgb, overlay.a);
        colour.a = max(colour.a, overlay.a);
    }
    // UNORM colour atlases carry encoded asset colour; SRGB image views already decode it.
    if ((mesh.textureFlags & 1u) != 0u) colour.rgb = pow(max(colour.rgb, vec3(0.0)), vec3(2.2));
    return colour;
}
vec3 environment(vec3 direction) {
    float horizon = sqrt(clamp(abs(direction.y), 0.0, 1.0));
    return mix(frame.skyHorizon.rgb, frame.skyUpper.rgb, horizon);
}
vec3 fresnel(vec3 f0, float cosine) { return f0 + (vec3(1.0) - f0) * pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0); }
float distribution(float nh, float alpha) {
    float a2 = alpha * alpha;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-10);
}
float smith(float cosine, float alpha) {
    return 2.0 * cosine / max(cosine + sqrt(alpha * alpha + (1.0 - alpha * alpha) * cosine * cosine), 1e-6);
}
void bsdf(Surface s, vec3 view, vec3 light, out vec3 diffuse, out vec3 specular, out float pdf) {
    float nl = max(dot(s.normal, light), 0.0), nv = max(dot(s.normal, view), 0.0);
    vec3 halfVector = safeNormal(view + light, s.normal);
    float nh = max(dot(s.normal, halfVector), 0.0), vh = max(dot(view, halfVector), 0.0);
    float alpha = max(s.roughness * s.roughness, 0.001);
    vec3 f0 = mix(vec3(0.04), s.albedo, s.metallic);
    vec3 f = fresnel(f0, vh);
    diffuse = (vec3(1.0) - f) * s.albedo * (1.0 - s.metallic) / PI;
    specular = f * distribution(nh, alpha) * smith(nl, alpha) * smith(nv, alpha) / max(4.0 * nl * nv, 1e-6);
    float weight = clamp(max(max(f0.r, f0.g), f0.b), 0.1, 0.9);
    pdf = (1.0 - weight) * nl / PI + weight * distribution(nh, alpha) * nh / max(4.0 * vh, 1e-6);
    if (nl == 0.0 || nv == 0.0) { diffuse = vec3(0); specular = vec3(0); pdf = 0.0; }
}
vec3 basisDirection(vec3 normal, vec3 local) {
    vec3 tangent = normalize(cross(abs(normal.z) < 0.999 ? vec3(0, 0, 1) : vec3(0, 1, 0), normal));
    return tangent * local.x + cross(normal, tangent) * local.y + normal * local.z;
}
vec3 sampleBsdf(Surface s, vec3 view, inout uint rng, out bool specular) {
    vec3 f0 = mix(vec3(0.04), s.albedo, s.metallic);
    float weight = clamp(max(max(f0.r, f0.g), f0.b), 0.1, 0.9);
    specular = random(rng) < weight;
    float u = random(rng), phi = 2.0 * PI * random(rng);
    if (specular) {
        float alpha = max(s.roughness * s.roughness, 0.001);
        float cosine = sqrt((1.0 - u) / max(1.0 + (alpha * alpha - 1.0) * u, 1e-6));
        float sine = sqrt(max(0.0, 1.0 - cosine * cosine));
        vec3 h = basisDirection(s.normal, vec3(cos(phi) * sine, sin(phi) * sine, cosine));
        return reflect(-view, h);
    }
    return basisDirection(s.normal, vec3(cos(phi) * sqrt(u), sin(phi) * sqrt(u), sqrt(1.0 - u)));
}
