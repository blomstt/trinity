#version 450
#extension GL_GOOGLE_include_directive : require

// Shadow casters into one cascade (draw.pass), skinned like every other mesh.

#include "frame.glsl"
#define TRINITY_BONES_SET 2
#include "skinning.glsl"

layout(push_constant) uniform Draw {
    uint material;
    uint pass;
    uint pad0, pad1;
} draw;

layout(location = 0) in vec3 inPos;
layout(location = 3) in vec2 inUv;
layout(location = 4) in uvec4 inJoints;
layout(location = 5) in vec4 inWeights;

layout(location = 0) out vec2 outUv;
layout(location = 1) flat out uint outMaterial;

void main() {
    Instance inst = instances[visible[gl_InstanceIndex]];
    mat3x4 skin = skinMatrix(int(inst.anim.x), inst.anim.y, inst.anim.z > 0.5, inJoints, inWeights);
    vec3 world = vec4(vec4(inPos, 1.0) * skin, 1.0) * instanceTransform(inst);
    outUv = inUv;
    outMaterial = inst.data.x != 0xFFFFFFFFu ? inst.data.x : draw.material;
    vec4 p = frame.shadow[draw.pass] * vec4(world, 1.0);
    // Shadow matrices map xy to 0..1 for lookups; clip space wants -1..1
    gl_Position = vec4(p.xy * 2.0 - 1.0, p.z, 1.0);
}
