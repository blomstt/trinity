#version 450

// A ground grid around the camera target, from six vertices
layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params; // time, grid half size, centre x, centre z
} frame;

layout(location = 0) out vec3 outWorld;

const vec2 CORNERS[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 c = CORNERS[gl_VertexIndex] * frame.params.y + frame.params.zw;
    outWorld = vec3(c.x, 0.0, c.y);
    gl_Position = frame.viewProj * vec4(outWorld, 1.0);
}
