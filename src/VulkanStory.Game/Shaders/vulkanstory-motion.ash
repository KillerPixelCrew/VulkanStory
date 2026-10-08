// Owned motion helpers. Render pixels, GL row order, unjittered previous clip.
#ifndef VULKANSTORY_MOTION_HELPERS
#define VULKANSTORY_MOTION_HELPERS
vec2 vulkanstoryMotionVector(vec4 previousClip, vec2 renderSize, vec2 jitter)
{
    vec2 previousPixel = (previousClip.xy / previousClip.w * 0.5 + 0.5) * renderSize;
    return previousPixel - (gl_FragCoord.xy - jitter);
}
#endif
