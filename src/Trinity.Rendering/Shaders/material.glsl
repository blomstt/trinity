// Materials and textures (set 1), from the MaterialLibrary. Define TRINITY_BINDLESS for the
// descriptor-indexing path; without it the array is a fixed size and indices must be uniform per draw.

struct Material {
    vec4 baseColor;
    vec4 emissive;  // rgb, normal scale
    vec4 params;    // metallic, roughness, alpha cutoff, occlusion strength
    uvec4 tex0;     // base colour, metallic-roughness, normal, occlusion
    uvec4 tex1;     // emissive, alpha mode (0 opaque, 1 mask, 2 blend), flags (bit 0: double-sided), unused
};

layout(std430, set = 1, binding = 0) readonly buffer Materials {
    Material materials[];
};

#ifdef TRINITY_BINDLESS
layout(set = 1, binding = 1) uniform sampler2D textures[];
#define TEX(i) textures[nonuniformEXT(i)]
#else
layout(constant_id = 1) const int TEXTURE_COUNT = 64;
layout(set = 1, binding = 1) uniform sampler2D textures[TEXTURE_COUNT];
#define TEX(i) textures[i]
#endif

// Per draw: which material, unless the instance overrides it
layout(push_constant) uniform Draw {
    uint material;
    uint pass;
    uint pad0, pad1;
} draw;
