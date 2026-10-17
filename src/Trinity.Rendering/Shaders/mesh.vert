#version 450
#extension GL_GOOGLE_include_directive : require

// Every mesh, skinned from its bone table (static meshes have one still bone), placed by its instance.

#include "frame.glsl"
#define TRINITY_BONES_SET 2
#include "skinning.glsl"

layout(push_constant) uniform Draw {
    uint material;
    uint pass;
    uint pad0, pad1;
} draw;

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) in uvec4 inJoints;
layout(location = 5) in vec4 inWeights;

layout(location = 0) out vec3 outWorld;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec4 outTangent;
layout(location = 3) out vec2 outUv;
layout(location = 4) flat out uint outMaterial;
layout(location = 5) flat out uint outFlags;

void main() {
    Instance inst = instances[visible[gl_InstanceIndex]];
    mat3x4 skin = skinMatrix(int(inst.anim.x), inst.anim.y, inst.anim.z > 0.5, inJoints, inWeights);
    mat3x4 place = instanceTransform(inst);
    vec3 world = vec4(vec4(inPos, 1.0) * skin, 1.0) * place;
    outWorld = world;
    outNormal = vec4(vec4(inNormal, 0.0) * skin, 0.0) * place;
    outTangent = vec4(vec4(vec4(inTangent.xyz, 0.0) * skin, 0.0) * place, inTangent.w);
    outUv = inUv;
    outMaterial = inst.data.x != 0xFFFFFFFFu ? inst.data.x : draw.material;
    outFlags = uint(inst.anim.w);
    gl_Position = frame.viewProj * vec4(world, 1.0);
}
