#version 450
#extension GL_GOOGLE_include_directive : require

// Debug lines (Debug.Line, Box, Sphere), in world space.

#include "frame.glsl"

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec4 inColor; // sRGB

layout(location = 0) out vec4 outColor;

void main() {
    outColor = vec4(pow(inColor.rgb, vec3(2.2)), inColor.a);
    gl_Position = frame.viewProj * vec4(inPos, 1.0);
}
