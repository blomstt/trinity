// Alpha-masked shadow casters (leaves, fences) cut out of the shadow like they're cut out on screen.

#include "material.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 1) flat in uint inMaterial;

void main() {
    Material m = materials[inMaterial];
    if (m.baseColor.a * texture(TEX(m.tex0.x), inUv).a < m.params.z)
        discard;
}
