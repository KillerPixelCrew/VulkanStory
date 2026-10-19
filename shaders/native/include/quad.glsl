// Stock particlesquad.vsh billboard and warp operations, shared by compute and motion replay.
#ifndef VULKANSTORY_QUAD_GEOMETRY
#define VULKANSTORY_QUAD_GEOMETRY
mat4 vsQuadBillboard(mat4 view, vec3 origin, vec3 direction, bool rain) {
    view[3] = view * vec4(origin, 1.0);
    if (rain) {
        direction = normalize(direction);
        float angle = acos(dot(direction.xy, vec2(0.0, -1.0)));
        mat4 rotation = mat4(cos(angle), -sin(angle), 0, 0,
            sin(angle), cos(angle), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        view = view * rotation;
    }
    view[0].xyz = vec3(1, 0, 0);
    if (!rain) view[1].xyz = vec3(0, 1, 0);
    view[2].xyz = vec3(0, 0, 1);
    return view;
}
vec4 vsQuadWarp(WarpState warp, vec3 position, bool rain, int flags) {
    vec4 world = vec4(position, 1.0);
    if (rain) { world.y = world.y * 8 - 3; world.xz /= 3.5; }
    return applyGlobalWarpingState(warp, applyVertexWarpingState(warp, flags, world));
}
#endif
