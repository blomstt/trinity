#version 450
#extension GL_GOOGLE_include_directive : require

// An editor ground grid around a point, from six vertices.

#include "frame.glsl"

layout(location = 0) out vec3 outWorld;

const vec2 CORNERS[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 c = CORNERS[gl_VertexIndex] * frame.grid.x + frame.grid.yz;
    outWorld = vec3(c.x, 0.0, c.y);
    gl_Position = frame.viewProj * vec4(outWorld, 1.0);
}
