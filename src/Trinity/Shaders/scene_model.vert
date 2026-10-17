#version 450
#extension GL_GOOGLE_include_directive : require

// Any model in a scene: skinned from its bone table (static models have one still bone),
// placed by a per-node transform, playing the node's clip.

#include "skinning.glsl"

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in uvec4 inJoints;
layout(location = 3) in vec4 inWeights;
layout(location = 4) in vec4 inColor; // sRGB

layout(location = 5) in vec4 inRow0; // object transform, 3x4
layout(location = 6) in vec4 inRow1;
layout(location = 7) in vec4 inRow2;
layout(location = 8) in vec4 inAnim; // clip index, seconds, loop, selected

layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params; // time
} frame;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec3 outColor;
layout(location = 2) out vec3 outWorld;
layout(location = 3) out float outSelected;

void main() {
    mat3x4 skin = skinMatrix(int(inAnim.x), inAnim.y, inAnim.z > 0.5, inJoints, inWeights);
    vec3 p = vec4(inPos, 1.0) * skin;
    vec3 n = vec4(inNormal, 0.0) * skin;

    mat3x4 place = mat3x4(inRow0, inRow1, inRow2);
    vec3 world = vec4(p, 1.0) * place;
    outNormal = vec4(n, 0.0) * place;
    outWorld = world;

    outColor = pow(inColor.rgb, vec3(2.2));
    outSelected = inAnim.w;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
