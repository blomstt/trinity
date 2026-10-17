#version 450
#extension GL_GOOGLE_include_directive : require

// The sky behind everything: the precomputed sky-view radiance and the sun's disc.

#include "frame.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const float PI_SKY = 3.14159265359;

vec2 skyViewUv(vec3 dir) {
    float lon = atan(dir.z, dir.x);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    float v = 0.5 + 0.5 * sign(lat) * sqrt(abs(lat) / (0.5 * PI_SKY));
    return vec2(lon / (2.0 * PI_SKY) + 0.5, 1.0 - v);
}

void main() {
    // The view direction through this pixel, from its clip-space position on the far plane
    vec4 far = frame.invViewProj * vec4(inUv * 2.0 - 1.0, 0.0, 1.0);
    vec3 dir = normalize(far.xyz / far.w - frame.cameraPos.xyz);
    vec3 color = textureLod(skyView, skyViewUv(dir), 0.0).rgb;

    // The sun's disc, 0.53 degrees across, darker towards its edge
    float c = dot(dir, -frame.sunDir.xyz);
    const float SUN_COS = 0.99998930; // cos(0.265 degrees)
    if (c > SUN_COS && dir.y > -0.01) {
        float edge = clamp((c - SUN_COS) / (1.0 - SUN_COS), 0.0, 1.0);
        color += frame.sunColor.rgb * 2000.0 * (0.4 + 0.6 * sqrt(edge));
    }
    int debugView = int(frame.params.z);
    if (debugView != 0) color = vec3(0.05);
    outColor = vec4(color, 1.0);
}
