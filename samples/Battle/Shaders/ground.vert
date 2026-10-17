#version 450

// A big flat field made from six vertices; no vertex buffer
layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params;
} frame;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec3 outColor;
layout(location = 2) out vec3 outWorld;

const float HALF = 1500.0;
const vec2 CORNERS[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 c = CORNERS[gl_VertexIndex] * HALF;
    outWorld = vec3(c.x, 0.0, c.y);
    outNormal = vec3(0, 1, 0);
    outColor = vec3(0.0); // coloured per pixel in ground.frag
    gl_Position = frame.viewProj * vec4(outWorld, 1.0);
}
