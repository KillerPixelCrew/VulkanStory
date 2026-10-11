layout(location = 0) rayPayloadInEXT Surface result;
void main() {
    result.distance = -1.0;
    result.previousPosition = vec3(0);
    result.emission = environment(gl_WorldRayDirectionEXT);
    result.glow = 0.0;
}
