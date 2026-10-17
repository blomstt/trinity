#version 450

// A textured rectangle from six vertices, placed by push constants (no vertex buffer)
layout(push_constant) uniform Rect {
    vec4 centreHalfSize; // centre x, y and half width, height, in clip space
} rect;

layout(location = 0) out vec2 outUv;

const vec2 CORNERS[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));

void main() {
    vec2 c = CORNERS[gl_VertexIndex];
    outUv = c;
    gl_Position = vec4(rect.centreHalfSize.xy + (c * 2.0 - 1.0) * rect.centreHalfSize.zw, 0.0, 1.0);
}
