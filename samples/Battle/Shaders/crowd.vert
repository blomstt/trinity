#version 450
#extension GL_GOOGLE_include_directive : require

// Soldiers, GPU-skinned from Trinity's bone table. Each soldier picks a clip and a time from its
// simulation state: dying, fighting, walking at its real speed, or idle.

#include "skinning.glsl"

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in uvec4 inJoints;
layout(location = 3) in vec4 inWeights;
layout(location = 4) in vec4 inColorTeam; // sRGB colour, a = 1 where the army colour applies

layout(location = 5) in vec4 inPlace; // x, z, facing, team
layout(location = 6) in vec4 inAnim;  // walk phase, attack phase (<0 none), death (0 alive .. 1 down), speed

layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params; // time, fog density
} frame;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec3 outColor;
layout(location = 2) out vec3 outWorld;

const int IDLE = 0, WALK = 1, ATTACK = 2, DEATH = 3; // GpuBoneTable clip order

float hash(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

void main() {
    float walk = inAnim.x, attack = inAnim.y, death = inAnim.z, speed = inAnim.w;

    int clip;
    float t;
    bool loop = true;
    if (death > 0.0) {
        clip = DEATH; t = death * clipDuration(DEATH); loop = false;
    } else if (attack >= 0.0) {
        clip = ATTACK; t = attack * clipDuration(ATTACK);
    } else if (speed > 0.35) {
        clip = WALK; t = fract(walk / 6.2831853) * clipDuration(WALK);
    } else {
        clip = IDLE; t = mod(frame.params.x + hash(float(gl_InstanceIndex) + inPlace.x) * 5.0, clipDuration(IDLE));
    }

    mat3x4 skin = skinMatrix(clip, t, loop, inJoints, inWeights);
    vec3 p = vec4(inPos, 1.0) * skin;
    vec3 n = vec4(inNormal, 0.0) * skin;

    float cy = cos(inPlace.z), sy = sin(inPlace.z);
    mat3 face = mat3(cy, 0, -sy, 0, 1, 0, sy, 0, cy);
    vec3 world = face * p + vec3(inPlace.x, 0.0, inPlace.y);

    vec3 base = pow(inColorTeam.rgb, vec3(2.2));
    vec3 team = inPlace.w < 0.5 ? vec3(0.13, 0.20, 0.42) : vec3(0.48, 0.10, 0.08); // navy / crimson
    vec3 color = inColorTeam.a > 0.5 ? pow(team, vec3(2.2)) * (base * 1.6) : base;

    outWorld = world;
    outNormal = face * n;
    outColor = death > 0.0 ? color * 0.6 : color;
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
